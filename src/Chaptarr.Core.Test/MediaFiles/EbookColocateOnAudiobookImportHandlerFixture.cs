using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class EbookColocateOnAudiobookImportHandlerFixture
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
            public Book AddWantedEdition(int bookId, int editionId, bool asNewVariant = false) => throw new NotImplementedException();
            public bool ShouldSearchForMediaType(Book book, string mediaType) => throw new NotImplementedException();
            public List<Book> GetMonitoredBooksForAuthor(int authorId, string mediaType) => throw new NotImplementedException();
            public BookBucketResource GetBookBuckets(string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null) => throw new NotImplementedException();
            public PagedBookResource GetBooksPaged(int offset, int pageSize, string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null) => throw new NotImplementedException();
        }

        private sealed class StubMediaFileService : IMediaFileService
        {
            public Dictionary<int, List<BookFile>> FilesByBook { get; } = new();
            public List<BookFile> Updated { get; } = new();

            public List<BookFile> GetFilesByBook(int bookId) => FilesByBook.TryGetValue(bookId, out var files) ? files : new List<BookFile>();
            public void Update(BookFile bookFile) => Updated.Add(bookFile);
            public BookFile Add(BookFile bookFile) => throw new NotImplementedException();
            public void AddMany(List<BookFile> bookFiles) => throw new NotImplementedException();
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

            public RootFolder GetBestRootFolder(string path) => GetBestRootFolder(path, _rootFolders);
            public RootFolder GetBestRootFolder(string path, List<RootFolder> allRootFolders)
            {
                return allRootFolders
                    .Where(r => r.Path.PathEquals(path) || r.Path.IsParentPath(path))
                    .OrderByDescending(r => r.Path?.Length ?? 0)
                    .FirstOrDefault();
            }

            public List<RootFolder> All() => _rootFolders;
            public List<RootFolder> AllWithSpaceStats() => throw new NotImplementedException();
            public RootFolder Add(RootFolder rootFolder) => throw new NotImplementedException();
            public RootFolder Update(RootFolder rootFolder) => throw new NotImplementedException();
            public void Remove(int id) => throw new NotImplementedException();
            public RootFolder Get(int id) => throw new NotImplementedException();
            public List<RootFolder> AllForTag(int tagId) => throw new NotImplementedException();
            public string GetBestRootFolderPath(string path) => throw new NotImplementedException();
            public string GetBestRootFolderPath(string path, List<RootFolder> allRootFolders) => throw new NotImplementedException();
        }

        private sealed class StubBookFileMover : IMoveBookFiles
        {
            private readonly Func<BookFile, string> _destinationFactory;

            public StubBookFileMover(Func<BookFile, string> destinationFactory)
            {
                _destinationFactory = destinationFactory;
            }

            public BookFileMovePlan GetOrganizeDestination(BookFile bookFile, Author author, bool moveToCanonicalAuthorFolder, RenameBatchContext renameBatchContext = null)
            {
                return new BookFileMovePlan
                {
                    CanOrganize = true,
                    DestinationPath = _destinationFactory(bookFile)
                };
            }

            public BookFile MoveBookFile(BookFile bookFile, Author author, BookFileMovePlan plan, RenameBatchContext renameBatchContext = null)
            {
                bookFile.Path = plan.DestinationPath;
                return bookFile;
            }

            public BookFile MoveBookFile(BookFile bookFile, LocalBook localBook) => throw new NotImplementedException();
            public BookFile CopyBookFile(BookFile bookFile, LocalBook localBook) => throw new NotImplementedException();
            public string GetImportDestinationPath(BookFile bookFile, LocalBook localBook) => throw new NotImplementedException();
        }

        private sealed class StubEventAggregator : IEventAggregator
        {
            public List<IEvent> Events { get; } = new();

            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
                Events.Add(@event);
            }
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
        public void should_publish_rename_events_when_backfill_moves_ebook()
        {
            var context = CreateContext(_ => "/library/Author/Book - Narrator/Book.epub");

            context.Handler.Handle(context.Event);

            Assert.That(context.MediaFileService.Updated, Is.EqualTo(new[] { context.EbookFile }));
            Assert.That(context.Events.Events.OfType<BookFileRenamedEvent>().Single().OriginalPath, Is.EqualTo("/library/Author/Book/Book.epub"));

            var authorEvent = context.Events.Events.OfType<AuthorRenamedEvent>().Single();
            Assert.That(authorEvent.RenamedFiles.Single().BookFile, Is.EqualTo(context.EbookFile));
            Assert.That(authorEvent.RenamedFiles.Single().PreviousPath, Is.EqualTo("/library/Author/Book/Book.epub"));
        }

        [Test]
        public void should_batch_author_renamed_event_when_backfill_moves_multiple_ebooks()
        {
            var context = CreateContext(
                file => $"/library/Author/Book - Narrator/{Path.GetFileName(file.Path)}",
                includeMobi: true);

            context.Handler.Handle(context.Event);

            Assert.That(context.MediaFileService.Updated, Is.EqualTo(context.EbookFiles));
            Assert.That(context.Events.Events.OfType<BookFileRenamedEvent>().Count(), Is.EqualTo(2));

            var authorEvent = context.Events.Events.OfType<AuthorRenamedEvent>().Single();
            Assert.That(authorEvent.RenamedFiles.Select(f => f.BookFile), Is.EqualTo(context.EbookFiles));
            Assert.That(authorEvent.RenamedFiles.Select(f => f.PreviousPath), Is.EqualTo(new[]
            {
                "/library/Author/Book/Book.epub",
                "/library/Author/Book/Book.mobi"
            }));
        }

        [Test]
        public void should_not_publish_rename_events_when_backfill_is_already_in_place()
        {
            var context = CreateContext(file => file.Path);

            context.Handler.Handle(context.Event);

            Assert.That(context.MediaFileService.Updated, Is.EqualTo(new[] { context.EbookFile }));
            Assert.That(context.Events.Events, Is.Empty);
        }

        private static TestContext CreateContext(Func<BookFile, string> destinationFactory, bool includeMobi = false)
        {
            var author = new Author
            {
                Id = 5,
                AudiobookRootFolderPath = "/library",
                EbookRootFolderPath = "/library"
            };

            var audiobookBook = new Book { Id = 10, AuthorId = author.Id, MediaType = BookMediaType.Audiobook, GoodreadsWorkId = "gr:100", Author = author };
            var ebookBook = new Book { Id = 20, AuthorId = author.Id, MediaType = BookMediaType.Ebook, GoodreadsWorkId = "gr:100", Author = author };
            var ebookEdition = new Edition { Id = 30, BookId = ebookBook.Id, Book = ebookBook };
            var audiobookEdition = new Edition { Id = 31, BookId = audiobookBook.Id, Book = audiobookBook };
            var ebookFile = new BookFile
            {
                Id = 40,
                Author = author,
                Edition = ebookEdition,
                EditionId = ebookEdition.Id,
                MediaType = "ebook",
                Quality = new QualityModel(Quality.EPUB),
                Path = "/library/Author/Book/Book.epub"
            };
            var ebookFiles = new List<BookFile> { ebookFile };

            if (includeMobi)
            {
                ebookFiles.Add(new BookFile
                {
                    Id = 41,
                    Author = author,
                    Edition = ebookEdition,
                    EditionId = ebookEdition.Id,
                    MediaType = "ebook",
                    Quality = new QualityModel(Quality.MOBI),
                    Path = "/library/Author/Book/Book.mobi"
                });
            }

            var imported = new BookFile
            {
                Id = 50,
                Author = author,
                Edition = audiobookEdition,
                EditionId = audiobookEdition.Id,
                MediaType = "audiobook",
                Quality = new QualityModel(Quality.M4B),
                Path = "/library/Author/Book - Narrator/Book.m4b"
            };

            var bookService = new StubBookService();
            bookService.Books.AddRange(new[] { audiobookBook, ebookBook });

            var mediaFileService = new StubMediaFileService();
            mediaFileService.FilesByBook[ebookBook.Id] = ebookFiles;

            var events = new StubEventAggregator();

            var handler = new EbookColocateOnAudiobookImportHandler(
                new StubRootFolderService(new RootFolder
                {
                    Id = 7,
                    Path = "/library",
                    FolderType = FolderType.Mixed,
                    PlaceEbooksWithAudiobooks = true
                }),
                bookService,
                mediaFileService,
                new StubBookFileMover(destinationFactory),
                DiskWith(ebookFiles.Select(f => f.Path).ToArray()),
                events,
                LogManager.GetCurrentClassLogger());

            return new TestContext(
                handler,
                new TrackImportedEvent(
                    new LocalBook { Author = author, Book = audiobookBook, Edition = audiobookEdition },
                    imported,
                    new List<BookFile>(),
                    true,
                    null),
                ebookFile,
                ebookFiles,
                mediaFileService,
                events);
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

        private sealed record TestContext(
            EbookColocateOnAudiobookImportHandler Handler,
            TrackImportedEvent Event,
            BookFile EbookFile,
            List<BookFile> EbookFiles,
            StubMediaFileService MediaFileService,
            StubEventAggregator Events);
    }
}
