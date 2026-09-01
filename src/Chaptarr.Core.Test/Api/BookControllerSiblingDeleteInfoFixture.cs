using System;
using System.Collections.Generic;
using System.Linq;
using System.IO.Abstractions;
using Chaptarr.Core.Test;
using Chaptarr.Api.V1.Books;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.AuthorStats;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Validation;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class BookControllerSiblingDeleteInfoFixture
    {
        private sealed class StubBookService : IBookService
        {
            private readonly Func<int, Book> _getBook;
            private readonly Func<int, List<Book>> _getBooksByAuthor;

            public StubBookService(Func<int, Book> getBook, Func<int, List<Book>> getBooksByAuthor)
            {
                _getBook = getBook;
                _getBooksByAuthor = getBooksByAuthor;
            }

            public Book GetBook(int bookId) => _getBook?.Invoke(bookId);
            public List<Book> GetBooksByAuthor(int authorId) => _getBooksByAuthor?.Invoke(authorId) ?? new List<Book>();

            public List<Book> GetBooks(IEnumerable<int> bookIds) => throw new NotImplementedException();
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
            public void InsertMany(List<Book> books, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction) => throw new NotImplementedException();
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

        private sealed class StubAuthorStatisticsService : IAuthorStatisticsService
        {
            private readonly Func<int, AuthorStatistics> _getStats;

            public StubAuthorStatisticsService(Func<int, AuthorStatistics> getStats)
            {
                _getStats = getStats;
            }

            public List<AuthorStatistics> AuthorStatistics() => throw new NotImplementedException();
            public AuthorStatistics AuthorStatistics(int authorId) => _getStats?.Invoke(authorId) ?? new AuthorStatistics();
            public List<AuthorStatistics> AuthorStatistics(string mediaType) => throw new NotImplementedException();
            public AuthorStatistics AuthorStatistics(int authorId, string mediaType) => throw new NotImplementedException();
            public void InvalidateAuthorCache(int authorId) => throw new NotImplementedException();
        }

        private sealed class StubMediaFileService : IMediaFileService
        {
            private readonly List<BookFile> _files;

            public StubMediaFileService(IEnumerable<BookFile> files = null)
            {
                _files = files?.ToList() ?? new List<BookFile>();
            }

            public List<BookFile> GetFilesByBooks(List<int> bookIds)
            {
                var ids = new HashSet<int>(bookIds ?? new List<int>());
                return _files
                    .Where(file => file?.Edition != null && ids.Contains(file.Edition.BookId))
                    .ToList();
            }

            public BookFile Add(BookFile bookFile) => throw new NotImplementedException();
            public void AddMany(List<BookFile> bookFiles) => throw new NotImplementedException();
            public void Update(BookFile bookFile) => throw new NotImplementedException();
            public void Update(List<BookFile> bookFiles) => throw new NotImplementedException();
            public void Delete(BookFile bookFile, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public void DeleteMany(List<BookFile> bookFiles, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public List<IFileInfo> FilterUnchangedFiles(List<IFileInfo> files, FilterFilesType filter) => throw new NotImplementedException();
            public List<BookFile> GetFilesByAuthor(int authorId) => throw new NotImplementedException();
            public List<BookFile> GetFilesByBook(int bookId) => throw new NotImplementedException();
            public List<BookFile> GetFilesByEdition(int editionId) => throw new NotImplementedException();
            public List<BookFile> GetUnmappedFiles() => throw new NotImplementedException();
            public BookFile Get(int id) => throw new NotImplementedException();
            public List<BookFile> Get(IEnumerable<int> ids) => throw new NotImplementedException();
            public List<BookFile> GetFilesWithBasePath(string path) => throw new NotImplementedException();
            public List<BookFile> GetFilesWithBasePath(string path, string mediaType) => throw new NotImplementedException();
            public List<BookFile> GetFileWithPath(List<string> path) => throw new NotImplementedException();
            public BookFile GetFileWithPath(string path) => throw new NotImplementedException();
            public void UpdateMediaInfo(List<BookFile> bookFiles) => throw new NotImplementedException();
            public List<BookFile> GetFilesByAuthorAndMediaType(int authorId, string mediaType) => throw new NotImplementedException();
        }

        private sealed class TestableBookController : Chaptarr.Api.V1.Books.BookController
        {
            public TestableBookController(IBookService bookService, IAuthorStatisticsService authorStatisticsService, Logger logger, IMediaFileService mediaFileService = null)
                : base(authorService: null,
                    bookService: bookService,
                    addBookService: null,
                    editionService: null,
                    editionSelector: null,
                    seriesBookLinkService: null,
                    authorStatisticsService: authorStatisticsService,
                    mediaFileService: mediaFileService ?? new StubMediaFileService(),
                    coverMapper: null,
                    upgradableSpecification: null,
                    signalRBroadcaster: null,
                    commandQueueManager: null,
                    eventAggregator: null,
                    metadataProfileService: null,
                    qualityProfileService: null,
                    rootFolderService: null,
                    qualityProfileExistsValidator: new QualityProfileExistsValidator(new TestQualityProfileService()),
                    metadataProfileExistsValidator: new MetadataProfileExistsValidator(new TestMetadataProfileService()),
                    logger: logger)
            {
            }
        }

        private static BookFile BuildFile(int bookId, string path, long size)
        {
            return new BookFile
            {
                Path = path,
                Size = size,
                Edition = new Edition { Id = bookId * 10, BookId = bookId }
            };
        }

        [Test]
        public void should_return_404_when_book_missing()
        {
            var controller = new TestableBookController(
                new StubBookService(getBook: _ => null, getBooksByAuthor: _ => new List<Book>()),
                new StubAuthorStatisticsService(getStats: _ => new AuthorStatistics()),
                LogManager.GetCurrentClassLogger());

            var result = controller.GetBookSiblings(123);

            Assert.That(result.Result, Is.InstanceOf<NotFoundResult>());
        }

        [Test]
        public void should_return_empty_when_no_siblings_exist()
        {
            var book = new Book
            {
                Id = 1,
                MediaType = BookMediaType.Audiobook,
                BaseBookId = "hc:loss-protocol",
                HardcoverBookId = "hc:loss-protocol"
            };
            book.AuthorId = 10;

            var controller = new TestableBookController(
                new StubBookService(getBook: id => id == 1 ? book : null, getBooksByAuthor: _ => new List<Book> { book }),
                new StubAuthorStatisticsService(getStats: _ => new AuthorStatistics()),
                LogManager.GetCurrentClassLogger());

            var result = controller.GetBookSiblings(1);

            Assert.That(result.Result, Is.Null);
            Assert.That(result.Value, Is.Not.Null);
            Assert.That(result.Value.SiblingMediaType, Is.EqualTo("ebook"));
            Assert.That(result.Value.BookIds, Is.Empty);
            Assert.That(result.Value.CurrentBook.BookId, Is.EqualTo(1));
            Assert.That(result.Value.Siblings, Is.Empty);
            Assert.That(result.Value.Statistics.BookFileCount, Is.EqualTo(0));
            Assert.That(result.Value.Statistics.SizeOnDisk, Is.EqualTo(0));
        }

        [Test]
        public void should_return_single_sibling_with_file_details_and_aggregated_stats()
        {
            var book = new Book
            {
                Id = 1,
                Title = "1984",
                MediaType = BookMediaType.Audiobook,
                BaseBookId = "hc:1984",
                HardcoverBookId = "hc:1984"
            };
            book.AuthorId = 10;

            var sibling = new Book
            {
                Id = 200,
                Title = "1984",
                MediaType = BookMediaType.Ebook,
                BaseBookId = "hc:1984",
                HardcoverBookId = "hc:1984"
            };
            sibling.AuthorId = 10;

            var mediaFileService = new StubMediaFileService(new[]
            {
                BuildFile(1, "/audiobooks/1984.m4b", 5000),
                BuildFile(200, "/ebooks/1984.epub", 1000),
                BuildFile(200, "/ebooks/1984.pdf", 234)
            });

            var controller = new TestableBookController(
                new StubBookService(getBook: id => id == 1 ? book : null, getBooksByAuthor: _ => new List<Book> { book, sibling }),
                new StubAuthorStatisticsService(getStats: _ => new AuthorStatistics()),
                LogManager.GetCurrentClassLogger(),
                mediaFileService);

            var result = controller.GetBookSiblings(1);

            Assert.That(result.Value.SiblingMediaType, Is.EqualTo("ebook"));
            Assert.That(result.Value.BookIds, Is.EqualTo(new List<int> { 200 }));
            Assert.That(result.Value.CurrentBook.Files.Select(file => file.Path), Is.EqualTo(new[] { "/audiobooks/1984.m4b" }));
            Assert.That(result.Value.Siblings.Single().Files.Select(file => file.Path), Is.EqualTo(new[] { "/ebooks/1984.epub", "/ebooks/1984.pdf" }));
            Assert.That(result.Value.Statistics.BookFileCount, Is.EqualTo(2));
            Assert.That(result.Value.Statistics.SizeOnDisk, Is.EqualTo(1234));
            Assert.That(result.Value.AudiobookCount, Is.EqualTo(1));
            Assert.That(result.Value.EbookCount, Is.EqualTo(1));
        }

        [Test]
        public void should_return_all_work_matched_clones_and_cross_format_siblings()
        {
            var book = new Book
            {
                Id = 1,
                Title = "Kindle Kobo PDF",
                MediaType = BookMediaType.Audiobook,
                BaseBookId = "hc:kindle-kobo-pdf",
                HardcoverBookId = "hc:kindle-kobo-pdf"
            };
            book.AuthorId = 10;

            var ebook1 = new Book { Id = 200, Title = "Kindle Kobo PDF", MediaType = BookMediaType.Ebook, BaseBookId = "hc:kindle-kobo-pdf", HardcoverBookId = "hc:kindle-kobo-pdf" };
            ebook1.AuthorId = 10;
            var ebook2 = new Book { Id = 201, Title = "Kindle Kobo PDF", MediaType = BookMediaType.Ebook, BaseBookId = "hc:kindle-kobo-pdf", HardcoverBookId = "hc:kindle-kobo-pdf" };
            ebook2.AuthorId = 10;
            var ebook3 = new Book { Id = 202, Title = "Kindle Kobo PDF", MediaType = BookMediaType.Ebook, BaseBookId = "hc:kindle-kobo-pdf", HardcoverBookId = "hc:kindle-kobo-pdf" };
            ebook3.AuthorId = 10;

            var audiobookClone = new Book { Id = 2, Title = "Kindle Kobo PDF", MediaType = BookMediaType.Audiobook, BaseBookId = "hc:kindle-kobo-pdf", HardcoverBookId = "hc:kindle-kobo-pdf" };
            audiobookClone.AuthorId = 10;

            var mediaFileService = new StubMediaFileService(new[]
            {
                BuildFile(1, "/audiobooks/original.m4b", 100),
                BuildFile(2, "/audiobooks/clone.m4b", 10),
                BuildFile(200, "/ebooks/kindle.azw3", 20),
                BuildFile(201, "/ebooks/kobo.epub", 30)
            });

            var controller = new TestableBookController(
                new StubBookService(
                    getBook: id => id == 1 ? book : null,
                    getBooksByAuthor: _ => new List<Book> { book, ebook3, ebook1, audiobookClone, ebook2 }),
                new StubAuthorStatisticsService(getStats: _ => new AuthorStatistics()),
                LogManager.GetCurrentClassLogger(),
                mediaFileService);

            var result = controller.GetBookSiblings(1);

            Assert.That(result.Value.SiblingMediaType, Is.EqualTo("mixed"));
            Assert.That(result.Value.BookIds, Is.EqualTo(new List<int> { 2, 200, 201, 202 }));
            Assert.That(result.Value.Siblings.Select(sibling => sibling.BookId), Is.EqualTo(new[] { 2, 200, 201, 202 }));
            Assert.That(result.Value.Statistics.BookFileCount, Is.EqualTo(3));
            Assert.That(result.Value.Statistics.SizeOnDisk, Is.EqualTo(60));
            Assert.That(result.Value.AudiobookCount, Is.EqualTo(2));
            Assert.That(result.Value.EbookCount, Is.EqualTo(3));
        }
    }
}
