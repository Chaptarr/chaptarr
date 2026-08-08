using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaCover;
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
            private readonly Dictionary<int, Book> _booksById;

            public RecordingBookService(IEnumerable<Book> books)
            {
                _booksById = books.ToDictionary(book => book.Id);
            }

            public List<int> RequestedBookIds { get; } = new();

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

                return ids
                    .Where(bookId => _booksById.ContainsKey(bookId))
                    .Select(bookId => _booksById[bookId])
                    .ToList();
            }

            public Book GetBook(int bookId) => throw new NotImplementedException();
            public List<Book> GetBooksByAuthor(int authorId) => throw new NotImplementedException();
            public List<Book> GetNextBooksByAuthorId(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Book> GetLastBooksByAuthorId(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Book> GetBooksByAuthorId(int authorId) => throw new NotImplementedException();
            public List<Book> GetBooksForRefresh(int authorId, List<string> foreignIds) => throw new NotImplementedException();
            public List<Book> GetBooksByFileIds(IEnumerable<int> fileIds) => throw new NotImplementedException();
            public Book AddBook(Book newBook, bool doRefresh = true) => throw new NotImplementedException();
            public Book FindBySlug(string titleSlug) => throw new NotImplementedException();
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
            public void DeleteMany(List<Book> books) => throw new NotImplementedException();
            public void SetAddOptions(IEnumerable<Book> books) => throw new NotImplementedException();
            public List<Book> GetAuthorBooksWithFiles(Author author) => throw new NotImplementedException();
            public List<Book> GetBooksForDisplay(int? authorId = null, string mediaType = null) => throw new NotImplementedException();
            public List<Book> GetBooksByBaseId(string baseBookId) => throw new NotImplementedException();
            public Book AddWantedEdition(int bookId, int editionId) => throw new NotImplementedException();
            public bool ShouldSearchForMediaType(Book book, string mediaType) => throw new NotImplementedException();
            public List<Book> GetMonitoredBooksForAuthor(int authorId, string mediaType) => throw new NotImplementedException();
            public BookBucketResource GetBookBuckets(string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null) => throw new NotImplementedException();
            public PagedBookResource GetBooksPaged(int offset, int pageSize, string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null) => throw new NotImplementedException();
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

            public void ConvertToLocalUrls(int entityId, MediaCoverEntity coverEntity, IEnumerable<NzbDrone.Core.MediaCover.MediaCover> covers, string selectedAuthorImageHash = null) => throw new NotImplementedException();
            public string GetCoverPath(int entityId, MediaCoverEntity coverEntity, MediaCoverTypes coverType, string extension, int? height = null) => throw new NotImplementedException();
            public void EnsureAuthorCovers(Author author) => throw new NotImplementedException();
            public System.Threading.Tasks.Task<EnsureImageResult> EnsureAuthorImage(Author author, NzbDrone.Core.MediaCover.MediaCover cover) => throw new NotImplementedException();
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
            var service = new DeferredCoverDownloadService(deferredCoverService, bookService, coverMapper, LogManager.GetCurrentClassLogger());

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
            var service = new DeferredCoverDownloadService(deferredCoverService, bookService, coverMapper, LogManager.GetCurrentClassLogger());

            service.HandleAsync(new ImportStageProgressEvent(ImportStage.MatchingBooks, "start") { CommandId = 9 });
            deferredCoverService.MarkBooksForCoverDownload(new[] { 1, 2, 3 });

            Assert.DoesNotThrow(() =>
                service.HandleAsync(new ImportStageProgressEvent(ImportStage.ImportComplete, "done") { CommandId = 9 }));

            Assert.That(bookService.RequestedBookIds, Is.EqualTo(new List<int> { 1, 2, 3 }));
            Assert.That(coverMapper.EnsuredBookIds, Is.EqualTo(new List<int> { 1, 3 }));
            Assert.That(deferredCoverService.GetPendingBookIds(), Is.Empty);
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
            var service = new DeferredCoverDownloadService(deferredCoverService, bookService, coverMapper, LogManager.GetCurrentClassLogger());

            service.HandleAsync(new ImportStageProgressEvent(ImportStage.MatchingBooks, "start") { CommandId = 15 });
            deferredCoverService.MarkBooksForCoverDownload(new[] { 1 });

            service.Handle(new CommandExecutedEvent(new CommandModel { Id = 15 }));
            service.HandleAsync(new ImportStageProgressEvent(ImportStage.MatchingBooks, "late stale stage") { CommandId = 15 });

            Assert.That(deferredCoverService.IsCoverDownloadDeferred, Is.False);
            Assert.That(bookService.RequestedBookIds, Is.EqualTo(new List<int> { 1 }));
            Assert.That(coverMapper.EnsuredBookIds, Is.EqualTo(new List<int> { 1 }));
            Assert.That(deferredCoverService.GetPendingBookIds(), Is.Empty);
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
            var service = new DeferredCoverDownloadService(deferredCoverService, bookService, coverMapper, LogManager.GetCurrentClassLogger());

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
