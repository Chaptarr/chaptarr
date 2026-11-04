using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.Books;
using Chaptarr.Http.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.AuthorStats;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaCover.Commands;
using MediaCoverModel = NzbDrone.Core.MediaCover.MediaCover;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Validation;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class BookControllerPagedResourceFixture
    {
        [Test]
        public void should_return_the_requested_page_and_full_library_count()
        {
            var bookService = new Mock<IBookService>();
            bookService
                .Setup(service => service.GetBooksPaged(150, 100, "bookshelfTitle", "ASC", false, null, null, null, null, null))
                .Returns(new PagedBookResource
                {
                    Records = new List<Book>(),
                    TotalCount = 321,
                    Offset = 150,
                    PageSize = 100
                });

            var controller = CreateController(bookService.Object);

            var action = controller.GetBooksPaged(offset: 150, pageSize: 100);
            var result = action.Result as OkObjectResult;
            Assert.That(result, Is.Not.Null);
            var page = (PagedBookApiResource)result!.Value!;

            Assert.Multiple(() =>
            {
                Assert.That(page.Offset, Is.EqualTo(150));
                Assert.That(page.PageSize, Is.EqualTo(100));
                Assert.That(page.TotalCount, Is.EqualTo(321));
                Assert.That(page.Records, Is.Empty);
            });

            bookService.Verify(service => service.GetBooksPaged(150, 100, "bookshelfTitle", "ASC", false, null, null, null, null, null), Times.Once);
        }

        [Test]
        public void should_preserve_bookshelf_default_page_resource_contract()
        {
            var bookService = new Mock<IBookService>();
            bookService
                .Setup(service => service.GetBooksPaged(0, 50, "bookshelfTitle", "ASC", false, null, null, null, null, null))
                .Returns(new PagedBookResource
                {
                    Records = new List<Book>
                    {
                        new Book
                        {
                            Id = 10,
                            AuthorId = 20,
                            Title = "Fixture book",
                            Links = new List<Links> { new Links { Name = "Source", Url = "https://example.org/book" } },
                            Author = new Author { Id = 20, Name = "Fixture author" }
                        }
                    },
                    TotalCount = 1,
                    Offset = 0,
                    PageSize = 50
                });

            var controller = CreateController(bookService.Object);
            var result = controller.GetBooksPaged().Result as OkObjectResult;
            Assert.That(result, Is.Not.Null);

            var page = (PagedBookApiResource)result!.Value!;
            Assert.Multiple(() =>
            {
                Assert.That(page.PageSize, Is.EqualTo(50));
                Assert.That(page.Records, Has.Count.EqualTo(1));
                Assert.That(page.Records.Single().Author?.Id, Is.EqualTo(20));
                Assert.That(page.Records.Single().Links?.Any(link => link.Name == "Source"), Is.True);
            });

            bookService.Verify(service => service.GetBooksPaged(0, 50, "bookshelfTitle", "ASC", false, null, null, null, null, null), Times.Once);
        }

        [Test]
        public void should_accept_the_configured_maximum_page_size_and_reject_larger_requests()
        {
            var bookService = new Mock<IBookService>();
            bookService
                .Setup(service => service.GetBooksPaged(0, 1000, "bookshelfTitle", "ASC", false, null, null, null, null, null))
                .Returns(new PagedBookResource
                {
                    Records = new List<Book>(),
                    TotalCount = 0,
                    Offset = 0,
                    PageSize = 1000
                });

            var controller = CreateController(bookService.Object);
            var accepted = controller.GetBooksPaged(offset: 0, pageSize: 1000).Result as OkObjectResult;
            Assert.That(accepted, Is.Not.Null);
            Assert.That(((PagedBookApiResource)accepted!.Value!).PageSize, Is.EqualTo(1000));

            var rejected = controller.GetBooksPaged(offset: 0, pageSize: 1001).Result as BadRequestObjectResult;
            Assert.That(rejected, Is.Not.Null);
            bookService.Verify(service => service.GetBooksPaged(0, 1000, "bookshelfTitle", "ASC", false, null, null, null, null, null), Times.Once);
        }

        [Test]
        public void should_resolve_a_migrated_bookshelf_id_to_the_requested_media_type()
        {
            var legacyIdentityMap = CreateLegacyIdentityMapMock();
            legacyIdentityMap
                .Setup(service => service.ResolveBookIds(It.Is<IEnumerable<int>>(ids => ids.SequenceEqual(new[] { 30 })), BookMediaType.Ebook))
                .Returns(new List<int> { 31 });

            var bookService = new Mock<IBookService>();
            bookService
                .Setup(service => service.GetBooks(It.Is<IEnumerable<int>>(ids => ids.SequenceEqual(new[] { 31 }))))
                .Returns(new List<Book>
                {
                    new Book
                    {
                        Id = 31,
                        AuthorId = 20,
                        MediaType = BookMediaType.Ebook,
                        Title = "Fixture ebook",
                        CleanTitle = "fixture ebook",
                        TitleSlug = "fixture-ebook"
                    }
                });

            var controller = CreateController(bookService.Object, legacyIdentityMap.Object);
            var books = controller.GetBooks(authorId: null, bookIds: new List<int> { 30 }, bookId: null, mediaType: "ebook");

            Assert.That(books.Select(book => book.Id), Is.EqualTo(new[] { 31 }));
            legacyIdentityMap.Verify(service => service.ResolveBookIds(It.IsAny<IEnumerable<int>>(), BookMediaType.Ebook), Times.Once);
            bookService.Verify(service => service.GetBooks(It.Is<IEnumerable<int>>(ids => ids.SequenceEqual(new[] { 31 }))), Times.Once);
        }

        [Test]
        public void should_resolve_a_migrated_bookshelf_id_when_getting_one_book_by_id()
        {
            var legacyIdentityMap = CreateLegacyIdentityMapMock();
            legacyIdentityMap
                .Setup(service => service.ResolveBookIds(It.Is<IEnumerable<int>>(ids => ids.SequenceEqual(new[] { 30 })), BookMediaType.Ebook))
                .Returns(new List<int> { 31 });

            var bookService = new Mock<IBookService>();
            bookService
                .Setup(service => service.GetBooks(It.Is<IEnumerable<int>>(ids => ids.SequenceEqual(new[] { 31 }))))
                .Returns(new List<Book>
                {
                    new Book
                    {
                        Id = 31,
                        AuthorId = 20,
                        MediaType = BookMediaType.Ebook,
                        Title = "Fixture ebook",
                        CleanTitle = "fixture ebook",
                        TitleSlug = "fixture-ebook"
                    }
                });

            var controller = CreateController(bookService.Object, legacyIdentityMap.Object);
            controller.ControllerContext.HttpContext.Request.QueryString = new QueryString("?mediaType=ebook");

            var result = controller.GetResourceByIdWithErrorHandler(30);

            Assert.That(result.Value?.Id, Is.EqualTo(31));
            legacyIdentityMap.Verify(service => service.ResolveBookIds(It.Is<IEnumerable<int>>(ids => ids.SequenceEqual(new[] { 30 })), BookMediaType.Ebook), Times.Once);
        }

        [Test]
        public void should_keep_the_legacy_cached_cover_url_for_a_migrated_bookshelf_book()
        {
            var legacyIdentityMap = CreateLegacyIdentityMapMock();
            legacyIdentityMap
                .Setup(service => service.ResolveBookIds(It.Is<IEnumerable<int>>(ids => ids.SequenceEqual(new[] { 30 })), BookMediaType.Ebook))
                .Returns(new List<int> { 31 });
            legacyIdentityMap
                .Setup(service => service.ResolveBookIds(It.Is<IEnumerable<int>>(ids => ids.SequenceEqual(new[] { 30 })), null))
                .Returns(new List<int> { 31 });
            legacyIdentityMap
                .Setup(service => service.GetMappingsByTargetIds("Book", It.Is<IEnumerable<int>>(ids => ids.SequenceEqual(new[] { 31 }))))
                .Returns(new List<BookshelfLegacyIdentityMapEntry>
                {
                    new() { SourceId = 30, TargetId = 31 }
                });

            var migratedBook = new Book
            {
                Id = 31,
                AuthorId = 20,
                MediaType = BookMediaType.Ebook,
                Title = "Fixture ebook",
                CleanTitle = "fixture ebook",
                TitleSlug = "fixture-ebook",
                Images = new List<MediaCoverModel>
                {
                    new(MediaCoverTypes.Cover, "/MediaCoverProxy/fake-hash/cover.jpg")
                }
            };

            var bookService = new Mock<IBookService>();
            bookService
                .Setup(service => service.GetBooks(It.Is<IEnumerable<int>>(ids => ids.SequenceEqual(new[] { 31 }))))
                .Returns(new List<Book> { migratedBook });

            var controller = CreateController(bookService.Object, legacyIdentityMap.Object);
            controller.ControllerContext.HttpContext.Request.QueryString = new QueryString("?mediaType=ebook");

            var result = controller.GetResourceByIdWithErrorHandler(30);

            Assert.That(result.Value?.Images?.Single().Url, Is.EqualTo("/MediaCover/Books/30/cover.jpg"));
        }

        [Test]
        public void should_keep_the_legacy_cached_cover_url_in_a_migrated_bookshelf_book_list()
        {
            var legacyIdentityMap = CreateLegacyIdentityMapMock();
            legacyIdentityMap
                .Setup(service => service.GetMappingsByTargetIds("Book", It.Is<IEnumerable<int>>(ids => ids.SequenceEqual(new[] { 31 }))))
                .Returns(new List<BookshelfLegacyIdentityMapEntry>
                {
                    new() { SourceId = 30, TargetId = 31 }
                });

            var migratedBook = new Book
            {
                Id = 31,
                AuthorId = 20,
                MediaType = BookMediaType.Ebook,
                Title = "Fixture ebook",
                CleanTitle = "fixture ebook",
                TitleSlug = "fixture-ebook",
                Images = new List<MediaCoverModel>
                {
                    new(MediaCoverTypes.Cover, "/MediaCoverProxy/fake-hash/cover.jpg")
                }
            };

            var bookService = new Mock<IBookService>();
            bookService
                .Setup(service => service.GetBooksForDisplay(null, null))
                .Returns(new List<Book> { migratedBook });

            var authorService = new Mock<IAuthorService>();
            authorService
                .Setup(service => service.GetAllAuthors())
                .Returns(new List<Author> { new() { Id = 20, Name = "Fixture author" } });

            var controller = CreateController(bookService.Object, legacyIdentityMap.Object, authorService: authorService.Object);

            var result = controller.GetBooks(authorId: null, bookIds: null, bookId: null);

            Assert.That(result.Single().Id, Is.EqualTo(30));
            Assert.That(result.Single().Images?.Single().Url, Is.EqualTo("/MediaCover/Books/30/cover.jpg"));
        }

        [Test]
        public void should_download_media_for_every_format_of_an_unscoped_migrated_book_id()
        {
            var legacyIdentityMap = CreateLegacyIdentityMapMock();
            legacyIdentityMap
                .Setup(service => service.ResolveBookIds(It.Is<IEnumerable<int>>(ids => ids.SequenceEqual(new[] { 30 })), null))
                .Returns(new List<int> { 30, 31 });
            var commandQueue = new Mock<IManageCommandQueue>();
            var controller = CreateController(
                new Mock<IBookService>().Object,
                legacyIdentityMap.Object,
                commandQueue.Object);

            var result = controller.DownloadBookMedia(30);

            Assert.That(result, Is.TypeOf<AcceptedResult>());
            commandQueue.Verify(queue => queue.Push(
                It.Is<DownloadBookMediaCommand>(command => command.BookIds.SequenceEqual(new[] { 30, 31 })),
                CommandPriority.Normal,
                CommandTrigger.Unspecified), Times.Once);
        }

        private static BookController CreateController(
            IBookService bookService,
            IBookshelfLegacyIdentityMapService legacyIdentityMap = null,
            IManageCommandQueue commandQueue = null,
            IAuthorService authorService = null)
        {
            var seriesLinks = new Mock<ISeriesBookLinkService>();
            seriesLinks.Setup(service => service.GetLinksByBook(It.IsAny<List<int>>()))
                .Returns(new List<SeriesBookLink>());

            var authorStatistics = new Mock<IAuthorStatisticsService>();
            authorStatistics.Setup(service => service.AuthorStatistics(It.IsAny<string>()))
                .Returns(new List<AuthorStatistics>());
            authorStatistics.Setup(service => service.BookshelfAuthorStatistics())
                .Returns(new List<AuthorStatistics>());

            var controller = new BookController(
                authorService: authorService,
                bookService: bookService,
                addBookService: null,
                editionService: null,
                editionSelector: null,
                seriesBookLinkService: seriesLinks.Object,
                authorStatisticsService: authorStatistics.Object,
                mediaFileService: null,
                coverMapper: new Mock<NzbDrone.Core.MediaCover.IMapCoversToLocal>().Object,
                mediaCoverProxy: null,
                upgradableSpecification: null,
                signalRBroadcaster: null,
                commandQueueManager: commandQueue,
                eventAggregator: null,
                metadataProfileService: null,
                qualityProfileService: null,
                rootFolderService: null,
                qualityProfileExistsValidator: new QualityProfileExistsValidator(null),
                metadataProfileExistsValidator: new MetadataProfileExistsValidator(null),
                logger: LogManager.GetCurrentClassLogger(),
                bookshelfLegacyIdentityMapService: legacyIdentityMap);
            var context = new DefaultHttpContext();
            if (legacyIdentityMap != null)
            {
                context.Items[ReadarrFacadeContext.ItemKey] = new ReadarrFacadeContext("hc", null, "/api", isBookshelfCompatibility: true);
            }

            controller.ControllerContext = new ControllerContext { HttpContext = context };
            return controller;
        }

        private static Mock<IBookshelfLegacyIdentityMapService> CreateLegacyIdentityMapMock()
        {
            var identityMap = new Mock<IBookshelfLegacyIdentityMapService>();
            identityMap
                .Setup(service => service.GetMappingsByTargetIds(It.IsAny<string>(), It.IsAny<IEnumerable<int>>()))
                .Returns(new List<BookshelfLegacyIdentityMapEntry>());
            return identityMap;
        }
    }
}
