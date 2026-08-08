using System.Collections.Generic;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class BookFileEventHandlersFixture
    {
        private sealed class RecordingBookDurationService : IBookDurationService
        {
            public readonly List<int> UpdatedBookIds = new List<int>();

            public void UpdateBookDuration(int bookId)
            {
                UpdatedBookIds.Add(bookId);
            }

            public void UpdateBookDuration(Book book, List<BookFile> bookFiles = null)
            {
                if (book != null)
                {
                    UpdatedBookIds.Add(book.Id);
                }
            }

            public void UpdateAllBookDurations()
            {
            }
        }

        private sealed class StubBookService : IBookService
        {
            public Book GetBook(int bookId) => throw new System.NotImplementedException();
            public List<Book> GetBooks(IEnumerable<int> bookIds) => throw new System.NotImplementedException();
            public List<Book> GetBooksByAuthor(int authorId) => throw new System.NotImplementedException();
            public List<Book> GetNextBooksByAuthorId(IEnumerable<int> authorIds) => throw new System.NotImplementedException();
            public List<Book> GetLastBooksByAuthorId(IEnumerable<int> authorIds) => throw new System.NotImplementedException();
            public List<Book> GetBooksByAuthorId(int authorId) => throw new System.NotImplementedException();
            public List<Book> GetBooksForRefresh(int authorId, List<string> foreignIds) => throw new System.NotImplementedException();
            public List<Book> GetBooksByFileIds(IEnumerable<int> fileIds) => throw new System.NotImplementedException();
            public Book AddBook(Book newBook, bool doRefresh = true) => throw new System.NotImplementedException();
            public Book FindBySlug(string titleSlug) => throw new System.NotImplementedException();
            public Book FindByTitle(int authorId, string title) => throw new System.NotImplementedException();
            public Book FindByTitleInexact(int authorId, string title) => throw new System.NotImplementedException();
            public Book FindByGoodreadsId(string goodreadsId) => throw new System.NotImplementedException();
            public Book FindByProviderId(string provider, string providerId) => throw new System.NotImplementedException();
            public Book FindByProviderId(string provider, string providerId, BookMediaType mediaType) => throw new System.NotImplementedException();
            public List<Book> FindAllByProviderId(string provider, string providerId, BookMediaType mediaType) => throw new System.NotImplementedException();
            public Book FindByISBN(string isbn) => throw new System.NotImplementedException();
            public Book FindByASIN(string asin) => throw new System.NotImplementedException();
            public List<Book> GetCandidates(int authorId, string title) => throw new System.NotImplementedException();
            public void DeleteBook(int bookId, bool deleteFiles, bool addImportListExclusion = false, bool applyToBothFormats = false) => throw new System.NotImplementedException();
            public List<Book> GetAllBooks() => throw new System.NotImplementedException();
            public Book UpdateBook(Book book) => throw new System.NotImplementedException();
            public void SetBookMonitored(int bookId, bool monitored) => throw new System.NotImplementedException();
            public void SetMonitored(IEnumerable<int> ids, bool monitored) => throw new System.NotImplementedException();
            public void SetMonitoredForMediaType(IEnumerable<int> ids, string mediaType, bool monitored) => throw new System.NotImplementedException();
            public void UpdateLastSearchTime(List<Book> books) => throw new System.NotImplementedException();
            public PagingSpec<Book> BooksWithoutFiles(PagingSpec<Book> pagingSpec) => throw new System.NotImplementedException();
            public List<Book> BooksBetweenDates(System.DateTime start, System.DateTime end, bool includeUnmonitored) => throw new System.NotImplementedException();
            public List<Book> AuthorBooksBetweenDates(Author author, System.DateTime start, System.DateTime end, bool includeUnmonitored) => throw new System.NotImplementedException();
            public void InsertMany(List<Book> books) => throw new System.NotImplementedException();
            public void InsertMany(List<Book> books, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction) => throw new System.NotImplementedException();
            public void UpdateMany(List<Book> books) => throw new System.NotImplementedException();
            public void DeleteMany(List<Book> books) => throw new System.NotImplementedException();
            public void SetAddOptions(IEnumerable<Book> books) => throw new System.NotImplementedException();
            public List<Book> GetAuthorBooksWithFiles(Author author) => throw new System.NotImplementedException();
            public List<Book> GetBooksForDisplay(int? authorId = null, string mediaType = null) => throw new System.NotImplementedException();
            public List<Book> GetBooksByBaseId(string baseBookId) => throw new System.NotImplementedException();
            public Book AddWantedEdition(int bookId, int editionId) => throw new System.NotImplementedException();
            public bool ShouldSearchForMediaType(Book book, string mediaType) => throw new System.NotImplementedException();
            public List<Book> GetMonitoredBooksForAuthor(int authorId, string mediaType) => throw new System.NotImplementedException();
            public BookBucketResource GetBookBuckets(string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null) => throw new System.NotImplementedException();
            public PagedBookResource GetBooksPaged(int offset, int pageSize, string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null) => throw new System.NotImplementedException();
        }

        [Test]
        public void should_update_duration_when_book_file_is_deleted()
        {
            var durationService = new RecordingBookDurationService();
            var sut = new BookFileEventHandlers(durationService, new StubBookService(), LogManager.GetLogger("test"));

            var message = new BookFileDeletedEvent(
                new BookFile
                {
                    Path = "/audiobooks/audiobooks/Joe Abercrombie/A Little Hatred - Steven Pacey/A Little Hatred.m4b",
                    Edition = new Edition
                    {
                        Book = new Book
                        {
                            Id = 4634,
                            Title = "A Little Hatred"
                        }
                    }
                },
                DeleteMediaFileReason.MissingFromDisk);

            sut.Handle(message);

            Assert.That(durationService.UpdatedBookIds, Is.EqualTo(new[] { 4634 }));
        }
    }
}
