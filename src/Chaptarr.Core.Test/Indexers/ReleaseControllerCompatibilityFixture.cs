using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Chaptarr.Api.V1.Indexers;
using Chaptarr.Http.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Parser;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    public class ReleaseControllerCompatibilityFixture
    {
        [Test]
        public async Task should_keep_legacy_release_search_returning_a_release_array()
        {
            const int bookId = 42;
            var book = new Book { Id = bookId, AuthorId = 7 };
            var bookService = new Mock<IBookService>();
            bookService.Setup(service => service.GetBook(bookId)).Returns(book);

            var releaseSearch = new Mock<ISearchForReleases>();
            releaseSearch
                .Setup(service => service.BookSearch(bookId, true, true, true, false))
                .ReturnsAsync(new List<DownloadDecision>());

            var prioritize = new Mock<IPrioritizeDownloadDecision>();
            prioritize
                .Setup(service => service.PrioritizeDecisions(It.IsAny<List<DownloadDecision>>()))
                .Returns((List<DownloadDecision> decisions) => decisions);

            var controller = CreateController(releaseSearch.Object, prioritize.Object, bookService.Object);

            var releases = await controller.GetReleases(bookId, null, null);

            Assert.That(releases, Is.TypeOf<List<ReleaseResource>>());
            Assert.That(releases, Is.Empty);
            releaseSearch.Verify(service => service.BookSearch(bookId, true, true, true, false), Times.Once);
        }

        [Test]
        public async Task should_search_all_format_rows_for_a_split_legacy_book_id()
        {
            const int sourceBookId = 24;
            const int audiobookId = 24;
            const int ebookId = 61;
            var bookService = new Mock<IBookService>();
            bookService.Setup(service => service.GetBook(audiobookId)).Returns(new Book { Id = audiobookId, AuthorId = 7 });
            bookService.Setup(service => service.GetBook(ebookId)).Returns(new Book { Id = ebookId, AuthorId = 7 });

            var identityMap = new Mock<IBookshelfLegacyIdentityMapService>();
            identityMap
                .Setup(service => service.ResolveBookIds(It.Is<IEnumerable<int>>(ids => ids.Single() == sourceBookId), null))
                .Returns(new List<int> { audiobookId, ebookId });

            var releaseSearch = new Mock<ISearchForReleases>();
            releaseSearch
                .Setup(service => service.BookSearch(audiobookId, true, true, true, false))
                .ReturnsAsync(new List<DownloadDecision>());
            releaseSearch
                .Setup(service => service.BookSearch(ebookId, true, true, true, false))
                .ReturnsAsync(new List<DownloadDecision>());

            var prioritize = new Mock<IPrioritizeDownloadDecision>();
            prioritize
                .Setup(service => service.PrioritizeDecisions(It.IsAny<List<DownloadDecision>>()))
                .Returns((List<DownloadDecision> decisions) => decisions);

            var controller = CreateController(
                releaseSearch.Object,
                prioritize.Object,
                bookService.Object,
                identityMapService: identityMap.Object);

            var releases = await controller.GetReleases(sourceBookId, null, null);

            Assert.That(releases, Is.Empty);
            releaseSearch.Verify(service => service.BookSearch(audiobookId, true, true, true, false), Times.Once);
            releaseSearch.Verify(service => service.BookSearch(ebookId, true, true, true, false), Times.Once);
        }

        [Test]
        public async Task should_return_the_interactive_search_wrapper_on_the_new_search_route()
        {
            const int bookId = 42;
            var book = new Book { Id = bookId, AuthorId = 7 };
            var author = new Author { Id = book.AuthorId, Name = "Test Author" };
            var bookService = new Mock<IBookService>();
            bookService.Setup(service => service.GetBook(bookId)).Returns(book);
            bookService.Setup(service => service.GetBooksByAuthor(author.Id)).Returns(new List<Book> { book });

            var authorService = new Mock<IAuthorService>();
            authorService.Setup(service => service.GetAuthor(author.Id)).Returns(author);

            var releaseSearch = new Mock<ISearchForReleases>();
            releaseSearch
                .Setup(service => service.BookSearch(bookId, true, true, true, false))
                .ReturnsAsync(new List<DownloadDecision>());

            var prioritize = new Mock<IPrioritizeDownloadDecision>();
            prioritize
                .Setup(service => service.PrioritizeDecisions(It.IsAny<List<DownloadDecision>>()))
                .Returns((List<DownloadDecision> decisions) => decisions);

            var controller = CreateController(releaseSearch.Object, prioritize.Object, bookService.Object, authorService.Object);

            var response = await controller.GetInteractiveReleases(bookId, null);

            Assert.That(response.Result, Is.InstanceOf<OkObjectResult>());
            Assert.That(((OkObjectResult)response.Result).Value, Is.TypeOf<ReleaseSearchResponse>());
        }

        private static ReleaseController CreateController(
            ISearchForReleases releaseSearch,
            IPrioritizeDownloadDecision prioritize,
            IBookService bookService,
            IAuthorService authorService = null,
            IBookshelfLegacyIdentityMapService identityMapService = null)
        {
            var controller = new ReleaseController(
                rssFetcherAndParser: null,
                releaseSearchService: releaseSearch,
                downloadDecisionMaker: null,
                prioritizeDownloadDecision: prioritize,
                downloadService: null,
                authorService: authorService,
                bookService: bookService,
                parsingService: null,
                indexerFactory: null,
                cacheManager: new CacheManager(),
                logger: LogManager.GetLogger(nameof(ReleaseControllerCompatibilityFixture)),
                bookshelfLegacyIdentityMapService: identityMapService);

            var context = new DefaultHttpContext();
            if (identityMapService != null)
            {
                context.Items[ReadarrFacadeContext.ItemKey] = new ReadarrFacadeContext("hc", null, "/api", isBookshelfCompatibility: true);
            }

            controller.ControllerContext = new ControllerContext { HttpContext = context };

            return controller;
        }
    }
}
