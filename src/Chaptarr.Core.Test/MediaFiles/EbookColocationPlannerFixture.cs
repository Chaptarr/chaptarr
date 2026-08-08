using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;
using NUnit.Framework;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class EbookColocationPlannerFixture
    {
        private sealed class StubBookService : IBookService
        {
            public List<Book> Books { get; } = new();

            public List<Book> GetBooksByAuthorId(int authorId) => Books.Where(b => b.AuthorId == authorId).ToList();
            public Book GetBook(int bookId) => throw new NotImplementedException();
            public List<Book> GetBooks(IEnumerable<int> bookIds) => throw new NotImplementedException();
            public List<Book> GetBooksByAuthor(int authorId) => throw new NotImplementedException();
            public List<Book> GetNextBooksByAuthorId(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Book> GetLastBooksByAuthorId(IEnumerable<int> authorIds) => throw new NotImplementedException();
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
            public NzbDrone.Core.Datastore.PagingSpec<Book> BooksWithoutFiles(NzbDrone.Core.Datastore.PagingSpec<Book> pagingSpec) => throw new NotImplementedException();
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

        private sealed class StubMediaFileService : IMediaFileService
        {
            public Dictionary<int, List<BookFile>> FilesByBook { get; } = new();

            public List<BookFile> GetFilesByBook(int bookId) => FilesByBook.TryGetValue(bookId, out var files) ? files : new List<BookFile>();
            public BookFile Add(BookFile bookFile) => throw new NotImplementedException();
            public void AddMany(List<BookFile> bookFiles) => throw new NotImplementedException();
            public void Update(BookFile bookFile) => throw new NotImplementedException();
            public void Update(List<BookFile> bookFiles) => throw new NotImplementedException();
            public void Delete(BookFile bookFile, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public void DeleteMany(List<BookFile> bookFiles, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public List<IFileInfo> FilterUnchangedFiles(List<IFileInfo> files, FilterFilesType filter) => throw new NotImplementedException();
            public List<BookFile> GetFilesByAuthor(int authorId) => throw new NotImplementedException();
            public List<BookFile> GetFilesByBooks(List<int> bookIds) => throw new NotImplementedException();
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

        private sealed class StubRootFolderService : IRootFolderService
        {
            private readonly List<RootFolder> _rootFolders;

            public StubRootFolderService(params RootFolder[] rootFolders)
            {
                _rootFolders = rootFolders.ToList();
            }

            public List<RootFolder> All() => _rootFolders;
            public RootFolder GetBestRootFolder(string path) => GetBestRootFolder(path, _rootFolders);
            public RootFolder GetBestRootFolder(string path, List<RootFolder> allRootFolders)
            {
                return allRootFolders
                    .Where(r => r.Path.PathEquals(path) || r.Path.IsParentPath(path))
                    .OrderByDescending(r => r.Path?.Length ?? 0)
                    .FirstOrDefault();
            }

            public List<RootFolder> AllWithSpaceStats() => throw new NotImplementedException();
            public RootFolder Add(RootFolder rootFolder) => throw new NotImplementedException();
            public RootFolder Update(RootFolder rootFolder) => throw new NotImplementedException();
            public void Remove(int id) => throw new NotImplementedException();
            public RootFolder Get(int id) => throw new NotImplementedException();
            public List<RootFolder> AllForTag(int tagId) => throw new NotImplementedException();
            public string GetBestRootFolderPath(string path) => throw new NotImplementedException();
            public string GetBestRootFolderPath(string path, List<RootFolder> allRootFolders) => throw new NotImplementedException();
        }

        private class DiskProviderProxy : DispatchProxy
        {
            public HashSet<string> ExistingFiles { get; } = new(PathEqualityComparer.Instance);

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IDiskProvider.FileExists) &&
                    args?.Length >= 1 &&
                    args[0] is string filePath)
                {
                    return ExistingFiles.Contains(filePath);
                }

                throw new NotImplementedException($"Test proxy does not implement IDiskProvider.{targetMethod?.Name}");
            }
        }

        [Test]
        public void should_keep_current_primary_candidate_when_audiobook_folders_are_remapped()
        {
            var root = new RootFolder
            {
                Id = 7,
                Path = "/library",
                FolderType = FolderType.Mixed,
                PlaceEbooksWithAudiobooks = true
            };

            var ebookBook = new Book { Id = 1, AuthorId = 5, MediaType = BookMediaType.Ebook, GoodreadsWorkId = "gr:100" };
            var audiobookA = new Book { Id = 10, AuthorId = 5, MediaType = BookMediaType.Audiobook, GoodreadsWorkId = "gr:100" };
            var audiobookB = new Book { Id = 20, AuthorId = 5, MediaType = BookMediaType.Audiobook, GoodreadsWorkId = "gr:100" };

            var bookService = new StubBookService();
            bookService.Books.AddRange(new[] { ebookBook, audiobookA, audiobookB });

            var mediaFileService = new StubMediaFileService();
            mediaFileService.FilesByBook[audiobookA.Id] = new List<BookFile>
            {
                new BookFile { Id = 101, Path = "/library/Author/Book - Narrator A/Book.m4b" }
            };
            mediaFileService.FilesByBook[audiobookB.Id] = new List<BookFile>
            {
                new BookFile { Id = 201, Path = "/library/Author/Book - Narrator B/Book.m4b" }
            };

            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var diskProxy = (DiskProviderProxy)(object)diskProvider;
            diskProxy.ExistingFiles.Add("/library/Author/Book - Narrator A/Book.m4b");
            diskProxy.ExistingFiles.Add("/library/Author/Book - Narrator B/Book.m4b");

            var planner = new EbookColocationPlanner(
                bookService,
                mediaFileService,
                new StubRootFolderService(root),
                diskProvider);

            var batchContext = new RenameBatchContext();
            batchContext.AddAudiobookFolderRemap("/library/Author/Book - Narrator A", "/library/Author/Book [2026] - Narrator A");
            batchContext.AddAudiobookFolderRemap("/library/Author/Book - Narrator B", "/library/Author/Book [2026] - Narrator B");

            var ebookFile = new BookFile
            {
                Id = 301,
                Path = "/library/Author/Book - Narrator B/Book.epub",
                Quality = new QualityModel(Quality.EPUB),
                MediaType = "ebook"
            };

            var author = new Author
            {
                Id = 5,
                EbookRootFolderPath = "/library"
            };

            var edition = new Edition
            {
                Id = 401,
                Book = ebookBook,
                BookId = ebookBook.Id
            };

            var plan = planner.Plan(ebookFile, author, edition, "Book.epub", batchContext);

            Assert.That(plan.Applies, Is.True);
            Assert.That(plan.PrimaryPath, Is.EqualTo("/library/Author/Book [2026] - Narrator B/Book.epub"));
            Assert.That(plan.ReplicaPaths, Is.EqualTo(new[] { "/library/Author/Book [2026] - Narrator A/Book.epub" }));
        }

        [Test]
        public void should_request_replica_cleanup_when_mixed_root_feature_is_disabled()
        {
            var root = new RootFolder
            {
                Id = 7,
                Path = "/library",
                FolderType = FolderType.Mixed,
                PlaceEbooksWithAudiobooks = false
            };

            var ebookBook = new Book { Id = 1, AuthorId = 5, MediaType = BookMediaType.Ebook, GoodreadsWorkId = "gr:100" };
            var planner = new EbookColocationPlanner(
                new StubBookService(),
                new StubMediaFileService(),
                new StubRootFolderService(root),
                DiskWith());

            var plan = planner.Plan(
                EbookFile("/library/Author/Book/Book.epub"),
                Author(),
                EditionFor(ebookBook),
                "Book.epub");

            Assert.That(plan.Applies, Is.False);
            Assert.That(plan.Reason, Is.EqualTo(EbookColocationSkipReason.RootNotMixedOrDisabled));
            Assert.That(plan.ShouldCleanupReplicas, Is.True);
        }

        [Test]
        public void should_request_replica_cleanup_when_no_audiobook_folders_exist()
        {
            var root = MixedRoot();
            var ebookBook = new Book { Id = 1, AuthorId = 5, MediaType = BookMediaType.Ebook, GoodreadsWorkId = "gr:100" };
            var audiobookBook = new Book { Id = 10, AuthorId = 5, MediaType = BookMediaType.Audiobook, GoodreadsWorkId = "gr:100" };

            var bookService = new StubBookService();
            bookService.Books.AddRange(new[] { ebookBook, audiobookBook });

            var planner = new EbookColocationPlanner(
                bookService,
                new StubMediaFileService(),
                new StubRootFolderService(root),
                DiskWith());

            var plan = planner.Plan(
                EbookFile("/library/Author/Book/Book.epub"),
                Author(),
                EditionFor(ebookBook),
                "Book.epub");

            Assert.That(plan.Applies, Is.False);
            Assert.That(plan.Reason, Is.EqualTo(EbookColocationSkipReason.NoAudiobookFolders));
            Assert.That(plan.ShouldCleanupReplicas, Is.True);
        }

        [Test]
        public void should_ignore_audiobook_candidates_outside_the_mixed_root()
        {
            var mixedRoot = MixedRoot();
            var otherRoot = new RootFolder { Id = 8, Path = "/other", FolderType = FolderType.Mixed, PlaceEbooksWithAudiobooks = true };
            var ebookBook = new Book { Id = 1, AuthorId = 5, MediaType = BookMediaType.Ebook, GoodreadsWorkId = "gr:100" };
            var audiobookBook = new Book { Id = 10, AuthorId = 5, MediaType = BookMediaType.Audiobook, GoodreadsWorkId = "gr:100" };

            var bookService = new StubBookService();
            bookService.Books.AddRange(new[] { ebookBook, audiobookBook });

            var mediaFileService = new StubMediaFileService();
            mediaFileService.FilesByBook[audiobookBook.Id] = new List<BookFile>
            {
                new BookFile { Id = 101, Path = "/other/Author/Book/Book.m4b" }
            };

            var planner = new EbookColocationPlanner(
                bookService,
                mediaFileService,
                new StubRootFolderService(mixedRoot, otherRoot),
                DiskWith("/other/Author/Book/Book.m4b"));

            var plan = planner.Plan(
                EbookFile("/library/Author/Book/Book.epub"),
                Author(),
                EditionFor(ebookBook),
                "Book.epub");

            Assert.That(plan.Applies, Is.False);
            Assert.That(plan.Reason, Is.EqualTo(EbookColocationSkipReason.NoAudiobookFolders));
            Assert.That(plan.ShouldCleanupReplicas, Is.True);
        }

        [Test]
        public void should_ignore_audiobook_candidates_missing_on_disk()
        {
            var root = MixedRoot();
            var ebookBook = new Book { Id = 1, AuthorId = 5, MediaType = BookMediaType.Ebook, GoodreadsWorkId = "gr:100" };
            var audiobookBook = new Book { Id = 10, AuthorId = 5, MediaType = BookMediaType.Audiobook, GoodreadsWorkId = "gr:100" };

            var bookService = new StubBookService();
            bookService.Books.AddRange(new[] { ebookBook, audiobookBook });

            var mediaFileService = new StubMediaFileService();
            mediaFileService.FilesByBook[audiobookBook.Id] = new List<BookFile>
            {
                new BookFile { Id = 101, Path = "/library/Author/Book/Book.m4b" }
            };

            var planner = new EbookColocationPlanner(
                bookService,
                mediaFileService,
                new StubRootFolderService(root),
                DiskWith());

            var plan = planner.Plan(
                EbookFile("/library/Author/Book/Book.epub"),
                Author(),
                EditionFor(ebookBook),
                "Book.epub");

            Assert.That(plan.Applies, Is.False);
            Assert.That(plan.Reason, Is.EqualTo(EbookColocationSkipReason.NoAudiobookFolders));
            Assert.That(plan.ShouldCleanupReplicas, Is.True);
        }

        [Test]
        public void should_choose_lowest_book_then_file_id_as_fallback_primary()
        {
            var root = MixedRoot();
            var ebookBook = new Book { Id = 1, AuthorId = 5, MediaType = BookMediaType.Ebook, GoodreadsWorkId = "gr:100" };
            var audiobookB = new Book { Id = 20, AuthorId = 5, MediaType = BookMediaType.Audiobook, GoodreadsWorkId = "gr:100" };
            var audiobookA = new Book { Id = 10, AuthorId = 5, MediaType = BookMediaType.Audiobook, GoodreadsWorkId = "gr:100" };

            var bookService = new StubBookService();
            bookService.Books.AddRange(new[] { ebookBook, audiobookB, audiobookA });

            var mediaFileService = new StubMediaFileService();
            mediaFileService.FilesByBook[audiobookA.Id] = new List<BookFile>
            {
                new BookFile { Id = 102, Path = "/library/Author/Book - Narrator A2/Book.m4b" },
                new BookFile { Id = 101, Path = "/library/Author/Book - Narrator A1/Book.m4b" }
            };
            mediaFileService.FilesByBook[audiobookB.Id] = new List<BookFile>
            {
                new BookFile { Id = 50, Path = "/library/Author/Book - Narrator B/Book.m4b" }
            };

            var planner = new EbookColocationPlanner(
                bookService,
                mediaFileService,
                new StubRootFolderService(root),
                DiskWith(
                    "/library/Author/Book - Narrator A1/Book.m4b",
                    "/library/Author/Book - Narrator A2/Book.m4b",
                    "/library/Author/Book - Narrator B/Book.m4b"));

            var plan = planner.Plan(
                EbookFile("/library/Author/Unmatched/Book.epub"),
                Author(),
                EditionFor(ebookBook),
                "Book.epub");

            Assert.That(plan.Applies, Is.True);
            Assert.That(plan.PrimaryPath, Is.EqualTo("/library/Author/Book - Narrator A1/Book.epub"));
            Assert.That(plan.ReplicaPaths, Is.EquivalentTo(new[]
            {
                "/library/Author/Book - Narrator A2/Book.epub",
                "/library/Author/Book - Narrator B/Book.epub"
            }));
        }

        private static RootFolder MixedRoot()
        {
            return new RootFolder
            {
                Id = 7,
                Path = "/library",
                FolderType = FolderType.Mixed,
                PlaceEbooksWithAudiobooks = true
            };
        }

        private static Author Author()
        {
            return new Author
            {
                Id = 5,
                EbookRootFolderPath = "/library"
            };
        }

        private static Edition EditionFor(Book book)
        {
            return new Edition
            {
                Id = 401,
                Book = book,
                BookId = book.Id
            };
        }

        private static BookFile EbookFile(string path)
        {
            return new BookFile
            {
                Id = 301,
                Path = path,
                Quality = new QualityModel(Quality.EPUB),
                MediaType = "ebook"
            };
        }

        private static IDiskProvider DiskWith(params string[] existingFiles)
        {
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var diskProxy = (DiskProviderProxy)(object)diskProvider;

            foreach (var path in existingFiles)
            {
                diskProxy.ExistingFiles.Add(path);
            }

            return diskProvider;
        }
    }
}
