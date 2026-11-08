using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
        [TestCase(BookMediaType.Audiobook, false)]
        [TestCase(BookMediaType.Audiobook, true)]
        [TestCase(BookMediaType.Ebook, false)]
        [TestCase(BookMediaType.Ebook, true)]
        public void native_mam_category_filters_reports_before_deciding(BookMediaType media, bool interactive)
        {
            var author = new Author { Id = 10, Name = "Catalog Author", AudiobookMonitored = true, EbookMonitored = true,
                AudiobookQualityProfileId = 1, EbookQualityProfileId = 2 };
            var book = BuildMonitoredBook(101, media, author);
            var indexer = DispatchProxy.Create<IIndexer, IndexerProxy>();
            ((IndexerProxy)(object)indexer).Reports = new List<ReleaseInfo>
            {
                Chaptarr.Core.Test.Indexers.MyAnonaMouseCategoryFixture.Report(13, "Catalog Author - Book", "unknown"),
                Chaptarr.Core.Test.Indexers.MyAnonaMouseCategoryFixture.Report(14, "Catalog Author - Book [Audiobook]", "unknown")
            };
            foreach (var report in ((IndexerProxy)(object)indexer).Reports) report.Indexer = "MyAnonaMouse";
            var factory = DispatchProxy.Create<IIndexerFactory, IndexerFactoryProxy>();
            ((IndexerFactoryProxy)(object)factory).Indexers.Add(indexer);
            var decisions = DispatchProxy.Create<IMakeDownloadDecision, DecisionMakerProxy>();
            var subject = new ReleaseSearchService(factory, DispatchProxy.Create<IBookService, BookServiceProxy>(),
                BuildAuthorService(author), null, decisions, LogManager.GetCurrentClassLogger());

            subject.BookSearch(book, new List<Book> { book }, false, true, interactive).GetAwaiter().GetResult();

            Assert.That(((DecisionMakerProxy)(object)decisions).ReportTitles,
                Is.EqualTo(new[] { media == BookMediaType.Audiobook ? "Catalog Author - Book" : "Catalog Author - Book [Audiobook]" }));
        }

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
                },
                new()
                {
                    Id = 103,
                    Title = "Other Media Book",
                    Author = author,
                    MediaType = BookMediaType.Ebook
                }
            };

            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var authorService = BuildAuthorService(author);
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
            Assert.That(((AuthorServiceProxy)(object)authorService).AuthorLoads, Is.EqualTo(1));
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
            var subject = BuildSubject(decisionMaker, author.Books);

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
            var subject = BuildSubject(decisionMaker, author.Books);

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
            var subject = BuildSubject(decisionMaker, author.Books);

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
            var subject = BuildSubject(decisionMaker, author.Books);

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
            var subject = BuildSubject(decisionMaker, author.Books);

            subject.AuthorSearch(author, false, true, false).GetAwaiter().GetResult();

            Assert.That(((DecisionMakerProxy)(object)decisionMaker).SearchBookIds, Is.EqualTo(new[] { 102 }));
        }

        [TestCase(false, null, new[] { 1, 2, 3, 4, 5 })]
        [TestCase(true, null, new[] { 1, 2, 3, 4, 5 })]
        [TestCase(true, BookMediaType.Audiobook, new[] { 1, 2 })]
        [TestCase(true, BookMediaType.Ebook, new[] { 3, 4, 5 })]
        public void author_search_should_dispatch_and_record_each_book_with_its_own_media_catalog(bool interactiveSearch, BookMediaType? mediaType, int[] expectedIds)
        {
            var author = new Author
            {
                Id = 10, Name = "Frank Herbert", AudiobookMonitored = true, EbookMonitored = true,
                AudiobookQualityProfileId = 1, EbookQualityProfileId = 2
            };
            author.Books = Enumerable.Range(1, 5)
                .Select(id => BuildMonitoredBook(id, id <= 2 ? BookMediaType.Audiobook : BookMediaType.Ebook, author))
                .ToList();
            foreach (var book in author.Books)
            {
                book.Title = "Dune";
                book.Editions = new List<Edition> { new() { Title = "Dune", Monitored = true } };
            }

            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)(object)bookService).Catalog = author.Books;
            var decisionMaker = DispatchProxy.Create<IMakeDownloadDecision, DecisionMakerProxy>();
            var indexer = DispatchProxy.Create<IIndexer, IndexerProxy>();
            var indexerFactory = DispatchProxy.Create<IIndexerFactory, IndexerFactoryProxy>();
            ((IndexerFactoryProxy)(object)indexerFactory).Indexers.Add(indexer);
            var subject = new ReleaseSearchService(indexerFactory, bookService,
                BuildAuthorService(author), null, decisionMaker,
                LogManager.GetCurrentClassLogger());

            subject.AuthorSearch(author, false, true, interactiveSearch, mediaType).GetAwaiter().GetResult();

            var criteria = ((DecisionMakerProxy)(object)decisionMaker).Criteria;
            Assert.Multiple(() =>
            {
                Assert.That(criteria.Count, Is.EqualTo(expectedIds.Length));
                Assert.That(criteria, Is.All.InstanceOf<BookSearchCriteria>());
                Assert.That(criteria.Select(c => c.Books.Count), Is.All.EqualTo(1));
                Assert.That(criteria.Select(c => c.Books.Single().Id), Is.EqualTo(expectedIds));
                Assert.That(criteria.All(c => c.AuthorCatalog.All(b => b.MediaType == c.Books.Single().MediaType)), Is.True);
                Assert.That(criteria.Select(c => c.AuthorCatalog.Count), Is.EqualTo(expectedIds.Select(id => id <= 2 ? 2 : 3)));
                Assert.That(((IndexerProxy)(object)indexer).Searches, Is.EqualTo(criteria));
                Assert.That(((DecisionMakerProxy)(object)decisionMaker).ReportTitles,
                    Is.EqualTo(expectedIds.Select(id => id <= 2 ? "Frank Herbert - Dune MP3" : "Frank Herbert - Dune EPUB")));
                Assert.That(((BookServiceProxy)(object)bookService).SearchTimeUpdates,
                    Is.EqualTo(expectedIds.Select(id => id.ToString())));
                Assert.That(author.Books.Where(book => book.LastSearchTime.HasValue).Select(book => book.Id), Is.EqualTo(expectedIds));
                Assert.That(((BookServiceProxy)(object)bookService).CatalogLoads, Is.EqualTo(1));
            });
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

        private static ReleaseSearchService BuildSubject(IMakeDownloadDecision decisionMaker, List<Book> catalog)
        {
            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)(object)bookService).Catalog = catalog;
            return new ReleaseSearchService(
                DispatchProxy.Create<IIndexerFactory, IndexerFactoryProxy>(),
                bookService,
                BuildAuthorService(catalog.First().Author),
                null,
                decisionMaker,
                LogManager.GetLogger(nameof(ReleaseSearchServiceCatalogReuseFixture)));
        }

        private class BookServiceProxy : DispatchProxy
        {
            public List<Book> Catalog { get; set; }
            public int CatalogLoads { get; private set; }
            public List<string> SearchTimeUpdates { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IBookService.UpdateLastSearchTime))
                {
                    SearchTimeUpdates.Add(string.Join(",", ((List<Book>)args[0]).Select(book => book.Id)));
                    return null;
                }

                if (targetMethod.Name == nameof(IBookService.GetBooksByAuthor))
                {
                    CatalogLoads++;
                    return Catalog ?? throw new AssertionException("The provided author catalog should have been reused");
                }

                throw new NotImplementedException(targetMethod.Name);
            }
        }

        private static IAuthorService BuildAuthorService(Author author)
        {
            var service = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)(object)service).Author = author;
            return service;
        }

        private class AuthorServiceProxy : DispatchProxy
        {
            public int AuthorLoads { get; private set; }
            public Author Author { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IAuthorService.GetAuthor))
                {
                    AuthorLoads++;
                    return Author;
                }

                throw new NotImplementedException(targetMethod.Name);
            }
        }

        private class IndexerFactoryProxy : DispatchProxy
        {
            public List<IIndexer> Indexers { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IIndexerFactory.AutomaticSearchEnabled) ||
                    targetMethod.Name == nameof(IIndexerFactory.InteractiveSearchEnabled))
                {
                    return Indexers;
                }

                throw new NotImplementedException(targetMethod.Name);
            }
        }

        private class IndexerProxy : DispatchProxy
        {
            public List<SearchCriteriaBase> Searches { get; } = new();
            public List<ReleaseInfo> Reports { get; set; }

            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (method.Name == "get_Definition")
                {
                    return new IndexerDefinition { Name = "Offline", Tags = new HashSet<int>() };
                }

                if (method.Name == nameof(IIndexer.Fetch))
                {
                    Searches.Add((SearchCriteriaBase)args[0]);
                    return Task.FromResult<IList<ReleaseInfo>>(Reports ?? new List<ReleaseInfo>
                    {
                        new() { Guid = "audio", Title = "Frank Herbert - Dune MP3", Indexer = "Offline" },
                        new() { Guid = "ebook", Title = "Frank Herbert - Dune EPUB", Indexer = "Offline" }
                    });
                }

                throw new NotImplementedException(method.Name);
            }
        }

        private class DecisionMakerProxy : DispatchProxy
        {
            public List<SearchCriteriaBase> Criteria { get; } = new();
            public List<string> ReportTitles { get; } = new();
            public int AuthorCatalogCount { get; private set; }
            public List<int> SearchBookIds { get; set; } = new();
            public bool MonitoredBooksOnly { get; private set; } = true;

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IMakeDownloadDecision.GetSearchDecision))
                {
                    var criteria = (SearchCriteriaBase)args[1];
                    Criteria.Add(criteria);
                    ReportTitles.AddRange(((IEnumerable<ReleaseInfo>)args[0]).Select(report => report.Title));
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
