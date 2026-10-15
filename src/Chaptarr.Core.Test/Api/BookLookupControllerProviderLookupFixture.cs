using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Chaptarr.Api.V1.Books;
using Chaptarr.Http.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MetadataSource;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class BookLookupControllerProviderLookupFixture
    {
        private sealed class CoverMapperStub : IMapCoversToLocal
        {
            public void EnsureBookCoversForBooks(List<Book> books) => throw new NotImplementedException();
            public void EnsureBookCoversForImport(Book book) => EnsureBookCovers(book);
            public void ConvertBookCoversToLocalUrls(IEnumerable<KeyValuePair<int, List<NzbDrone.Core.MediaCover.MediaCover>>> covers)
            {
                foreach (var book in covers)
                {
                    ConvertToLocalUrls(book.Key, MediaCoverEntity.Book, book.Value);
                }
            }

            public void DeleteBookCovers(int bookId) => throw new NotImplementedException();

            public void ConvertToLocalUrls(int entityId, MediaCoverEntity coverEntity, IList<NzbDrone.Core.MediaCover.MediaCover> covers, string selectedAuthorImageHash = null)
            {
            }

            public string GetCoverPath(int entityId, MediaCoverEntity coverEntity, MediaCoverTypes coverType, string extension, int? height = null) => throw new NotImplementedException();
            public void EnsureAuthorCovers(Author author) => throw new NotImplementedException();
            public void EnsureBookCovers(Book book) => throw new NotImplementedException();
            public Task<EnsureImageResult> EnsureAuthorImage(Author author, NzbDrone.Core.MediaCover.MediaCover cover) => throw new NotImplementedException();
        }

        private sealed class MediaCoverProxyStub : IMediaCoverProxy
        {
            public List<string> RegisteredUrls { get; } = new();

            public string RegisterUrl(string url)
            {
                RegisteredUrls.Add(url);
                return "/MediaCoverProxy/test/" + System.IO.Path.GetFileName(new Uri(url).AbsolutePath);
            }

            public void ProxyRemoteUrls(IEnumerable<NzbDrone.Core.MediaCover.MediaCover> covers)
            {
                foreach (var cover in covers ?? Enumerable.Empty<NzbDrone.Core.MediaCover.MediaCover>())
                {
                    cover.Url = RegisterUrl(cover.Url);
                }
            }

            public string GetUrl(string hash) => throw new NotImplementedException();
            public byte[] GetImageFromUrl(string url) => throw new NotImplementedException();
        }

        private class BookServiceProxy : DispatchProxy
        {
            public readonly List<(string Provider, string ProviderId, BookMediaType MediaType)> FindAllCalls = new();
            public readonly List<List<(string Scope, string Provider, string Id)>> BatchLookupCalls = new();
            public bool SingularLookupCalled { get; private set; }
            public List<Book> Books { get; set; }

            private List<Book> Catalog => Books ?? new List<Book>
            {
                new() { Id = 1, Title = "Audiobook", MediaType = BookMediaType.Audiobook, HardcoverBookId = "hc:123" },
                new() { Id = 2, Title = "Ebook", MediaType = BookMediaType.Ebook, HardcoverBookId = "hc:123" }
            };

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IBookService.FindBookIdsByProviderIds))
                {
                    var keys = ((IEnumerable<(string Scope, string Provider, string Id)>)args[0]).Distinct().ToList();
                    BatchLookupCalls.Add(keys);
                    return keys.ToDictionary(key => key, _ => Catalog.Select(book => book.Id).ToList());
                }
                if (targetMethod?.Name == nameof(IBookService.GetBooksForLookup))
                {
                    return Books ?? new List<Book>();
                }
                if (targetMethod?.Name == nameof(IBookService.GetExistingBooks))
                {
                    var ids = ((IEnumerable<int>)args[0]).ToHashSet();
                    return Catalog.Where(book => ids.Contains(book.Id)).ToList();
                }
                if (targetMethod?.Name == nameof(IBookService.FindAllByProviderId) || targetMethod?.Name == nameof(IBookService.FindAllByWorkProviderId))
                {
                    var provider = (string)args[0];
                    var providerId = (string)args[1];
                    var mediaType = (BookMediaType)args[2];
                    FindAllCalls.Add((provider, providerId, mediaType));

                    if (Books != null)
                    {
                        return Books.Where(book => book.MediaType == mediaType).ToList();
                    }

                    return mediaType == BookMediaType.Audiobook
                        ? new List<Book> { new Book { Id = 1, Title = "Audiobook", MediaType = BookMediaType.Audiobook, HardcoverBookId = "hc:123" } }
                        : new List<Book> { new Book { Id = 2, Title = "Ebook", MediaType = BookMediaType.Ebook, HardcoverBookId = "hc:123" } };
                }

                if (targetMethod?.Name == nameof(IBookService.FindByProviderId) ||
                    targetMethod?.Name == nameof(IBookService.FindByASIN) ||
                    targetMethod?.Name == nameof(IBookService.FindByISBN))
                {
                    SingularLookupCalled = true;
                    throw new AssertionException("Book lookup must not collapse unscoped provider matches through a singular lookup");
                }

                throw new NotImplementedException($"Unexpected IBookService.{targetMethod?.Name}");
            }
        }

        private class EditionServiceProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IEditionService.GetEditionsByBook) ||
                    targetMethod?.Name == nameof(IEditionService.GetEditionsByProviderAndId))
                {
                    return new List<Edition>();
                }

                throw new NotImplementedException($"Unexpected IEditionService.{targetMethod?.Name}");
            }
        }

        private class MediaFileServiceProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IMediaFileService.GetFilesByBook) || targetMethod?.Name == nameof(IMediaFileService.GetFilesByBooks))
                {
                    return new List<BookFile>();
                }

                throw new NotImplementedException($"Unexpected IMediaFileService.{targetMethod?.Name}");
            }
        }

        [Test]
        public void should_normalize_work_terms_by_readarr_facade_dialect_only()
        {
            var native = ReadarrFacadeProviderIdTranslator.NormalizeLookupTerm("work:123", null);
            var hc = ReadarrFacadeProviderIdTranslator.NormalizeLookupTerm("work:123", new ReadarrFacadeContext("hc", "audiobook", "/readarr/hc/audiobook"));
            var gr = ReadarrFacadeProviderIdTranslator.NormalizeLookupTerm("work:123", new ReadarrFacadeContext("gr", "ebook", "/readarr/gr/ebook"));

            Assert.That(native, Is.EqualTo("work:123"));
            Assert.That(hc, Is.EqualTo("hc:123"));
            Assert.That(gr, Is.EqualTo("gr:123"));
        }

        [TestCase("hc", "hc-ed:123")]
        [TestCase("gr", "gr-ed:123")]
        public void facade_edition_term_keeps_edition_identity(string provider, string expected)
        {
            var facade = new ReadarrFacadeContext(provider, "ebook", "/readarr/" + provider + "/ebook");
            Assert.That(ReadarrFacadeProviderIdTranslator.NormalizeLookupTerm("edition:123", facade), Is.EqualTo(expected));
            Assert.That(ReadarrFacadeProviderIdTranslator.NormalizeLookupTerm("edition:123", null), Is.EqualTo("edition:123"));
            Assert.That(ReadarrFacadeProviderIdTranslator.NormalizeLookupTerm("edition:not-an-id", facade), Is.EqualTo("edition:not-an-id"));
        }

        [TestCase("audiobook", BookMediaType.Audiobook)]
        [TestCase("ebook", BookMediaType.Ebook)]
        public void facade_lookup_puts_an_already_selected_copy_first_without_crossing_formats(string scope, BookMediaType mediaType)
        {
            var books = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var proxy = (BookServiceProxy)books;
            proxy.Books = Enumerable.Range(1, 3).Select(id => new Book
            {
                Id = id, Title = "Requested work", HardcoverBookId = "hc:123", MediaType = mediaType,
                AudiobookMonitored = mediaType == BookMediaType.Audiobook && id > 1,
                EbookMonitored = mediaType == BookMediaType.Ebook && id > 1
            }).ToList();
            proxy.Books.Add(new Book { Id = 4, HardcoverBookId = "hc:123", Title = "Other format",
                MediaType = mediaType == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook });
            var context = new DefaultHttpContext();
            context.Items[ReadarrFacadeContext.ItemKey] = new ReadarrFacadeContext("hc", scope, "/" + scope);
            var controller = new BookLookupController(null, new CoverMapperStub(), books,
                DispatchProxy.Create<IEditionService, EditionServiceProxy>(), DispatchProxy.Create<IMediaFileService, MediaFileServiceProxy>(),
                new MediaCoverProxyStub(), null) { ControllerContext = new ControllerContext { HttpContext = context } };
            var result = controller.Search("work:123").Value;
            Assert.That(result.Select(book => book.Id), Is.EqualTo(new[] { 2, 1, 3 }));
            Assert.That(result[0].Monitored, Is.True);
        }

        [Test]
        public void should_reject_native_bare_work_terms()
        {
            var controller = new BookLookupController(
                searchProxy: null,
                coverMapper: null,
                bookService: null,
                editionService: null,
                mediaFileService: null,
                mediaCoverProxy: null,
                providerAliasService: null)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext()
                }
            };

            var result = controller.Search("work:123");

            Assert.That(result.Result, Is.TypeOf<BadRequestObjectResult>());
        }

        [Test]
        public void should_return_all_media_type_matches_for_unscoped_provider_lookup()
        {
            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var bookProxy = (BookServiceProxy)(object)bookService;
            var editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
            var mediaFileService = DispatchProxy.Create<IMediaFileService, MediaFileServiceProxy>();
            var controller = new BookLookupController(
                searchProxy: null,
                coverMapper: null,
                bookService: bookService,
                editionService: editionService,
                mediaFileService: mediaFileService,
                mediaCoverProxy: null,
                providerAliasService: null);

            var method = typeof(BookLookupController).GetMethod("LookupLocalByProvider", BindingFlags.Instance | BindingFlags.NonPublic);
            var result = (List<Book>)method.Invoke(controller, new object[] { "hc", "hc:123", null, new List<Book>() });

            Assert.That(result.Select(book => book.Id).ToList(), Is.EqualTo(new List<int> { 1, 2 }));
            Assert.That(bookProxy.SingularLookupCalled, Is.False);
            Assert.That(bookProxy.FindAllCalls, Is.Empty);
            Assert.That(bookProxy.BatchLookupCalls.Single(), Is.EqualTo(new[] { (Scope: "work", Provider: "hc", Id: "hc:123") }));
        }

        [Test]
        public void should_use_plural_book_column_lookup_for_isbn_when_no_edition_rows_match()
        {
            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var bookProxy = (BookServiceProxy)(object)bookService;
            var editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
            var mediaFileService = DispatchProxy.Create<IMediaFileService, MediaFileServiceProxy>();
            var controller = new BookLookupController(
                searchProxy: null,
                coverMapper: null,
                bookService: bookService,
                editionService: editionService,
                mediaFileService: mediaFileService,
                mediaCoverProxy: null,
                providerAliasService: null);

            var method = typeof(BookLookupController).GetMethod("LookupLocalByProviderOrEdition", BindingFlags.Instance | BindingFlags.NonPublic);
            var result = (List<Book>)method.Invoke(controller, new object[] { "isbn:9780123456789", null, new List<Book>() });

            Assert.That(result.Select(book => book.Id).ToList(), Is.EqualTo(new List<int> { 1, 2 }));
            Assert.That(bookProxy.SingularLookupCalled, Is.False);
            Assert.That(bookProxy.FindAllCalls.Select(call => call.Provider).ToList(), Is.EqualTo(new List<string> { "isbn", "isbn" }));
            Assert.That(bookProxy.FindAllCalls.Select(call => call.MediaType).ToList(), Is.EqualTo(new List<BookMediaType> { BookMediaType.Audiobook, BookMediaType.Ebook }));
        }

        [Test]
        public void edition_choices_should_keep_their_own_remote_art_instead_of_borrowing_the_monitored_book_cover()
        {
            var firstCover = "https://images.example/edition-one.jpg";
            var secondCover = "https://images.example/edition-two.png";
            var book = new Book
            {
                Id = 0,
                Title = "Book",
                MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition>
                {
                    new()
                    {
                        Id = 1,
                        BookId = 0,
                        Title = "Edition One",
                        Monitored = true,
                        Images = new List<NzbDrone.Core.MediaCover.MediaCover>
                        {
                            new(MediaCoverTypes.Cover, firstCover)
                        }
                    },
                    new()
                    {
                        Id = 2,
                        BookId = 0,
                        Title = "Edition Two",
                        Monitored = false,
                        Images = new List<NzbDrone.Core.MediaCover.MediaCover>
                        {
                            new(MediaCoverTypes.Cover, secondCover)
                        }
                    }
                }
            };
            var proxy = new MediaCoverProxyStub();
            var controller = new BookLookupController(
                searchProxy: null,
                coverMapper: new CoverMapperStub(),
                bookService: null,
                editionService: null,
                mediaFileService: null,
                mediaCoverProxy: proxy,
                providerAliasService: null);
            var method = typeof(BookLookupController).GetMethod("MapToResource", BindingFlags.Instance | BindingFlags.NonPublic);

            var resources = ((IEnumerable<BookResource>)method.Invoke(controller, new object[] { new[] { book }, null, null })).ToList();

            Assert.That(proxy.RegisteredUrls, Is.EqualTo(new[] { firstCover, secondCover }));
            Assert.That(resources.Single().Editions.SelectMany(edition => edition.Images).Select(image => image.Url),
                Is.EqualTo(new[]
                {
                    "/MediaCoverProxy/test/edition-one.jpg",
                    "/MediaCoverProxy/test/edition-two.png"
                }));
        }
    }
}
