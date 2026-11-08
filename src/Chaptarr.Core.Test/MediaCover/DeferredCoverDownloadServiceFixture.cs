using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaCover.Commands;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.MediaCover
{
    [TestFixture]
    public class DeferredCoverDownloadServiceFixture
    {
        private sealed class RecordingBookService : IBookService
        {
            public List<Book> GetImportListBookIdentities() => GetAllBooks();

            private readonly Dictionary<int, Book> _booksById;

            public RecordingBookService(IEnumerable<Book> books)
            {
                _booksById = books.ToDictionary(book => book.Id);
            }

            public List<int> RequestedBookIds { get; } = new();
            public List<List<int>> RequestedFileBatches { get; } = new();
            public Action OnGetExistingBooks { get; set; }

            public List<Book> GetBooks(IEnumerable<int> bookIds)
            {
                var ids = bookIds.Distinct().ToList();
                RequestedBookIds.AddRange(ids);

                var books = ids
                    .Where(bookId => _booksById.ContainsKey(bookId))
                    .Select(bookId => _booksById[bookId])
                    .ToList();

                if (books.Count != ids.Count)
                {
                    throw new ApplicationException("Strict bulk fetch cannot return a partial result");
                }

                return books;
            }

            public List<Book> GetExistingBooks(IEnumerable<int> bookIds)
            {
                var ids = bookIds.Distinct().ToList();
                RequestedBookIds.AddRange(ids);
                OnGetExistingBooks?.Invoke();

                return ids
                    .Where(bookId => _booksById.ContainsKey(bookId))
                    .Select(bookId => _booksById[bookId])
                    .ToList();
            }

            public Book GetBook(int bookId) => throw new NotImplementedException();
            public List<Book> GetBooksByAuthor(int authorId, bool onlyMonitoredEditions = false) => throw new NotImplementedException();
            public List<Book> GetNextBooksByAuthorId(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Book> GetLastBooksByAuthorId(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Book> GetBooksByAuthorId(int authorId) => throw new NotImplementedException();
            public List<Book> GetBooksForRefresh(int authorId, List<string> foreignIds) => throw new NotImplementedException();
            public List<Book> GetBooksByFileIds(IEnumerable<int> fileIds)
            {
                RequestedFileBatches.Add(fileIds.ToList());
                return _booksById.Values.ToList();
            }
            public Book AddBook(Book newBook, bool doRefresh = true) => throw new NotImplementedException();
            public List<Book> FindBySlug(string titleSlug) => throw new NotImplementedException();
            public Book FindByTitle(int authorId, string title) => throw new NotImplementedException();
            public Book FindByTitleInexact(int authorId, string title) => throw new NotImplementedException();
            public Book FindByGoodreadsId(string goodreadsId) => throw new NotImplementedException();
            public Book FindByProviderId(string provider, string providerId) => throw new NotImplementedException();
            public Book FindByProviderId(string provider, string providerId, BookMediaType mediaType) => throw new NotImplementedException();
            public List<Book> FindAllByProviderId(string provider, string providerId, BookMediaType mediaType) => throw new NotImplementedException();
            public Book FindByISBN(string isbn) => throw new NotImplementedException();
            public Book FindByASIN(string asin) => throw new NotImplementedException();
            public List<Book> GetCandidates(int authorId, string title) => throw new NotImplementedException();
            public void DeleteBook(int bookId, bool deleteFiles, bool addImportListExclusion = false, bool applyToBothFormats = false) => throw new NotImplementedException();
            public List<Book> GetAllBooks() => throw new NotImplementedException();
            public Book UpdateBook(Book book, List<Edition> editionUpdates, string monitoredMediaType = null, System.Action<System.Data.IDbConnection, System.Data.IDbTransaction> writeRelated = null) => throw new System.NotSupportedException();
            public Book UpdateBook(Book book) => throw new NotImplementedException();
            public void SetBookMonitored(int bookId, bool monitored) => throw new NotImplementedException();
            public void SetMonitored(IEnumerable<int> ids, bool monitored) => throw new NotImplementedException();
            public void SetMonitoredForMediaType(IEnumerable<int> ids, string mediaType, bool monitored) => throw new NotImplementedException();
            public void UpdateLastSearchTime(List<Book> books) => throw new NotImplementedException();
            public PagingSpec<Book> BooksWithoutFiles(PagingSpec<Book> pagingSpec) => throw new NotImplementedException();
            public List<Book> BooksBetweenDates(DateTime start, DateTime end, bool includeUnmonitored) => throw new NotImplementedException();
            public List<Book> AuthorBooksBetweenDates(Author author, DateTime start, DateTime end, bool includeUnmonitored) => throw new NotImplementedException();
            public void InsertMany(List<Book> books) => throw new NotImplementedException();
            public void InsertMany(List<Book> books, IDbConnection connection, IDbTransaction transaction) => throw new NotImplementedException();
            public void UpdateMany(List<Book> books) => throw new NotImplementedException();
            public void DeleteMany(List<Book> books, bool filesAlreadyDeleted = false, List<NzbDrone.Core.MediaFiles.BookFile> deletedBookFiles = null) => throw new NotImplementedException();
            public void SetAddOptions(IEnumerable<Book> books) => throw new NotImplementedException();
            public List<Book> GetAuthorBooksWithFiles(Author author) => throw new NotImplementedException();
            public List<Book> GetBooksForDisplay(int? authorId = null, string mediaType = null) => throw new NotImplementedException();
            public List<Book> GetBooksByBaseId(string baseBookId) => throw new NotImplementedException();
            public (Book Book, bool ExistingCopy) AddWantedEditionCopy(int bookId, int editionId) => throw new NotImplementedException();
            public bool ShouldSearchForMediaType(Book book, string mediaType) => throw new NotImplementedException();
            public List<Book> GetMonitoredBooksForAuthor(int authorId, string mediaType) => throw new NotImplementedException();
            public BookBucketResource GetBookBuckets(string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null) => throw new NotImplementedException();
            public PagedBookResource GetBooksPaged(int offset, int pageSize, string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null) => throw new NotImplementedException();
        }

        private class RecordingCommandQueue : DispatchProxy
        {
            public List<Command> Commands { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IManageCommandQueue.Push))
                {
                    Commands.Add((Command)args[0]);
                    return null;
                }

                throw new NotImplementedException($"Unexpected command queue call: {targetMethod?.Name}");
            }
        }

        private static (IManageCommandQueue Queue, RecordingCommandQueue Recorder) CreateCommandQueue()
        {
            var queue = DispatchProxy.Create<IManageCommandQueue, RecordingCommandQueue>();
            return (queue, (RecordingCommandQueue)(object)queue);
        }

        private sealed class RecordingCoverMapper : IMapCoversToLocal
        {
            public List<int> EnsuredBookIds { get; } = new();
            public Action<Book> OnEnsureBook { get; set; }

            public void EnsureBookCovers(Book book)
            {
                EnsuredBookIds.Add(book.Id);
                OnEnsureBook?.Invoke(book);
            }

            public void EnsureBookCoversForBooks(List<Book> books)
            {
                foreach (var book in books)
                {
                    EnsureBookCovers(book);
                }
            }

            public void EnsureBookCoversForImport(Book book)
            {
                EnsureBookCovers(book);
            }

            public void DeleteBookCovers(int bookId)
            {
                throw new NotImplementedException();
            }

            public void ConvertBookCoversToLocalUrls(IEnumerable<KeyValuePair<int, List<NzbDrone.Core.MediaCover.MediaCover>>> covers)
            {
                throw new NotImplementedException();
            }

            public void ConvertToLocalUrls(int entityId, MediaCoverEntity coverEntity, IList<NzbDrone.Core.MediaCover.MediaCover> covers, string selectedAuthorImageHash = null) => throw new NotImplementedException();
            public string GetCoverPath(int entityId, MediaCoverEntity coverEntity, MediaCoverTypes coverType, string extension, int? height = null) => throw new NotImplementedException();
            public void EnsureAuthorCovers(Author author) => throw new NotImplementedException();
            public System.Threading.Tasks.Task<EnsureImageResult> EnsureAuthorImage(Author author, NzbDrone.Core.MediaCover.MediaCover cover) => throw new NotImplementedException();
        }

        [Test]
        public void absorbed_book_leaves_the_deferred_cover_queue_without_deleting_files()
        {
            var deferred = new DeferredCoverService(LogManager.GetCurrentClassLogger());
            deferred.RequeueBooksForCoverDownload(new[] { 1, 2 });
            var service = new DeferredCoverDownloadService(deferred, null, null, null, LogManager.GetCurrentClassLogger());
            service.Handle(new LibraryIdentityChangedEvent(new[] { 1 }, new[] { 1, 2 }, new[] { 2 }));
            Assert.That(deferred.GetPendingBookIds(), Is.EqualTo(new[] { 1 }));
        }

        [Test]
        public void should_queue_one_batch_for_newly_monitored_books_in_an_aggregate_edit()
        {
            var deferredCoverService = new DeferredCoverService(LogManager.GetCurrentClassLogger());
            var bookService = new RecordingBookService(new[]
            {
                new Book { Id = 1, Title = "One" },
                new Book { Id = 2, Title = "Two" },
                new Book { Id = 3, Title = "Three" }
            });
            var coverMapper = new RecordingCoverMapper();
            var commandQueue = CreateCommandQueue();
            var service = new DeferredCoverDownloadService(deferredCoverService, bookService, commandQueue.Queue, coverMapper, LogManager.GetCurrentClassLogger());

            service.Handle(new BooksEditedEvent(new[]
            {
                new BookEditedEvent(
                    new Book { Id = 1, AudiobookMonitored = true },
                    new Book { Id = 1, AudiobookMonitored = false }),
                new BookEditedEvent(
                    new Book { Id = 2, AudiobookMonitored = false },
                    new Book { Id = 2, AudiobookMonitored = true }),
                new BookEditedEvent(
                    new Book { Id = 3, EbookMonitored = true },
                    new Book { Id = 3, EbookMonitored = false })
            }));

            Assert.That(commandQueue.Recorder.Commands, Has.Count.EqualTo(1));
            var command = commandQueue.Recorder.Commands.Single() as DownloadBookMediaCommand;
            Assert.That(command.BookIds, Is.EqualTo(new[] { 1, 3 }));
        }

        [Test]
        public void should_queue_one_batch_for_a_persisted_batch_of_added_files()
        {
            var deferredCoverService = new DeferredCoverService(LogManager.GetCurrentClassLogger());
            var bookService = new RecordingBookService(new[]
            {
                new Book { Id = 1, Title = "One" },
                new Book { Id = 3, Title = "Three" }
            });
            var coverMapper = new RecordingCoverMapper();
            var commandQueue = CreateCommandQueue();
            var service = new DeferredCoverDownloadService(deferredCoverService, bookService, commandQueue.Queue, coverMapper, LogManager.GetCurrentClassLogger());

            service.Handle(new BookFilesAddedEvent(new[]
            {
                new BookFile { Id = 11, Edition = new Edition { BookId = 1, Book = new Book { Id = 1 } } },
                new BookFile { Id = 12, Edition = new Edition { BookId = 1, Book = new Book { Id = 1 } } },
                new BookFile { Id = 13, Edition = new Edition { BookId = 3, Book = new Book { Id = 3 } } }
            }));

            Assert.That(commandQueue.Recorder.Commands, Has.Count.EqualTo(1));
            var command = commandQueue.Recorder.Commands.Single() as DownloadBookMediaCommand;
            Assert.That(command.BookIds, Is.EqualTo(new[] { 1, 3 }));
        }

        private sealed class UnloadedRelation<T> : LazyLoaded<T>
        {
            public override void LazyLoad() => throw new InvalidOperationException("Cover batching must not lazy-load each file's relations");
        }

        [Test]
        public void file_covers_should_use_loaded_edition_foreign_keys_and_batch_unloaded_relations()
        {
            var books = new RecordingBookService(new[] { new Book { Id = 3 } });
            var queue = CreateCommandQueue();
            var service = new DeferredCoverDownloadService(new DeferredCoverService(LogManager.GetCurrentClassLogger()),
                books, queue.Queue, new RecordingCoverMapper(), LogManager.GetCurrentClassLogger());

            service.Handle(new BookFilesAddedEvent(new[]
            {
                new BookFile { Id = 11, Edition = new Edition { BookId = 1, LazyBook = new UnloadedRelation<Book>() } },
                new BookFile { Id = 12, LazyEdition = new UnloadedRelation<Edition>() },
                new BookFile { Id = 13, LazyEdition = new UnloadedRelation<Edition>() }
            }));

            Assert.That(books.RequestedFileBatches, Has.Count.EqualTo(1));
            Assert.That(books.RequestedFileBatches[0], Is.EqualTo(new[] { 12, 13 }));
            Assert.That(queue.Recorder.Commands, Has.Count.EqualTo(1));
            Assert.That(((DownloadBookMediaCommand)queue.Recorder.Commands.Single()).BookIds, Is.EqualTo(new[] { 1, 3 }));
        }

        [Test]
        public void should_atomically_report_whether_a_cover_was_deferred()
        {
            var service = new DeferredCoverService(LogManager.GetCurrentClassLogger());

            Assert.That(service.MarkBookForCoverDownload(1), Is.False);

            service.IsCoverDownloadDeferred = true;

            Assert.That(service.MarkBookForCoverDownload(1), Is.True);
            Assert.That(service.GetPendingBookIds(), Is.EqualTo(new[] { 1 }));

            service.IsCoverDownloadDeferred = false;

            Assert.That(service.MarkBookForCoverDownload(2), Is.False);
            Assert.That(service.GetPendingBookIds(), Is.EqualTo(new[] { 1 }));
        }

        [Test]
        public void should_remove_deleted_books_from_pending_queue_before_flush()
        {
            var deferredCoverService = new DeferredCoverService(LogManager.GetCurrentClassLogger());
            var bookService = new RecordingBookService(new[]
            {
                new Book { Id = 1, Title = "One" },
                new Book { Id = 3, Title = "Three" }
            });
            var coverMapper = new RecordingCoverMapper();
            var commandQueue = CreateCommandQueue();
            var service = new DeferredCoverDownloadService(deferredCoverService, bookService, commandQueue.Queue, coverMapper, LogManager.GetCurrentClassLogger());

            service.HandleAsync(new ImportStageProgressEvent(ImportStage.MatchingBooks, "start") { CommandId = 7 });
            deferredCoverService.MarkBooksForCoverDownload(new[] { 1, 2, 3 });
            service.Handle(new BookDeletedEvent(new Book { Id = 2, Title = "Deleted" }, false, false));

            Assert.DoesNotThrow(() =>
                service.HandleAsync(new ImportStageProgressEvent(ImportStage.ImportComplete, "done") { CommandId = 7 }));

            Assert.That(bookService.RequestedBookIds, Is.EqualTo(new List<int> { 1, 3 }));
            Assert.That(coverMapper.EnsuredBookIds, Is.EqualTo(new List<int> { 1, 3 }));
            Assert.That(deferredCoverService.GetPendingBookIds(), Is.Empty);
        }

        [Test]
        public void should_skip_stale_pending_book_ids_when_books_were_deleted_before_flush()
        {
            var deferredCoverService = new DeferredCoverService(LogManager.GetCurrentClassLogger());
            var bookService = new RecordingBookService(new[]
            {
                new Book { Id = 1, Title = "One" },
                new Book { Id = 3, Title = "Three" }
            });
            var coverMapper = new RecordingCoverMapper();
            var commandQueue = CreateCommandQueue();
            var service = new DeferredCoverDownloadService(deferredCoverService, bookService, commandQueue.Queue, coverMapper, LogManager.GetCurrentClassLogger());

            service.HandleAsync(new ImportStageProgressEvent(ImportStage.MatchingBooks, "start") { CommandId = 9 });
            deferredCoverService.MarkBooksForCoverDownload(new[] { 1, 2, 3 });

            Assert.DoesNotThrow(() =>
                service.HandleAsync(new ImportStageProgressEvent(ImportStage.ImportComplete, "done") { CommandId = 9 }));

            Assert.That(bookService.RequestedBookIds, Is.EqualTo(new List<int> { 1, 2, 3 }));
            Assert.That(coverMapper.EnsuredBookIds, Is.EqualTo(new List<int> { 1, 3 }));
            Assert.That(deferredCoverService.GetPendingBookIds(), Is.Empty);
        }

        [Test]
        public void should_keep_failed_cover_batch_pending_until_a_later_flush_succeeds()
        {
            var deferredCoverService = new DeferredCoverService(LogManager.GetCurrentClassLogger());
            var bookService = new RecordingBookService(new[]
            {
                new Book { Id = 1, Title = "One" },
                new Book { Id = 2, Title = "Two" }
            });
            var coverMapper = new RecordingCoverMapper
            {
                OnEnsureBook = _ => throw new InvalidOperationException("Cover batch failed")
            };
            var commandQueue = CreateCommandQueue();
            var service = new DeferredCoverDownloadService(deferredCoverService, bookService, commandQueue.Queue, coverMapper, LogManager.GetCurrentClassLogger());

            service.HandleAsync(new ImportStageProgressEvent(ImportStage.MatchingBooks, "start") { CommandId = 31 });
            deferredCoverService.MarkBooksForCoverDownload(new[] { 1, 2 });

            Assert.DoesNotThrow(() =>
                service.HandleAsync(new ImportStageProgressEvent(ImportStage.ImportComplete, "done") { CommandId = 31 }));

            Assert.That(deferredCoverService.GetPendingBookIds(), Is.EquivalentTo(new[] { 1, 2 }));
            Assert.That(coverMapper.EnsuredBookIds, Is.EqualTo(new[] { 1 }));

            coverMapper.OnEnsureBook = null;
            service.HandleAsync(new ImportStageProgressEvent(ImportStage.MatchingBooks, "next start") { CommandId = 32 });
            service.HandleAsync(new ImportStageProgressEvent(ImportStage.ImportComplete, "next done") { CommandId = 32 });

            Assert.That(coverMapper.EnsuredBookIds, Is.EqualTo(new[] { 1, 1, 2 }));
            Assert.That(deferredCoverService.GetPendingBookIds(), Is.Empty);
            Assert.That(commandQueue.Recorder.Commands, Is.Empty);
        }

        [Test]
        public void should_ignore_stale_import_stage_events_after_command_terminal_cleanup()
        {
            var deferredCoverService = new DeferredCoverService(LogManager.GetCurrentClassLogger());
            var bookService = new RecordingBookService(new[]
            {
                new Book { Id = 1, Title = "One" }
            });
            var coverMapper = new RecordingCoverMapper();
            var commandQueue = CreateCommandQueue();
            var service = new DeferredCoverDownloadService(deferredCoverService, bookService, commandQueue.Queue, coverMapper, LogManager.GetCurrentClassLogger());

            service.HandleAsync(new ImportStageProgressEvent(ImportStage.MatchingBooks, "start") { CommandId = 15 });
            deferredCoverService.MarkBooksForCoverDownload(new[] { 1 });

            service.Handle(new CommandExecutedEvent(new CommandModel { Id = 15 }));
            service.HandleAsync(new ImportStageProgressEvent(ImportStage.MatchingBooks, "late stale stage") { CommandId = 15 });

            Assert.That(deferredCoverService.IsCoverDownloadDeferred, Is.False);
            Assert.That(bookService.RequestedBookIds, Is.EqualTo(new List<int> { 1 }));
            Assert.That(coverMapper.EnsuredBookIds, Is.EqualTo(new List<int> { 1 }));
            Assert.That(deferredCoverService.GetPendingBookIds(), Is.Empty);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void failed_batch_should_merge_back_with_new_work_even_when_deferral_has_ended(bool lookupFailure)
        {
            var deferred = new DeferredCoverService(LogManager.GetCurrentClassLogger());
            var books = new RecordingBookService(new[] { new Book { Id = 1 }, new Book { Id = 2 }, new Book { Id = 3 } });
            var covers = new RecordingCoverMapper();
            var service = new DeferredCoverDownloadService(deferred, books, null, covers, LogManager.GetCurrentClassLogger());
            service.HandleAsync(new ImportStageProgressEvent(ImportStage.MatchingBooks, "start") { CommandId = 81 });
            deferred.MarkBooksForCoverDownload(new[] { 1, 2 });
            Action failAfterNewWork = () =>
            {
                deferred.IsCoverDownloadDeferred = true;
                deferred.MarkBooksForCoverDownload(new[] { 1, 3 });
                deferred.IsCoverDownloadDeferred = false;
                throw new InvalidOperationException("Batch dependency failed");
            };
            if (lookupFailure)
            {
                books.OnGetExistingBooks = failAfterNewWork;
            }
            else
            {
                covers.OnEnsureBook = _ => failAfterNewWork();
            }

            Assert.DoesNotThrow(() => service.HandleAsync(new ImportStageProgressEvent(ImportStage.ImportComplete, "done") { CommandId = 81 }));
            Assert.That(deferred.GetPendingBookIds(), Is.EquivalentTo(new[] { 1, 2, 3 }));
            Assert.That(deferred.IsCoverDownloadDeferred, Is.False);
            books.OnGetExistingBooks = null;
            covers.OnEnsureBook = null;
            covers.EnsuredBookIds.Clear();
            service.Handle(new CommandExecutedEvent(new CommandModel { Id = 81 }));
            Assert.That(covers.EnsuredBookIds, Is.Empty);
            service.HandleAsync(new ImportStageProgressEvent(ImportStage.MatchingBooks, "retry") { CommandId = 82 });
            service.HandleAsync(new ImportStageProgressEvent(ImportStage.ImportComplete, "retried") { CommandId = 82 });

            Assert.That(covers.EnsuredBookIds, Is.EquivalentTo(new[] { 1, 2, 3 }));
            Assert.That(deferred.GetPendingBookIds(), Is.Empty);
        }

        [Test]
        public void overlapping_flush_should_not_process_an_already_claimed_batch()
        {
            var deferred = new DeferredCoverService(LogManager.GetCurrentClassLogger());
            var books = new RecordingBookService(new[] { new Book { Id = 1 } });
            var covers = new RecordingCoverMapper();
            var service = new DeferredCoverDownloadService(deferred, books, null, covers, LogManager.GetCurrentClassLogger());
            service.HandleAsync(new ImportStageProgressEvent(ImportStage.MatchingBooks, "start") { CommandId = 91 });
            deferred.MarkBookForCoverDownload(1);
            covers.OnEnsureBook = _ =>
            {
                covers.OnEnsureBook = null;
                service.HandleAsync(new ImportStageProgressEvent(ImportStage.ImportComplete, "overlapping completion") { CommandId = 92 });
            };

            service.HandleAsync(new ImportStageProgressEvent(ImportStage.ImportComplete, "done") { CommandId = 91 });

            Assert.That(books.RequestedBookIds, Is.EqualTo(new[] { 1 }));
            Assert.That(covers.EnsuredBookIds, Is.EqualTo(new[] { 1 }));
            Assert.That(deferred.GetPendingBookIds(), Is.Empty);
        }

        [Test]
        public void should_not_remove_books_queued_after_the_flush_snapshot()
        {
            var deferredCoverService = new DeferredCoverService(LogManager.GetCurrentClassLogger());
            var bookService = new RecordingBookService(new[]
            {
                new Book { Id = 1, Title = "First import" },
                new Book { Id = 2, Title = "Next import" }
            });
            var coverMapper = new RecordingCoverMapper();
            var commandQueue = CreateCommandQueue();
            var service = new DeferredCoverDownloadService(deferredCoverService, bookService, commandQueue.Queue, coverMapper, LogManager.GetCurrentClassLogger());

            service.HandleAsync(new ImportStageProgressEvent(ImportStage.MatchingBooks, "first start") { CommandId = 21 });
            deferredCoverService.MarkBookForCoverDownload(1);
            coverMapper.OnEnsureBook = _ =>
            {
                service.HandleAsync(new ImportStageProgressEvent(ImportStage.MatchingBooks, "next start") { CommandId = 22 });
                deferredCoverService.MarkBookForCoverDownload(2);
            };

            service.HandleAsync(new ImportStageProgressEvent(ImportStage.ImportComplete, "first done") { CommandId = 21 });

            Assert.That(coverMapper.EnsuredBookIds, Is.EqualTo(new[] { 1 }));
            Assert.That(deferredCoverService.GetPendingBookIds(), Is.EqualTo(new[] { 2 }));
        }
    }
}
