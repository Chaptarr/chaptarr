using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using DryIoc;
using Microsoft.Data.Sqlite;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Composition.Extensions;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Extras;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class ImportApprovedBooksNotificationFixture
    {
        private sealed class StubMediaFileService : IMediaFileService
        {
            private int _nextId = 1000;

            public List<BookFile> AddedFiles { get; } = new();
            public List<BookFile> UpdatedFiles { get; } = new();
            public List<BookFile> DeletedFiles { get; } = new();
            public List<BookFile> FilesByBook { get; } = new();
            public bool ClearEditionOnAdd { get; set; }
            public bool ThrowOnReplace { get; set; }
            public Exception ReplaceException { get; set; }
            public int ReplaceManyCalls { get; private set; }
            public BookFile FileAtPath { get; set; }
            public string ThrowOnGetFileWithPath { get; set; }
            public List<string> OperationOrder { get; set; }

            public BookFile Add(BookFile bookFile) => throw new NotImplementedException();

            public void AddMany(List<BookFile> bookFiles)
            {
                foreach (var bookFile in bookFiles)
                {
                    bookFile.Id = ++_nextId;

                    if (ClearEditionOnAdd)
                    {
                        bookFile.Edition = null;
                    }

                    AddedFiles.Add(bookFile);
                }
            }

            public void ReplaceMany(List<BookFile> bookFiles, List<BookFile> replacedFiles, DeleteMediaFileReason reason)
            {
                ReplaceManyCalls++;

                if (ReplaceException != null)
                {
                    throw ReplaceException;
                }

                if (ThrowOnReplace)
                {
                    throw new InvalidOperationException("synthetic persistence failure");
                }

                foreach (var replacedFile in replacedFiles ?? new List<BookFile>())
                {
                    Delete(replacedFile, reason);
                }

                AddMany(bookFiles);
            }

            public void Update(BookFile bookFile)
            {
                UpdatedFiles.Add(bookFile);
                OperationOrder?.Add("update");
            }

            public void Update(List<BookFile> bookFiles) { }
            public void Delete(BookFile bookFile, DeleteMediaFileReason reason)
            {
                DeletedFiles.Add(bookFile);

                if (FileAtPath?.Id == bookFile.Id)
                {
                    FileAtPath = null;
                }
            }

            public void DeleteMany(List<BookFile> bookFiles, DeleteMediaFileReason reason) { }
            public List<System.IO.Abstractions.IFileInfo> FilterUnchangedFiles(List<System.IO.Abstractions.IFileInfo> files, FilterFilesType filter) => throw new NotImplementedException();
            public List<BookFile> GetFilesByAuthor(int authorId) => new();
            public List<BookFile> GetFilesByBook(int bookId) => FilesByBook.ToList();
            public List<BookFile> GetFilesByBooks(List<int> bookIds) => new();
            public List<BookFile> GetFilesByEdition(int editionId) => new();
            public List<BookFile> GetUnmappedFiles() => new();
            public BookFile Get(int id) => null;
            public List<BookFile> Get(IEnumerable<int> ids) => new();
            public List<BookFile> GetFilesWithBasePath(string path) => new();
            public List<BookFile> GetFilesWithBasePath(string path, string mediaType) => new();
            public List<BookFile> GetFileWithPath(List<string> path) => new();
            public BookFile GetFileWithPath(string path)
            {
                if (string.Equals(ThrowOnGetFileWithPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("synthetic destination lookup failure");
                }

                return string.Equals(FileAtPath?.Path, path, StringComparison.OrdinalIgnoreCase) ? FileAtPath : null;
            }
            public void UpdateMediaInfo(List<BookFile> bookFiles) { }
            public List<BookFile> GetFilesByAuthorAndMediaType(int authorId, string mediaType) => new();
        }

        private sealed class StubMetadataTagService : IMetadataTagService
        {
            public bool ThrowOnWrite { get; set; }
            public List<string> OperationOrder { get; set; }

            public Dictionary<string, List<string>> ReadAllTags(System.IO.Abstractions.IFileInfo file) => new();
            public (Dictionary<string, List<string>> Tags, int? DurationSeconds) ReadAllTagsAndDuration(System.IO.Abstractions.IFileInfo file) => (new Dictionary<string, List<string>>(), null);
            public string ReadAllTagsAsJson(System.IO.Abstractions.IFileInfo file) => "{}";
            public void WriteTags(BookFile trackfile, bool newDownload, bool force = false)
            {
                OperationOrder?.Add("tags");

                if (ThrowOnWrite)
                {
                    throw new InvalidOperationException("synthetic tag write failure");
                }
            }

            public void SyncTags(List<Edition> books) => throw new NotImplementedException();
            public List<RetagBookFilePreview> GetRetagPreviewsByAuthor(int authorId) => throw new NotImplementedException();
            public List<RetagBookFilePreview> GetRetagPreviewsByBook(int authorId) => throw new NotImplementedException();
        }

        private sealed class StubMediaInfoExtractor : IMediaInfoExtractor
        {
            public MediaInfoModel ExtractMediaInfo(string filePath) => new();
            public TimeSpan GetDuration(string filePath) => TimeSpan.Zero;
            public bool IsAudiobookFile(string filePath, MediaInfoModel mediaInfo = null) => false;
        }

        private sealed class StubMoveBookFiles : IMoveBookFiles
        {
            public string DestinationPath { get; set; }
            public int MoveCalls { get; private set; }

            public BookFile MoveBookFile(BookFile bookFile, Author author, bool forceRename = false, RenameBatchContext renameBatchContext = null) => throw new NotImplementedException();

            public BookFile MoveBookFile(BookFile bookFile, LocalBook localBook)
            {
                MoveCalls++;
                bookFile.Path = DestinationPath;
                return bookFile;
            }

            public BookFile CopyBookFile(BookFile bookFile, LocalBook localBook) => throw new NotImplementedException();
            public string GetImportDestinationPath(BookFile bookFile, LocalBook localBook) => DestinationPath;
        }

        private sealed class StubUpgradeMediaFiles : IUpgradeMediaFiles
        {
            public int CalibreImportCalls { get; private set; }
            public int CompleteCalls { get; private set; }
            public int RollbackCalls { get; private set; }
            public bool ThrowOnCalibreImport { get; set; }
            public bool ThrowOnRollback { get; set; }
            public BookFile ReturnedBookFile { get; private set; }

            public BookFileMoveResult UpgradeBookFile(BookFile bookFile, LocalBook localBook, bool copyOnly = false) => throw new NotImplementedException();

            public CalibreBookFileImport PrepareCalibreImport(BookFile bookFile, RootFolder rootFolder, bool copyOnly = false)
            {
                CalibreImportCalls++;

                if (rootFolder?.CalibreSettings == null)
                {
                    throw new InvalidOperationException("A Calibre root folder with settings is required.");
                }

                if (ThrowOnCalibreImport)
                {
                    throw new InvalidOperationException("synthetic Calibre import failure");
                }

                ReturnedBookFile = new BookFile
                {
                    Path = "/calibre/Best Served Cold.epub",
                    Size = bookFile.Size,
                    Modified = bookFile.Modified,
                    DateAdded = bookFile.DateAdded,
                    OriginalFilePath = bookFile.OriginalFilePath,
                    Quality = bookFile.Quality,
                    EditionId = bookFile.EditionId,
                    Edition = bookFile.Edition,
                    Author = bookFile.Author,
                    CalibreId = 42,
                    MediaType = bookFile.MediaType
                };

                return new CalibreBookFileImport
                {
                    BookFile = ReturnedBookFile,
                    RootFolder = rootFolder,
                    SourcePath = "/downloads/Best Served Cold.epub",
                    CopyOnly = copyOnly,
                    CreatedBook = true
                };
            }

            public void CompleteCalibreImport(CalibreBookFileImport import) => CompleteCalls++;
            public void RollbackCalibreImport(CalibreBookFileImport import)
            {
                RollbackCalls++;

                if (ThrowOnRollback)
                {
                    throw new InvalidOperationException("synthetic Calibre rollback failure");
                }
            }
        }

        private class RootFolderServiceProxy : DispatchProxy
        {
            public RootFolder RootFolder { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IRootFolderService.GetBestRootFolder))
                {
                    return RootFolder;
                }

                throw new NotImplementedException($"Unexpected call to IRootFolderService.{targetMethod?.Name}");
            }
        }

        private class ContainerDependencyProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Container resolution test should not call {targetMethod?.Name}");
            }
        }

        private sealed class CapturingEventAggregator : IEventAggregator
        {
            public List<IEvent> Events { get; } = new();
            public List<string> OperationOrder { get; set; }

            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
                Events.Add(@event);

                if (@event is BookFileAddedEvent)
                {
                    OperationOrder?.Add("event");
                }
            }
        }

        private class BookServiceProxy : DispatchProxy
        {
            public Book Book { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IBookService.GetBook))
                {
                    return Book;
                }

                throw new NotImplementedException($"Unexpected call to IBookService.{targetMethod?.Name}");
            }
        }

        private class AuthorServiceProxy : DispatchProxy
        {
            public Author Author { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IAuthorService.GetAuthor))
                {
                    return Author;
                }

                throw new NotImplementedException($"Unexpected call to IAuthorService.{targetMethod?.Name}");
            }
        }

        private class EditionServiceProxy : DispatchProxy
        {
            public Edition Edition { get; set; }
            public List<Edition> EditionsByBook { get; set; } = new();
            public List<(Edition Edition, bool IsManualSelection)> SetMonitoredCalls { get; } = new();
            public List<string> OperationOrder { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IEditionService.GetEdition))
                {
                    return Edition;
                }

                if (targetMethod?.Name == nameof(IEditionService.GetEditionsByBook))
                {
                    return EditionsByBook;
                }

                if (targetMethod?.Name == nameof(IEditionService.SetMonitored))
                {
                    SetMonitoredCalls.Add(((Edition)args[0], (bool)args[1]));
                    OperationOrder?.Add("monitor");
                    return null;
                }

                throw new NotImplementedException($"Unexpected call to IEditionService.{targetMethod?.Name}");
            }
        }

        private class ThrowingProxy<T> : DispatchProxy where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Unexpected call to {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private sealed class StubCustomFormatCalculationService : ICustomFormatCalculationService
        {
            private readonly List<CustomFormat> _formats;

            public StubCustomFormatCalculationService(params CustomFormat[] formats)
            {
                _formats = formats.ToList();
            }

            public int LocalBookCalls { get; private set; }

            public List<CustomFormat> ParseCustomFormat(LocalBook localBook)
            {
                LocalBookCalls++;
                return _formats;
            }

            public List<CustomFormat> ParseCustomFormat(RemoteBook remoteBook, long size) => throw new NotImplementedException();
            public List<CustomFormat> ParseCustomFormat(BookFile bookFile, Author artist) => throw new NotImplementedException();
            public List<CustomFormat> ParseCustomFormat(BookFile bookFile) => throw new NotImplementedException();
            public List<CustomFormat> ParseCustomFormat(Blocklist blocklist, Author artist) => throw new NotImplementedException();
            public List<CustomFormat> ParseCustomFormat(EntityHistory history, Author artist) => throw new NotImplementedException();
        }

        private static T Proxy<T>() where T : class
        {
            return DispatchProxy.Create<T, ThrowingProxy<T>>();
        }

        private static SqliteException CreateBookFilesPathUniqueViolation()
        {
            using var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE BookFiles (Path TEXT UNIQUE); INSERT INTO BookFiles VALUES ('same'); INSERT INTO BookFiles VALUES ('same');";

            try
            {
                command.ExecuteNonQuery();
            }
            catch (SqliteException ex)
            {
                return ex;
            }

            throw new AssertionException("Expected a SQLite unique-path violation.");
        }

        private static (Author Author, Book Book, Edition Edition, string Path) CreateExistingEbookContext(bool monitored = true)
        {
            var qualityProfile = new QualityProfile
            {
                Id = 1,
                Name = "Ebooks",
                ProfileType = ProfileType.Ebook,
                UpgradeAllowed = true,
                Items = new List<QualityProfileQualityItem>
                {
                    new() { Allowed = true, Quality = Quality.EPUB }
                }
            };

            var author = new Author
            {
                Id = 57,
                Name = "Riley Sager",
                EbookRootFolderPath = "/ebooks",
                EbookPath = "/ebooks/Riley Sager",
                EbookQualityProfileId = qualityProfile.Id,
                EbookQualityProfile = qualityProfile
            };

            var book = new Book
            {
                Id = 3076,
                AuthorId = author.Id,
                Author = author,
                Title = "The House Across the Lake",
                CleanTitle = "thehouseacrossthelake",
                MediaType = BookMediaType.Ebook,
                AnyEditionOk = true
            };

            var edition = new Edition
            {
                Id = 12387,
                BookId = book.Id,
                Book = book,
                Title = "The House Across the Lake",
                Monitored = monitored,
                IsEbook = true
            };

            return (author, book, edition, "/ebooks/Riley Sager/The House Across the Lake.epub");
        }

        private static (List<ImportResult> Results, EditionServiceProxy EditionService) ImportExistingEbook(
            StubMediaFileService mediaFileService,
            CapturingEventAggregator eventAggregator,
            Author author,
            Book book,
            Edition edition,
            string path,
            bool existingFile = true,
            IReadOnlyCollection<Edition> editionsByBook = null,
            IMoveBookFiles bookFileMover = null,
            StubMetadataTagService metadataTagService = null,
            List<string> operationOrder = null,
            ImportMode importMode = ImportMode.Copy,
            RootFolder rootFolder = null,
            IUpgradeMediaFiles upgradeMediaFiles = null)
        {
            mediaFileService.OperationOrder = operationOrder;
            eventAggregator.OperationOrder = operationOrder;
            metadataTagService ??= new StubMetadataTagService();
            metadataTagService.OperationOrder = operationOrder;

            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)(object)bookService).Book = book;

            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)(object)authorService).Author = author;

            var editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
            var editionServiceProxy = (EditionServiceProxy)(object)editionService;
            editionServiceProxy.Edition = edition;
            editionServiceProxy.EditionsByBook = editionsByBook?.Any() == true ? editionsByBook.ToList() : new List<Edition> { edition };
            editionServiceProxy.OperationOrder = operationOrder;

            var rootFolderService = DispatchProxy.Create<IRootFolderService, RootFolderServiceProxy>();
            ((RootFolderServiceProxy)(object)rootFolderService).RootFolder = rootFolder;

            var service = new ImportApprovedBooks(
                mediaFileService,
                metadataTagService,
                new StubMediaInfoExtractor(),
                authorService,
                bookService,
                editionService,
                Proxy<IRecycleBinProvider>(),
                Proxy<IExtraService>(),
                bookFileMover ?? Proxy<IMoveBookFiles>(),
                Proxy<IHistoryService>(),
                Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(),
                eventAggregator,
                Proxy<IManageCommandQueue>(),
                Proxy<ISeriesBookLinkService>(),
                Proxy<ISeriesService>(),
                Proxy<IQualityProfileService>(),
                Proxy<IM4bConversionService>(),
                LogManager.GetLogger("ImportApprovedBooksNotificationFixture"),
                rootFolderService: rootFolderService,
                upgradeMediaFiles: upgradeMediaFiles);

            var localBook = new LocalBook
            {
                Path = path,
                ExistingFile = existingFile,
                IsManualImport = true,
                Book = book,
                Author = author,
                Edition = edition,
                Quality = new QualityModel { Quality = Quality.EPUB, Revision = new Revision() },
                RawTags = new RawFileTags
                {
                    AllTags = new Dictionary<string, List<string>>
                    {
                        ["title"] = new() { book.Title }
                    }
                },
                Part = 1,
                PartCount = 1
            };

            var results = service.Import(
                new List<ImportDecision<LocalBook>> { new(localBook) },
                replaceExisting: true,
                downloadClientItem: new DownloadClientItem
                {
                    DownloadId = "download-id",
                    DownloadClientInfo = new DownloadClientItemClientInfo { Name = "qBittorrent", Type = "qbittorrent" }
                },
                importMode: importMode,
                cancellationToken: CancellationToken.None);

            return (results, editionServiceProxy);
        }

        [Test]
        public void production_container_rules_should_inject_calibre_routing_dependencies()
        {
            using var container = new Container(rules => rules.WithNzbDroneRules());
            var constructor = typeof(ImportApprovedBooks).GetConstructors().Single();

            foreach (var parameter in constructor.GetParameters())
            {
                var dependency = parameter.ParameterType == typeof(Logger)
                    ? LogManager.GetCurrentClassLogger()
                    : DispatchProxy.Create(parameter.ParameterType, typeof(ContainerDependencyProxy));

                container.RegisterInstance(parameter.ParameterType, dependency);
            }

            container.Register<ImportApprovedBooks>();
            var subject = container.Resolve<ImportApprovedBooks>();
            var rootFolderService = typeof(ImportApprovedBooks).GetField("_rootFolderService", BindingFlags.Instance | BindingFlags.NonPublic);
            var upgradeMediaFiles = typeof(ImportApprovedBooks).GetField("_upgradeMediaFiles", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.Multiple(() =>
            {
                Assert.That(rootFolderService?.GetValue(subject), Is.Not.Null);
                Assert.That(upgradeMediaFiles?.GetValue(subject), Is.Not.Null);
            });
        }

        [Test]
        public void should_import_a_new_calibre_book_through_the_calibre_aware_path()
        {
            var context = CreateExistingEbookContext();
            var mediaFileService = new StubMediaFileService();
            var eventAggregator = new CapturingEventAggregator();
            var mover = new StubMoveBookFiles { DestinationPath = "/ebooks/Riley Sager/Best Served Cold.epub" };
            var upgradeMediaFiles = new StubUpgradeMediaFiles();
            var calibreRoot = new RootFolder
            {
                Path = "/ebooks",
                IsCalibreLibrary = true,
                CalibreSettings = new()
            };

            var import = ImportExistingEbook(
                mediaFileService,
                eventAggregator,
                context.Author,
                context.Book,
                context.Edition,
                "/downloads/Best Served Cold.epub",
                existingFile: false,
                bookFileMover: mover,
                importMode: ImportMode.Auto,
                rootFolder: calibreRoot,
                upgradeMediaFiles: upgradeMediaFiles);

            Assert.Multiple(() =>
            {
                Assert.That(import.Results.Single().Result, Is.EqualTo(ImportResultType.Imported));
                Assert.That(upgradeMediaFiles.CalibreImportCalls, Is.EqualTo(1));
                Assert.That(upgradeMediaFiles.CompleteCalls, Is.EqualTo(1));
                Assert.That(mover.MoveCalls, Is.Zero);
                Assert.That(mediaFileService.AddedFiles.Single(), Is.SameAs(upgradeMediaFiles.ReturnedBookFile));
                Assert.That(mediaFileService.AddedFiles.Single().CalibreId, Is.EqualTo(42));
                Assert.That(mediaFileService.AddedFiles.Single().Path, Is.EqualTo("/calibre/Best Served Cold.epub"));
            });
        }

        [Test]
        public void should_fail_closed_for_a_calibre_root_without_settings()
        {
            var context = CreateExistingEbookContext();
            var mediaFileService = new StubMediaFileService();
            var mover = new StubMoveBookFiles { DestinationPath = "/ebooks/Riley Sager/Best Served Cold.epub" };
            var upgradeMediaFiles = new StubUpgradeMediaFiles();

            var import = ImportExistingEbook(
                mediaFileService,
                new CapturingEventAggregator(),
                context.Author,
                context.Book,
                context.Edition,
                "/downloads/Best Served Cold.epub",
                existingFile: false,
                bookFileMover: mover,
                importMode: ImportMode.Auto,
                rootFolder: new RootFolder
                {
                    Path = "/ebooks",
                    IsCalibreLibrary = true
                },
                upgradeMediaFiles: upgradeMediaFiles);

            Assert.Multiple(() =>
            {
                Assert.That(import.Results.Single().Result, Is.EqualTo(ImportResultType.Skipped));
                Assert.That(upgradeMediaFiles.CalibreImportCalls, Is.EqualTo(1));
                Assert.That(mover.MoveCalls, Is.Zero);
                Assert.That(mediaFileService.AddedFiles, Is.Empty);
            });
        }

        [Test]
        public void should_keep_new_non_calibre_imports_on_the_generic_mover_path()
        {
            var context = CreateExistingEbookContext();
            var mediaFileService = new StubMediaFileService();
            var eventAggregator = new CapturingEventAggregator();
            var mover = new StubMoveBookFiles { DestinationPath = "/ebooks/Riley Sager/Best Served Cold.epub" };
            var upgradeMediaFiles = new StubUpgradeMediaFiles();

            var import = ImportExistingEbook(
                mediaFileService,
                eventAggregator,
                context.Author,
                context.Book,
                context.Edition,
                "/downloads/Best Served Cold.epub",
                existingFile: false,
                bookFileMover: mover,
                importMode: ImportMode.Auto,
                rootFolder: new RootFolder { Path = "/ebooks" },
                upgradeMediaFiles: upgradeMediaFiles);

            Assert.Multiple(() =>
            {
                Assert.That(import.Results.Single().Result, Is.EqualTo(ImportResultType.Imported));
                Assert.That(mover.MoveCalls, Is.EqualTo(1));
                Assert.That(upgradeMediaFiles.CalibreImportCalls, Is.Zero);
            });
        }

        [Test]
        public void should_not_move_or_persist_a_new_book_when_calibre_import_fails()
        {
            var context = CreateExistingEbookContext();
            var mediaFileService = new StubMediaFileService();
            var eventAggregator = new CapturingEventAggregator();
            var mover = new StubMoveBookFiles { DestinationPath = "/ebooks/Riley Sager/Best Served Cold.epub" };
            var upgradeMediaFiles = new StubUpgradeMediaFiles { ThrowOnCalibreImport = true };

            var import = ImportExistingEbook(
                mediaFileService,
                eventAggregator,
                context.Author,
                context.Book,
                context.Edition,
                "/downloads/Best Served Cold.epub",
                existingFile: false,
                bookFileMover: mover,
                importMode: ImportMode.Auto,
                rootFolder: new RootFolder
                {
                    Path = "/ebooks",
                    IsCalibreLibrary = true,
                    CalibreSettings = new()
                },
                upgradeMediaFiles: upgradeMediaFiles);

            Assert.Multiple(() =>
            {
                Assert.That(import.Results.Single().Result, Is.EqualTo(ImportResultType.Skipped));
                Assert.That(upgradeMediaFiles.CalibreImportCalls, Is.EqualTo(1));
                Assert.That(mover.MoveCalls, Is.Zero);
                Assert.That(mediaFileService.AddedFiles, Is.Empty);
            });
        }

        [Test]
        public void should_reject_a_tracked_same_book_row_before_calibre_mutation()
        {
            var context = CreateExistingEbookContext();
            var trackedFile = new BookFile
            {
                Id = 8007,
                Path = context.Path,
                EditionId = context.Edition.Id,
                Edition = context.Edition,
                CalibreId = 0,
                Quality = new QualityModel(Quality.EPUB)
            };
            var mediaFileService = new StubMediaFileService { FileAtPath = trackedFile };
            var mover = new StubMoveBookFiles { DestinationPath = "/calibre/Best Served Cold.epub" };
            var upgradeMediaFiles = new StubUpgradeMediaFiles();

            var import = ImportExistingEbook(
                mediaFileService,
                new CapturingEventAggregator(),
                context.Author,
                context.Book,
                context.Edition,
                context.Path,
                existingFile: false,
                bookFileMover: mover,
                importMode: ImportMode.Move,
                rootFolder: new RootFolder
                {
                    Path = "/ebooks",
                    IsCalibreLibrary = true,
                    CalibreSettings = new()
                },
                upgradeMediaFiles: upgradeMediaFiles);

            Assert.Multiple(() =>
            {
                Assert.That(import.Results.Single().Result, Is.EqualTo(ImportResultType.Rejected));
                Assert.That(upgradeMediaFiles.CalibreImportCalls, Is.Zero);
                Assert.That(upgradeMediaFiles.CompleteCalls, Is.Zero);
                Assert.That(upgradeMediaFiles.RollbackCalls, Is.Zero);
                Assert.That(mover.MoveCalls, Is.Zero);
                Assert.That(mediaFileService.ReplaceManyCalls, Is.Zero);
            });
        }

        [Test]
        public void should_compensate_and_reject_when_the_calibre_destination_is_already_tracked()
        {
            var context = CreateExistingEbookContext();
            var conflict = new BookFile
            {
                Id = 8008,
                Path = "/calibre/Best Served Cold.epub",
                EditionId = context.Edition.Id
            };
            var mediaFileService = new StubMediaFileService { FileAtPath = conflict };
            var upgradeMediaFiles = new StubUpgradeMediaFiles();

            var import = ImportExistingEbook(
                mediaFileService,
                new CapturingEventAggregator(),
                context.Author,
                context.Book,
                context.Edition,
                "/downloads/Best Served Cold.epub",
                existingFile: false,
                bookFileMover: new StubMoveBookFiles(),
                importMode: ImportMode.Copy,
                rootFolder: new RootFolder
                {
                    Path = "/ebooks",
                    IsCalibreLibrary = true,
                    CalibreSettings = new()
                },
                upgradeMediaFiles: upgradeMediaFiles);

            Assert.Multiple(() =>
            {
                Assert.That(import.Results.Single().Result, Is.EqualTo(ImportResultType.Rejected));
                Assert.That(upgradeMediaFiles.CalibreImportCalls, Is.EqualTo(1));
                Assert.That(upgradeMediaFiles.RollbackCalls, Is.EqualTo(1));
                Assert.That(upgradeMediaFiles.CompleteCalls, Is.Zero);
                Assert.That(mediaFileService.ReplaceManyCalls, Is.Zero);
                Assert.That(mediaFileService.AddedFiles, Is.Empty);
            });
        }

        [Test]
        public void should_compensate_without_shared_replacement_retry_when_calibre_persistence_hits_a_path_race()
        {
            var context = CreateExistingEbookContext();
            var mediaFileService = new StubMediaFileService
            {
                ReplaceException = CreateBookFilesPathUniqueViolation()
            };
            var eventAggregator = new CapturingEventAggregator();
            var upgradeMediaFiles = new StubUpgradeMediaFiles();

            Assert.Throws<InvalidOperationException>(() => ImportExistingEbook(
                mediaFileService,
                eventAggregator,
                context.Author,
                context.Book,
                context.Edition,
                "/downloads/Best Served Cold.epub",
                existingFile: false,
                bookFileMover: new StubMoveBookFiles(),
                importMode: ImportMode.Copy,
                rootFolder: new RootFolder
                {
                    Path = "/ebooks",
                    IsCalibreLibrary = true,
                    CalibreSettings = new()
                },
                upgradeMediaFiles: upgradeMediaFiles));

            Assert.Multiple(() =>
            {
                Assert.That(upgradeMediaFiles.RollbackCalls, Is.EqualTo(1));
                Assert.That(upgradeMediaFiles.CompleteCalls, Is.Zero);
                Assert.That(mediaFileService.ReplaceManyCalls, Is.EqualTo(1));
                Assert.That(mediaFileService.DeletedFiles, Is.Empty);
                Assert.That(eventAggregator.Events.OfType<TrackImportedEvent>(), Is.Empty);
                Assert.That(eventAggregator.Events.OfType<BookImportedEvent>(), Is.Empty);
            });
        }

        [Test]
        public void should_compensate_when_destination_lookup_throws_after_calibre_mutation()
        {
            var context = CreateExistingEbookContext();
            var mediaFileService = new StubMediaFileService
            {
                ThrowOnGetFileWithPath = "/calibre/Best Served Cold.epub"
            };
            var eventAggregator = new CapturingEventAggregator();
            var upgradeMediaFiles = new StubUpgradeMediaFiles();

            var import = ImportExistingEbook(
                mediaFileService,
                eventAggregator,
                context.Author,
                context.Book,
                context.Edition,
                "/downloads/Best Served Cold.epub",
                existingFile: false,
                bookFileMover: new StubMoveBookFiles(),
                importMode: ImportMode.Move,
                rootFolder: new RootFolder
                {
                    Path = "/ebooks",
                    IsCalibreLibrary = true,
                    CalibreSettings = new()
                },
                upgradeMediaFiles: upgradeMediaFiles);

            Assert.Multiple(() =>
            {
                Assert.That(import.Results.Single().Result, Is.EqualTo(ImportResultType.Skipped));
                Assert.That(import.Results.Single().Errors.Single(), Does.Contain("synthetic destination lookup failure"));
                Assert.That(upgradeMediaFiles.CalibreImportCalls, Is.EqualTo(1));
                Assert.That(upgradeMediaFiles.RollbackCalls, Is.EqualTo(1));
                Assert.That(upgradeMediaFiles.CompleteCalls, Is.Zero, "move source cleanup must only happen after commit");
                Assert.That(mediaFileService.ReplaceManyCalls, Is.Zero);
                Assert.That(mediaFileService.AddedFiles, Is.Empty);
                Assert.That(eventAggregator.Events.OfType<TrackImportedEvent>(), Is.Empty);
                Assert.That(eventAggregator.Events.OfType<BookImportedEvent>(), Is.Empty);
            });
        }

        [Test]
        public void should_surface_persistence_and_calibre_rollback_failures_together()
        {
            var context = CreateExistingEbookContext();
            var mediaFileService = new StubMediaFileService { ThrowOnReplace = true };
            var eventAggregator = new CapturingEventAggregator();
            var upgradeMediaFiles = new StubUpgradeMediaFiles { ThrowOnRollback = true };

            var exception = Assert.Throws<AggregateException>(() => ImportExistingEbook(
                mediaFileService,
                eventAggregator,
                context.Author,
                context.Book,
                context.Edition,
                "/downloads/Best Served Cold.epub",
                existingFile: false,
                bookFileMover: new StubMoveBookFiles(),
                importMode: ImportMode.Move,
                rootFolder: new RootFolder
                {
                    Path = "/ebooks",
                    IsCalibreLibrary = true,
                    CalibreSettings = new()
                },
                upgradeMediaFiles: upgradeMediaFiles));

            Assert.Multiple(() =>
            {
                Assert.That(exception.ToString(), Does.Contain("synthetic persistence failure"));
                Assert.That(exception.ToString(), Does.Contain("synthetic Calibre rollback failure"));
                Assert.That(upgradeMediaFiles.RollbackCalls, Is.EqualTo(1));
                Assert.That(upgradeMediaFiles.CompleteCalls, Is.Zero);
                Assert.That(mediaFileService.AddedFiles, Is.Empty);
                Assert.That(eventAggregator.Events.OfType<TrackImportedEvent>(), Is.Empty);
                Assert.That(eventAggregator.Events.OfType<BookImportedEvent>(), Is.Empty);
            });
        }

        [Test]
        public void should_reject_a_calibre_book_replacement_without_transferring_or_persisting()
        {
            var context = CreateExistingEbookContext();
            var existing = new BookFile
            {
                Id = 99,
                Path = "/ebooks/Riley Sager/Best Served Cold.epub",
                CalibreId = 7,
                EditionId = context.Edition.Id,
                Edition = context.Edition,
                Quality = new QualityModel(Quality.EPUB)
            };
            var mediaFileService = new StubMediaFileService();
            mediaFileService.FilesByBook.Add(existing);
            context.Book.BookFiles = new List<BookFile> { existing };
            var mover = new StubMoveBookFiles { DestinationPath = "/ebooks/Riley Sager/Best Served Cold.epub" };
            var upgradeMediaFiles = new StubUpgradeMediaFiles();

            var import = ImportExistingEbook(
                mediaFileService,
                new CapturingEventAggregator(),
                context.Author,
                context.Book,
                context.Edition,
                "/downloads/Best Served Cold.epub",
                existingFile: false,
                bookFileMover: mover,
                importMode: ImportMode.Auto,
                rootFolder: new RootFolder
                {
                    Path = "/ebooks",
                    IsCalibreLibrary = true,
                    CalibreSettings = new()
                },
                upgradeMediaFiles: upgradeMediaFiles);

            Assert.Multiple(() =>
            {
                Assert.That(import.Results.Single().Result, Is.EqualTo(ImportResultType.Rejected));
                Assert.That(upgradeMediaFiles.CalibreImportCalls, Is.Zero);
                Assert.That(mover.MoveCalls, Is.Zero);
                Assert.That(mediaFileService.AddedFiles, Is.Empty);
            });
        }

        [Test]
        public void should_compensate_calibre_and_not_persist_when_the_batch_commit_fails()
        {
            var context = CreateExistingEbookContext();
            var mediaFileService = new StubMediaFileService { ThrowOnReplace = true };
            var eventAggregator = new CapturingEventAggregator();
            var mover = new StubMoveBookFiles { DestinationPath = "/ebooks/Riley Sager/Best Served Cold.epub" };
            var upgradeMediaFiles = new StubUpgradeMediaFiles();

            Assert.Throws<InvalidOperationException>(() => ImportExistingEbook(
                mediaFileService,
                eventAggregator,
                context.Author,
                context.Book,
                context.Edition,
                "/downloads/Best Served Cold.epub",
                existingFile: false,
                bookFileMover: mover,
                importMode: ImportMode.Auto,
                rootFolder: new RootFolder
                {
                    Path = "/ebooks",
                    IsCalibreLibrary = true,
                    CalibreSettings = new()
                },
                upgradeMediaFiles: upgradeMediaFiles));

            Assert.Multiple(() =>
            {
                Assert.That(upgradeMediaFiles.RollbackCalls, Is.EqualTo(1));
                Assert.That(upgradeMediaFiles.CompleteCalls, Is.Zero);
                Assert.That(mover.MoveCalls, Is.Zero);
                Assert.That(mediaFileService.AddedFiles, Is.Empty);
                Assert.That(eventAggregator.Events.OfType<TrackImportedEvent>(), Is.Empty);
                Assert.That(eventAggregator.Events.OfType<BookImportedEvent>(), Is.Empty);
            });
        }

        [TestCase(false, true, ImportResultType.Skipped)]
        [TestCase(true, true, ImportResultType.Imported)]
        [TestCase(true, false, ImportResultType.Imported)]
        public void manual_grab_override_should_survive_profile_import_gates(bool downloadForced, bool hasQualityProfile, ImportResultType expectedResult)
        {
            var format = new CustomFormat
            {
                Id = 77,
                Name = "No Audio Plays",
                BuiltInKey = BuiltInCustomFormats.DramatizedAudioKey
            };
            var qualityProfile = new QualityProfile
            {
                Id = 1,
                Name = "Reject Dramatized",
                ProfileType = ProfileType.Audiobook,
                MinFormatScore = 0,
                UpgradeAllowed = true,
                Items = new List<QualityProfileQualityItem>
                {
                    new() { Allowed = !downloadForced, Quality = Quality.M4B }
                },
                FormatItems = new List<ProfileFormatItem>
                {
                    new() { Format = format, Score = -100 }
                }
            };

            var author = new Author
            {
                Id = 57,
                Name = "Jim Butcher",
                AudiobookRootFolderPath = "/audiobooks",
                AudiobookPath = "/audiobooks/Jim Butcher",
                AudiobookQualityProfileId = hasQualityProfile ? qualityProfile.Id : null,
                AudiobookQualityProfile = hasQualityProfile ? qualityProfile : null
            };

            var book = new Book
            {
                Id = 3076,
                AuthorId = author.Id,
                Author = author,
                Title = "Storm Front",
                CleanTitle = "stormfront",
                MediaType = BookMediaType.Audiobook
            };

            var edition = new Edition
            {
                Id = 12387,
                BookId = book.Id,
                Book = book,
                Title = "Storm Front",
                Monitored = true,
                IsEbook = false,
                ReadingFormatId = 2,
                NarratorNames = new List<string> { "James Marsters" }
            };

            var mediaFileService = new StubMediaFileService();
            var metadataTagService = new StubMetadataTagService();
            var mediaInfoExtractor = new StubMediaInfoExtractor();
            var eventAggregator = new CapturingEventAggregator();
            var formatCalculationService = new StubCustomFormatCalculationService(format);
            var mover = new StubMoveBookFiles { DestinationPath = "/audiobooks/Jim Butcher/Storm Front/Storm Front.m4b" };

            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)(object)bookService).Book = book;

            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)(object)authorService).Author = author;

            var editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
            ((EditionServiceProxy)(object)editionService).Edition = edition;
            ((EditionServiceProxy)(object)editionService).EditionsByBook = new List<Edition> { edition };

            var service = new ImportApprovedBooks(
                mediaFileService,
                metadataTagService,
                mediaInfoExtractor,
                authorService,
                bookService,
                editionService,
                Proxy<IRecycleBinProvider>(),
                Proxy<IExtraService>(),
                downloadForced ? mover : Proxy<IMoveBookFiles>(),
                Proxy<IHistoryService>(),
                Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(),
                eventAggregator,
                Proxy<IManageCommandQueue>(),
                Proxy<ISeriesBookLinkService>(),
                Proxy<ISeriesService>(),
                Proxy<IQualityProfileService>(),
                Proxy<IM4bConversionService>(),
                LogManager.GetLogger("ImportApprovedBooksNotificationFixture"),
                customFormatCalculationService: formatCalculationService);

            var localBook = new LocalBook
            {
                Path = "/downloads/complete/Storm Front.m4b",
                ExistingFile = false,
                Book = book,
                Author = author,
                Edition = edition,
                Quality = new QualityModel { Quality = Quality.M4B, Revision = new Revision() },
                RawTags = new RawFileTags
                {
                    AllTags = new Dictionary<string, List<string>>
                    {
                        ["description"] = new() { "A GraphicAudio production" }
                    }
                },
                Part = 1,
                PartCount = 1
            };

            var results = service.Import(
                new List<ImportDecision<LocalBook>> { new(localBook) },
                replaceExisting: true,
                downloadClientItem: new DownloadClientItem
                {
                    DownloadId = "download-id",
                    DownloadForced = downloadForced,
                    DownloadClientInfo = new DownloadClientItemClientInfo { Name = "qBittorrent", Type = "qbittorrent" }
                },
                importMode: downloadForced ? ImportMode.Move : ImportMode.Copy,
                cancellationToken: CancellationToken.None);

            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(results[0].Result, Is.EqualTo(expectedResult));

            if (downloadForced)
            {
                Assert.That(formatCalculationService.LocalBookCalls, Is.EqualTo(0));
                Assert.That(mediaFileService.AddedFiles, Has.Count.EqualTo(1));
                Assert.That(eventAggregator.Events.OfType<BookImportedEvent>(), Has.Exactly(1).Items);
            }
            else
            {
                Assert.That(results[0].Errors.Single(), Does.Contain("matched No Audio Plays custom format on file tags"));
                Assert.That(formatCalculationService.LocalBookCalls, Is.EqualTo(1));
                Assert.That(mediaFileService.AddedFiles, Is.Empty);
                Assert.That(eventAggregator.Events.OfType<BookImportedEvent>(), Is.Empty);
            }
        }

        [Test]
        public void should_publish_book_imported_event_for_new_download_when_edition_book_navigation_is_missing()
        {
            var qualityProfile = new QualityProfile
            {
                Id = 1,
                Name = "Ebooks",
                ProfileType = ProfileType.Ebook,
                UpgradeAllowed = true,
                Items = new List<QualityProfileQualityItem>
                {
                    new() { Allowed = true, Quality = Quality.EPUB }
                }
            };

            var author = new Author
            {
                Id = 57,
                Name = "Riley Sager",
                EbookRootFolderPath = "/ebooks",
                EbookPath = "/ebooks/Riley Sager",
                EbookQualityProfileId = qualityProfile.Id,
                EbookQualityProfile = qualityProfile
            };

            var book = new Book
            {
                Id = 3076,
                AuthorId = author.Id,
                Author = author,
                Title = "The House Across the Lake",
                CleanTitle = "thehouseacrossthelake",
                MediaType = BookMediaType.Ebook
            };

            var edition = new Edition
            {
                Id = 12387,
                BookId = book.Id,
                Book = null,
                Title = "The House Across the Lake",
                Monitored = true,
                IsEbook = true
            };

            var mediaFileService = new StubMediaFileService();
            var metadataTagService = new StubMetadataTagService();
            var mediaInfoExtractor = new StubMediaInfoExtractor();
            var eventAggregator = new CapturingEventAggregator();

            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)(object)bookService).Book = book;

            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)(object)authorService).Author = author;

            var editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
            ((EditionServiceProxy)(object)editionService).Edition = edition;
            ((EditionServiceProxy)(object)editionService).EditionsByBook = new List<Edition> { edition };

            var service = new ImportApprovedBooks(
                mediaFileService,
                metadataTagService,
                mediaInfoExtractor,
                authorService,
                bookService,
                editionService,
                Proxy<IRecycleBinProvider>(),
                Proxy<IExtraService>(),
                Proxy<IMoveBookFiles>(),
                Proxy<IHistoryService>(),
                Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(),
                eventAggregator,
                Proxy<IManageCommandQueue>(),
                Proxy<ISeriesBookLinkService>(),
                Proxy<ISeriesService>(),
                Proxy<IQualityProfileService>(),
                Proxy<IM4bConversionService>(),
                LogManager.GetLogger("ImportApprovedBooksNotificationFixture"));

            var localBook = new LocalBook
            {
                Path = "/downloads/complete/The House Across the Lake.epub",
                ExistingFile = true,
                Book = book,
                Author = author,
                Edition = new Edition
                {
                    Id = edition.Id,
                    BookId = book.Id,
                    Book = null,
                    Title = edition.Title,
                    Monitored = true,
                    IsEbook = true
                },
                Quality = new QualityModel { Quality = Quality.EPUB, Revision = new Revision() },
                Part = 1,
                PartCount = 1
            };

            var results = service.Import(
                new List<ImportDecision<LocalBook>> { new(localBook) },
                replaceExisting: true,
                downloadClientItem: new DownloadClientItem
                {
                    DownloadId = "download-id",
                    DownloadClientInfo = new DownloadClientItemClientInfo { Name = "qBittorrent", Type = "qbittorrent" }
                },
                importMode: ImportMode.Copy,
                cancellationToken: CancellationToken.None);

            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(results[0].Result, Is.EqualTo(ImportResultType.Imported));

            var importedEvents = eventAggregator.Events.OfType<BookImportedEvent>().ToList();
            Assert.That(importedEvents, Has.Count.EqualTo(1));
            Assert.That(importedEvents[0].Book.Id, Is.EqualTo(book.Id));
            Assert.That(importedEvents[0].Author.Id, Is.EqualTo(author.Id));
            Assert.That(importedEvents[0].NewDownload, Is.True);
            Assert.That(importedEvents[0].ImportedBooks, Has.Count.EqualTo(1));

            Assert.That(eventAggregator.Events.OfType<BookAddedEvent>(), Is.Empty);
        }

        [Test]
        public void should_publish_book_imported_event_for_new_download_when_book_file_edition_navigation_is_missing()
        {
            var qualityProfile = new QualityProfile
            {
                Id = 1,
                Name = "Ebooks",
                ProfileType = ProfileType.Ebook,
                UpgradeAllowed = true,
                Items = new List<QualityProfileQualityItem>
                {
                    new() { Allowed = true, Quality = Quality.EPUB }
                }
            };

            var author = new Author
            {
                Id = 57,
                Name = "Riley Sager",
                EbookRootFolderPath = "/ebooks",
                EbookPath = "/ebooks/Riley Sager",
                EbookQualityProfileId = qualityProfile.Id,
                EbookQualityProfile = qualityProfile
            };

            var book = new Book
            {
                Id = 3076,
                AuthorId = author.Id,
                Author = author,
                Title = "The House Across the Lake",
                CleanTitle = "thehouseacrossthelake",
                MediaType = BookMediaType.Ebook
            };

            var edition = new Edition
            {
                Id = 12387,
                BookId = book.Id,
                Book = null,
                Title = "The House Across the Lake",
                Monitored = true,
                IsEbook = true
            };

            var mediaFileService = new StubMediaFileService
            {
                ClearEditionOnAdd = true
            };
            var metadataTagService = new StubMetadataTagService();
            var mediaInfoExtractor = new StubMediaInfoExtractor();
            var eventAggregator = new CapturingEventAggregator();

            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)(object)bookService).Book = book;

            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)(object)authorService).Author = author;

            var editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
            ((EditionServiceProxy)(object)editionService).Edition = edition;
            ((EditionServiceProxy)(object)editionService).EditionsByBook = new List<Edition> { edition };

            var service = new ImportApprovedBooks(
                mediaFileService,
                metadataTagService,
                mediaInfoExtractor,
                authorService,
                bookService,
                editionService,
                Proxy<IRecycleBinProvider>(),
                Proxy<IExtraService>(),
                Proxy<IMoveBookFiles>(),
                Proxy<IHistoryService>(),
                Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(),
                eventAggregator,
                Proxy<IManageCommandQueue>(),
                Proxy<ISeriesBookLinkService>(),
                Proxy<ISeriesService>(),
                Proxy<IQualityProfileService>(),
                Proxy<IM4bConversionService>(),
                LogManager.GetLogger("ImportApprovedBooksNotificationFixture"));

            var localBook = new LocalBook
            {
                Path = "/downloads/complete/The House Across the Lake.epub",
                ExistingFile = true,
                Book = book,
                Author = author,
                Edition = new Edition
                {
                    Id = edition.Id,
                    BookId = book.Id,
                    Book = null,
                    Title = edition.Title,
                    Monitored = true,
                    IsEbook = true
                },
                Quality = new QualityModel { Quality = Quality.EPUB, Revision = new Revision() },
                Part = 1,
                PartCount = 1
            };

            var results = service.Import(
                new List<ImportDecision<LocalBook>> { new(localBook) },
                replaceExisting: true,
                downloadClientItem: new DownloadClientItem
                {
                    DownloadId = "download-id",
                    DownloadClientInfo = new DownloadClientItemClientInfo { Name = "qBittorrent", Type = "qbittorrent" }
                },
                importMode: ImportMode.Copy,
                cancellationToken: CancellationToken.None);

            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(results[0].Result, Is.EqualTo(ImportResultType.Imported));

            var importedEvents = eventAggregator.Events.OfType<BookImportedEvent>().ToList();
            Assert.That(importedEvents, Has.Count.EqualTo(1));
            Assert.That(importedEvents[0].Book.Id, Is.EqualTo(book.Id));
            Assert.That(importedEvents[0].Author.Id, Is.EqualTo(author.Id));
            Assert.That(importedEvents[0].NewDownload, Is.True);
            Assert.That(importedEvents[0].ImportedBooks, Has.Count.EqualTo(1));

            Assert.That(eventAggregator.Events.OfType<BookAddedEvent>(), Is.Empty);
        }

        [Test]
        public void should_publish_book_file_added_event_when_adopting_existing_unmapped_file()
        {
            var qualityProfile = new QualityProfile
            {
                Id = 1,
                Name = "Ebooks",
                ProfileType = ProfileType.Ebook,
                UpgradeAllowed = true,
                Items = new List<QualityProfileQualityItem>
                {
                    new() { Allowed = true, Quality = Quality.EPUB }
                }
            };

            var author = new Author
            {
                Id = 57,
                Name = "Riley Sager",
                EbookRootFolderPath = "/ebooks",
                EbookPath = "/ebooks/Riley Sager",
                EbookQualityProfileId = qualityProfile.Id,
                EbookQualityProfile = qualityProfile
            };

            var book = new Book
            {
                Id = 3076,
                AuthorId = author.Id,
                Author = author,
                Title = "The House Across the Lake",
                CleanTitle = "thehouseacrossthelake",
                MediaType = BookMediaType.Ebook
            };

            var edition = new Edition
            {
                Id = 12387,
                BookId = book.Id,
                Book = book,
                Title = "The House Across the Lake",
                Monitored = true,
                IsEbook = true
            };

            var path = "/ebooks/Riley Sager/The House Across the Lake.epub";
            var originalDateAdded = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            var orphan = new BookFile
            {
                Id = 8001,
                Path = path,
                EditionId = 0,
                DateAdded = originalDateAdded,
                CalibreId = 41
            };

            var mediaFileService = new StubMediaFileService
            {
                FileAtPath = orphan
            };
            var metadataTagService = new StubMetadataTagService();
            var mediaInfoExtractor = new StubMediaInfoExtractor();
            var eventAggregator = new CapturingEventAggregator();

            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)(object)bookService).Book = book;

            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)(object)authorService).Author = author;

            var editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
            ((EditionServiceProxy)(object)editionService).Edition = edition;
            ((EditionServiceProxy)(object)editionService).EditionsByBook = new List<Edition> { edition };

            var service = new ImportApprovedBooks(
                mediaFileService,
                metadataTagService,
                mediaInfoExtractor,
                authorService,
                bookService,
                editionService,
                Proxy<IRecycleBinProvider>(),
                Proxy<IExtraService>(),
                Proxy<IMoveBookFiles>(),
                Proxy<IHistoryService>(),
                Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(),
                eventAggregator,
                Proxy<IManageCommandQueue>(),
                Proxy<ISeriesBookLinkService>(),
                Proxy<ISeriesService>(),
                Proxy<IQualityProfileService>(),
                Proxy<IM4bConversionService>(),
                LogManager.GetLogger("ImportApprovedBooksNotificationFixture"));

            var localBook = new LocalBook
            {
                Path = path,
                ExistingFile = true,
                Book = book,
                Author = author,
                Edition = edition,
                Quality = new QualityModel { Quality = Quality.EPUB, Revision = new Revision() },
                RawTags = new RawFileTags
                {
                    AllTags = new Dictionary<string, List<string>>
                    {
                        ["title"] = new() { "The House Across the Lake" }
                    }
                },
                Part = 1,
                PartCount = 1
            };

            var results = service.Import(
                new List<ImportDecision<LocalBook>> { new(localBook) },
                replaceExisting: true,
                downloadClientItem: new DownloadClientItem
                {
                    DownloadId = "download-id",
                    DownloadClientInfo = new DownloadClientItemClientInfo { Name = "qBittorrent", Type = "qbittorrent" }
                },
                importMode: ImportMode.Copy,
                cancellationToken: CancellationToken.None);

            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(results[0].Result, Is.EqualTo(ImportResultType.Imported));
            Assert.That(mediaFileService.AddedFiles, Is.Empty);
            Assert.That(mediaFileService.UpdatedFiles, Has.Count.EqualTo(1));
            Assert.That(mediaFileService.DeletedFiles, Is.Empty);
            Assert.That(mediaFileService.FileAtPath, Is.SameAs(orphan));
            Assert.That(orphan.DateAdded, Is.GreaterThan(originalDateAdded));
            Assert.That(orphan.CalibreId, Is.EqualTo(41));

            var addedEvents = eventAggregator.Events.OfType<BookFileAddedEvent>().ToList();
            Assert.That(addedEvents, Has.Count.EqualTo(1));
            Assert.That(addedEvents[0].BookFile.Id, Is.EqualTo(orphan.Id));
            Assert.That(addedEvents[0].BookFile.EditionId, Is.EqualTo(edition.Id));
            Assert.That(addedEvents[0].BookFile.Edition.Book.Id, Is.EqualTo(book.Id));
        }

        [Test]
        public void should_not_delete_or_emit_added_event_when_reimporting_tracked_file_to_same_edition()
        {
            var context = CreateExistingEbookContext();
            var originalDateAdded = new DateTime(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc);
            var trackedFile = new BookFile
            {
                Id = 8002,
                Path = context.Path,
                EditionId = context.Edition.Id,
                Edition = context.Edition,
                DateAdded = originalDateAdded,
                CalibreId = 42,
                LastMatchAttempt = DateTime.UtcNow,
                MatchDetails = "APPLY_FAILED:OLD"
            };
            var mediaFileService = new StubMediaFileService { FileAtPath = trackedFile };
            var eventAggregator = new CapturingEventAggregator();

            var import = ImportExistingEbook(
                mediaFileService,
                eventAggregator,
                context.Author,
                context.Book,
                context.Edition,
                context.Path);

            Assert.That(import.Results, Has.Count.EqualTo(1));
            Assert.That(import.Results[0].Result, Is.EqualTo(ImportResultType.Imported));
            Assert.That(mediaFileService.AddedFiles, Is.Empty);
            Assert.That(mediaFileService.DeletedFiles, Is.Empty);
            Assert.That(mediaFileService.UpdatedFiles, Has.Count.EqualTo(1));
            Assert.That(mediaFileService.UpdatedFiles[0], Is.SameAs(trackedFile));
            Assert.That(mediaFileService.FileAtPath, Is.SameAs(trackedFile));
            Assert.That(trackedFile.Id, Is.EqualTo(8002));
            Assert.That(trackedFile.EditionId, Is.EqualTo(context.Edition.Id));
            Assert.That(trackedFile.DateAdded, Is.EqualTo(originalDateAdded));
            Assert.That(trackedFile.CalibreId, Is.EqualTo(42));
            Assert.That(trackedFile.LastMatchAttempt, Is.Null);
            Assert.That(trackedFile.MatchDetails, Is.Null);
            Assert.That(trackedFile.MatchProvenance.Mode, Is.EqualTo("Manual"));
            Assert.That(trackedFile.MatchProvenance.Route, Is.EqualTo("manual_selection"));
            Assert.That(trackedFile.MatchProvenance.SupportingSignals.Single().Type, Is.EqualTo("manual_selection"));
            Assert.That(eventAggregator.Events.OfType<BookFileAddedEvent>(), Is.Empty);
            Assert.That(import.EditionService.SetMonitoredCalls, Is.Empty);
        }

        [Test]
        public void should_update_in_place_and_emit_added_event_when_relinking_tracked_file_to_another_edition_of_same_book()
        {
            var context = CreateExistingEbookContext(monitored: false);
            var previousEdition = new Edition
            {
                Id = 12386,
                BookId = context.Book.Id,
                Book = context.Book,
                Title = context.Book.Title,
                Monitored = true,
                IsEbook = true
            };
            var originalDateAdded = new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc);
            var trackedFile = new BookFile
            {
                Id = 8003,
                Path = context.Path,
                EditionId = previousEdition.Id,
                Edition = previousEdition,
                DateAdded = originalDateAdded,
                CalibreId = 43
            };
            var mediaFileService = new StubMediaFileService { FileAtPath = trackedFile };
            var eventAggregator = new CapturingEventAggregator();
            var operationOrder = new List<string>();

            var import = ImportExistingEbook(
                mediaFileService,
                eventAggregator,
                context.Author,
                context.Book,
                context.Edition,
                context.Path,
                editionsByBook: new[] { previousEdition, context.Edition },
                operationOrder: operationOrder);

            Assert.That(import.Results, Has.Count.EqualTo(1));
            Assert.That(import.Results[0].Result, Is.EqualTo(ImportResultType.Imported));
            Assert.That(mediaFileService.AddedFiles, Is.Empty);
            Assert.That(mediaFileService.DeletedFiles, Is.Empty);
            Assert.That(mediaFileService.UpdatedFiles, Has.Count.EqualTo(1));
            Assert.That(mediaFileService.UpdatedFiles[0], Is.SameAs(trackedFile));
            Assert.That(mediaFileService.FileAtPath, Is.SameAs(trackedFile));
            Assert.That(trackedFile.Id, Is.EqualTo(8003));
            Assert.That(trackedFile.EditionId, Is.EqualTo(context.Edition.Id));
            Assert.That(trackedFile.Edition, Is.SameAs(context.Edition));
            Assert.That(trackedFile.DateAdded, Is.EqualTo(originalDateAdded));
            Assert.That(trackedFile.CalibreId, Is.EqualTo(43));

            var addedEvents = eventAggregator.Events.OfType<BookFileAddedEvent>().ToList();
            Assert.That(addedEvents, Has.Count.EqualTo(1));
            Assert.That(addedEvents[0].BookFile, Is.SameAs(trackedFile));
            Assert.That(import.EditionService.SetMonitoredCalls, Has.Count.EqualTo(1));
            Assert.That(import.EditionService.SetMonitoredCalls[0].Edition, Is.SameAs(context.Edition));
            Assert.That(import.EditionService.SetMonitoredCalls[0].IsManualSelection, Is.False);
            Assert.That(operationOrder, Is.EqualTo(new[] { "update", "monitor", "event", "tags" }));
        }

        [Test]
        public void should_complete_relink_and_emit_event_when_tag_write_fails()
        {
            var context = CreateExistingEbookContext(monitored: false);
            var previousEdition = new Edition
            {
                Id = 12386,
                BookId = context.Book.Id,
                Book = context.Book,
                Title = context.Book.Title,
                Monitored = true,
                IsEbook = true
            };
            var trackedFile = new BookFile
            {
                Id = 8004,
                Path = context.Path,
                EditionId = previousEdition.Id,
                Edition = previousEdition,
                DateAdded = new DateTime(2024, 4, 5, 6, 7, 8, DateTimeKind.Utc),
                CalibreId = 44
            };
            var mediaFileService = new StubMediaFileService { FileAtPath = trackedFile };
            var eventAggregator = new CapturingEventAggregator();
            var metadataTagService = new StubMetadataTagService { ThrowOnWrite = true };
            var operationOrder = new List<string>();

            var import = ImportExistingEbook(
                mediaFileService,
                eventAggregator,
                context.Author,
                context.Book,
                context.Edition,
                context.Path,
                editionsByBook: new[] { previousEdition, context.Edition },
                metadataTagService: metadataTagService,
                operationOrder: operationOrder);

            Assert.That(import.Results, Has.Count.EqualTo(1));
            Assert.That(import.Results[0].Result, Is.EqualTo(ImportResultType.Imported));
            Assert.That(mediaFileService.AddedFiles, Is.Empty);
            Assert.That(mediaFileService.UpdatedFiles, Has.Count.EqualTo(1));
            Assert.That(mediaFileService.UpdatedFiles[0], Is.SameAs(trackedFile));
            Assert.That(mediaFileService.DeletedFiles, Is.Empty);
            Assert.That(eventAggregator.Events.OfType<BookFileAddedEvent>().ToList(), Has.Count.EqualTo(1));
            Assert.That(import.EditionService.SetMonitoredCalls, Has.Count.EqualTo(1));
            Assert.That(import.EditionService.SetMonitoredCalls[0].Edition, Is.SameAs(context.Edition));
            Assert.That(operationOrder, Is.EqualTo(new[] { "update", "monitor", "event", "tags" }));
        }

        [Test]
        public void should_preserve_same_book_relocation_when_existing_file_is_false()
        {
            var context = CreateExistingEbookContext();
            var originalPath = context.Path;
            var destinationPath = "/ebooks/Riley Sager/The House Across the Lake (Renamed).epub";
            var originalDateAdded = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
            var trackedFile = new BookFile
            {
                Id = 8005,
                Path = originalPath,
                EditionId = context.Edition.Id,
                Edition = context.Edition,
                DateAdded = originalDateAdded,
                CalibreId = 45
            };
            var mediaFileService = new StubMediaFileService { FileAtPath = trackedFile };
            var eventAggregator = new CapturingEventAggregator();
            var mover = new StubMoveBookFiles { DestinationPath = destinationPath };

            var import = ImportExistingEbook(
                mediaFileService,
                eventAggregator,
                context.Author,
                context.Book,
                context.Edition,
                originalPath,
                existingFile: false,
                bookFileMover: mover,
                importMode: ImportMode.Move);

            Assert.That(import.Results, Has.Count.EqualTo(1));
            Assert.That(import.Results[0].Result, Is.EqualTo(ImportResultType.Imported));
            Assert.That(mover.MoveCalls, Is.EqualTo(1));
            Assert.That(mediaFileService.AddedFiles, Is.Empty);
            Assert.That(mediaFileService.DeletedFiles, Is.Empty);
            Assert.That(mediaFileService.UpdatedFiles, Has.Count.EqualTo(1));
            Assert.That(mediaFileService.UpdatedFiles[0], Is.SameAs(trackedFile));
            Assert.That(trackedFile.Id, Is.EqualTo(8005));
            Assert.That(trackedFile.Path, Is.EqualTo(destinationPath));
            Assert.That(trackedFile.DateAdded, Is.EqualTo(originalDateAdded));
            Assert.That(trackedFile.CalibreId, Is.EqualTo(45));
            Assert.That(eventAggregator.Events.OfType<BookFileAddedEvent>(), Is.Empty);

            var renamedEvent = eventAggregator.Events.OfType<BookFileRenamedEvent>().Single();
            Assert.That(renamedEvent.BookFile, Is.SameAs(trackedFile));
            Assert.That(renamedEvent.OriginalPath, Is.EqualTo(originalPath));
        }

        [Test]
        public void should_reject_file_tracked_to_a_different_book_without_mutating_it()
        {
            var context = CreateExistingEbookContext();
            var otherBook = new Book
            {
                Id = 9999,
                AuthorId = context.Author.Id,
                Author = context.Author,
                Title = "A Different Book",
                CleanTitle = "adifferentbook",
                MediaType = BookMediaType.Ebook
            };
            var otherEdition = new Edition
            {
                Id = 19999,
                BookId = otherBook.Id,
                Book = otherBook,
                Title = otherBook.Title,
                Monitored = true,
                IsEbook = true
            };
            var trackedFile = new BookFile
            {
                Id = 8006,
                Path = context.Path,
                EditionId = otherEdition.Id,
                Edition = otherEdition,
                DateAdded = new DateTime(2024, 6, 7, 8, 9, 10, DateTimeKind.Utc),
                CalibreId = 46
            };
            var mediaFileService = new StubMediaFileService { FileAtPath = trackedFile };
            var eventAggregator = new CapturingEventAggregator();

            var import = ImportExistingEbook(
                mediaFileService,
                eventAggregator,
                context.Author,
                context.Book,
                context.Edition,
                context.Path);

            Assert.That(import.Results, Has.Count.EqualTo(1));
            Assert.That(import.Results[0].Result, Is.EqualTo(ImportResultType.Skipped));
            Assert.That(import.Results[0].Errors.Single(), Does.Contain("linked to a different book"));
            Assert.That(mediaFileService.AddedFiles, Is.Empty);
            Assert.That(mediaFileService.UpdatedFiles, Is.Empty);
            Assert.That(mediaFileService.DeletedFiles, Is.Empty);
            Assert.That(mediaFileService.FileAtPath, Is.SameAs(trackedFile));
            Assert.That(trackedFile.Id, Is.EqualTo(8006));
            Assert.That(trackedFile.EditionId, Is.EqualTo(otherEdition.Id));
            Assert.That(trackedFile.CalibreId, Is.EqualTo(46));
            Assert.That(eventAggregator.Events, Is.Empty);
            Assert.That(import.EditionService.SetMonitoredCalls, Is.Empty);
        }
    }
}
