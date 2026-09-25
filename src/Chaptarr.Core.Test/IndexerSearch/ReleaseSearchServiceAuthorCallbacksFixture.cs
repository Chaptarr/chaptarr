using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
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
    public class ReleaseSearchServiceAuthorCallbacksFixture
    {
        [Test]
        public async Task author_search_should_publish_ordered_roster_and_book_scoped_results()
        {
            var author = BuildAuthor();
            var first = BuildBook(101, BookMediaType.Audiobook, author, new DateTime(2026, 1, 1));
            var empty = BuildBook(102, BookMediaType.Audiobook, author, new DateTime(2026, 1, 2));
            var third = BuildBook(103, BookMediaType.Audiobook, author, new DateTime(2026, 1, 3));
            var otherMedia = BuildBook(104, BookMediaType.Ebook, author, new DateTime(2025, 1, 1));
            var harness = BuildHarness(new List<Book> { empty, otherMedia, third, first }, author);
            var fetchedBookIds = new List<int>();
            harness.Indexer.FetchHandler = criteria =>
            {
                var bookId = criteria.Books.Single().Id;
                fetchedBookIds.Add(bookId);
                return Task.FromResult<IList<ReleaseInfo>>(bookId == empty.Id
                    ? new List<ReleaseInfo>()
                    : new List<ReleaseInfo> { BuildRelease("shared-guid"), BuildRelease("shared-guid") });
            };

            IReadOnlyList<Book> roster = null;
            var completed = new List<(int BookId, List<DownloadDecision> Decisions, Exception Exception)>();
            await harness.Subject.AuthorSearch(
                author.Id,
                false,
                true,
                false,
                BookMediaType.Audiobook,
                books =>
                {
                    roster = books;
                    return Task.CompletedTask;
                },
                (book, decisions, exception) =>
                {
                    completed.Add((book.Id, decisions, exception));
                    return Task.CompletedTask;
                });

            Assert.Multiple(() =>
            {
                Assert.That(roster.Select(book => book.Id), Is.EqualTo(new[] { first.Id, empty.Id, third.Id }));
                Assert.That(completed.Select(item => item.BookId), Is.EqualTo(new[] { first.Id, empty.Id, third.Id }));
                Assert.That(completed[0].Decisions, Has.Count.EqualTo(1));
                Assert.That(completed[1].Decisions, Is.Empty);
                Assert.That(completed[2].Decisions, Has.Count.EqualTo(1));
                Assert.That(completed.All(item => item.Exception == null), Is.True);
                Assert.That(completed[0].Decisions.Single().RemoteBook.Release.Guid, Is.EqualTo("shared-guid"));
                Assert.That(completed[2].Decisions.Single().RemoteBook.Release.Guid, Is.EqualTo("shared-guid"));
                Assert.That(fetchedBookIds, Is.EqualTo(new[] { first.Id, empty.Id, third.Id }));
                Assert.That(harness.BookService.CatalogLoads, Is.EqualTo(1));
                Assert.That(harness.AuthorService.AuthorLoads, Is.EqualTo(1));
                Assert.That(harness.Indexer.Searches.Select(criteria => criteria.AuthorCatalog.Count), Is.All.EqualTo(3));
                Assert.That(harness.Indexer.Searches.All(criteria => criteria.AuthorCatalog.All(book => book.MediaType == BookMediaType.Audiobook)), Is.True);
            });
        }

        [Test]
        public async Task legacy_author_search_should_keep_its_flat_deduped_result()
        {
            var author = BuildAuthor();
            var first = BuildBook(151, BookMediaType.Audiobook, author, DateTime.MinValue);
            var second = BuildBook(152, BookMediaType.Audiobook, author, DateTime.MinValue.AddDays(1));
            var harness = BuildHarness(new List<Book> { first, second }, author);
            harness.Indexer.FetchHandler = _ => Task.FromResult<IList<ReleaseInfo>>(new List<ReleaseInfo>
            {
                BuildRelease("shared-guid")
            });

            var result = await harness.Subject.AuthorSearch(author.Id, false, true, false, BookMediaType.Audiobook, CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(result, Has.Count.EqualTo(1));
                Assert.That(result.Single().RemoteBook.Release.Guid, Is.EqualTo("shared-guid"));
                Assert.That(harness.Indexer.Searches.Select(criteria => criteria.Books.Single().Id), Is.EqualTo(new[] { first.Id, second.Id }));
                Assert.That(harness.AuthorService.AuthorLoads, Is.EqualTo(1));
                Assert.That(harness.BookService.CatalogLoads, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task author_search_should_publish_first_book_before_second_book_finishes()
        {
            var author = BuildAuthor();
            var first = BuildBook(201, BookMediaType.Audiobook, author, DateTime.MinValue);
            var second = BuildBook(202, BookMediaType.Audiobook, author, DateTime.MinValue.AddDays(1));
            var harness = BuildHarness(new List<Book> { first, second }, author);
            var firstCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseSecond = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var completedBookIds = new List<int>();
            harness.Indexer.FetchHandler = async criteria =>
            {
                var bookId = criteria.Books.Single().Id;
                if (bookId == second.Id)
                {
                    secondStarted.TrySetResult(true);
                    await releaseSecond.Task;
                }

                return new List<ReleaseInfo> { BuildRelease($"guid-{bookId}") };
            };

            var search = harness.Subject.AuthorSearch(
                author,
                false,
                true,
                false,
                null,
                null,
                (book, decisions, exception) =>
                {
                    completedBookIds.Add(book.Id);
                    if (book.Id == first.Id)
                    {
                        firstCompleted.TrySetResult(true);
                    }

                    return Task.CompletedTask;
                });

            try
            {
                await firstCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Multiple(() =>
                {
                    Assert.That(completedBookIds, Is.EqualTo(new[] { first.Id }));
                    Assert.That(search.IsCompleted, Is.False);
                });
            }
            finally
            {
                releaseSecond.TrySetResult(true);
            }

            await search;
            Assert.That(completedBookIds, Is.EqualTo(new[] { first.Id, second.Id }));
        }

        [Test]
        public void author_search_should_stop_at_the_next_book_when_cancelled_after_a_completion()
        {
            var author = BuildAuthor();
            var first = BuildBook(301, BookMediaType.Audiobook, author, DateTime.MinValue);
            var second = BuildBook(302, BookMediaType.Audiobook, author, DateTime.MinValue.AddDays(1));
            var harness = BuildHarness(new List<Book> { first, second }, author);
            using var cancellation = new CancellationTokenSource();
            var fetchedBookIds = new List<int>();
            var completedBookIds = new List<int>();
            harness.Indexer.FetchHandler = criteria =>
            {
                var bookId = criteria.Books.Single().Id;
                fetchedBookIds.Add(bookId);
                return Task.FromResult<IList<ReleaseInfo>>(new List<ReleaseInfo> { BuildRelease($"guid-{bookId}") });
            };

            var search = harness.Subject.AuthorSearch(
                author,
                false,
                true,
                false,
                null,
                null,
                (book, decisions, exception) =>
                {
                    completedBookIds.Add(book.Id);
                    cancellation.Cancel();
                    return Task.CompletedTask;
                },
                cancellation.Token);

            Assert.ThrowsAsync<OperationCanceledException>(async () => await search);
            Assert.Multiple(() =>
            {
                Assert.That(fetchedBookIds, Is.EqualTo(new[] { first.Id }));
                Assert.That(completedBookIds, Is.EqualTo(new[] { first.Id }));
            });
        }

        [Test]
        public async Task author_search_should_report_a_book_failure_and_continue_to_the_next_book()
        {
            var author = BuildAuthor();
            var first = BuildBook(401, BookMediaType.Audiobook, author, DateTime.MinValue);
            var second = BuildBook(402, BookMediaType.Audiobook, author, DateTime.MinValue.AddDays(1));
            var harness = BuildHarness(new List<Book> { first, second }, author);
            harness.Indexer.FetchHandler = _ => Task.FromResult<IList<ReleaseInfo>>(new List<ReleaseInfo> { BuildRelease("guid") });
            harness.DecisionMaker.DecisionHandler = (reports, criteria) =>
            {
                if (criteria.Books.Single().Id == first.Id)
                {
                    throw new InvalidOperationException("first book decision failed");
                }

                return BuildDecisions(reports);
            };
            var completed = new List<(int BookId, List<DownloadDecision> Decisions, Exception Exception)>();

            await harness.Subject.AuthorSearch(
                author,
                false,
                true,
                false,
                null,
                null,
                (book, decisions, exception) =>
                {
                    completed.Add((book.Id, decisions, exception));
                    return Task.CompletedTask;
                });

            Assert.Multiple(() =>
            {
                Assert.That(completed.Select(item => item.BookId), Is.EqualTo(new[] { first.Id, second.Id }));
                Assert.That(completed[0].Decisions, Is.Empty);
                Assert.That(completed[0].Exception, Is.TypeOf<InvalidOperationException>());
                Assert.That(completed[1].Decisions, Has.Count.EqualTo(1));
                Assert.That(completed[1].Exception, Is.Null);
            });
        }

        [Test]
        public async Task author_search_should_report_completion_callback_failure_and_continue_to_the_next_book()
        {
            var author = BuildAuthor();
            var first = BuildBook(501, BookMediaType.Audiobook, author, DateTime.MinValue);
            var second = BuildBook(502, BookMediaType.Audiobook, author, DateTime.MinValue.AddDays(1));
            var harness = BuildHarness(new List<Book> { first, second }, author);
            var fetchedBookIds = new List<int>();
            var callbackFailure = new InvalidOperationException("completion callback failed");
            harness.Indexer.FetchHandler = criteria =>
            {
                var bookId = criteria.Books.Single().Id;
                fetchedBookIds.Add(bookId);
                return Task.FromResult<IList<ReleaseInfo>>(new List<ReleaseInfo> { BuildRelease($"guid-{bookId}") });
            };
            var callbackResults = new List<(int BookId, int DecisionCount, Exception Exception)>();

            await harness.Subject.AuthorSearch(
                author,
                false,
                true,
                false,
                null,
                null,
                (book, decisions, exception) =>
                {
                    callbackResults.Add((book.Id, decisions.Count, exception));
                    if (book.Id == first.Id && exception == null)
                    {
                        return Task.FromException(callbackFailure);
                    }

                    return Task.CompletedTask;
                });

            Assert.Multiple(() =>
            {
                Assert.That(fetchedBookIds, Is.EqualTo(new[] { first.Id, second.Id }));
                Assert.That(callbackResults.Select(result => result.BookId), Is.EqualTo(new[] { first.Id, first.Id, second.Id }));
                Assert.That(callbackResults[0].DecisionCount, Is.EqualTo(1));
                Assert.That(callbackResults[0].Exception, Is.Null);
                Assert.That(callbackResults[1].DecisionCount, Is.Zero);
                Assert.That(callbackResults[1].Exception, Is.SameAs(callbackFailure));
                Assert.That(callbackResults[2].DecisionCount, Is.EqualTo(1));
                Assert.That(callbackResults[2].Exception, Is.Null);
            });
        }

        [Test]
        public void author_search_should_stop_when_the_error_result_callback_also_fails()
        {
            var author = BuildAuthor();
            var first = BuildBook(511, BookMediaType.Audiobook, author, DateTime.MinValue);
            var second = BuildBook(512, BookMediaType.Audiobook, author, DateTime.MinValue.AddDays(1));
            var harness = BuildHarness(new List<Book> { first, second }, author);
            var fetchedBookIds = new List<int>();
            harness.Indexer.FetchHandler = criteria =>
            {
                fetchedBookIds.Add(criteria.Books.Single().Id);
                return Task.FromResult<IList<ReleaseInfo>>(new List<ReleaseInfo> { BuildRelease("guid") });
            };
            var callbackFailure = new InvalidOperationException("completion callback failed");
            var errorCallbackFailure = new InvalidOperationException("failed result callback failed");
            var callbackResults = new List<(int BookId, int DecisionCount, Exception Exception)>();

            var search = harness.Subject.AuthorSearch(
                author,
                false,
                true,
                false,
                null,
                null,
                (book, decisions, exception) =>
                {
                    callbackResults.Add((book.Id, decisions.Count, exception));
                    return Task.FromException(exception == null ? callbackFailure : errorCallbackFailure);
                });

            var thrown = Assert.ThrowsAsync<InvalidOperationException>(async () => await search);
            Assert.Multiple(() =>
            {
                Assert.That(thrown, Is.SameAs(errorCallbackFailure));
                Assert.That(fetchedBookIds, Is.EqualTo(new[] { first.Id }));
                Assert.That(callbackResults.Select(result => result.BookId), Is.EqualTo(new[] { first.Id, first.Id }));
                Assert.That(callbackResults[0].DecisionCount, Is.EqualTo(1));
                Assert.That(callbackResults[0].Exception, Is.Null);
                Assert.That(callbackResults[1].DecisionCount, Is.Zero);
                Assert.That(callbackResults[1].Exception, Is.SameAs(callbackFailure));
            });
        }

        private static Author BuildAuthor()
        {
            return new Author
            {
                Id = 10,
                Name = "Callback Author",
                AudiobookMonitored = true,
                EbookMonitored = true,
                AudiobookQualityProfileId = 1,
                EbookQualityProfileId = 2
            };
        }

        private static Book BuildBook(int id, BookMediaType mediaType, Author author, DateTime lastSearchTime)
        {
            return new Book
            {
                Id = id,
                Title = $"Book {id}",
                Author = author,
                AuthorId = author.Id,
                MediaType = mediaType,
                AudiobookMonitored = mediaType == BookMediaType.Audiobook,
                EbookMonitored = mediaType == BookMediaType.Ebook,
                LastSearchTime = lastSearchTime
            };
        }

        private static ReleaseInfo BuildRelease(string guid)
        {
            return new ReleaseInfo { Guid = guid, Title = "Callback Author - Book", Indexer = "Offline" };
        }

        private static List<DownloadDecision> BuildDecisions(IEnumerable<ReleaseInfo> reports)
        {
            return reports.Select(report => new DownloadDecision(new RemoteBook { Release = report })).ToList();
        }

        private static TestHarness BuildHarness(List<Book> catalog, Author author)
        {
            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            var decisionMaker = DispatchProxy.Create<IMakeDownloadDecision, DecisionMakerProxy>();
            var indexerFactory = DispatchProxy.Create<IIndexerFactory, IndexerFactoryProxy>();
            var indexer = DispatchProxy.Create<IIndexer, IndexerProxy>();
            ((BookServiceProxy)(object)bookService).Catalog = catalog;
            ((AuthorServiceProxy)(object)authorService).Author = author;
            ((IndexerFactoryProxy)(object)indexerFactory).Indexers.Add(indexer);
            var subject = new ReleaseSearchService(indexerFactory, bookService, authorService, null, decisionMaker,
                LogManager.GetLogger(nameof(ReleaseSearchServiceAuthorCallbacksFixture)));

            return new TestHarness
            {
                Subject = subject,
                BookService = (BookServiceProxy)(object)bookService,
                AuthorService = (AuthorServiceProxy)(object)authorService,
                DecisionMaker = (DecisionMakerProxy)(object)decisionMaker,
                Indexer = (IndexerProxy)(object)indexer
            };
        }

        private sealed class TestHarness
        {
            public ReleaseSearchService Subject { get; init; }
            public BookServiceProxy BookService { get; init; }
            public AuthorServiceProxy AuthorService { get; init; }
            public DecisionMakerProxy DecisionMaker { get; init; }
            public IndexerProxy Indexer { get; init; }
        }

        private class BookServiceProxy : DispatchProxy
        {
            public List<Book> Catalog { get; set; }
            public int CatalogLoads { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IBookService.GetBooksByAuthor))
                {
                    CatalogLoads++;
                    return Catalog;
                }

                if (targetMethod.Name == nameof(IBookService.UpdateLastSearchTime))
                {
                    return null;
                }

                throw new NotImplementedException(targetMethod.Name);
            }
        }

        private class AuthorServiceProxy : DispatchProxy
        {
            public Author Author { get; set; }
            public int AuthorLoads { get; private set; }

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
            public Func<SearchCriteriaBase, Task<IList<ReleaseInfo>>> FetchHandler { get; set; }
            public List<SearchCriteriaBase> Searches { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == "get_Definition")
                {
                    return new IndexerDefinition { Name = "Offline", Tags = new HashSet<int>() };
                }

                if (targetMethod.Name == nameof(IIndexer.Fetch))
                {
                    var criteria = (SearchCriteriaBase)args[0];
                    Searches.Add(criteria);
                    return FetchHandler(criteria);
                }

                throw new NotImplementedException(targetMethod.Name);
            }
        }

        private class DecisionMakerProxy : DispatchProxy
        {
            public Func<IEnumerable<ReleaseInfo>, SearchCriteriaBase, List<DownloadDecision>> DecisionHandler { get; set; } = (reports, _) => BuildDecisions(reports);

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IMakeDownloadDecision.GetSearchDecision))
                {
                    return DecisionHandler((IEnumerable<ReleaseInfo>)args[0], (SearchCriteriaBase)args[1]);
                }

                throw new NotImplementedException(targetMethod.Name);
            }
        }
    }
}
