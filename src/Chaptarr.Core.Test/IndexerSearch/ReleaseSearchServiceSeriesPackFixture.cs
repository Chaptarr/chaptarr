using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Moq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.IndexerSearch
{
    [TestFixture]
    public class ReleaseSearchServiceSeriesPackFixture
    {
        [Test]
        public async Task series_search_should_search_ordered_books_for_the_selected_author_and_media_type()
        {
            var author = new Author
            {
                Id = 10,
                Name = "Series Author",
                AudiobookQualityProfileId = 1
            };
            var firstBook = BuildBook(101, author, BookMediaType.Audiobook);
            var secondBook = BuildBook(102, author, BookMediaType.Audiobook);
            var ebook = BuildBook(103, author, BookMediaType.Ebook);
            var authorCatalog = new List<Book> { firstBook, secondBook, ebook };
            author.Books = authorCatalog;

            var series = new Series
            {
                Id = 20,
                Title = "The Example Series",
                MediaType = BookMediaType.Audiobook
            };
            var seriesLinks = new List<SeriesBookLink>
            {
                new() { SeriesId = series.Id, BookId = firstBook.Id, SeriesPosition = 2 },
                new() { SeriesId = series.Id, BookId = secondBook.Id, SeriesPosition = 1 },
                new() { SeriesId = series.Id, BookId = ebook.Id, SeriesPosition = 3 },
                new() { SeriesId = series.Id, BookId = 999, SeriesPosition = 4 }
            };

            var release = new ReleaseInfo { Guid = "series-pack-guid", Title = "The Example Series - Complete" };
            var remoteBook = new RemoteBook { Release = release };
            var decision = new DownloadDecision(remoteBook);
            var indexer = new Mock<IIndexer>();
            var indexerFactory = new Mock<IIndexerFactory>();
            var bookService = new Mock<IBookService>();
            var authorService = new Mock<IAuthorService>();
            var seriesService = new Mock<ISeriesService>();
            var seriesBookLinkService = new Mock<ISeriesBookLinkService>();
            var decisionMaker = new Mock<IMakeDownloadDecision>();
            BookSearchCriteria capturedCriteria = null;

            indexer.SetupGet(x => x.Definition).Returns(new IndexerDefinition { Name = "Series test" });
            indexer.Setup(x => x.Fetch(It.IsAny<BookSearchCriteria>()))
                .Callback<BookSearchCriteria>(criteria => capturedCriteria = criteria)
                .ReturnsAsync(new List<ReleaseInfo> { release });
            indexerFactory.Setup(x => x.InteractiveSearchEnabled(It.IsAny<bool>())).Returns(new List<IIndexer> { indexer.Object });
            bookService.Setup(x => x.GetBooksByAuthor(author.Id)).Returns(authorCatalog);
            authorService.Setup(x => x.GetAuthor(author.Id)).Returns(author);
            seriesService.Setup(x => x.GetSeries(series.Id)).Returns(series);
            seriesBookLinkService.Setup(x => x.GetLinksBySeries(series.Id)).Returns(seriesLinks);
            decisionMaker.Setup(x => x.GetSearchDecision(It.IsAny<List<ReleaseInfo>>(), It.IsAny<SearchCriteriaBase>()))
                .Returns(new List<DownloadDecision> { decision });

            var subject = new ReleaseSearchService(
                indexerFactory.Object,
                bookService.Object,
                authorService.Object,
                null,
                decisionMaker.Object,
                LogManager.GetLogger(nameof(ReleaseSearchServiceSeriesPackFixture)),
                seriesService.Object,
                seriesBookLinkService.Object);

            var decisions = await subject.SeriesSearch(series.Id, author.Id, true, true);

            Assert.That(decisions, Has.Count.EqualTo(1));
            Assert.That(capturedCriteria, Is.Not.Null);
            Assert.That(capturedCriteria.Author, Is.SameAs(author));
            Assert.That(capturedCriteria.BookTitle, Is.EqualTo(series.Title));
            Assert.That(capturedCriteria.Books.Select(book => book.Id), Is.EqualTo(new[] { secondBook.Id, firstBook.Id }));
            Assert.That(remoteBook.Author, Is.SameAs(author));
            Assert.That(remoteBook.Books.Select(book => book.Id), Is.EqualTo(new[] { secondBook.Id, firstBook.Id }));
            Assert.That(remoteBook.DownloadAllowed, Is.True);
            bookService.Verify(x => x.GetBooksByAuthor(author.Id), Times.Once);
        }

        private static Book BuildBook(int id, Author author, BookMediaType mediaType)
        {
            return new Book
            {
                Id = id,
                AuthorId = author.Id,
                Author = author,
                Title = $"Book {id}",
                MediaType = mediaType
            };
        }
    }
}
