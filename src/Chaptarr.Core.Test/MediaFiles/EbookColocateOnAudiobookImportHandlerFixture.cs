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
using NzbDrone.Core.Organizer;
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
            public List<Book> GetImportListBookIdentities() => GetAllBooks();

            public List<Book> Books { get; } = new();

            public int AuthorReads { get; private set; }
            public List<Book> GetBooksByAuthorId(int authorId)
            {
                AuthorReads++;
                return Books.Where(b => b.AuthorId == authorId).ToList();
            }
            public Book GetBook(int bookId) => throw new NotImplementedException();
            public List<Book> GetBooks(IEnumerable<int> bookIds) => throw new NotImplementedException();
            public List<Book> GetBooksByAuthor(int authorId, bool onlyMonitoredEditions = false) => throw new NotImplementedException();
            public List<Book> GetNextBooksByAuthorId(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Book> GetLastBooksByAuthorId(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Book> GetBooksForRefresh(int authorId, List<string> foreignIds) => throw new NotImplementedException();
            public List<Book> GetBooksByFileIds(IEnumerable<int> fileIds) => throw new NotImplementedException();
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
            public void InsertMany(List<Book> books, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction) => throw new NotImplementedException();
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

        private sealed class StubMediaFileService : IMediaFileService
        {
            public void ValidateReplacementFiles(System.Collections.Generic.List<BookFile> replacedFiles,
                System.Collections.Generic.IReadOnlyCollection<NzbDrone.Core.MediaFiles.BookImport.Manual.ManualImportReplacementFile> confirmedFiles)
                => throw new System.NotSupportedException("This test adapter does not support replacement validation.");

            public void ReplaceMany(System.Collections.Generic.List<BookFile> bookFiles, System.Collections.Generic.List<BookFile> replacedFiles,
                DeleteMediaFileReason reason, System.Collections.Generic.IReadOnlyCollection<NzbDrone.Core.MediaFiles.BookImport.Manual.ManualImportReplacementFile> confirmedFiles,
                System.Action<System.Action<System.Data.IDbConnection, System.Data.IDbTransaction>> persist)
                => throw new System.NotSupportedException("This test adapter does not support transactional replacement.");

            public Dictionary<int, List<BookFile>> FilesByBook { get; } = new();
            public List<BookFile> Updated { get; } = new();

            public int SingleReads { get; private set; }
            public int BulkReads { get; private set; }
            public List<BookFile> GetFilesByBook(int bookId)
            {
                SingleReads++;
                return FilesByBook.TryGetValue(bookId, out var files) ? files : new List<BookFile>();
            }
            public void Update(BookFile bookFile) => Updated.Add(bookFile);
            public BookFile Add(BookFile bookFile) => throw new NotImplementedException();
            public void AddMany(List<BookFile> bookFiles) => throw new NotImplementedException();
            public (int Relinked, int Reclassified) RelinkAndClassifyFilesByBook(int bookId, int? editionId, NzbDrone.Core.Books.BookMediaType mediaType, NzbDrone.Core.MediaFiles.AudioProductionCorrection correction, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction) => throw new System.NotSupportedException();

            public void Update(List<BookFile> bookFiles) => throw new NotImplementedException();
            public void Delete(BookFile bookFile, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public void DeleteMany(List<BookFile> bookFiles, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public List<IFileInfo> FilterUnchangedFiles(List<IFileInfo> files, FilterFilesType filter) => throw new NotImplementedException();
            public List<BookFile> GetFilesByAuthor(int authorId) => throw new NotImplementedException();
            public List<BookFile> GetFilesByBooks(List<int> bookIds)
            {
                BulkReads++;
                return bookIds.SelectMany(id => FilesByBook.GetValueOrDefault(id) ?? new List<BookFile>()).ToList();
            }
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
            public Func<BookFile, Author, RenameBatchContext, BookFileMovePlan> PlanFactory { get; set; }
            public List<BookFileMovePlan> Plans { get; } = new();
            public List<BookFile> Moved { get; } = new();

            public StubBookFileMover(Func<BookFile, string> destinationFactory)
            {
                _destinationFactory = destinationFactory;
            }

            public BookFileMovePlan GetOrganizeDestination(BookFile bookFile, Author author, bool moveToCanonicalAuthorFolder, RenameBatchContext renameBatchContext = null)
            {
                var plan = PlanFactory?.Invoke(bookFile, author, renameBatchContext) ?? new BookFileMovePlan
                {
                    CanOrganize = true,
                    DestinationPath = _destinationFactory(bookFile),
                    ReplicaPaths = new List<string>()
                };
                Plans.Add(plan);
                return plan;
            }

            public BookFile MoveBookFile(BookFile bookFile, Author author, BookFileMovePlan plan, RenameBatchContext renameBatchContext = null)
            {
                Moved.Add(bookFile);
                bookFile.Path = plan.DestinationPath;
                return bookFile;
            }

            public BookFile MoveBookFile(BookFile bookFile, LocalBook localBook) => throw new NotImplementedException();
            public BookFile CopyBookFile(BookFile bookFile, LocalBook localBook) => throw new NotImplementedException();
            public string GetImportDestinationPath(BookFile bookFile, LocalBook localBook) => throw new NotImplementedException();
            public bool CompleteImportReplicas(BookFile bookFile, LocalBook localBook) => false;
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

        private class NamingProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod.Name switch
                {
                    nameof(INamingConfigService.GetConfig) => new NamingConfig(),
                    nameof(IBuildFileNames.BuildBookFileName) => Path.Combine("Renamed", "Book"),
                    _ => throw new NotImplementedException(targetMethod.Name)
                };
            }
        }

        [Test]
        public void should_publish_rename_events_when_backfill_moves_ebook()
        {
            var context = CreateContext(_ => @"C:\library\Author\Book - Narrator\Book.epub".AsOsAgnostic());

            context.Handler.Handle(context.Event);

            Assert.That(context.MediaFileService.Updated, Is.EqualTo(new[] { context.EbookFile }));
            Assert.That(context.Events.Events.OfType<BookFileRenamedEvent>().Single().OriginalPath, Is.EqualTo(@"C:\library\Author\Book\Book.epub".AsOsAgnostic()));

            var authorEvent = context.Events.Events.OfType<AuthorRenamedEvent>().Single();
            Assert.That(authorEvent.RenamedFiles.Single().BookFile, Is.EqualTo(context.EbookFile));
            Assert.That(authorEvent.RenamedFiles.Single().PreviousPath, Is.EqualTo(@"C:\library\Author\Book\Book.epub".AsOsAgnostic()));
        }

        [Test]
        public void should_batch_author_renamed_event_when_backfill_moves_multiple_ebooks()
        {
            var context = CreateContext(
                file => Path.Combine(@"C:\library\Author\Book - Narrator".AsOsAgnostic(), Path.GetFileName(file.Path)),
                includeMobi: true);

            context.Handler.Handle(context.Event);

            Assert.That(context.MediaFileService.Updated, Is.EqualTo(context.EbookFiles));
            Assert.That(context.Events.Events.OfType<BookFileRenamedEvent>().Count(), Is.EqualTo(2));

            var authorEvent = context.Events.Events.OfType<AuthorRenamedEvent>().Single();
            Assert.That(authorEvent.RenamedFiles.Select(f => f.BookFile), Is.EqualTo(context.EbookFiles));
            Assert.That(authorEvent.RenamedFiles.Select(f => f.PreviousPath), Is.EqualTo(new[]
            {
                @"C:\library\Author\Book\Book.epub".AsOsAgnostic(),
                @"C:\library\Author\Book\Book.mobi".AsOsAgnostic()
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

        [TestCase(FolderType.Ebook, false, false)]
        [TestCase(FolderType.Mixed, false, false)]
        [TestCase(FolderType.Mixed, true, false)]
        [TestCase(FolderType.Mixed, true, true)]
        public void should_only_move_ebooks_when_the_real_planner_applies(FolderType ebookRootType, bool colocationEnabled, bool sameRoot)
        {
            var ebookRoot = sameRoot ? null : new RootFolder
            {
                Id = 8,
                Path = @"C:\ebooks".AsOsAgnostic(),
                FolderType = ebookRootType,
                PlaceEbooksWithAudiobooks = colocationEnabled
            };
            var context = CreateContext(file => file.Path, includeMobi: true, ebookRoot: ebookRoot);
            var originalPaths = context.EbookFiles.Select(file => file.Path).ToList();
            var planner = new EbookColocationPlanner(context.Books, context.MediaFileService, context.Roots, context.Disk);
            var mover = new BookFileMovingService(
                null, null, DispatchProxy.Create<IBuildFileNames, NamingProxy>(), null,
                DispatchProxy.Create<INamingConfigService, NamingProxy>(), planner, null, context.Disk,
                null, null, null, context.Events, null, null, LogManager.GetCurrentClassLogger());
            context.Mover.PlanFactory = (file, author, batch) => mover.GetOrganizeDestination(file, author, false, batch);

            context.Handler.Handle(context.Event);

            Assert.That(context.Mover.Plans, Has.Count.EqualTo(2), "Both ebooks must reach the real planner");
            Assert.That(context.Mover.Plans.All(plan => plan.CanOrganize), Is.True);
            if (sameRoot)
            {
                Assert.That(context.Mover.Plans.All(plan => plan.ReplicaPaths is { Count: 0 }), Is.True);
                Assert.That(context.Mover.Moved, Is.EqualTo(context.EbookFiles));
                Assert.That(context.MediaFileService.Updated, Is.EqualTo(context.EbookFiles));
                Assert.That(context.EbookFiles.All(file => Path.GetDirectoryName(file.Path) == Path.GetDirectoryName(context.Event.ImportedBooks.First().Path)), Is.True);
                Assert.That(context.Events.Events.OfType<BookFileRenamedEvent>().Count(), Is.EqualTo(2));
                Assert.That(context.Events.Events.OfType<AuthorRenamedEvent>().Count(), Is.EqualTo(1));
            }
            else
            {
                Assert.That(context.Mover.Plans.All(plan => plan.ReplicaPaths == null), Is.True);
                Assert.That(context.Mover.Moved, Is.Empty);
                Assert.That(context.MediaFileService.Updated, Is.Empty);
                Assert.That(context.Events.Events, Is.Empty);
                Assert.That(context.EbookFiles.Select(file => file.Path), Is.EqualTo(originalPaths));
            }
        }

        [TestCase(false, FolderType.Mixed)]
        [TestCase(true, FolderType.Audiobook)]
        public void should_not_plan_when_the_audiobook_root_does_not_enable_colocation(bool enabled, FolderType folderType)
        {
            var context = CreateContext(file => file.Path);
            context.Roots.All()[0].PlaceEbooksWithAudiobooks = enabled;
            context.Roots.All()[0].FolderType = folderType;

            context.Handler.Handle(context.Event);

            Assert.That(context.Mover.Plans, Is.Empty);
            Assert.That(context.Mover.Moved, Is.Empty);
            Assert.That(context.MediaFileService.Updated, Is.Empty);
            Assert.That(context.Events.Events, Is.Empty);
        }

        [TestCase(1)]
        [TestCase(10)]
        public void imported_book_batches_catalog_and_file_reads_through_the_real_planner(int parts)
        {
            var context = CreateContext(file => file.Path, includeMobi: true);
            var first = context.Event.ImportedBooks.First();
            context.Event.ImportedBooks.Clear();
            for (var part = 0; part < parts; part++)
            {
                var file = new BookFile { Id = 100 + part, Edition = first.Edition, EditionId = first.EditionId,
                    Quality = new QualityModel(Quality.MP3), MediaType = "audiobook", Path = first.Path + part + ".mp3" };
                context.Event.ImportedBooks.Add(file);
                ((DiskProviderProxy)(object)context.Disk).ExistingFiles.Add(file.Path);
            }
            context.MediaFileService.FilesByBook[context.Event.Book.Id] = context.Event.ImportedBooks;
            var planner = new EbookColocationPlanner(context.Books, context.MediaFileService, context.Roots, context.Disk);
            var mover = new BookFileMovingService(null, null, DispatchProxy.Create<IBuildFileNames, NamingProxy>(), null,
                DispatchProxy.Create<INamingConfigService, NamingProxy>(), planner, null, context.Disk,
                null, null, null, context.Events, null, null, LogManager.GetCurrentClassLogger());
            context.Mover.PlanFactory = (file, author, batch) => mover.GetOrganizeDestination(file, author, false, batch);

            context.Handler.Handle(context.Event);

            Assert.That(context.Books.AuthorReads, Is.EqualTo(1));
            Assert.That(context.MediaFileService.BulkReads, Is.EqualTo(1));
            Assert.That(context.MediaFileService.SingleReads, Is.Zero);
            Assert.That(context.Mover.Plans.Count, Is.EqualTo(2), "One plan for each existing ebook file, independent of audiobook part count");
            Assert.That(context.Mover.Moved, Is.EqualTo(context.EbookFiles));
            Assert.That(context.Handler, Is.Not.InstanceOf<IHandle<TrackImportedEvent>>());
        }

        [Test]
        public void two_imported_books_each_colocate_their_own_ebook_once()
        {
            var context = CreateContext(file => file.Path + ".moved");
            var author = context.Event.Author;
            var audio = new Book { Id = 11, AuthorId = author.Id, Author = author,
                MediaType = BookMediaType.Audiobook, GoodreadsWorkId = "gr:200" };
            var ebook = new Book { Id = 21, AuthorId = author.Id, Author = author,
                MediaType = BookMediaType.Ebook, GoodreadsWorkId = "gr:200" };
            var edition = new Edition { Id = 32, BookId = ebook.Id, Book = ebook };
            var file = new BookFile { Id = 42, Author = author, Edition = edition, EditionId = edition.Id,
                MediaType = "ebook", Quality = new QualityModel(Quality.EPUB),
                Path = Path.Combine(author.EbookRootFolderPath, "Author", "Second book", "Second.epub") };
            context.Books.Books.AddRange(new[] { audio, ebook });
            context.MediaFileService.FilesByBook[ebook.Id] = new List<BookFile> { file };
            ((DiskProviderProxy)(object)context.Disk).ExistingFiles.Add(file.Path);
            var secondEvent = new BookImportedEvent(author, audio, new List<BookFile>
            {
                new BookFile { MediaType = "audiobook", Quality = new QualityModel(Quality.M4B) }
            }, new List<BookFile>(), true, null);

            context.Handler.Handle(context.Event);
            context.Handler.Handle(secondEvent);

            Assert.That(context.Books.AuthorReads, Is.EqualTo(2));
            Assert.That(context.MediaFileService.BulkReads, Is.EqualTo(2));
            Assert.That(context.MediaFileService.SingleReads, Is.Zero);
            Assert.That(context.Mover.Plans, Has.Count.EqualTo(2));
            Assert.That(context.Mover.Moved, Is.EqualTo(new[] { context.EbookFile, file }));
        }

        private static TestContext CreateContext(Func<BookFile, string> destinationFactory, bool includeMobi = false, RootFolder ebookRoot = null)
        {
            var author = new Author
            {
                Id = 5,
                AudiobookRootFolderPath = @"C:\library".AsOsAgnostic(),
                EbookRootFolderPath = ebookRoot?.Path ?? @"C:\library".AsOsAgnostic()
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
                Path = Path.Combine(author.EbookRootFolderPath, "Author", "Book", "Book.epub")
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
                    Path = Path.Combine(author.EbookRootFolderPath, "Author", "Book", "Book.mobi")
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
                Path = @"C:\library\Author\Book - Narrator\Book.m4b".AsOsAgnostic()
            };

            var bookService = new StubBookService();
            bookService.Books.AddRange(new[] { audiobookBook, ebookBook });

            var mediaFileService = new StubMediaFileService();
            mediaFileService.FilesByBook[ebookBook.Id] = ebookFiles;
            mediaFileService.FilesByBook[audiobookBook.Id] = new List<BookFile> { imported };

            var events = new StubEventAggregator();
            var roots = new StubRootFolderService(new RootFolder
                {
                    Id = 7,
                    Path = author.AudiobookRootFolderPath,
                    FolderType = FolderType.Mixed,
                    PlaceEbooksWithAudiobooks = true
                });
            if (ebookRoot != null)
            {
                roots.All().Add(ebookRoot);
            }

            var mover = new StubBookFileMover(destinationFactory);
            var disk = DiskWith(ebookFiles.Select(f => f.Path).Append(imported.Path).ToArray());
            var handler = new EbookColocateOnAudiobookImportHandler(
                roots,
                bookService,
                mediaFileService,
                mover,
                disk,
                events,
                LogManager.GetCurrentClassLogger());

            return new TestContext(
                handler,
                new BookImportedEvent(
                    author, audiobookBook, new List<BookFile> { imported },
                    new List<BookFile>(),
                    true,
                    null),
                ebookFile,
                ebookFiles,
                mediaFileService,
                events,
                bookService,
                roots,
                mover,
                disk);
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
            BookImportedEvent Event,
            BookFile EbookFile,
            List<BookFile> EbookFiles,
            StubMediaFileService MediaFileService,
            StubEventAggregator Events,
            StubBookService Books,
            StubRootFolderService Roots,
            StubBookFileMover Mover,
            IDiskProvider Disk);
    }
}
