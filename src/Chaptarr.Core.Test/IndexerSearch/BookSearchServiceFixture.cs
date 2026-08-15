using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Queue;

namespace Chaptarr.Core.Test.IndexerSearch
{
    [TestFixture]
    public class BookSearchServiceFixture
    {
        [Test]
        public void book_search_command_should_search_each_book_id_once()
        {
            var releaseSearch = new RecordingReleaseSearch();
            var subject = new BookSearchService(
                releaseSearch,
                null,
                null,
                null,
                new EmptyDecisionProcessor(),
                LogManager.GetLogger("BookSearchServiceFixture"));

            subject.Execute(new BookSearchCommand(new List<int> { 5792, 5792, 0, 5792 })
            {
                Trigger = CommandTrigger.Manual
            });

            Assert.That(releaseSearch.BookSearchIds, Is.EqualTo(new List<int> { 5792 }));
        }

        [Test]
        public void automatic_book_search_should_skip_a_book_on_a_paused_media_side()
        {
            var releaseSearch = new RecordingReleaseSearch();
            var bookService = CreateBookService(new Book
            {
                Id = 42,
                MediaType = BookMediaType.Audiobook,
                AudiobookMonitored = true,
                Author = new Author
                {
                    Monitored = true,
                    AudiobookMonitorExisting = 0,
                    AudiobookMonitorFuture = false,
                    EbookMonitorExisting = 2
                }
            });
            var subject = CreateSubject(releaseSearch, bookService);

            subject.Execute(new BookSearchCommand(new List<int> { 42 }));

            Assert.That(releaseSearch.BookSearchIds, Is.Empty);
        }

        [Test]
        public void automatic_book_search_should_allow_selected_monitoring_for_the_book_media_side()
        {
            var releaseSearch = new RecordingReleaseSearch();
            var bookService = CreateBookService(new Book
            {
                Id = 44,
                MediaType = BookMediaType.Audiobook,
                AudiobookMonitored = true,
                Author = new Author
                {
                    AudiobookMonitorExisting = 2,
                    AudiobookMonitorFuture = false
                }
            });
            var subject = CreateSubject(releaseSearch, bookService);

            subject.Execute(new BookSearchCommand(new List<int> { 44 }));

            Assert.That(releaseSearch.BookSearchIds, Is.EqualTo(new List<int> { 44 }));
        }

        [Test]
        public void automatic_book_search_should_allow_future_monitoring_for_the_book_media_side()
        {
            var releaseSearch = new RecordingReleaseSearch();
            var bookService = CreateBookService(new Book
            {
                Id = 43,
                MediaType = BookMediaType.Audiobook,
                AudiobookMonitored = true,
                Author = new Author
                {
                    AudiobookMonitorExisting = 0,
                    AudiobookMonitorFuture = true
                }
            });
            var subject = CreateSubject(releaseSearch, bookService);

            subject.Execute(new BookSearchCommand(new List<int> { 43 }));

            Assert.That(releaseSearch.BookSearchIds, Is.EqualTo(new List<int> { 43 }));
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
        }

        [Test]
        public void whole_library_missing_search_should_be_type_exclusive()
        {
            Assert.That(new MissingBookSearchCommand().IsTypeExclusive, Is.True);
            Assert.That(new MissingBookSearchCommand(10).IsTypeExclusive, Is.False);
        }

        [Test]
        public void missing_search_should_revalidate_targets_and_queue_before_loading_an_author()
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
                new StaticQueueService(103),
                new EmptyDecisionProcessor(),
                LogManager.GetLogger("BookSearchServiceFixture"));

            subject.Execute(new MissingBookSearchCommand { Trigger = CommandTrigger.Manual });

            Assert.That(bookServiceProxy.AuthorCatalogRequests, Is.EqualTo(new[] { 10 }));
            Assert.That(releaseSearch.BookSearchBooks.Select(book => book.Id), Is.EqualTo(new[] { 102 }));
        }

        [Test]
        public void cutoff_unmet_search_should_revalidate_targets_and_load_each_local_author_catalog_once()
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
                new EmptyQueueService(),
                new EmptyDecisionProcessor(),
                LogManager.GetLogger("BookSearchServiceFixture"));

            subject.Execute(new CutoffUnmetBookSearchCommand { Trigger = CommandTrigger.Manual });

            Assert.That(cutoffService.TargetRequests, Is.EqualTo(new int?[] { null, 10, 20 }));
            Assert.That(cutoffService.QualityRuleRequests, Is.EqualTo(1));
            Assert.That(bookServiceProxy.AuthorCatalogRequests, Is.EqualTo(new[] { 10, 20 }));
            Assert.That(releaseSearch.BookSearchBooks.Select(book => book.Id), Is.EqualTo(new[] { 102, 101, 201 }));
            Assert.That(releaseSearch.AuthorCatalogSizes, Is.EqualTo(new[] { 3, 3, 1 }));
        }

        [Test]
        public void whole_library_cutoff_unmet_search_should_be_type_exclusive()
        {
            Assert.That(new CutoffUnmetBookSearchCommand().IsTypeExclusive, Is.True);
            Assert.That(new CutoffUnmetBookSearchCommand(10).IsTypeExclusive, Is.False);
        }

        private static BookSearchService CreateSubject(RecordingReleaseSearch releaseSearch, IBookService bookService)
        {
            return new BookSearchService(
                releaseSearch,
                bookService,
                null,
                null,
                new EmptyDecisionProcessor(),
                LogManager.GetLogger("BookSearchServiceFixture"));
        }

        private static IBookService CreateBookService(params Book[] books)
        {
            var service = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)(object)service).Books = books.ToList();
            return service;
        }

        private class BookServiceProxy : DispatchProxy
        {
            public List<Book> Books { get; set; } = new();
            public List<BookSearchTarget> SearchTargets { get; set; } = new();
            public Func<BookMediaType?, int?, List<BookSearchTarget>> MissingTargetSelector { get; set; }
            public Dictionary<int, List<Book>> AuthorBooks { get; } = new();
            public List<int> AuthorCatalogRequests { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IBookService.GetBooks))
                {
                    var ids = ((IEnumerable<int>)args[0]).ToHashSet();
                    return Books.Where(book => ids.Contains(book.Id)).ToList();
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
            public List<int> BookSearchIds { get; } = new();
            public List<Book> BookSearchBooks { get; } = new();
            public List<int> AuthorCatalogSizes { get; } = new();

            public Task<List<DownloadDecision>> BookSearch(int bookId, bool missingOnly, bool userInvokedSearch, bool interactiveSearch)
            {
                BookSearchIds.Add(bookId);
                return Task.FromResult(new List<DownloadDecision>());
            }

            public Task<List<DownloadDecision>> BookSearch(Book book, bool missingOnly, bool userInvokedSearch, bool interactiveSearch)
            {
                BookSearchBooks.Add(book);
                AuthorCatalogSizes.Add(book.Author?.Books?.Count ?? 0);
                return Task.FromResult(new List<DownloadDecision>());
            }

            public Task<List<DownloadDecision>> BookSearch(Book book, List<Book> authorCatalog, bool missingOnly, bool userInvokedSearch, bool interactiveSearch)
            {
                BookSearchBooks.Add(book);
                AuthorCatalogSizes.Add(authorCatalog?.Count ?? 0);
                return Task.FromResult(new List<DownloadDecision>());
            }

            public Task<List<DownloadDecision>> AuthorSearch(int authorId, bool missingOnly, bool userInvokedSearch, bool interactiveSearch)
            {
                return Task.FromResult(new List<DownloadDecision>());
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

            public StaticQueueService(params int[] bookIds)
            {
                _items = bookIds.Select(bookId => new NzbDrone.Core.Queue.Queue
                {
                    Book = new Book { Id = bookId }
                }).ToList();
            }

            public List<NzbDrone.Core.Queue.Queue> GetQueue()
            {
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
            public List<NzbDrone.Core.Queue.Queue> GetQueue()
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
            public Task<ProcessedDecisions> ProcessDecisions(List<DownloadDecision> decisions)
            {
                return Task.FromResult(new ProcessedDecisions(new List<DownloadDecision>(), new List<DownloadDecision>(), new List<DownloadDecision>()));
            }

            public Task<ProcessedDecisionResult> ProcessDecision(DownloadDecision decision, int? downloadClientId)
            {
                return Task.FromResult(ProcessedDecisionResult.Skipped);
            }
        }
    }
}
