using System;
using System.Collections.Generic;
using System.Linq;
using Moq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Download.Aggregation;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.DecisionEngine
{
    [TestFixture]
    public class BookshelfReleaseParsingFixture
    {
        [Test]
        public void migrated_search_should_preserve_the_predecessor_parser_and_identification()
        {
            var config = new Mock<IConfigService>();
            config.SetupGet(c => c.BookshelfMatchCompatibility).Returns(true);
            var author = new Author { Id = 10, Name = "Mark Twain" };
            var book = new Book
            {
                Id = 30,
                AuthorId = author.Id,
                Author = author,
                Title = "The Adventures of Tom Sawyer",
                MediaType = BookMediaType.Ebook,
                Editions = new List<Edition>
                {
                    new()
                    {
                        Id = 40,
                        Title = "The Adventures of Tom Sawyer",
                        IsEbook = true,
                        Monitored = true
                    }
                }
            };
            var books = new List<Book> { book };
            author.Books = books;
            var parsing = new Mock<IParsingService>();
            parsing.Setup(p => p.Map(It.IsAny<ParsedBookInfo>(), It.IsAny<SearchCriteriaBase>()))
                .Returns((ParsedBookInfo info, SearchCriteriaBase criteria) => new RemoteBook
                {
                    ParsedBookInfo = info,
                    Author = author,
                    Books = books
                });
            var formats = new Mock<ICustomFormatCalculationService>();
            formats.Setup(f => f.ParseCustomFormat(It.IsAny<RemoteBook>(), It.IsAny<long>())).Returns(new List<CustomFormat>());
            var logger = LogManager.GetCurrentClassLogger();
            var maker = new DownloadDecisionMaker(
                new[] { new ReleaseTitleMatchSpecification(logger, config.Object) },
                parsing.Object,
                Mock.Of<IEditionFtsRepository>(),
                formats.Object,
                Mock.Of<IRemoteBookAggregationService>(),
                Mock.Of<IReleaseNarratorMetadataEnricher>(),
                config.Object,
                logger);
            const string title = "TWAIN, MARK - THE.ADVENTURES.OF.TOM.SAWYER.epub";
            var report = new ReleaseInfo { Title = title, Indexer = "Generic", PublishDate = DateTime.UtcNow };

            var result = maker.GetSearchDecision(new List<ReleaseInfo> { report }, new BookSearchCriteria
            {
                Author = author,
                Books = books,
                AuthorCatalog = books,
                InteractiveSearch = true
            }).Single();

            Assert.Multiple(() =>
            {
                Assert.That(result.Approved, Is.True);
                Assert.That(result.RemoteBook.DownloadAllowed, Is.True);
                Assert.That(result.RemoteBook.ParsedBookInfo.AuthorName, Is.EqualTo("TWAIN, MARK"));
                Assert.That(result.RemoteBook.ParsedBookInfo.BookTitle, Is.EqualTo("THE ADVENTURES OF TOM SAWYER"));
            });
            parsing.Verify(p => p.Map(It.IsAny<ParsedBookInfo>(), It.IsAny<SearchCriteriaBase>()), Times.AtLeastOnce);
        }

        [Test]
        public void migrated_matching_should_still_reject_an_unidentified_release()
        {
            var config = new Mock<IConfigService>();
            config.SetupGet(c => c.BookshelfMatchCompatibility).Returns(true);
            var author = new Author { Name = "Mark Twain" };
            var book = new Book { Title = "The Adventures of Tom Sawyer", Author = author };
            var books = new List<Book> { book };
            var parsing = new Mock<IParsingService>();
            parsing.Setup(p => p.Map(It.IsAny<ParsedBookInfo>(), It.IsAny<SearchCriteriaBase>()))
                .Returns((ParsedBookInfo info, SearchCriteriaBase criteria) => new RemoteBook { ParsedBookInfo = info, Books = new List<Book>() });
            var maker = new DownloadDecisionMaker(
                new List<IDecisionEngineSpecification>(), parsing.Object, Mock.Of<IEditionFtsRepository>(),
                Mock.Of<ICustomFormatCalculationService>(), Mock.Of<IRemoteBookAggregationService>(),
                Mock.Of<IReleaseNarratorMetadataEnricher>(), config.Object, LogManager.GetCurrentClassLogger());

            var result = maker.GetSearchDecision(new List<ReleaseInfo> { new() { Title = "Unknown Writer - An Unrelated Book EPUB" } },
                new BookSearchCriteria { Author = author, Books = books, InteractiveSearch = true }).Single();

            Assert.That(result.Approved, Is.False);
            Assert.That(result.RemoteBook.DownloadAllowed, Is.False);
        }

        [TestCase(true, true)]
        [TestCase(false, false)]
        public void migrated_identified_releases_should_keep_source_matching_instead_of_the_native_subtitle_filter(bool compatibility, bool accepted)
        {
            var config = new Mock<IConfigService>();
            config.SetupGet(c => c.BookshelfMatchCompatibility).Returns(compatibility);
            var author = new Author { Name = "Mark Twain" };
            var book = new Book
            {
                Title = "The Adventures of Tom Sawyer",
                Author = author,
                Editions = new List<Edition>
                {
                    new() { Title = "The Adventures of Tom Sawyer: A New Annotated Edition", Subtitle = "A New Annotated Edition", Monitored = true }
                }
            };
            var books = new List<Book> { book };
            var subject = new RemoteBook
            {
                Author = author,
                Books = books,
                Release = new ReleaseInfo { Title = "Mark Twain - The Adventures of Tom Sawyer EPUB" },
                ParsedBookInfo = new ParsedBookInfo { AuthorName = author.Name, BookTitle = book.Title }
            };
            var specification = new ReleaseTitleMatchSpecification(LogManager.GetCurrentClassLogger(), config.Object);

            Assert.That(specification.IsSatisfiedBy(subject, new BookSearchCriteria { Author = author, Books = books, InteractiveSearch = true }).Accepted,
                Is.EqualTo(accepted));
        }
    }
}
