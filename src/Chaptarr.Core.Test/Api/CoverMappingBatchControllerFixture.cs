using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Chaptarr.Api.V1.Books;
using Chaptarr.Api.V1.Search;
using Microsoft.AspNetCore.Mvc;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.MediaCover;
using CoverImage = NzbDrone.Core.MediaCover.MediaCover;
using NzbDrone.Core.MetadataSource;

using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class CoverMappingBatchControllerFixture
    {
        private class BatchCoverMapperProxy : DispatchProxy
        {
            public List<List<int>> BatchBookIds { get; } = new();
            public List<int> SingleBookMappingIds { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IMapCoversToLocal.ConvertBookCoversToLocalUrls))
                {
                    var covers = ((IEnumerable<KeyValuePair<int, List<CoverImage>>>)args[0]).ToList();
                    BatchBookIds.Add(covers.Select(entry => entry.Key).ToList());

                    foreach (var entry in covers)
                    {
                        foreach (var cover in entry.Value)
                        {
                            cover.RemoteUrl ??= cover.Url;
                            if (entry.Key == 0)
                            {
                                cover.Url = "/MediaCoverProxy/unpersisted-book";
                            }
                            else
                            {
                                cover.Url = null;
                                cover.TemporaryUrl = $"/MediaCoverProxy/temporary/{entry.Key}";
                            }
                        }
                    }

                    return null;
                }

                if (targetMethod.Name == nameof(IMapCoversToLocal.ConvertToLocalUrls))
                {
                    var entity = (MediaCoverEntity)args[1];
                    if (entity == MediaCoverEntity.Book)
                    {
                        SingleBookMappingIds.Add((int)args[0]);
                        throw new AssertionException("Book rows must use the set-based cover mapper");
                    }

                    return null;
                }

                throw new NotImplementedException(targetMethod.Name);
            }
        }

        private static IMapCoversToLocal CoverMapper(out BatchCoverMapperProxy mapper)
        {
            var proxy = DispatchProxy.Create<IMapCoversToLocal, BatchCoverMapperProxy>();
            mapper = (BatchCoverMapperProxy)(object)proxy;
            return proxy;
        }

        private static Book MakeBook(int id)
        {
            return new Book
            {
                Id = id,
                Title = $"Book {id}",
                AuthorId = 9,
                Author = new Author { Name = "Fixture Author" },
                MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition>(),
                Images = new List<CoverImage>
                {
                    new(MediaCoverTypes.Cover, $"/MediaCover/Book/{id}/Cover.jpg")
                    {
                        RemoteUrl = $"https://covers.example/{id}.jpg"
                    }
                }
            };
        }

        private static IEnumerable<BookResource> MapLookupBooks(BookLookupController controller, IEnumerable<Book> books)
        {
            var method = typeof(BookLookupController).GetMethod("MapToResource", BindingFlags.Instance | BindingFlags.NonPublic);
            return (IEnumerable<BookResource>)method.Invoke(controller, new object[] { books, null, null });
        }

        private static IEnumerable<SearchResource> MapSearchResults(SearchController controller, IEnumerable<object> results)
        {
            var method = typeof(SearchController).GetMethod("MapToResource", BindingFlags.Instance | BindingFlags.NonPublic);
            return (IEnumerable<SearchResource>)method.Invoke(controller, new object[] { results, null, null });
        }

        [Test]
        public void library_search_maps_book_covers_as_one_batch_and_preserves_rows()
        {
            var result = new LibrarySearchResult
            {
                Books = new List<LibrarySearchBook>
                {
                    new() { Id = 51, Title = "First", Images = new List<CoverImage> { new(MediaCoverTypes.Cover, "https://covers.example/51.jpg") } },
                    new() { Id = 52, Title = "Second", Images = new List<CoverImage> { new(MediaCoverTypes.Cover, "https://covers.example/52.jpg") } },
                    new() { Id = 53, Title = "Third", Images = new List<CoverImage> { new(MediaCoverTypes.Cover, "https://covers.example/53.jpg") } }
                }
            };
            var searchService = Proxy<ILibrarySearchService>((method, args) =>
            {
                if (method.Name == nameof(ILibrarySearchService.Search))
                {
                    Assert.That(args, Is.EqualTo(new object[] { "term", 7 }));
                    return result;
                }

                throw new NotImplementedException(method.Name);
            });
            var coverMapper = CoverMapper(out var mapper);
            var controller = new LibrarySearchController(searchService, coverMapper);

            var mapped = controller.Search("term", 7);

            Assert.Multiple(() =>
            {
                Assert.That(mapped, Is.SameAs(result));
                Assert.That(mapped.Books.Select(book => book.Title), Is.EqualTo(new[] { "First", "Second", "Third" }));
                Assert.That(mapper.BatchBookIds, Is.EqualTo(new[] { new[] { 51, 52, 53 } }));
                Assert.That(mapper.SingleBookMappingIds, Is.Empty);
                Assert.That(mapped.Books.Select(book => book.Images.Single().Url), Is.All.Null);
                Assert.That(mapped.Books.Select(book => book.Images.Single().TemporaryUrl),
                    Is.EqualTo(new[] { "/MediaCoverProxy/temporary/51", "/MediaCoverProxy/temporary/52", "/MediaCoverProxy/temporary/53" }));
            });
        }

        [Test]
        public void book_lookup_maps_persisted_and_unpersisted_book_covers_in_one_batch()
        {
            var books = new[] { MakeBook(0), MakeBook(61), MakeBook(62) };
            var coverMapper = CoverMapper(out var mapper);
            var controller = new BookLookupController(
                searchProxy: null,
                coverMapper: coverMapper,
                bookService: null,
                editionService: null,
                mediaFileService: null,
                mediaCoverProxy: null);

            var mapped = MapLookupBooks(controller, books).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(mapped.Select(book => book.Id), Is.EqualTo(new[] { 0, 61, 62 }));
                Assert.That(mapped.Select(book => book.Title), Is.EqualTo(new[] { "Book 0", "Book 61", "Book 62" }));
                Assert.That(mapper.BatchBookIds, Is.EqualTo(new[] { new[] { 0, 61, 62 } }));
                Assert.That(mapper.SingleBookMappingIds, Is.Empty);
                Assert.That(mapped[0].Images.Single().Url, Is.EqualTo("/MediaCoverProxy/unpersisted-book"));
                Assert.That(mapped.Skip(1).Select(book => book.Images.Single().Url), Is.All.Null);
                Assert.That(mapped.Skip(1).Select(book => book.Images.Single().TemporaryUrl),
                    Is.EqualTo(new[] { "/MediaCoverProxy/temporary/61", "/MediaCoverProxy/temporary/62" }));
                Assert.That(mapped.Select(book => book.RemoteCover), Is.All.EqualTo(string.Empty));
            });
        }

        [Test]
        public void search_maps_persisted_and_unpersisted_book_covers_in_one_batch_without_reordering_results()
        {
            var books = new[] { MakeBook(0), MakeBook(71), MakeBook(72) };
            var coverMapper = CoverMapper(out var mapper);
            var controller = new SearchController(
                searchProxy: null,
                fileNameBuilder: null,
                coverMapper: coverMapper,
                mediaCoverProxy: null,
                authorService: null,
                bookService: null);

            var mapped = MapSearchResults(controller, books).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(mapped.Select(result => result.Id), Is.EqualTo(new[] { 1, 2, 3 }));
                Assert.That(mapped.Select(result => result.Book.Id), Is.EqualTo(new[] { 0, 71, 72 }));
                Assert.That(mapped.Select(result => result.Book.Title), Is.EqualTo(new[] { "Book 0", "Book 71", "Book 72" }));
                Assert.That(mapper.BatchBookIds, Is.EqualTo(new[] { new[] { 0, 71, 72 } }));
                Assert.That(mapper.SingleBookMappingIds, Is.Empty);
                Assert.That(mapped[0].Book.Images.Single().Url, Is.EqualTo("/MediaCoverProxy/unpersisted-book"));
                Assert.That(mapped.Skip(1).Select(result => result.Book.Images.Single().Url), Is.All.Null);
                Assert.That(mapped.Skip(1).Select(result => result.Book.Images.Single().TemporaryUrl),
                    Is.EqualTo(new[] { "/MediaCoverProxy/temporary/71", "/MediaCoverProxy/temporary/72" }));
            });
        }
    }
}
