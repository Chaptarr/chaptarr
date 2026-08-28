using System;
using System.Collections.Generic;
using System.Reflection;
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
    public class ReleaseSearchServiceCatalogReuseFixture
    {
        [Test]
        public void book_search_should_reuse_the_provided_author_catalog()
        {
            var author = new Author
            {
                Id = 10,
                Name = "Catalog Author",
                AudiobookMonitored = true,
                AudiobookQualityProfileId = 1
            };
            var book = new Book
            {
                Id = 101,
                Title = "Target Book",
                Author = author,
                MediaType = BookMediaType.Audiobook,
                AudiobookMonitored = true
            };
            var authorCatalog = new List<Book>
            {
                book,
                new()
                {
                    Id = 102,
                    Title = "Sibling Book",
                    Author = author,
                    MediaType = BookMediaType.Audiobook
                }
            };

            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            var decisionMaker = DispatchProxy.Create<IMakeDownloadDecision, DecisionMakerProxy>();
            var subject = new ReleaseSearchService(
                DispatchProxy.Create<IIndexerFactory, IndexerFactoryProxy>(),
                bookService,
                authorService,
                null,
                decisionMaker,
                LogManager.GetLogger("ReleaseSearchServiceCatalogReuseFixture"));

            subject.BookSearch(book, authorCatalog, false, true, false).GetAwaiter().GetResult();

            Assert.That(((BookServiceProxy)(object)bookService).CatalogLoads, Is.Zero);
            Assert.That(((AuthorServiceProxy)(object)authorService).AuthorLoads, Is.Zero);
            Assert.That(((DecisionMakerProxy)(object)decisionMaker).AuthorCatalogCount, Is.EqualTo(2));
            Assert.That(author.Books, Is.Null);
        }

        [Test]
        public void automatic_author_search_should_respect_each_media_side_author_gate()
        {
            var author = new Author
            {
                Id = 10,
                Name = "Catalog Author",
                AudiobookMonitored = false,
                EbookMonitored = true,
                AudiobookQualityProfileId = 1,
                EbookQualityProfileId = 2
            };
            author.Books = new List<Book>
            {
                BuildMonitoredBook(101, BookMediaType.Audiobook, author),
                BuildMonitoredBook(102, BookMediaType.Ebook, author)
            };
            var decisionMaker = DispatchProxy.Create<IMakeDownloadDecision, DecisionMakerProxy>();
            var subject = BuildSubject(decisionMaker);

            subject.AuthorSearch(author, false, false, false).GetAwaiter().GetResult();

            Assert.That(((DecisionMakerProxy)(object)decisionMaker).SearchBookIds, Is.EqualTo(new[] { 102 }));
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void book_search_should_only_bypass_the_author_gate_when_explicitly_allowed(
            bool userInvokedSearch,
            bool interactiveSearch)
        {
            var author = new Author
            {
                Id = 10,
                Name = "Catalog Author",
                AudiobookMonitored = false,
                AudiobookQualityProfileId = 1
            };
            var book = BuildMonitoredBook(101, BookMediaType.Audiobook, author);
            author.Books = new List<Book> { book };
            var decisionMaker = DispatchProxy.Create<IMakeDownloadDecision, DecisionMakerProxy>();
            var subject = BuildSubject(decisionMaker);

            subject.BookSearch(book, false, userInvokedSearch, interactiveSearch).GetAwaiter().GetResult();

            var expected = interactiveSearch ? new[] { 101 } : Array.Empty<int>();
            Assert.That(((DecisionMakerProxy)(object)decisionMaker).SearchBookIds, Is.EqualTo(expected));
        }

        [Test]
        public void manual_exact_book_search_can_bypass_book_and_author_monitoring_without_changing_them()
        {
            var author = new Author
            {
                Id = 10,
                Name = "Catalog Author",
                AudiobookMonitored = false,
                AudiobookQualityProfileId = 1
            };
            var book = BuildMonitoredBook(101, BookMediaType.Audiobook, author);
            book.AudiobookMonitored = false;
            author.Books = new List<Book> { book };
            var decisionMaker = DispatchProxy.Create<IMakeDownloadDecision, DecisionMakerProxy>();
            var subject = BuildSubject(decisionMaker);

            subject.BookSearch(book, false, true, false, allowUnmonitored: true).GetAwaiter().GetResult();

            Assert.Multiple(() =>
            {
                Assert.That(((DecisionMakerProxy)(object)decisionMaker).SearchBookIds, Is.EqualTo(new[] { 101 }));
                Assert.That(((DecisionMakerProxy)(object)decisionMaker).MonitoredBooksOnly, Is.False);
                Assert.That(author.AudiobookMonitored, Is.False);
                Assert.That(book.AudiobookMonitored, Is.False);
            });
        }

        [Test]
        public void book_search_should_require_the_book_row_and_author_media_side()
        {
            var author = new Author
            {
                Id = 10,
                Name = "Catalog Author",
                AudiobookMonitored = true,
                AudiobookQualityProfileId = 1
            };
            var book = BuildMonitoredBook(101, BookMediaType.Audiobook, author);
            author.Books = new List<Book> { book };
            var decisionMaker = DispatchProxy.Create<IMakeDownloadDecision, DecisionMakerProxy>();
            var subject = BuildSubject(decisionMaker);

            subject.BookSearch(book, false, false, false).GetAwaiter().GetResult();
            Assert.That(((DecisionMakerProxy)(object)decisionMaker).SearchBookIds, Is.EqualTo(new[] { 101 }));

            book.AudiobookMonitored = false;
            ((DecisionMakerProxy)(object)decisionMaker).SearchBookIds = new List<int>();
            subject.BookSearch(book, false, false, false).GetAwaiter().GetResult();

            Assert.That(((DecisionMakerProxy)(object)decisionMaker).SearchBookIds, Is.Empty);
        }

        [Test]
        public void manual_author_search_should_respect_each_media_side_author_gate()
        {
            var author = new Author
            {
                Id = 10,
                Name = "Catalog Author",
                AudiobookMonitored = false,
                EbookMonitored = true,
                AudiobookQualityProfileId = 1,
                EbookQualityProfileId = 2
            };
            author.Books = new List<Book>
            {
                BuildMonitoredBook(101, BookMediaType.Audiobook, author),
                BuildMonitoredBook(102, BookMediaType.Ebook, author)
            };
            var decisionMaker = DispatchProxy.Create<IMakeDownloadDecision, DecisionMakerProxy>();
            var subject = BuildSubject(decisionMaker);

            subject.AuthorSearch(author, false, true, false).GetAwaiter().GetResult();

            Assert.That(((DecisionMakerProxy)(object)decisionMaker).SearchBookIds, Is.EqualTo(new[] { 102 }));
        }

        private static Book BuildMonitoredBook(int id, BookMediaType mediaType, Author author)
        {
            return new Book
            {
                Id = id,
                Title = $"Book {id}",
                Author = author,
                AuthorId = author.Id,
                MediaType = mediaType,
                AudiobookMonitored = mediaType == BookMediaType.Audiobook,
                EbookMonitored = mediaType == BookMediaType.Ebook
            };
        }

        private static ReleaseSearchService BuildSubject(IMakeDownloadDecision decisionMaker)
        {
            return new ReleaseSearchService(
                DispatchProxy.Create<IIndexerFactory, IndexerFactoryProxy>(),
                DispatchProxy.Create<IBookService, BookServiceProxy>(),
                DispatchProxy.Create<IAuthorService, AuthorServiceProxy>(),
                null,
                decisionMaker,
                LogManager.GetLogger(nameof(ReleaseSearchServiceCatalogReuseFixture)));
        }

        private class BookServiceProxy : DispatchProxy
        {
            public int CatalogLoads { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IBookService.GetBooksByAuthor))
                {
                    CatalogLoads++;
                    throw new AssertionException("The provided author catalog should have been reused");
                }

                throw new NotImplementedException(targetMethod.Name);
            }
        }

        private class AuthorServiceProxy : DispatchProxy
        {
            public int AuthorLoads { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IAuthorService.GetAuthor))
                {
                    AuthorLoads++;
                    throw new AssertionException("The attached author should have been reused");
                }

                throw new NotImplementedException(targetMethod.Name);
            }
        }

        private class IndexerFactoryProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IIndexerFactory.AutomaticSearchEnabled) ||
                    targetMethod.Name == nameof(IIndexerFactory.InteractiveSearchEnabled))
                {
                    return new List<IIndexer>();
                }

                throw new NotImplementedException(targetMethod.Name);
            }
        }

        private class DecisionMakerProxy : DispatchProxy
        {
            public int AuthorCatalogCount { get; private set; }
            public List<int> SearchBookIds { get; set; } = new();
            public bool MonitoredBooksOnly { get; private set; } = true;

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IMakeDownloadDecision.GetSearchDecision))
                {
                    var criteria = (SearchCriteriaBase)args[1];
                    AuthorCatalogCount = criteria.AuthorCatalog?.Count ?? 0;
                    SearchBookIds = criteria.Books?.ConvertAll(book => book.Id) ?? new List<int>();
                    MonitoredBooksOnly = criteria.MonitoredBooksOnly;
                    return new List<DownloadDecision>();
                }

                throw new NotImplementedException(targetMethod.Name);
            }
        }
    }
}
