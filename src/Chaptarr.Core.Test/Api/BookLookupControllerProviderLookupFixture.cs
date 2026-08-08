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
            public void ConvertToLocalUrls(int entityId, MediaCoverEntity coverEntity, IEnumerable<NzbDrone.Core.MediaCover.MediaCover> covers, string selectedAuthorImageHash = null)
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
            public byte[] GetImage(string hash) => throw new NotImplementedException();
        }

        private class BookServiceProxy : DispatchProxy
        {
            public readonly List<(string Provider, string ProviderId, BookMediaType MediaType)> FindAllCalls = new();
            public bool SingularLookupCalled { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IBookService.FindAllByProviderId))
                {
                    var provider = (string)args[0];
                    var providerId = (string)args[1];
                    var mediaType = (BookMediaType)args[2];
                    FindAllCalls.Add((provider, providerId, mediaType));

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
                if (targetMethod?.Name == nameof(IMediaFileService.GetFilesByBook))
                {
                    return new List<BookFile>();
                }

                throw new NotImplementedException($"Unexpected IMediaFileService.{targetMethod?.Name}");
            }
        }

        [Test]
        public void should_normalize_work_terms_by_readarr_facade_dialect_only()
        {
            var native = ReadarrFacadeProviderIdTranslator.NormalizeWorkTerm("work:123", null);
            var hc = ReadarrFacadeProviderIdTranslator.NormalizeWorkTerm("work:123", new ReadarrFacadeContext("hc", "audiobook", "/readarr/hc/audiobook"));
            var gr = ReadarrFacadeProviderIdTranslator.NormalizeWorkTerm("work:123", new ReadarrFacadeContext("gr", "ebook", "/readarr/gr/ebook"));

            Assert.That(native, Is.EqualTo("work:123"));
            Assert.That(hc, Is.EqualTo("hc:123"));
            Assert.That(gr, Is.EqualTo("gr:123"));
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
            var result = (List<Book>)method.Invoke(controller, new object[] { "hc", "hc:123", null });

            Assert.That(result.Select(book => book.Id).ToList(), Is.EqualTo(new List<int> { 1, 2 }));
            Assert.That(bookProxy.SingularLookupCalled, Is.False);
            Assert.That(bookProxy.FindAllCalls.Select(call => call.MediaType).ToList(), Is.EqualTo(new List<BookMediaType> { BookMediaType.Audiobook, BookMediaType.Ebook }));
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
            var result = (List<Book>)method.Invoke(controller, new object[] { "isbn:9780123456789", null });

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

            var resources = ((IEnumerable<BookResource>)method.Invoke(controller, new object[] { new[] { book }, null })).ToList();

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
