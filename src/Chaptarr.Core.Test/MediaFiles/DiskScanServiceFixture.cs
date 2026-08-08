using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class DiskScanServiceFixture
    {
        private sealed class StubMediaFileService : IMediaFileService
        {
            public BookFile ExistingFile { get; set; }
            public List<BookFile> ExistingFiles { get; set; } = new List<BookFile>();
            public List<BookFile> UnmappedFiles { get; set; } = new List<BookFile>();
            public int AddCalls { get; private set; }
            public int AddManyCalls { get; private set; }
            public int UpdateCalls { get; private set; }
            public int GetFileWithPathListCalls { get; private set; }
            public int DeleteManyCalls { get; private set; }
            public bool PersistAdds { get; set; } = true;
            public List<BookFile> AddedMany { get; private set; } = new List<BookFile>();
            public List<BookFile> UpdatedMany { get; private set; } = new List<BookFile>();
            public List<BookFile> DeletedMany { get; private set; } = new List<BookFile>();

            public BookFile Add(BookFile bookFile)
            {
                AddCalls++;
                if (PersistAdds && bookFile != null)
                {
                    if (bookFile.Id <= 0)
                    {
                        bookFile.Id = 1000 + AddCalls + AddedMany.Count;
                    }

                    ExistingFiles.RemoveAll(file => string.Equals(file?.Path, bookFile.Path, StringComparison.Ordinal));
                    ExistingFiles.Add(bookFile);
                }

                return bookFile;
            }

            public void AddMany(List<BookFile> bookFiles)
            {
                AddManyCalls++;
                AddedMany.AddRange(bookFiles);
                if (!PersistAdds)
                {
                    return;
                }

                foreach (var bookFile in bookFiles ?? new List<BookFile>())
                {
                    if (bookFile.Id <= 0)
                    {
                        bookFile.Id = 2000 + ExistingFiles.Count;
                    }

                    ExistingFiles.RemoveAll(file => string.Equals(file?.Path, bookFile.Path, StringComparison.Ordinal));
                    ExistingFiles.Add(bookFile);
                }
            }
            public void Update(BookFile bookFile)
            {
                UpdateCalls++;
                if (bookFile != null)
                {
                    UpdatedMany.Add(bookFile);
                }
            }

            public void Update(List<BookFile> bookFiles)
            {
                UpdateCalls++;
                UpdatedMany.AddRange(bookFiles ?? new List<BookFile>());
            }
            public void Delete(BookFile bookFile, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public void DeleteMany(List<BookFile> bookFiles, DeleteMediaFileReason reason)
            {
                DeleteManyCalls++;
                DeletedMany.AddRange(bookFiles ?? new List<BookFile>());
            }
            public List<IFileInfo> FilterUnchangedFiles(List<IFileInfo> files, FilterFilesType filter) => files;
            public List<BookFile> GetFilesByAuthor(int authorId) => throw new NotImplementedException();
            public List<BookFile> GetFilesByBook(int bookId) => throw new NotImplementedException();
            public List<BookFile> GetFilesByBooks(List<int> bookIds) => throw new NotImplementedException();
            public List<BookFile> GetFilesByEdition(int editionId) => throw new NotImplementedException();
            public List<BookFile> GetUnmappedFiles() => UnmappedFiles.Where(f => f.EditionId == 0).ToList();
            public List<BookFile> GetUnmappedFiles(string mediaType) => GetUnmappedFiles()
                .Where(f => string.IsNullOrWhiteSpace(mediaType) || string.Equals(f.MediaType, mediaType, StringComparison.OrdinalIgnoreCase))
                .ToList();
            public List<BookFile> GetUnmappedFiles(IEnumerable<int> ids, string mediaType)
            {
                var requestedIds = ids?.ToHashSet() ?? new HashSet<int>();
                return GetUnmappedFiles(mediaType)
                    .Where(f => requestedIds.Contains(f.Id))
                    .ToList();
            }
            public BookFile Get(int id) => throw new NotImplementedException();
            public List<BookFile> Get(IEnumerable<int> ids) => throw new NotImplementedException();
            public List<BookFile> GetFilesWithBasePath(string path)
            {
                var files = ExistingFiles.ToList();
                if (ExistingFile != null)
                {
                    files.Add(ExistingFile);
                }

                return files
                    .Where(file => file?.Path != null && file.Path.StartsWith(path, StringComparison.Ordinal))
                    .ToList();
            }
            public List<BookFile> GetFilesWithBasePath(string path, string mediaType) => throw new NotImplementedException();
            public List<BookFile> GetFileWithPath(List<string> path)
            {
                GetFileWithPathListCalls++;
                var paths = path?.ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>();
                var files = ExistingFiles.ToList();

                if (ExistingFile != null)
                {
                    files.Add(ExistingFile);
                }

                return files
                    .Where(file => file?.Path != null && paths.Contains(file.Path))
                    .ToList();
            }

            public BookFile GetFileWithPath(string path)
            {
                if (ExistingFile != null && string.Equals(ExistingFile.Path, path, StringComparison.Ordinal))
                {
                    return ExistingFile;
                }

                return null;
            }

            public void UpdateMediaInfo(List<BookFile> bookFiles) => throw new NotImplementedException();
            public List<BookFile> GetFilesByAuthorAndMediaType(int authorId, string mediaType) => throw new NotImplementedException();
        }

        private class IngestQueueRepositoryProxy : DispatchProxy
        {
            public List<string> PurgedPaths { get; } = new List<string>();
            public int PurgePathsCalls { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IIngestQueueRepository.PurgeUnderPath))
                {
                    PurgedPaths.Add((string)args[0]);
                    return 1;
                }

                if (targetMethod?.Name == nameof(IIngestQueueRepository.PurgePaths))
                {
                    PurgePathsCalls++;
                    var paths = ((IEnumerable<string>)args[0]).ToList();
                    PurgedPaths.AddRange(paths);
                    return paths.Count;
                }

                throw new NotImplementedException($"Test proxy does not implement {typeof(IIngestQueueRepository).Name}.{targetMethod?.Name}");
            }
        }

        private class DiskProviderProxy : DispatchProxy
        {
            public bool FolderExistsResult { get; set; } = true;
            public bool FileExistsResult { get; set; } = true;
            public long FileLength { get; set; } = 100;
            public DateTime FileLastWriteTime { get; set; } = DateTime.UtcNow;

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == "FolderExists")
                {
                    return FolderExistsResult;
                }

                if (targetMethod?.Name == "GetFileInfo")
                {
                    var fileInfo = DispatchProxy.Create<IFileInfo, FileInfoProxy>();
                    var proxy = (FileInfoProxy)(object)fileInfo;
                    proxy.ExistsResult = FileExistsResult;
                    proxy.Length = FileLength;
                    proxy.LastWriteTime = FileLastWriteTime;
                    proxy.FullName = args != null && args.Length > 0 ? args[0] as string : null;
                    return fileInfo;
                }

                return GetDefaultValue(targetMethod?.ReturnType);
            }
        }

        private class FileInfoProxy : DispatchProxy
        {
            public bool ExistsResult { get; set; }
            public long Length { get; set; }
            public DateTime LastWriteTime { get; set; }
            public string FullName { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    "get_Exists" => ExistsResult,
                    "get_Length" => Length,
                    "get_LastWriteTime" => LastWriteTime,
                    "get_LastWriteTimeUtc" => LastWriteTime.ToUniversalTime(),
                    "get_FullName" => FullName,
                    "get_Name" => System.IO.Path.GetFileName(FullName),
                    _ => GetDefaultValue(targetMethod?.ReturnType)
                };
            }
        }

        private sealed class StubMetadataTagService : IMetadataTagService
        {
            public Dictionary<string, List<string>> Tags { get; set; } = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            public int? DurationSeconds { get; set; }
            public int ReadAllTagsAndDurationCalls { get; private set; }

            public Dictionary<string, List<string>> ReadAllTags(IFileInfo file) => Tags;
            public (Dictionary<string, List<string>> Tags, int? DurationSeconds) ReadAllTagsAndDuration(IFileInfo file)
            {
                ReadAllTagsAndDurationCalls++;
                return (Tags, DurationSeconds);
            }
            public string ReadAllTagsAsJson(IFileInfo file) => "{}";
            public void WriteTags(BookFile trackfile, bool newDownload, bool force = false) => throw new NotImplementedException();
            public void SyncTags(List<Edition> books) => throw new NotImplementedException();
            public List<RetagBookFilePreview> GetRetagPreviewsByAuthor(int authorId) => throw new NotImplementedException();
            public List<RetagBookFilePreview> GetRetagPreviewsByBook(int authorId) => throw new NotImplementedException();
        }

        private class RootFolderServiceProxy : DispatchProxy
        {
            public RootFolder RootFolder { get; set; } = new RootFolder
            {
                Id = 1,
                Path = "/books",
                FolderType = FolderType.Audiobook
            };

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IRootFolderService.GetBestRootFolder):
                        return RootFolder;
                    case nameof(IRootFolderService.All):
                        return new List<RootFolder> { RootFolder };
                    case nameof(IRootFolderService.GetBestRootFolderPath):
                        return RootFolder.Path;
                    default:
                        return GetDefaultValue(targetMethod?.ReturnType);
                }
            }
        }

        private class ImportOrchestratorProxy : DispatchProxy
        {
            public OrchestratorImportResult Result { get; set; } = new OrchestratorImportResult();
            public int Calls { get; private set; }
            public List<IReadOnlyCollection<string>> ForceStagePaths { get; } = new List<IReadOnlyCollection<string>>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IImportOrchestrator.ProcessFilesAsync))
                {
                    Calls++;
                    ForceStagePaths.Add((IReadOnlyCollection<string>)args[3]);
                    return Task.FromResult(Result);
                }

                return GetDefaultValue(targetMethod?.ReturnType);
            }
        }

        private class MediaFileTableCleanupServiceProxy : DispatchProxy
        {
            public List<List<string>> CleanedPaths { get; } = new List<List<string>>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IMediaFileTableCleanupService.Clean))
                {
                    CleanedPaths.Add(((List<string>)args[1]).ToList());
                }

                return null;
            }
        }

        private class AuthorServiceProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IAuthorService.GetAuthors))
                {
                    return new List<Author>();
                }

                return GetDefaultValue(targetMethod?.ReturnType);
            }
        }

        private class EventAggregatorProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return null;
            }
        }

        private sealed class StubCommandQueueManager : IManageCommandQueue
        {
            public CommandModel Get(int id) => null;

            public List<CommandModel> PushMany<TCommand>(List<TCommand> commands) where TCommand : Command => throw new NotImplementedException();
            public CommandModel Push<TCommand>(TCommand command, CommandPriority priority = CommandPriority.Normal, CommandTrigger trigger = CommandTrigger.Unspecified) where TCommand : Command => throw new NotImplementedException();
            public CommandModel Push(string commandName, DateTime? lastExecutionTime, DateTime? lastStartTime, CommandPriority priority = CommandPriority.Normal, CommandTrigger trigger = CommandTrigger.Unspecified) => throw new NotImplementedException();
            public IEnumerable<CommandModel> Queue(CancellationToken cancellationToken) => throw new NotImplementedException();
            public List<CommandModel> All() => throw new NotImplementedException();
            public List<CommandModel> GetStarted() => throw new NotImplementedException();
            public void SetMessage(CommandModel command, string message) => throw new NotImplementedException();
            public void TouchProgress(CommandModel command) => throw new NotImplementedException();
            public void SetResult(CommandModel command, CommandResult result) => throw new NotImplementedException();
            public void Start(CommandModel command) => throw new NotImplementedException();
            public void Complete(CommandModel command, string message) => throw new NotImplementedException();
            public void Fail(CommandModel command, string message, Exception e) => throw new NotImplementedException();
            public void Requeue() => throw new NotImplementedException();
            public void Cancel(int id) => throw new NotImplementedException();
            public void Pause(int id) => throw new NotImplementedException();
            public void Resume(int id) => throw new NotImplementedException();
            public void CleanCommands() => throw new NotImplementedException();
            public CancellationToken GetCancellationToken(int commandId) => throw new NotImplementedException();
            public void RegisterCancellationToken(int commandId, CancellationTokenSource cancellationTokenSource) => throw new NotImplementedException();
            public void UnregisterCancellationToken(int commandId) => throw new NotImplementedException();
        }

        private static object GetDefaultValue(Type type)
        {
            if (type == null || type == typeof(void))
            {
                return null;
            }

            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }

        private static DiskScanService CreateScanService(
            bool folderExists,
            OrchestratorImportResult orchestratorResult,
            out ImportOrchestratorProxy importOrchestratorProxy,
            out MediaFileTableCleanupServiceProxy cleanupProxy,
            IMediaFileService mediaFileService = null,
            IMetadataTagService metadataTagService = null,
            RootFolder rootFolder = null)
        {
            var diskProvider = DispatchProxy.Create<NzbDrone.Common.Disk.IDiskProvider, DiskProviderProxy>();
            ((DiskProviderProxy)(object)diskProvider).FolderExistsResult = folderExists;

            var rootFolderService = DispatchProxy.Create<IRootFolderService, RootFolderServiceProxy>();
            ((RootFolderServiceProxy)(object)rootFolderService).RootFolder = rootFolder ?? new RootFolder
            {
                Id = 1,
                Path = "/books",
                FolderType = FolderType.Audiobook
            };

            var importOrchestrator = DispatchProxy.Create<IImportOrchestrator, ImportOrchestratorProxy>();
            importOrchestratorProxy = (ImportOrchestratorProxy)(object)importOrchestrator;
            importOrchestratorProxy.Result = orchestratorResult;

            var cleanupService = DispatchProxy.Create<IMediaFileTableCleanupService, MediaFileTableCleanupServiceProxy>();
            cleanupProxy = (MediaFileTableCleanupServiceProxy)(object)cleanupService;

            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            var eventAggregator = DispatchProxy.Create<IEventAggregator, EventAggregatorProxy>();

            return new DiskScanService(
                configService: null,
                diskProvider: diskProvider,
                calibre: null,
                mediaFileService: mediaFileService ?? new StubMediaFileService(),
                metadataTagService: metadataTagService,
                ingestQueueRepository: null,
                importOrchestrator: importOrchestrator,
                authorService: authorService,
                rootFolderService: rootFolderService,
                mediaFileTableCleanupService: cleanupService,
                commandQueueManager: new StubCommandQueueManager(),
                eventAggregator: eventAggregator,
                logger: LogManager.GetCurrentClassLogger());
        }

        [Test]
        public void should_not_downgrade_tracked_files_to_unmapped()
        {
            var filePath = "/books/audiobooks/some-book.mp3";
            var existing = new BookFile { Path = filePath, EditionId = 123 };
            var mediaFileService = new StubMediaFileService { ExistingFile = existing };

            var sut = new DiskScanService(
                configService: null,
                diskProvider: null,
                calibre: null,
                mediaFileService: mediaFileService,
                metadataTagService: null,
                ingestQueueRepository: null,
                importOrchestrator: null,
                authorService: null,
                rootFolderService: null,
                mediaFileTableCleanupService: null,
                commandQueueManager: null,
                eventAggregator: null,
                logger: LogManager.GetCurrentClassLogger());

            var method = typeof(DiskScanService).GetMethod("SaveUnmappedFiles", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            method.Invoke(sut, new object[]
            {
                new List<UnmappedFile> { new UnmappedFile { FilePath = filePath, Reason = "NO_MATCH_HOLY_GRAIL" } }
            });

            Assert.That(existing.EditionId, Is.EqualTo(123));
            Assert.That(mediaFileService.UpdateCalls, Is.EqualTo(0));
            Assert.That(mediaFileService.AddCalls, Is.EqualTo(0));
            Assert.That(mediaFileService.AddManyCalls, Is.EqualTo(0));
        }

        [Test]
        public void should_hydrate_existing_unmapped_file_metadata()
        {
            var filePath = "/books/audiobooks/attack-surface/01.m4b";
            var existing = new BookFile { Path = filePath, EditionId = 0 };
            var mediaFileService = new StubMediaFileService { ExistingFile = existing };
            var metadataTagService = new StubMetadataTagService
            {
                Tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ALBUM"] = new List<string> { "Attack Surface" },
                    ["ALBUMARTIST"] = new List<string> { "Amber Benson" }
                },
                DurationSeconds = 3017
            };

            var diskProvider = DispatchProxy.Create<NzbDrone.Common.Disk.IDiskProvider, DiskProviderProxy>();
            var sut = new DiskScanService(
                configService: null,
                diskProvider: diskProvider,
                calibre: null,
                mediaFileService: mediaFileService,
                metadataTagService: metadataTagService,
                ingestQueueRepository: null,
                importOrchestrator: null,
                authorService: null,
                rootFolderService: null,
                mediaFileTableCleanupService: null,
                commandQueueManager: null,
                eventAggregator: null,
                logger: LogManager.GetCurrentClassLogger());

            var method = typeof(DiskScanService).GetMethod("SaveUnmappedFiles", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            method.Invoke(sut, new object[]
            {
                new List<UnmappedFile> { new UnmappedFile { FilePath = filePath, Reason = "NO_MATCH_HOLY_GRAIL" } }
            });

            Assert.That(existing.EditionId, Is.EqualTo(0));
            Assert.That(existing.AllTags?["ALBUM"], Is.EquivalentTo(new[] { "Attack Surface" }));
            Assert.That(existing.AllTags?["ALBUMARTIST"], Is.EquivalentTo(new[] { "Amber Benson" }));
            Assert.That(existing.DurationSeconds, Is.EqualTo(3017));
            Assert.That(mediaFileService.UpdateCalls, Is.EqualTo(1));
            Assert.That(mediaFileService.AddManyCalls, Is.EqualTo(0));
        }

        [Test]
        public void should_not_rehydrate_existing_unmapped_ebook_when_tags_are_already_present()
        {
            var filePath = "/books/ebooks/attack-surface/Attack Surface.epub";
            var existing = new BookFile
            {
                Path = filePath,
                EditionId = 0,
                AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["TITLE"] = new List<string> { "Attack Surface" },
                    ["AUTHOR"] = new List<string> { "Cory Doctorow" }
                }
            };
            var mediaFileService = new StubMediaFileService { ExistingFile = existing };
            var metadataTagService = new StubMetadataTagService
            {
                Tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["TITLE"] = new List<string> { "Attack Surface" }
                }
            };

            var diskProvider = DispatchProxy.Create<NzbDrone.Common.Disk.IDiskProvider, DiskProviderProxy>();
            var sut = new DiskScanService(
                configService: null,
                diskProvider: diskProvider,
                calibre: null,
                mediaFileService: mediaFileService,
                metadataTagService: metadataTagService,
                ingestQueueRepository: null,
                importOrchestrator: null,
                authorService: null,
                rootFolderService: null,
                mediaFileTableCleanupService: null,
                commandQueueManager: null,
                eventAggregator: null,
                logger: LogManager.GetCurrentClassLogger());

            var method = typeof(DiskScanService).GetMethod("SaveUnmappedFiles", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            method.Invoke(sut, new object[]
            {
                new List<UnmappedFile> { new UnmappedFile { FilePath = filePath, Reason = "NO_MATCH_HOLY_GRAIL" } }
            });

            Assert.That(metadataTagService.ReadAllTagsAndDurationCalls, Is.EqualTo(0));
            Assert.That(mediaFileService.UpdateCalls, Is.EqualTo(0));
            Assert.That(mediaFileService.AddManyCalls, Is.EqualTo(0));
        }

        [Test]
        public void specific_file_retry_should_purge_stale_staging_rows_for_selected_paths()
        {
            var ingestQueue = DispatchProxy.Create<IIngestQueueRepository, IngestQueueRepositoryProxy>();
            var ingestQueueProxy = (IngestQueueRepositoryProxy)(object)ingestQueue;
            var sut = new DiskScanService(
                configService: null,
                diskProvider: null,
                calibre: null,
                mediaFileService: null,
                metadataTagService: null,
                ingestQueueRepository: ingestQueue,
                importOrchestrator: null,
                authorService: null,
                rootFolderService: null,
                mediaFileTableCleanupService: null,
                commandQueueManager: null,
                eventAggregator: null,
                logger: LogManager.GetCurrentClassLogger());

            var method = typeof(DiskScanService).GetMethod("PurgeSpecificFileStaging", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            method.Invoke(sut, new object[]
            {
                new List<string>
                {
                    "/books/books/Jim Butcher/Storm Front/Storm Front.m4b",
                    "/books/books/Jim Butcher/Storm Front/Storm Front.m4b",
                    "/books/books/Jim Butcher/Fool Moon/Fool Moon.m4b"
                }
            });

            Assert.That(ingestQueueProxy.PurgedPaths, Is.EquivalentTo(new[]
            {
                "/books/books/Jim Butcher/Storm Front/Storm Front.m4b",
                "/books/books/Jim Butcher/Fool Moon/Fool Moon.m4b"
            }));
            Assert.That(ingestQueueProxy.PurgedPaths.Count, Is.EqualTo(2));
            Assert.That(ingestQueueProxy.PurgePathsCalls, Is.EqualTo(1));
        }

        [Test]
        public void rescan_unmapped_all_should_resolve_paths_server_side_and_filter_media_type()
        {
            var mediaFileService = new StubMediaFileService
            {
                UnmappedFiles = new List<BookFile>
                {
                    new BookFile { Id = 1, Path = "/books/audio/one.mp3", EditionId = 0, MediaType = "audiobook" },
                    new BookFile { Id = 2, Path = "/books/ebook/two.epub", EditionId = 0, MediaType = "ebook" },
                    new BookFile { Id = 3, Path = "/books/audio/mapped.mp3", EditionId = 42, MediaType = "audiobook" }
                }
            };

            var sut = CreateScanService(
                folderExists: true,
                orchestratorResult: new OrchestratorImportResult(),
                out var importOrchestratorProxy,
                out _,
                mediaFileService);

            sut.Execute(new RescanFoldersCommand
            {
                Filter = FilterFilesType.None,
                MediaType = "audiobook",
                UnmappedFiles = new UnmappedFilesSelection { Scope = "all" }
            });

            Assert.That(importOrchestratorProxy.Calls, Is.EqualTo(1));
            Assert.That(importOrchestratorProxy.ForceStagePaths.Single(), Is.EquivalentTo(new[] { "/books/audio/one.mp3" }));
        }

        [Test]
        public void rescan_unmapped_all_should_apply_explicit_exclusions()
        {
            var mediaFileService = new StubMediaFileService
            {
                UnmappedFiles = new List<BookFile>
                {
                    new BookFile { Id = 1, Path = "/books/audio/one.mp3", EditionId = 0, MediaType = "audiobook" },
                    new BookFile { Id = 2, Path = "/books/audio/two.mp3", EditionId = 0, MediaType = "audiobook" }
                }
            };

            var sut = CreateScanService(
                folderExists: true,
                orchestratorResult: new OrchestratorImportResult(),
                out var importOrchestratorProxy,
                out _,
                mediaFileService);

            sut.Execute(new RescanFoldersCommand
            {
                Filter = FilterFilesType.None,
                MediaType = "audiobook",
                UnmappedFiles = new UnmappedFilesSelection
                {
                    Scope = "all",
                    ExceptBookFileIds = new List<int> { 2 }
                }
            });

            Assert.That(importOrchestratorProxy.Calls, Is.EqualTo(1));
            Assert.That(importOrchestratorProxy.ForceStagePaths.Single(), Is.EquivalentTo(new[] { "/books/audio/one.mp3" }));
        }

        [Test]
        public void rescan_unmapped_scope_should_preserve_existing_row_while_reporting_current_failure()
        {
            var existing = new BookFile { Id = 1, Path = "/books/audio/one.mp3", EditionId = 0, MediaType = "audiobook" };
            var mediaFileService = new StubMediaFileService
            {
                UnmappedFiles = new List<BookFile> { existing },
                ExistingFiles = new List<BookFile> { existing }
            };

            var sut = CreateScanService(
                folderExists: true,
                orchestratorResult: new OrchestratorImportResult
                {
                    UnmappedFiles = new List<UnmappedFile>
                    {
                        new UnmappedFile { FilePath = "/books/audio/one.mp3", Reason = "NO_MATCH" }
                    }
                },
                out _,
                out _,
                mediaFileService);

            sut.Execute(new RescanFoldersCommand
            {
                Filter = FilterFilesType.None,
                MediaType = "audiobook",
                UnmappedFiles = new UnmappedFilesSelection { Scope = "all" }
            });

            Assert.That(mediaFileService.GetFileWithPathListCalls, Is.GreaterThan(0));
            Assert.That(mediaFileService.AddManyCalls, Is.EqualTo(0));
            Assert.That(mediaFileService.AddCalls, Is.EqualTo(0));
        }

        [Test]
        public void rescan_unmapped_selected_should_ignore_stale_mapped_or_wrong_media_type_ids()
        {
            var mediaFileService = new StubMediaFileService
            {
                UnmappedFiles = new List<BookFile>
                {
                    new BookFile { Id = 1, Path = "/books/audio/one.mp3", EditionId = 0, MediaType = "audiobook" },
                    new BookFile { Id = 2, Path = "/books/ebook/two.epub", EditionId = 0, MediaType = "ebook" },
                    new BookFile { Id = 3, Path = "/books/audio/mapped.mp3", EditionId = 42, MediaType = "audiobook" }
                }
            };

            var sut = CreateScanService(
                folderExists: true,
                orchestratorResult: new OrchestratorImportResult(),
                out var importOrchestratorProxy,
                out _,
                mediaFileService);

            sut.Execute(new RescanFoldersCommand
            {
                Filter = FilterFilesType.None,
                MediaType = "audiobook",
                UnmappedFiles = new UnmappedFilesSelection
                {
                    Scope = "selected",
                    BookFileIds = new List<int> { 1, 2, 3, 999 }
                }
            });

            Assert.That(importOrchestratorProxy.Calls, Is.EqualTo(1));
            Assert.That(importOrchestratorProxy.ForceStagePaths.Single(), Is.EquivalentTo(new[] { "/books/audio/one.mp3" }));
        }

        [Test]
        public void rescan_unmapped_selected_should_reject_empty_id_list()
        {
            var sut = CreateScanService(
                folderExists: true,
                orchestratorResult: new OrchestratorImportResult(),
                out _,
                out _);

            var ex = Assert.Throws<ArgumentException>(() => sut.Execute(new RescanFoldersCommand
            {
                Filter = FilterFilesType.None,
                UnmappedFiles = new UnmappedFilesSelection
                {
                    Scope = "selected",
                    BookFileIds = new List<int>()
                }
            }));

            Assert.That(ex.Message, Does.Contain("bookFileId"));
        }

        [Test]
        public void scan_should_skip_cleanup_when_folder_is_not_visible()
        {
            var sut = CreateScanService(
                folderExists: false,
                orchestratorResult: new OrchestratorImportResult
                {
                    CleanupSafe = true,
                    ScannedFilePaths = new List<string> { "/books/a.mp3" }
                },
                out var importOrchestratorProxy,
                out var cleanupProxy);

            sut.Scan(new List<string> { "/books" }, authorIds: new List<int>());

            Assert.That(importOrchestratorProxy.Calls, Is.EqualTo(0));
            Assert.That(cleanupProxy.CleanedPaths, Is.Empty);
        }

        [Test]
        public void scan_should_skip_cleanup_when_orchestrator_result_is_not_cleanup_safe()
        {
            var sut = CreateScanService(
                folderExists: true,
                orchestratorResult: new OrchestratorImportResult
                {
                    CleanupSafe = false,
                    ScannedFilePaths = new List<string> { "/books/a.mp3" }
                },
                out var importOrchestratorProxy,
                out var cleanupProxy);

            sut.Scan(new List<string> { "/books" }, authorIds: new List<int>());

            Assert.That(importOrchestratorProxy.Calls, Is.EqualTo(1));
            Assert.That(cleanupProxy.CleanedPaths, Is.Empty);
        }

        [Test]
        public void scan_should_skip_cleanup_when_safe_scan_finds_no_media_files()
        {
            var sut = CreateScanService(
                folderExists: true,
                orchestratorResult: new OrchestratorImportResult
                {
                    CleanupSafe = true,
                    ScannedFilePaths = new List<string>()
                },
                out var importOrchestratorProxy,
                out var cleanupProxy);

            sut.Scan(new List<string> { "/books" }, authorIds: new List<int>());

            Assert.That(importOrchestratorProxy.Calls, Is.EqualTo(1));
            Assert.That(cleanupProxy.CleanedPaths, Is.Empty);
        }

        [Test]
        public void scan_should_cleanup_with_scanned_paths_only_when_orchestrator_result_is_cleanup_safe()
        {
            var sut = CreateScanService(
                folderExists: true,
                orchestratorResult: new OrchestratorImportResult
                {
                    CleanupSafe = true,
                    ScannedFilePaths = new List<string> { "/books/a.mp3" },
                    ImportedFiles = new List<ImportedFile>
                    {
                        new ImportedFile { FilePath = "/books/imported-only.mp3" }
                    }
                },
                out var importOrchestratorProxy,
                out var cleanupProxy);

            sut.Scan(new List<string> { "/books" }, authorIds: new List<int>());

            Assert.That(importOrchestratorProxy.Calls, Is.EqualTo(1));
            Assert.That(cleanupProxy.CleanedPaths.Count, Is.EqualTo(1));
            Assert.That(cleanupProxy.CleanedPaths.Single(), Is.EqualTo(new List<string> { "/books/a.mp3" }));
        }

        [Test]
        public void scan_should_create_visible_unmapped_row_for_seen_file_whose_apply_failed()
        {
            var filePath = "/books/failed.epub";
            var mediaFileService = new StubMediaFileService();
            var sut = CreateScanService(
                folderExists: true,
                orchestratorResult: new OrchestratorImportResult
                {
                    CleanupSafe = true,
                    ScannedFilePaths = new List<string> { filePath },
                    FailedFiles = new List<FailedFile>
                    {
                        new FailedFile { FilePath = filePath, Reason = "ADD_FAILED_AT_APPLY" }
                    }
                },
                out _,
                out _,
                mediaFileService);

            sut.Scan(new List<string> { "/books" }, authorIds: new List<int>());

            Assert.That(mediaFileService.AddedMany, Has.Count.EqualTo(1));
            Assert.That(mediaFileService.AddedMany[0].Path, Is.EqualTo(filePath));
            Assert.That(mediaFileService.AddedMany[0].EditionId, Is.EqualTo(0));
            Assert.That(mediaFileService.AddedMany[0].MatchDetails, Is.EqualTo("APPLY_FAILED:ADD_FAILED_AT_APPLY"));
        }

        [Test]
        public void scan_should_create_visible_unmapped_row_for_seen_root_type_mismatch()
        {
            var filePath = "/books/misplaced.m4b";
            var mediaFileService = new StubMediaFileService();
            var sut = CreateScanService(
                folderExists: true,
                orchestratorResult: new OrchestratorImportResult
                {
                    CleanupSafe = true,
                    ScannedFilePaths = new List<string> { filePath }
                },
                out _,
                out _,
                mediaFileService,
                rootFolder: new RootFolder
                {
                    Id = 1,
                    Path = "/books",
                    FolderType = FolderType.Ebook
                });

            sut.Scan(new List<string> { "/books" }, authorIds: new List<int>());

            Assert.That(mediaFileService.AddedMany, Has.Count.EqualTo(1));
            Assert.That(mediaFileService.AddedMany[0].Path, Is.EqualTo(filePath));
            Assert.That(mediaFileService.AddedMany[0].EditionId, Is.EqualTo(0));
            Assert.That(mediaFileService.AddedMany[0].MatchDetails, Is.EqualTo("INVENTORY_RECONCILIATION"));
        }

        [Test]
        public void scan_should_fail_instead_of_claiming_success_when_inventory_row_cannot_be_persisted()
        {
            var filePath = "/books/failed.epub";
            var mediaFileService = new StubMediaFileService { PersistAdds = false };
            var sut = CreateScanService(
                folderExists: true,
                orchestratorResult: new OrchestratorImportResult
                {
                    CleanupSafe = true,
                    ScannedFilePaths = new List<string> { filePath }
                },
                out _,
                out var cleanupProxy,
                mediaFileService);

            var exception = Assert.Catch<Exception>(() =>
                sut.Scan(new List<string> { "/books" }, authorIds: new List<int>()));

            Assert.That(exception.Message, Does.Contain("every observed root media file"));
            Assert.That(mediaFileService.AddManyCalls, Is.EqualTo(1));
            Assert.That(mediaFileService.AddCalls, Is.EqualTo(0), "The postcondition must catch a non-throwing persistence no-op");
            Assert.That(cleanupProxy.CleanedPaths, Is.Empty, "Cleanup must not run after an unproven inventory reconciliation");
        }

        [Test]
        public void scan_should_not_delete_root_type_mismatched_tracked_files()
        {
            var filePath = "/books/misplaced.m4b";
            var mediaFileService = new StubMediaFileService
            {
                ExistingFiles = new List<BookFile>
                {
                    new BookFile
                    {
                        Path = filePath,
                        EditionId = 0,
                        MediaType = "audiobook"
                    }
                }
            };

            var sut = CreateScanService(
                folderExists: true,
                orchestratorResult: new OrchestratorImportResult
                {
                    CleanupSafe = true,
                    ScannedFilePaths = new List<string> { filePath }
                },
                out var importOrchestratorProxy,
                out var cleanupProxy,
                mediaFileService,
                rootFolder: new RootFolder
                {
                    Id = 1,
                    Path = "/books",
                    FolderType = FolderType.Ebook
                });

            sut.Scan(new List<string> { "/books" }, authorIds: new List<int>());

            Assert.That(importOrchestratorProxy.Calls, Is.EqualTo(1));
            Assert.That(cleanupProxy.CleanedPaths.Count, Is.EqualTo(1));
            Assert.That(mediaFileService.AddManyCalls, Is.EqualTo(0));
            Assert.That(mediaFileService.GetFileWithPathListCalls, Is.EqualTo(1), "Already-represented inventory should require one bulk identity read and no rewrite");
            Assert.That(mediaFileService.DeleteManyCalls, Is.EqualTo(0));
            Assert.That(mediaFileService.DeletedMany, Is.Empty);
        }
    }
}
