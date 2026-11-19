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
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Queue;

namespace Chaptarr.Core.Test.IndexerSearch
{
    [TestFixture]
    public partial class BookSearchServiceFixture
    {
        private static IBookService SelectedBooks()
        {
            var service = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var author = new Author { Id = 10, AudiobookQualityProfileId = 1 };
            ((BookServiceProxy)(object)service).AuthorBooks[10] = new List<Book>
            {
                new() { Id = 5792, AuthorId = 10, Author = author, MediaType = BookMediaType.Audiobook },
                new() { Id = 5793, AuthorId = 10, Author = author, MediaType = BookMediaType.Audiobook }
            };
            return service;
        }

        [Test]
        public void book_search_command_should_search_each_book_id_once()
        {
            var releaseSearch = new RecordingReleaseSearch();
            var subject = new BookSearchService(
                releaseSearch,
                SelectedBooks(),
                null,
                null,
                new EmptyDecisionProcessor(),
                LogManager.GetLogger("BookSearchServiceFixture"));

            subject.Execute(new BookSearchCommand(new List<int> { 5792, 5792, 0, 5792 })
            {
                Trigger = CommandTrigger.Manual
            });

            Assert.That(releaseSearch.BookSearchIds, Is.EqualTo(new List<int> { 5792 }));
            Assert.That(releaseSearch.AllowUnmonitoredValues, Is.EqualTo(new[] { true }));
        }

        [Test]
        public void automatic_exact_book_search_should_not_bypass_monitoring()
        {
            var releaseSearch = new RecordingReleaseSearch();
            var subject = new BookSearchService(
                releaseSearch,
                SelectedBooks(),
                null,
                null,
                new EmptyDecisionProcessor(),
                LogManager.GetLogger("BookSearchServiceFixture"));

            subject.Execute(new BookSearchCommand(new List<int> { 5792 })
            {
                Trigger = CommandTrigger.Unspecified
            });

            Assert.That(releaseSearch.AllowUnmonitoredValues, Is.EqualTo(new[] { false }));
        }

        [Test]
        public void manual_selected_books_search_should_bypass_monitoring_once()
        {
            var releaseSearch = new RecordingReleaseSearch();
            var subject = new BookSearchService(
                releaseSearch,
                SelectedBooks(),
                null,
                null,
                new EmptyDecisionProcessor(),
                LogManager.GetLogger("BookSearchServiceFixture"));

            subject.Execute(new BookSearchCommand(new List<int> { 5792, 5793 })
            {
                Trigger = CommandTrigger.Manual
            });

            Assert.That(releaseSearch.AllowUnmonitoredValues, Is.EqualTo(new[] { true, true }));
        }

        [Test]
        public void automatic_list_batch_respects_monitoring_and_delay_profiles()
        {
            var search = new RecordingReleaseSearch();
            var subject = new BookSearchService(search, SelectedBooks(), null, null, new EmptyDecisionProcessor(), LogManager.GetCurrentClassLogger());
            subject.Execute(new BookSearchCommand(new List<int> { 5792, 5793 }));
            Assert.That(search.BookSearchIds, Is.EqualTo(new[] { 5792, 5793 }));
            Assert.That(search.AllowUnmonitoredValues, Is.All.False);
            Assert.That(search.UserInvokedValues, Is.All.False);
        }

        [TestCase(CommandTrigger.Manual)]
        [TestCase(CommandTrigger.Unspecified)]
        public void author_command_should_search_and_process_each_eligible_book_sequentially(CommandTrigger trigger)
        {
            var author = new Author
            {
                Id = 10,
                AudiobookMonitored = true,
                EbookMonitored = true,
                AudiobookQualityProfileId = 1,
                EbookQualityProfileId = 2
            };
            var catalog = Enumerable.Range(1, 6).Select(id => new Book
            {
                Id = id,
                AuthorId = author.Id,
                Author = author,
                MediaType = id <= 2 ? BookMediaType.Audiobook : BookMediaType.Ebook,
                AudiobookMonitored = id <= 2,
                EbookMonitored = id > 2 && id <= 5
            }).ToList();
            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var bookProxy = (BookServiceProxy)(object)bookService;
            bookProxy.AuthorBooks[author.Id] = catalog;
            var operations = new List<string>();
            var releaseSearch = new RecordingReleaseSearch
            {
                OnBookSearch = book => operations.Add($"search:{book.Id}")
            };
            var subject = new BookSearchService(releaseSearch, bookService, null, null,
                new EmptyDecisionProcessor { OnProcess = () => operations.Add("process") },
                LogManager.GetCurrentClassLogger());

            Assert.That(subject, Is.InstanceOf<IExecute<AuthorSearchCommand>>());
            ((IExecute<AuthorSearchCommand>)subject).Execute(new AuthorSearchCommand
            {
                AuthorId = author.Id,
                Trigger = trigger
            });

            Assert.Multiple(() =>
            {
                Assert.That(bookProxy.AuthorCatalogRequests, Is.EqualTo(new[] { author.Id }));
                Assert.That(releaseSearch.BookSearchBooks.Select(book => book.Id), Is.EqualTo(new[] { 1, 2, 3, 4, 5 }));
                Assert.That(releaseSearch.AuthorCatalogSizes, Is.All.EqualTo(6));
                Assert.That(releaseSearch.AllowUnmonitoredValues, Is.All.False);
                Assert.That(operations, Is.EqualTo(new[]
                {
                    "search:1", "process", "search:2", "process", "search:3", "process",
                    "search:4", "process", "search:5", "process"
                }));
            });
        }

        [Test]
        public void missing_search_should_load_each_local_author_catalog_once()
        {
            var firstAuthor = new Author { Id = 10, AudiobookQualityProfileId = 1 };
            var secondAuthor = new Author { Id = 20, AudiobookQualityProfileId = 1 };
            var firstAuthorBooks = new List<Book>
            {
                new() { Id = 101, Title = "Later Search", Author = firstAuthor, MediaType = BookMediaType.Audiobook, LastSearchTime = DateTime.UtcNow },
                new() { Id = 102, Title = "Oldest Search", Author = firstAuthor, MediaType = BookMediaType.Audiobook },
                new() { Id = 103, Title = "Catalog Sibling", Author = firstAuthor, MediaType = BookMediaType.Audiobook }
            };
            var secondAuthorBooks = new List<Book>
            {
                new() { Id = 201, Title = "Second Author", Author = secondAuthor, MediaType = BookMediaType.Audiobook }
            };

            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var bookServiceProxy = (BookServiceProxy)(object)bookService;
            bookServiceProxy.SearchTargets = new List<BookSearchTarget>
            {
                new() { BookId = 101, AuthorId = 10 },
                new() { BookId = 201, AuthorId = 20 },
                new() { BookId = 102, AuthorId = 10 }
            };
            bookServiceProxy.AuthorBooks[10] = firstAuthorBooks;
            bookServiceProxy.AuthorBooks[20] = secondAuthorBooks;

            var releaseSearch = new RecordingReleaseSearch();
            var subject = new BookSearchService(
                releaseSearch,
                bookService,
                null,
                new EmptyQueueService(),
                new EmptyDecisionProcessor(),
                LogManager.GetLogger("BookSearchServiceFixture"));

            subject.Execute(new MissingBookSearchCommand { Trigger = CommandTrigger.Manual });

            Assert.That(bookServiceProxy.AuthorCatalogRequests, Is.EqualTo(new[] { 10, 20 }));
            Assert.That(releaseSearch.BookSearchBooks.Select(book => book.Id), Is.EqualTo(new[] { 102, 101, 201 }));
            Assert.That(releaseSearch.AuthorCatalogSizes, Is.EqualTo(new[] { 3, 3, 1 }));
            Assert.That(releaseSearch.AllowUnmonitoredValues, Is.All.EqualTo(false));
        }

        [Test]
        public void whole_library_missing_search_should_be_type_exclusive()
        {
            Assert.That(new MissingBookSearchCommand().IsTypeExclusive, Is.True);
            Assert.That(new MissingBookSearchCommand(10).IsTypeExclusive, Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void missing_search_should_revalidate_targets_and_queue_before_loading_an_author(bool cleanupWarning)
        {
            var author = new Author { Id = 10, AudiobookQualityProfileId = 1 };
            var books = new List<Book>
            {
                new() { Id = 101, Title = "No Longer Missing", Author = author, MediaType = BookMediaType.Audiobook },
                new() { Id = 102, Title = "Still Missing", Author = author, MediaType = BookMediaType.Audiobook },
                new() { Id = 103, Title = "Already Queued", Author = author, MediaType = BookMediaType.Audiobook }
            };

            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var bookServiceProxy = (BookServiceProxy)(object)bookService;
            bookServiceProxy.AuthorBooks[10] = books;
            bookServiceProxy.MissingTargetSelector = (_, authorId) => authorId.HasValue
                ? new List<BookSearchTarget>
                {
                    new() { BookId = 102, AuthorId = 10 },
                    new() { BookId = 103, AuthorId = 10 }
                }
                : new List<BookSearchTarget>
                {
                    new() { BookId = 101, AuthorId = 10 },
                    new() { BookId = 102, AuthorId = 10 },
                    new() { BookId = 103, AuthorId = 10 }
                };

            var releaseSearch = new RecordingReleaseSearch();
            var subject = new BookSearchService(
                releaseSearch,
                bookService,
                null,
                new StaticQueueService(103) { State = cleanupWarning ? TrackedDownloadState.Imported : TrackedDownloadState.Downloading },
                new EmptyDecisionProcessor(),
                LogManager.GetLogger("BookSearchServiceFixture"));

            subject.Execute(new MissingBookSearchCommand { Trigger = CommandTrigger.Manual });

            Assert.That(bookServiceProxy.AuthorCatalogRequests, Is.EqualTo(new[] { 10 }));
            Assert.That(releaseSearch.BookSearchBooks.Select(book => book.Id), Is.EqualTo(cleanupWarning ? new[] { 102, 103 } : new[] { 102 }));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void cutoff_unmet_search_should_revalidate_targets_and_load_each_local_author_catalog_once(bool cleanupWarning)
        {
            var firstAuthor = new Author { Id = 10, AudiobookQualityProfileId = 1 };
            var secondAuthor = new Author { Id = 20, AudiobookQualityProfileId = 1 };
            var firstAuthorBooks = new List<Book>
            {
                new() { Id = 101, Title = "Later Search", Author = firstAuthor, MediaType = BookMediaType.Audiobook, LastSearchTime = DateTime.UtcNow },
                new() { Id = 102, Title = "Oldest Search", Author = firstAuthor, MediaType = BookMediaType.Audiobook },
                new() { Id = 103, Title = "No Longer Below Cutoff", Author = firstAuthor, MediaType = BookMediaType.Audiobook }
            };
            var secondAuthorBooks = new List<Book>
            {
                new() { Id = 201, Title = "Second Author", Author = secondAuthor, MediaType = BookMediaType.Audiobook }
            };

            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var bookServiceProxy = (BookServiceProxy)(object)bookService;
            bookServiceProxy.AuthorBooks[10] = firstAuthorBooks;
            bookServiceProxy.AuthorBooks[20] = secondAuthorBooks;

            var cutoffService = new RecordingCutoffService((_, authorId) => authorId switch
            {
                10 => new List<BookSearchTarget>
                {
                    new() { BookId = 101, AuthorId = 10 },
                    new() { BookId = 102, AuthorId = 10 }
                },
                20 => new List<BookSearchTarget>
                {
                    new() { BookId = 201, AuthorId = 20 }
                },
                _ => new List<BookSearchTarget>
                {
                    new() { BookId = 101, AuthorId = 10 },
                    new() { BookId = 102, AuthorId = 10 },
                    new() { BookId = 103, AuthorId = 10 },
                    new() { BookId = 201, AuthorId = 20 }
                }
            });

            var releaseSearch = new RecordingReleaseSearch();
            var subject = new BookSearchService(
                releaseSearch,
                bookService,
                cutoffService,
                new StaticQueueService(201) { State = cleanupWarning ? TrackedDownloadState.Imported : TrackedDownloadState.Downloading },
                new EmptyDecisionProcessor(),
                LogManager.GetLogger("BookSearchServiceFixture"));

            subject.Execute(new CutoffUnmetBookSearchCommand { Trigger = CommandTrigger.Manual });

            Assert.That(cutoffService.TargetRequests, Is.EqualTo(new int?[] { null, 10, 20 }));
            Assert.That(cutoffService.QualityRuleRequests, Is.EqualTo(1));
            Assert.That(bookServiceProxy.AuthorCatalogRequests, Is.EqualTo(cleanupWarning ? new[] { 10, 20 } : new[] { 10 }));
            Assert.That(releaseSearch.BookSearchBooks.Select(book => book.Id), Is.EqualTo(cleanupWarning ? new[] { 102, 101, 201 } : new[] { 102, 101 }));
            Assert.That(releaseSearch.AuthorCatalogSizes, Is.EqualTo(cleanupWarning ? new[] { 3, 3, 1 } : new[] { 3, 3 }));
        }

        [Test]
        public void whole_library_cutoff_unmet_search_should_be_type_exclusive()
        {
            Assert.That(new CutoffUnmetBookSearchCommand().IsTypeExclusive, Is.True);
            Assert.That(new CutoffUnmetBookSearchCommand(10).IsTypeExclusive, Is.False);
        }

        private class BookServiceProxy : DispatchProxy
        {
            public List<BookSearchTarget> SearchTargets { get; set; } = new();
            public Func<BookMediaType?, int?, List<BookSearchTarget>> MissingTargetSelector { get; set; }
            public int SelectedBookRequests { get; private set; }
            public Dictionary<int, List<Book>> AuthorBooks { get; } = new();
            public List<int> AuthorCatalogRequests { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IBookService.GetExistingBooks))
                {
                    SelectedBookRequests++;
                    var ids = ((IEnumerable<int>)args[0]).ToHashSet();
                    return AuthorBooks.Values.SelectMany(books => books).Where(book => ids.Contains(book.Id)).ToList();
                }

                if (targetMethod.Name == nameof(IBookService.GetMissingBookSearchTargets))
                {
                    return (MissingTargetSelector?.Invoke((BookMediaType?)args[0], (int?)args[1]) ?? SearchTargets).ToList();
                }

                if (targetMethod.Name == nameof(IBookService.GetBooksByAuthor))
                {
                    var authorId = (int)args[0];
                    AuthorCatalogRequests.Add(authorId);
                    return AuthorBooks[authorId];
                }

                throw new NotImplementedException(targetMethod.Name);
            }
        }

        private sealed class RecordingReleaseSearch : ISearchForReleases
        {
            public Action<Book> OnBookSearch { get; set; }
            public List<int> BookSearchIds { get; } = new();
            public List<Book> BookSearchBooks { get; } = new();
            public List<int> AuthorCatalogSizes { get; } = new();
            public List<bool> AllowUnmonitoredValues { get; } = new();
            public List<bool> UserInvokedValues { get; } = new();

            public Task<List<DownloadDecision>> BookSearch(int bookId, bool missingOnly, bool userInvokedSearch, bool interactiveSearch, bool allowUnmonitored = false)
            {
                BookSearchIds.Add(bookId);
                AllowUnmonitoredValues.Add(allowUnmonitored);
                UserInvokedValues.Add(userInvokedSearch);
                return Task.FromResult(new List<DownloadDecision>());
            }

            public Task<List<DownloadDecision>> BookSearch(Book book, bool missingOnly, bool userInvokedSearch, bool interactiveSearch, bool allowUnmonitored = false)
            {
                BookSearchBooks.Add(book);
                AuthorCatalogSizes.Add(book.Author?.Books?.Count ?? 0);
                AllowUnmonitoredValues.Add(allowUnmonitored);
                UserInvokedValues.Add(userInvokedSearch);
                return Task.FromResult(new List<DownloadDecision>());
            }

            public Task<List<DownloadDecision>> BookSearch(Book book, List<Book> authorCatalog, bool missingOnly, bool userInvokedSearch, bool interactiveSearch, bool allowUnmonitored = false)
            {
                OnBookSearch?.Invoke(book);
                BookSearchIds.Add(book.Id);
                BookSearchBooks.Add(book);
                AuthorCatalogSizes.Add(authorCatalog?.Count ?? 0);
                AllowUnmonitoredValues.Add(allowUnmonitored);
                UserInvokedValues.Add(userInvokedSearch);
                return Task.FromResult(new List<DownloadDecision>());
            }

            public async Task<BookSearchResult> BookSearchWithOutcome(Book book, List<Book> authorCatalog, bool missingOnly, bool userInvokedSearch, bool interactiveSearch, bool allowUnmonitored = false)
            {
                return new BookSearchResult { Decisions = await BookSearch(book, authorCatalog, missingOnly, userInvokedSearch, interactiveSearch, allowUnmonitored) };
            }

            public Task<List<DownloadDecision>> AuthorSearch(int authorId, bool missingOnly, bool userInvokedSearch, bool interactiveSearch, BookMediaType? mediaType, CancellationToken cancellationToken)
            {
                return Task.FromResult(new List<DownloadDecision>());
            }

            public Task AuthorSearch(int authorId, bool missingOnly, bool userInvokedSearch, bool interactiveSearch, BookMediaType? mediaType, Func<IReadOnlyList<Book>, Task> onSearchStarted, Func<Book, BookSearchResult, Exception, Task> onBookCompleted, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }
        }

        private sealed class RecordingCutoffService : IBookCutoffService
        {
            private readonly Func<BookMediaType?, int?, List<BookSearchTarget>> _targetSelector;

            public RecordingCutoffService(Func<BookMediaType?, int?, List<BookSearchTarget>> targetSelector)
            {
                _targetSelector = targetSelector;
            }

            public List<int?> TargetRequests { get; } = new();
            public int QualityRuleRequests { get; private set; }

            public List<NzbDrone.Core.Qualities.QualitiesBelowCutoff> GetQualitiesBelowCutoff()
            {
                QualityRuleRequests++;
                return new List<NzbDrone.Core.Qualities.QualitiesBelowCutoff>();
            }

            public List<BookSearchTarget> GetCutoffUnmetSearchTargets(List<NzbDrone.Core.Qualities.QualitiesBelowCutoff> qualitiesBelowCutoff, BookMediaType? mediaType, int? authorId)
            {
                TargetRequests.Add(authorId);
                return _targetSelector(mediaType, authorId).ToList();
            }

            public NzbDrone.Core.Datastore.PagingSpec<Book> BooksWhereCutoffUnmet(NzbDrone.Core.Datastore.PagingSpec<Book> pagingSpec)
            {
                throw new NotImplementedException();
            }
        }

        private sealed class StaticQueueService : IQueueService
        {
            private readonly List<NzbDrone.Core.Queue.Queue> _items;

            public TrackedDownloadState? State { get; set; }

            public StaticQueueService(params int[] bookIds)
            {
                _items = bookIds.Select(bookId => new NzbDrone.Core.Queue.Queue
                {
                    Book = new Book { Id = bookId }
                }).ToList();
            }

            public List<NzbDrone.Core.Queue.Queue> GetQueue(bool includeConversionStatus = true)
            {
                foreach (var item in _items) item.TrackedDownloadState = State;
                return _items.ToList();
            }

            public NzbDrone.Core.Queue.Queue Find(int id)
            {
                return null;
            }

            public void Remove(int id)
            {
            }
        }

        private sealed class EmptyQueueService : IQueueService
        {
            public List<NzbDrone.Core.Queue.Queue> GetQueue(bool includeConversionStatus = true)
            {
                return new List<NzbDrone.Core.Queue.Queue>();
            }

            public NzbDrone.Core.Queue.Queue Find(int id)
            {
                return null;
            }

            public void Remove(int id)
            {
            }
        }

        private sealed class EmptyDecisionProcessor : IProcessDownloadDecisions
        {
            public Action OnProcess { get; set; }

            public Task<ProcessedDecisions> ProcessDecisions(List<DownloadDecision> decisions)
            {
                OnProcess?.Invoke();
                return Task.FromResult(new ProcessedDecisions(new List<DownloadDecision>(), new List<DownloadDecision>(), new List<DownloadDecision>()));
            }

            public Task<ProcessedDecisions> ProcessDecisions(List<DownloadDecision> decisions, int? downloadClientId) => ProcessDecisions(decisions);

            public Task ProcessSelectedDecisions(List<RemoteBook> selected, int? downloadClientId = null) => Task.CompletedTask;

        }
    }
}
