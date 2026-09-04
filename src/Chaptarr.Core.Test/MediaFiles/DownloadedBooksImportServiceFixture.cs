using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class DownloadedBooksImportServiceFixture
    {
        private sealed class RecordingImportApprovedBooks : IImportApprovedBooks
        {
            public List<ImportDecision<LocalBook>> Decisions { get; private set; } = new();

            public List<ImportResult> Import(List<ImportDecision<LocalBook>> decisions, bool replaceExisting, DownloadClientItem downloadClientItem = null, ImportMode importMode = ImportMode.Auto, CancellationToken cancellationToken = default)
            {
                Decisions = decisions ?? new List<ImportDecision<LocalBook>>();
                return new List<ImportResult>();
            }
        }

        private sealed class StubFileMatchingService : IFileMatchingService
        {
            public sealed class MatchCall
            {
                public int? RestrictToAuthorId { get; init; }
                public bool ForDownloads { get; init; }
                public int FileCount { get; init; }
                public MatchingContext Context { get; init; }
            }

            public List<MatchCall> Calls { get; } = new();

            public FileMatchResult InitialResult { get; set; } = new();
            public FileMatchResult RematchResult { get; set; } = new();
            public bool UseRematchResultWhenAuthorRestricted { get; set; }

            public Task<FileMatchResult> MatchFilesToLibraryAsync(DiscoveredFileWithMetadata[] filesWithMetadata)
            {
                return MatchFilesToLibraryAsync(filesWithMetadata, null, false);
            }

            public Task<FileMatchResult> MatchFilesToLibraryAsync(DiscoveredFileWithMetadata[] filesWithMetadata, int? restrictToAuthorId)
            {
                return MatchFilesToLibraryAsync(filesWithMetadata, restrictToAuthorId, false);
            }

            public Task<FileMatchResult> MatchFilesToLibraryAsync(DiscoveredFileWithMetadata[] filesWithMetadata, int? restrictToAuthorId, bool forDownloads)
            {
                Calls.Add(new MatchCall
                {
                    RestrictToAuthorId = restrictToAuthorId,
                    ForDownloads = forDownloads,
                    FileCount = filesWithMetadata?.Length ?? 0
                });

                if (restrictToAuthorId.HasValue && UseRematchResultWhenAuthorRestricted)
                {
                    return Task.FromResult(RematchResult);
                }

                return Task.FromResult(InitialResult);
            }

            public Task<FileMatchResult> MatchFilesToLibraryAsync(DiscoveredFileWithMetadata[] filesWithMetadata, int? restrictToAuthorId, MatchingContext context)
            {
                Calls.Add(new MatchCall
                {
                    RestrictToAuthorId = restrictToAuthorId,
                    ForDownloads = context?.PerFileMatching ?? false,
                    FileCount = filesWithMetadata?.Length ?? 0,
                    Context = context
                });

                if (restrictToAuthorId.HasValue && UseRematchResultWhenAuthorRestricted)
                {
                    return Task.FromResult(RematchResult);
                }

                return Task.FromResult(InitialResult);
            }

            public EditionFtsMatch HolyGrailMatch(int? authorId, IEnumerable<string> allTagTokens, BookMediaType mediaType) => throw new NotImplementedException();

            public FileMatch HolyGrailMatchFile(DiscoveredFileWithMetadata file, BookMediaType mediaType, int? restrictToAuthorId = null) => throw new NotImplementedException();
        }

        private sealed class StubMetadataTagService : IMetadataTagService
        {
            public Dictionary<string, List<string>> Tags { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, Dictionary<string, List<string>>> TagsByPath { get; } = new(StringComparer.OrdinalIgnoreCase);

            public Dictionary<string, List<string>> ReadAllTags(IFileInfo file) => GetTags(file);
            public (Dictionary<string, List<string>> Tags, int? DurationSeconds) ReadAllTagsAndDuration(IFileInfo file) => (GetTags(file), null);
            public string ReadAllTagsAsJson(IFileInfo file) => throw new NotImplementedException();
            public void WriteTags(BookFile trackfile, bool newDownload, bool force = false) => throw new NotImplementedException();
            public void SyncTags(List<Edition> books) => throw new NotImplementedException();
            public List<RetagBookFilePreview> GetRetagPreviewsByAuthor(int authorId) => throw new NotImplementedException();
            public List<RetagBookFilePreview> GetRetagPreviewsByBook(int authorId) => throw new NotImplementedException();

            private Dictionary<string, List<string>> GetTags(IFileInfo file)
            {
                if (file != null && TagsByPath.TryGetValue(file.FullName, out var tagsByPath))
                {
                    return tagsByPath;
                }

                return Tags;
            }
        }

        private sealed class StubAuthorLibraryService : IAuthorLibraryService
        {
            public List<(string providerId, MonitoringConfig config)> AddCalls { get; } = new();
            public Author ResultAuthor { get; set; }

            public Task<Author> AddAuthorAsync(string providerId, MonitoringConfig config = null)
            {
                AddCalls.Add((providerId, config));
                return Task.FromResult(ResultAuthor);
            }

            public Task<Author> AddAuthorMonitoringBookAsync(string authorProviderId, string bookProviderId) => throw new NotImplementedException();
            public Task<List<Author>> AddAuthorsMonitoringSeriesAsync(string[] authorProviderIds, string seriesProviderId) => throw new NotImplementedException();
            public Task<Author> RefreshAuthorAsync(int authorId) => throw new NotImplementedException();
            public Task RemoveAuthorAsync(int authorId) => throw new NotImplementedException();
        }

        private sealed class StubRootFolderService : IRootFolderService
        {
            public List<RootFolder> RootFolders { get; set; } = new();

            public List<RootFolder> All() => RootFolders;

            public List<RootFolder> AllWithSpaceStats() => throw new NotImplementedException();
            public RootFolder Add(RootFolder rootFolder) => throw new NotImplementedException();
            public RootFolder Update(RootFolder rootFolder) => throw new NotImplementedException();
            public void Remove(int id) => throw new NotImplementedException();
            public RootFolder Get(int id) => throw new NotImplementedException();
            public List<RootFolder> AllForTag(int tagId) => throw new NotImplementedException();
            public RootFolder GetBestRootFolder(string path) => throw new NotImplementedException();
            public RootFolder GetBestRootFolder(string path, List<RootFolder> allRootFolders) => throw new NotImplementedException();
            public string GetBestRootFolderPath(string path) => throw new NotImplementedException();
            public string GetBestRootFolderPath(string path, List<RootFolder> allRootFolders) => throw new NotImplementedException();
        }

        private sealed class StubDiskProvider : IDiskProvider
        {
            private readonly System.IO.Abstractions.FileSystem _fileSystem = new();

            public HashSet<string> LockedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

            public bool FolderExists(string path) => Directory.Exists(path);
            public bool FileExists(string path) => File.Exists(path);
            public bool FileExistsCanonical(string path) => File.Exists(path);
            public bool FileExists(string path, StringComparison stringComparison) => File.Exists(path);
            public IFileInfo GetFileInfo(string path) => _fileSystem.FileInfo.FromFileName(path);
            public long GetFileSize(string path) => new FileInfo(path).Length;
            public DateTime FileGetLastWrite(string path) => new FileInfo(path).LastWriteTimeUtc;
            public bool IsFileLocked(string path) => LockedPaths.Contains(path);

            public long? GetAvailableSpace(string path) => throw new NotImplementedException();
            public void InheritFolderPermissions(string filename) => throw new NotImplementedException();
            public void SetEveryonePermissions(string filename) => throw new NotImplementedException();
            public void SetFilePermissions(string path, string mask, string group) => throw new NotImplementedException();
            public void SetPermissions(string path, string mask, string group) => throw new NotImplementedException();
            public void CopyPermissions(string sourcePath, string targetPath) => throw new NotImplementedException();
            public long? GetTotalSize(string path) => throw new NotImplementedException();
            public DateTime FolderGetCreationTime(string path) => throw new NotImplementedException();
            public DateTime FolderGetLastWrite(string path) => throw new NotImplementedException();
            public void EnsureFolder(string path) => throw new NotImplementedException();
            public bool FolderWritable(string path) => throw new NotImplementedException();
            public bool FolderEmpty(string path) => throw new NotImplementedException();
            public IEnumerable<string> GetDirectories(string path) => throw new NotImplementedException();
            public IEnumerable<string> GetFiles(string path, bool recursive) => throw new NotImplementedException();
            public long GetFolderSize(string path) => throw new NotImplementedException();
            public void CreateFolder(string path) => throw new NotImplementedException();
            public void DeleteFile(string path) => throw new NotImplementedException();
            public void CloneFile(string source, string destination, bool overwrite = false) => throw new NotImplementedException();
            public void CopyFile(string source, string destination, bool overwrite = false) => throw new NotImplementedException();
            public void MoveFile(string source, string destination, bool overwrite = false) => throw new NotImplementedException();
            public void MoveFolder(string source, string destination) => throw new NotImplementedException();
            public bool TryRenameFile(string source, string destination) => throw new NotImplementedException();
            public bool TryCreateHardLink(string source, string destination) => throw new NotImplementedException();
            public int? GetFileLinkCount(string path) => 1;
            public bool TryCreateRefLink(string source, string destination) => throw new NotImplementedException();
            public void DeleteFolder(string path, bool recursive) => throw new NotImplementedException();
            public string ReadAllText(string filePath) => throw new NotImplementedException();
            public void WriteAllText(string filename, string contents) => throw new NotImplementedException();
            public void FolderSetLastWriteTime(string path, DateTime dateTime) => throw new NotImplementedException();
            public void FileSetLastWriteTime(string path, DateTime dateTime) => throw new NotImplementedException();
            public string GetPathRoot(string path) => throw new NotImplementedException();
            public string GetParentFolder(string path) => throw new NotImplementedException();
            public FileAttributes GetFileAttributes(string path) => throw new NotImplementedException();
            public void EmptyFolder(string path) => throw new NotImplementedException();
            public string GetVolumeLabel(string path) => throw new NotImplementedException();
            public FileStream OpenReadStream(string path) => throw new NotImplementedException();
            public FileStream OpenWriteStream(string path) => throw new NotImplementedException();
            public List<IMount> GetMounts() => throw new NotImplementedException();
            public IMount GetMount(string path) => throw new NotImplementedException();
            public IDirectoryInfo GetDirectoryInfo(string path) => _fileSystem.DirectoryInfo.FromDirectoryName(path);
            public List<IDirectoryInfo> GetDirectoryInfos(string path) => throw new NotImplementedException();
            public List<IFileInfo> GetFileInfos(string path, bool recursive = false)
            {
                var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                return Directory.EnumerateFiles(path, "*", option)
                    .Select(file => _fileSystem.FileInfo.FromFileName(file))
                    .Cast<IFileInfo>()
                    .ToList();
            }
            public void RemoveEmptySubfolders(string path) => throw new NotImplementedException();
            public void SaveStream(Stream stream, string path) => throw new NotImplementedException();
            public bool IsValidFolderPermissionMask(string mask) => throw new NotImplementedException();
        }

        private sealed class StubDiskScanService : IDiskScanService
        {
            public void Scan(Author author) => throw new NotImplementedException();
            public void ScanRootFolder(string path, AuthorScanMode mode = AuthorScanMode.All, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task ScanRootFolderAsync(string path, AuthorScanMode mode = AuthorScanMode.All, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public IFileInfo[] GetBookFiles(string path, bool allDirectories = true) => throw new NotImplementedException();
            public string[] GetNonBookFiles(string path, bool allDirectories = true) => throw new NotImplementedException();
            public List<IFileInfo> FilterFiles(string basePath, IEnumerable<IFileInfo> files) => throw new NotImplementedException();
            public List<string> FilterPaths(string basePath, IEnumerable<string> paths) => throw new NotImplementedException();
        }

        private class ThrowingProxy<T> : DispatchProxy where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private class BookServiceProxy : DispatchProxy
        {
            public Book Book { get; set; }
            public Dictionary<int, Book> BooksById { get; } = new();
            public List<int> GetBookCalls { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IBookService.GetBook))
                {
                    var id = args?.Length > 0 && args[0] is int bookId ? bookId : 0;
                    GetBookCalls.Add(id);

                    if (BooksById.TryGetValue(id, out var mapped))
                    {
                        return mapped;
                    }

                    return Book;
                }

                throw new NotImplementedException($"Test proxy does not implement IBookService.{targetMethod?.Name}");
            }
        }

        private class AuthorServiceProxy : DispatchProxy
        {
            public Author Author { get; set; }
            public List<int> GetAuthorCalls { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IAuthorService.GetAuthor))
                {
                    GetAuthorCalls.Add(args?.Length > 0 && args[0] is int authorId ? authorId : 0);
                    return Author;
                }

                if (targetMethod?.Name == nameof(IAuthorService.GetAuthors))
                {
                    return new List<Author> { Author };
                }

                throw new NotImplementedException($"Test proxy does not implement IAuthorService.{targetMethod?.Name}");
            }
        }

        private class EditionServiceProxy : DispatchProxy
        {
            public Edition Edition { get; set; }
            public List<Edition> EditionsByBook { get; set; } = new();
            public Dictionary<int, Edition> EditionsById { get; } = new();
            public Dictionary<int, List<Edition>> EditionsByBookId { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IEditionService.GetEdition))
                {
                    if (args?.Length > 0 && args[0] is int id && EditionsById.TryGetValue(id, out var mapped))
                    {
                        return mapped;
                    }

                    return Edition;
                }

                if (targetMethod?.Name == nameof(IEditionService.GetEditionsByBook))
                {
                    if (args?.Length > 0 && args[0] is int id && EditionsByBookId.TryGetValue(id, out var mapped))
                    {
                        return mapped;
                    }

                    return EditionsByBook;
                }

                throw new NotImplementedException($"Test proxy does not implement IEditionService.{targetMethod?.Name}");
            }
        }

        private class HistoryServiceProxy : DispatchProxy
        {
            public List<EntityHistory> HistoryItems { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IHistoryService.FindByDownloadId))
                {
                    return HistoryItems;
                }

                throw new NotImplementedException($"Test proxy does not implement IHistoryService.{targetMethod?.Name}");
            }
        }

        [Test]
        public void should_report_unsupported_files_when_download_folder_has_no_supported_media()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "chaptarr-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var filePath = Path.Combine(tempDir, "The Five People You Meet in Heaven (2004).mkv");
            File.WriteAllBytes(filePath, new byte[] { 1, 2, 3, 4 });

            try
            {
                var service = new DownloadedBooksImportService(
                    new StubDiskProvider(),
                    new StubDiskScanService(),
                    new StubFileMatchingService(),
                    new StubMetadataTagService(),
                    new RecordingImportApprovedBooks(),
                    DispatchProxy.Create<IBookService, ThrowingProxy<IBookService>>(),
                    DispatchProxy.Create<IAuthorService, ThrowingProxy<IAuthorService>>(),
                    DispatchProxy.Create<IEditionService, ThrowingProxy<IEditionService>>(),
                    DispatchProxy.Create<IImportOrchestrator, ThrowingProxy<IImportOrchestrator>>(),
                    new StubAuthorLibraryService(),
                    new StubRootFolderService(),
                    ConfigServiceTestProxy.Create(),
                    DispatchProxy.Create<IHistoryService, ThrowingProxy<IHistoryService>>(),
                    DispatchProxy.Create<IEventAggregator, ThrowingProxy<IEventAggregator>>(),
                    DispatchProxy.Create<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo, ThrowingProxy<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo>>(),
                    DispatchProxy.Create<IMediaInfoExtractor, ThrowingProxy<IMediaInfoExtractor>>(),
                    LogManager.GetCurrentClassLogger());

                var results = service.ProcessPath(tempDir, ImportMode.Auto, author: null, downloadClientItem: null);

                Assert.That(results, Has.Count.EqualTo(1));
                Assert.That(results[0].ImportDecision.Rejections.Single().Type, Is.EqualTo(RejectionType.Permanent));
                Assert.That(results[0].Errors.Single(), Does.Contain("The Five People You Meet in Heaven (2004).mkv"));
                Assert.That(results[0].Errors.Single(), Does.Contain("Unsupported extension(s): .mkv"));
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Test]
        public void should_only_retry_missing_supported_media_from_an_authoritative_file_list()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "chaptarr-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var existingArchive = Path.Combine(tempDir, "Book.rar");
            var existingNfo = Path.Combine(tempDir, "Book.nfo");
            var existingMedia = Path.Combine(tempDir, "Book.epub");
            var missingMedia = Path.Combine(tempDir, "Book.m4b");
            var missingMediaTwo = Path.Combine(tempDir, "Book Part 2.m4b");
            var missingMediaThree = Path.Combine(tempDir, "Book Part 3.m4b");
            var missingMediaFour = Path.Combine(tempDir, "Book Part 4.m4b");
            var missingNfo = Path.Combine(tempDir, "Missing.nfo");
            File.WriteAllBytes(existingArchive, new byte[] { 1, 2, 3, 4 });
            File.WriteAllBytes(existingNfo, new byte[] { 1, 2, 3, 4 });
            File.WriteAllBytes(existingMedia, new byte[] { 1, 2, 3, 4 });

            try
            {
                var matchingService = new StubFileMatchingService();
                var importApprovedBooks = new RecordingImportApprovedBooks();
                var service = new DownloadedBooksImportService(
                    new StubDiskProvider(),
                    new StubDiskScanService(),
                    matchingService,
                    new StubMetadataTagService(),
                    importApprovedBooks,
                    DispatchProxy.Create<IBookService, ThrowingProxy<IBookService>>(),
                    DispatchProxy.Create<IAuthorService, ThrowingProxy<IAuthorService>>(),
                    DispatchProxy.Create<IEditionService, ThrowingProxy<IEditionService>>(),
                    DispatchProxy.Create<IImportOrchestrator, ThrowingProxy<IImportOrchestrator>>(),
                    new StubAuthorLibraryService(),
                    new StubRootFolderService(),
                    ConfigServiceTestProxy.Create(),
                    DispatchProxy.Create<IHistoryService, ThrowingProxy<IHistoryService>>(),
                    DispatchProxy.Create<IEventAggregator, ThrowingProxy<IEventAggregator>>(),
                    DispatchProxy.Create<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo, ThrowingProxy<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo>>(),
                    DispatchProxy.Create<IMediaInfoExtractor, ThrowingProxy<IMediaInfoExtractor>>(),
                    LogManager.GetCurrentClassLogger());

                RejectionType Classify(DownloadClientItem downloadClientItem)
                {
                    return service.ProcessPath(tempDir, ImportMode.Auto, author: null, downloadClientItem: downloadClientItem)
                        .Single()
                        .ImportDecision
                        .Rejections
                        .Single()
                        .Type;
                }

                var authoritativeMediaList = new DownloadClientItem
                {
                    FilePaths = new List<string> { existingNfo, missingMedia },
                    FileListConfidence = DownloadClientFileListConfidence.Authoritative
                };
                var degradedMediaList = new DownloadClientItem
                {
                    FilePaths = new List<string> { existingNfo, missingMedia },
                    FileListConfidence = DownloadClientFileListConfidence.Degraded
                };
                var authoritativeNonMediaList = new DownloadClientItem
                {
                    FilePaths = new List<string> { existingArchive, missingNfo },
                    FileListConfidence = DownloadClientFileListConfidence.Authoritative
                };
                var partiallyVisibleAuthoritativeMediaList = new DownloadClientItem
                {
                    FilePaths = new List<string> { existingMedia, missingMedia },
                    FileListConfidence = DownloadClientFileListConfidence.Authoritative
                };

                Assert.That(Classify(authoritativeMediaList), Is.EqualTo(RejectionType.Temporary));
                Assert.That(Classify(degradedMediaList), Is.EqualTo(RejectionType.Permanent));
                Assert.That(Classify(authoritativeNonMediaList), Is.EqualTo(RejectionType.Permanent));
                Assert.That(Classify(partiallyVisibleAuthoritativeMediaList), Is.EqualTo(RejectionType.Temporary));

                var detailedResult = service.ProcessPath(
                    tempDir,
                    ImportMode.Auto,
                    author: null,
                    downloadClientItem: new DownloadClientItem
                    {
                        FilePaths = new List<string> { missingMedia, missingMediaTwo, missingMediaThree, missingMediaFour },
                        FileListConfidence = DownloadClientFileListConfidence.Authoritative
                    }).Single();
                var detailedReason = detailedResult.Errors.Single();

                Assert.That(detailedResult.ImportDecision.Rejections.Single().Category,
                    Is.EqualTo(DownloadedBooksImportService.MissingAuthoritativeMediaFilesRejectionCategory));
                Assert.That(detailedReason, Does.Contain(missingMedia));
                Assert.That(detailedReason, Does.Contain(missingMediaTwo));
                Assert.That(detailedReason, Does.Contain(missingMediaThree));
                Assert.That(detailedReason, Does.Contain("and 1 more"));
                Assert.That(detailedReason, Does.Not.Contain(missingMediaFour));
                Assert.That(matchingService.Calls, Is.Empty);
                Assert.That(importApprovedBooks.Decisions, Is.Empty);
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Test]
        public void should_reject_locked_media_before_matching_a_manually_imported_folder()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "chaptarr-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var filePath = Path.Combine(tempDir, "Book.m4b");
            File.WriteAllBytes(filePath, new byte[] { 1, 2, 3, 4 });

            try
            {
                var diskProvider = new StubDiskProvider();
                diskProvider.LockedPaths.Add(filePath);
                var matchingService = new StubFileMatchingService();
                var importApprovedBooks = new RecordingImportApprovedBooks();
                var service = new DownloadedBooksImportService(
                    diskProvider,
                    new StubDiskScanService(),
                    matchingService,
                    new StubMetadataTagService(),
                    importApprovedBooks,
                    DispatchProxy.Create<IBookService, ThrowingProxy<IBookService>>(),
                    DispatchProxy.Create<IAuthorService, ThrowingProxy<IAuthorService>>(),
                    DispatchProxy.Create<IEditionService, ThrowingProxy<IEditionService>>(),
                    DispatchProxy.Create<IImportOrchestrator, ThrowingProxy<IImportOrchestrator>>(),
                    new StubAuthorLibraryService(),
                    new StubRootFolderService(),
                    ConfigServiceTestProxy.Create(),
                    DispatchProxy.Create<IHistoryService, ThrowingProxy<IHistoryService>>(),
                    DispatchProxy.Create<IEventAggregator, ThrowingProxy<IEventAggregator>>(),
                    DispatchProxy.Create<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo, ThrowingProxy<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo>>(),
                    DispatchProxy.Create<IMediaInfoExtractor, ThrowingProxy<IMediaInfoExtractor>>(),
                    LogManager.GetCurrentClassLogger());

                var result = service.ProcessPath(tempDir, ImportMode.Auto, author: null, downloadClientItem: null).Single();

                Assert.That(result.ImportDecision.Rejections.Single().Type, Is.EqualTo(RejectionType.Temporary));
                Assert.That(result.Errors.Single(), Does.Contain("Locked file"));
                Assert.That(matchingService.Calls, Is.Empty);
                Assert.That(importApprovedBooks.Decisions, Is.Empty);
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Test]
        public void should_auto_add_suggested_author_and_rematch_during_manual_download_import()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "chaptarr-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var filePath = Path.Combine(tempDir, "Test.m4b");
            File.WriteAllBytes(filePath, new byte[] { 1, 2, 3, 4 });

            try
            {
                var diskProvider = new StubDiskProvider();
                var diskScanService = new StubDiskScanService();
                var tagsService = new StubMetadataTagService
                {
                    Tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["artist"] = new List<string> { "Test Author" },
                        ["album"] = new List<string> { "Test Book" }
                    }
                };

                var matchingService = new StubFileMatchingService();
                matchingService.InitialResult = new FileMatchResult
                {
                    MatchedFiles = Array.Empty<FileMatch>(),
                    UnmatchedFiles = new[]
                    {
                        new UnmatchedFile
                        {
                            File = new DiscoveredFileWithMetadata { Path = filePath, Size = 4, Modified = DateTime.UtcNow, AllTags = tagsService.Tags },
                            Reason = "No local match",
                            PotentialAuthors = new[]
                            {
                                new AuthorSuggestion
                                {
                                    ProviderId = "hc:123",
                                    AuthorName = "Test Author",
                                    Confidence = 0.8
                                }
                            }
                        }
                    }
                };

                matchingService.RematchResult = new FileMatchResult
                {
                    MatchedFiles = new[]
                    {
                        new FileMatch
                        {
                            File = new DiscoveredFileWithMetadata { Path = filePath, Size = 4, Modified = DateTime.UtcNow, AllTags = tagsService.Tags },
                            AuthorId = 7,
                            AuthorName = "Test Author",
                            BookId = 10,
                            BookTitle = "Test Book",
                            EditionId = 3
                        }
                    },
                    UnmatchedFiles = Array.Empty<UnmatchedFile>()
                };
                matchingService.UseRematchResultWhenAuthorRestricted = true;

                var importApproved = new RecordingImportApprovedBooks();

                var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
                ((BookServiceProxy)(object)bookService).Book = new Book { Id = 10, AuthorId = 7, Title = "Test Book" };

                var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
                ((AuthorServiceProxy)(object)authorService).Author = new Author { Id = 7, Name = "Test Author" };

                var editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
                ((EditionServiceProxy)(object)editionService).Edition = new Edition { Id = 3, BookId = 10, Monitored = true, ReadingFormatId = 2 };
                ((EditionServiceProxy)(object)editionService).EditionsByBook = new List<Edition> { ((EditionServiceProxy)(object)editionService).Edition };

                var rootFolderService = new StubRootFolderService
                {
                    RootFolders = new List<RootFolder>
                    {
                        new RootFolder
                        {
                            Path = "/library/audiobooks",
                            FolderType = FolderType.Audiobook,
                            AudiobookSettings = "{\"QualityProfileId\":1,\"MetadataProfileId\":1,\"MonitorExisting\":1,\"MonitorFuture\":true,\"Tags\":[5]}"
                        },
                        new RootFolder
                        {
                            Path = "/library/other-audiobooks",
                            FolderType = FolderType.Audiobook,
                            AudiobookSettings = "{\"QualityProfileId\":1,\"MetadataProfileId\":1,\"MonitorExisting\":1,\"MonitorFuture\":true}"
                        }
                    }
                };

                var authorLibraryService = new StubAuthorLibraryService
                {
                    ResultAuthor = new Author { Id = 7, Name = "Test Author" }
                };

                var importOrchestrator = DispatchProxy.Create<IImportOrchestrator, ThrowingProxy<IImportOrchestrator>>();
                var configService = ConfigServiceTestProxy.Create();
                var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
                var eventAggregator = DispatchProxy.Create<IEventAggregator, ThrowingProxy<IEventAggregator>>();
                var runtimeInfo = DispatchProxy.Create<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo, ThrowingProxy<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo>>();

                var service = new DownloadedBooksImportService(
                    diskProvider,
                    diskScanService,
                    matchingService,
                    tagsService,
                    importApproved,
                    bookService,
                    authorService,
                    editionService,
                    importOrchestrator,
                    authorLibraryService,
                    rootFolderService,
                    configService,
                    historyService,
                    eventAggregator,
                    runtimeInfo,
                    DispatchProxy.Create<IMediaInfoExtractor, ThrowingProxy<IMediaInfoExtractor>>(),
                    LogManager.GetCurrentClassLogger());

                _ = service.ProcessPath(filePath, ImportMode.Auto, author: null, downloadClientItem: null);

                Assert.That(authorLibraryService.AddCalls, Has.Count.EqualTo(1));
                Assert.That(authorLibraryService.AddCalls[0].providerId, Is.EqualTo("hc:123"));
                Assert.That(authorLibraryService.AddCalls[0].config.AudiobookRootFolderPath, Is.EqualTo("/library/audiobooks"));
                Assert.That(matchingService.Calls.Select(c => c.RestrictToAuthorId).ToList(), Is.EqualTo(new List<int?> { null, 7 }));

                Assert.That(importApproved.Decisions, Has.Count.EqualTo(1));
                Assert.That(importApproved.Decisions[0].Approved, Is.True);
                Assert.That(importApproved.Decisions[0].Item.Book, Is.Not.Null);
                Assert.That(importApproved.Decisions[0].Item.Book.Id, Is.EqualTo(10));
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Test]
        public void should_hydrate_matched_book_and_author_once_per_download_import()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "chaptarr-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var firstPath = Path.Combine(tempDir, "Test 01.m4b");
            var secondPath = Path.Combine(tempDir, "Test 02.m4b");
            File.WriteAllBytes(firstPath, new byte[] { 1, 2, 3, 4 });
            File.WriteAllBytes(secondPath, new byte[] { 5, 6, 7, 8 });

            try
            {
                var author = new Author { Id = 7, Name = "Test Author" };
                var edition = new Edition { Id = 3, BookId = 10, Monitored = true, ReadingFormatId = 2 };
                var book = new Book
                {
                    Id = 10,
                    AuthorId = author.Id,
                    Title = "Test Book",
                    Editions = new List<Edition> { edition }
                };

                var tagsService = new StubMetadataTagService
                {
                    Tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["artist"] = new List<string> { "Test Author" },
                        ["album"] = new List<string> { "Test Book" }
                    }
                };

                var matchingService = new StubFileMatchingService
                {
                    InitialResult = new FileMatchResult
                    {
                        MatchedFiles = new[]
                        {
                            new FileMatch
                            {
                                File = new DiscoveredFileWithMetadata { Path = firstPath, Size = 4, Modified = DateTime.UtcNow, AllTags = tagsService.Tags },
                                AuthorId = author.Id,
                                AuthorName = author.Name,
                                BookId = book.Id,
                                BookTitle = book.Title,
                                EditionId = edition.Id
                            },
                            new FileMatch
                            {
                                File = new DiscoveredFileWithMetadata { Path = secondPath, Size = 4, Modified = DateTime.UtcNow, AllTags = tagsService.Tags },
                                AuthorId = author.Id,
                                AuthorName = author.Name,
                                BookId = book.Id,
                                BookTitle = book.Title,
                                EditionId = edition.Id
                            }
                        },
                        UnmatchedFiles = Array.Empty<UnmatchedFile>()
                    }
                };

                var importApproved = new RecordingImportApprovedBooks();

                var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
                var bookProxy = (BookServiceProxy)(object)bookService;
                bookProxy.Book = book;

                var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
                var authorProxy = (AuthorServiceProxy)(object)authorService;
                authorProxy.Author = author;

                var editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
                ((EditionServiceProxy)(object)editionService).Edition = edition;
                ((EditionServiceProxy)(object)editionService).EditionsByBook = new List<Edition> { edition };

                var service = new DownloadedBooksImportService(
                    new StubDiskProvider(),
                    new StubDiskScanService(),
                    matchingService,
                    tagsService,
                    importApproved,
                    bookService,
                    authorService,
                    editionService,
                    DispatchProxy.Create<IImportOrchestrator, ThrowingProxy<IImportOrchestrator>>(),
                    new StubAuthorLibraryService(),
                    new StubRootFolderService(),
                    ConfigServiceTestProxy.Create(),
                    DispatchProxy.Create<IHistoryService, HistoryServiceProxy>(),
                    DispatchProxy.Create<IEventAggregator, ThrowingProxy<IEventAggregator>>(),
                    DispatchProxy.Create<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo, ThrowingProxy<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo>>(),
                    DispatchProxy.Create<IMediaInfoExtractor, ThrowingProxy<IMediaInfoExtractor>>(),
                    LogManager.GetCurrentClassLogger());

                _ = service.ProcessPath(tempDir, ImportMode.Auto, author: null, downloadClientItem: null);

                Assert.That(importApproved.Decisions, Has.Count.EqualTo(2));
                Assert.That(bookProxy.GetBookCalls, Is.EqualTo(new List<int> { book.Id }));
                Assert.That(authorProxy.GetAuthorCalls, Is.EqualTo(new List<int> { author.Id }));
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Test]
        public void should_not_auto_add_manual_import_when_strict_default_roots_required_and_multiple_compatible_roots_have_no_default()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "chaptarr-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var filePath = Path.Combine(tempDir, "Test.m4b");
            File.WriteAllBytes(filePath, new byte[] { 1, 2, 3, 4 });

            try
            {
                var diskProvider = new StubDiskProvider();
                var diskScanService = new StubDiskScanService();
                var tagsService = new StubMetadataTagService
                {
                    Tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["artist"] = new List<string> { "Test Author" },
                        ["album"] = new List<string> { "Test Book" }
                    }
                };

                var matchingService = new StubFileMatchingService
                {
                    InitialResult = new FileMatchResult
                    {
                        MatchedFiles = Array.Empty<FileMatch>(),
                        UnmatchedFiles = new[]
                        {
                            new UnmatchedFile
                            {
                                File = new DiscoveredFileWithMetadata { Path = filePath, Size = 4, Modified = DateTime.UtcNow, AllTags = tagsService.Tags },
                                Reason = "No local match",
                                PotentialAuthors = new[]
                                {
                                    new AuthorSuggestion
                                    {
                                        ProviderId = "hc:123",
                                        AuthorName = "Test Author",
                                        Confidence = 0.8
                                    }
                                }
                            }
                        }
                    }
                };

                var importApproved = new RecordingImportApprovedBooks();

                var rootFolderService = new StubRootFolderService
                {
                    RootFolders = new List<RootFolder>
                    {
                        new RootFolder { Path = "/library/audiobooks", FolderType = FolderType.Audiobook },
                        new RootFolder { Path = "/library/other-audiobooks", FolderType = FolderType.Audiobook }
                    }
                };

                var authorLibraryService = new StubAuthorLibraryService { ResultAuthor = new Author { Id = 7, Name = "Test Author" } };

                var service = new DownloadedBooksImportService(
                    diskProvider,
                    diskScanService,
                    matchingService,
                    tagsService,
                    importApproved,
                    DispatchProxy.Create<IBookService, ThrowingProxy<IBookService>>(),
                    DispatchProxy.Create<IAuthorService, ThrowingProxy<IAuthorService>>(),
                    DispatchProxy.Create<IEditionService, ThrowingProxy<IEditionService>>(),
                    DispatchProxy.Create<IImportOrchestrator, ThrowingProxy<IImportOrchestrator>>(),
                    authorLibraryService,
                    rootFolderService,
                    ConfigServiceTestProxy.Create(),
                    DispatchProxy.Create<IHistoryService, HistoryServiceProxy>(),
                    DispatchProxy.Create<IEventAggregator, ThrowingProxy<IEventAggregator>>(),
                    DispatchProxy.Create<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo, ThrowingProxy<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo>>(),
                    DispatchProxy.Create<IMediaInfoExtractor, ThrowingProxy<IMediaInfoExtractor>>(),
                    LogManager.GetCurrentClassLogger());

                _ = service.ProcessPath(filePath, ImportMode.Auto, author: null, downloadClientItem: null, requireDefaultRootFolderForMissingAuthors: true);

                Assert.That(matchingService.Calls, Has.Count.EqualTo(1));
                Assert.That(authorLibraryService.AddCalls, Has.Count.EqualTo(0));
                Assert.That(importApproved.Decisions, Has.Count.EqualTo(1));
                Assert.That(importApproved.Decisions[0].Approved, Is.False);
                Assert.That(importApproved.Decisions[0].Rejections.Single().Reason, Does.Contain("Multiple audiobook or mixed root folders are configured"));
                Assert.That(importApproved.Decisions[0].Rejections.Single().Reason, Does.Contain("select a default audiobook root folder"));
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Test]
        public void should_not_auto_add_suggested_author_for_completed_download_when_disabled()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "chaptarr-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var filePath = Path.Combine(tempDir, "Test.m4b");
            File.WriteAllBytes(filePath, new byte[] { 1, 2, 3, 4 });

            try
            {
                var diskProvider = new StubDiskProvider();
                var diskScanService = new StubDiskScanService();
                var tagsService = new StubMetadataTagService();

                var matchingService = new StubFileMatchingService
                {
                    InitialResult = new FileMatchResult
                    {
                        MatchedFiles = Array.Empty<FileMatch>(),
                        UnmatchedFiles = new[]
                        {
                            new UnmatchedFile
                            {
                                File = new DiscoveredFileWithMetadata { Path = filePath, Size = 4, Modified = DateTime.UtcNow, AllTags = tagsService.Tags },
                                Reason = "No local match",
                                PotentialAuthors = new[]
                                {
                                    new AuthorSuggestion
                                    {
                                        ProviderId = "hc:123",
                                        AuthorName = "Test Author",
                                        Confidence = 0.8
                                    }
                                }
                            }
                        }
                    }
                };

                var importApproved = new RecordingImportApprovedBooks();

                var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
                ((BookServiceProxy)(object)bookService).Book = null;

                var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
                ((AuthorServiceProxy)(object)authorService).Author = null;

                var editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
                ((EditionServiceProxy)(object)editionService).Edition = null;
                ((EditionServiceProxy)(object)editionService).EditionsByBook = new List<Edition>();

                var authorLibraryService = new StubAuthorLibraryService
                {
                    ResultAuthor = new Author { Id = 7, Name = "Test Author" }
                };

                var service = new DownloadedBooksImportService(
                    diskProvider,
                    diskScanService,
                    matchingService,
                    tagsService,
                    importApproved,
                    bookService,
                    authorService,
                    editionService,
                    DispatchProxy.Create<IImportOrchestrator, ThrowingProxy<IImportOrchestrator>>(),
                    authorLibraryService,
                    new StubRootFolderService(),
                    ConfigServiceTestProxy.Create(),
                    DispatchProxy.Create<IHistoryService, HistoryServiceProxy>(),
                    DispatchProxy.Create<IEventAggregator, ThrowingProxy<IEventAggregator>>(),
                    DispatchProxy.Create<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo, ThrowingProxy<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo>>(),
                    DispatchProxy.Create<IMediaInfoExtractor, ThrowingProxy<IMediaInfoExtractor>>(),
                    LogManager.GetCurrentClassLogger());

                _ = service.ProcessPath(filePath, ImportMode.Auto, author: null, downloadClientItem: new DownloadClientItem { DownloadId = "external-grab" });

                Assert.That(matchingService.Calls, Has.Count.EqualTo(1));
                Assert.That(matchingService.Calls[0].Context.AllowV5Identification, Is.False);
                Assert.That(authorLibraryService.AddCalls, Has.Count.EqualTo(0));
                Assert.That(importApproved.Decisions, Has.Count.EqualTo(1));
                Assert.That(importApproved.Decisions[0].Approved, Is.False);
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Test]
        public void should_auto_add_completed_download_using_only_compatible_root_when_default_not_configured()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "chaptarr-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var filePath = Path.Combine(tempDir, "Test.m4b");
            File.WriteAllBytes(filePath, new byte[] { 1, 2, 3, 4 });

            try
            {
                var diskProvider = new StubDiskProvider();
                var diskScanService = new StubDiskScanService();
                var tagsService = new StubMetadataTagService
                {
                    Tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["artist"] = new List<string> { "Test Author" },
                        ["album"] = new List<string> { "Test Book" }
                    }
                };

                var matchingService = new StubFileMatchingService
                {
                    InitialResult = new FileMatchResult
                    {
                        MatchedFiles = Array.Empty<FileMatch>(),
                        UnmatchedFiles = new[]
                        {
                            new UnmatchedFile
                            {
                                File = new DiscoveredFileWithMetadata { Path = filePath, Size = 4, Modified = DateTime.UtcNow, AllTags = tagsService.Tags },
                                Reason = "No local match",
                                PotentialAuthors = new[]
                                {
                                    new AuthorSuggestion
                                    {
                                        ProviderId = "hc:123",
                                        AuthorName = "Test Author",
                                        Confidence = 0.8
                                    }
                                }
                            }
                        }
                    },
                    RematchResult = new FileMatchResult
                    {
                        MatchedFiles = new[]
                        {
                            new FileMatch
                            {
                                File = new DiscoveredFileWithMetadata { Path = filePath, Size = 4, Modified = DateTime.UtcNow, AllTags = tagsService.Tags },
                                AuthorId = 7,
                                AuthorName = "Test Author",
                                BookId = 10,
                                BookTitle = "Test Book",
                                EditionId = 3
                            }
                        },
                        UnmatchedFiles = Array.Empty<UnmatchedFile>()
                    },
                    UseRematchResultWhenAuthorRestricted = true
                };

                var importApproved = new RecordingImportApprovedBooks();

                var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
                ((BookServiceProxy)(object)bookService).Book = new Book { Id = 10, AuthorId = 7, Title = "Test Book" };

                var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
                ((AuthorServiceProxy)(object)authorService).Author = new Author { Id = 7, Name = "Test Author" };

                var editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
                ((EditionServiceProxy)(object)editionService).Edition = new Edition { Id = 3, BookId = 10, Monitored = true, ReadingFormatId = 2 };
                ((EditionServiceProxy)(object)editionService).EditionsByBook = new List<Edition> { ((EditionServiceProxy)(object)editionService).Edition };

                var rootFolderService = new StubRootFolderService
                {
                    RootFolders = new List<RootFolder>
                    {
                        new RootFolder
                        {
                            Path = "/library/audiobooks",
                            FolderType = FolderType.Audiobook,
                            AudiobookSettings = "{\"QualityProfileId\":1,\"MetadataProfileId\":2,\"MonitorExisting\":1,\"MonitorFuture\":true,\"Tags\":[5]}"
                        }
                    }
                };

                var authorLibraryService = new StubAuthorLibraryService
                {
                    ResultAuthor = new Author { Id = 7, Name = "Test Author" }
                };

                var service = new DownloadedBooksImportService(
                    diskProvider,
                    diskScanService,
                    matchingService,
                    tagsService,
                    importApproved,
                    bookService,
                    authorService,
                    editionService,
                    DispatchProxy.Create<IImportOrchestrator, ThrowingProxy<IImportOrchestrator>>(),
                    authorLibraryService,
                    rootFolderService,
                    ConfigServiceTestProxy.Create(autoAddMissingAuthorsFromCompletedDownloads: true),
                    DispatchProxy.Create<IHistoryService, HistoryServiceProxy>(),
                    DispatchProxy.Create<IEventAggregator, ThrowingProxy<IEventAggregator>>(),
                    DispatchProxy.Create<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo, ThrowingProxy<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo>>(),
                    DispatchProxy.Create<IMediaInfoExtractor, ThrowingProxy<IMediaInfoExtractor>>(),
                    LogManager.GetCurrentClassLogger());

                _ = service.ProcessPath(filePath, ImportMode.Auto, author: null, downloadClientItem: new DownloadClientItem { DownloadId = "external-grab" });

                Assert.That(authorLibraryService.AddCalls, Has.Count.EqualTo(1));
                Assert.That(authorLibraryService.AddCalls[0].providerId, Is.EqualTo("hc:123"));
                Assert.That(authorLibraryService.AddCalls[0].config.AudiobookRootFolderPath, Is.EqualTo("/library/audiobooks"));
                Assert.That(authorLibraryService.AddCalls[0].config.AudiobookQualityProfileId, Is.EqualTo(1));
                Assert.That(authorLibraryService.AddCalls[0].config.AudiobookMetadataProfileId, Is.EqualTo(2));
                Assert.That(matchingService.Calls.Select(c => c.RestrictToAuthorId).ToList(), Is.EqualTo(new List<int?> { null, 7 }));
                Assert.That(importApproved.Decisions, Has.Count.EqualTo(1));
                Assert.That(importApproved.Decisions[0].Approved, Is.True);
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Test]
        public void should_not_auto_add_completed_download_when_multiple_compatible_roots_have_no_default()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "chaptarr-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var filePath = Path.Combine(tempDir, "Test.m4b");
            File.WriteAllBytes(filePath, new byte[] { 1, 2, 3, 4 });

            try
            {
                var diskProvider = new StubDiskProvider();
                var diskScanService = new StubDiskScanService();
                var tagsService = new StubMetadataTagService();

                var matchingService = new StubFileMatchingService
                {
                    InitialResult = new FileMatchResult
                    {
                        MatchedFiles = Array.Empty<FileMatch>(),
                        UnmatchedFiles = new[]
                        {
                            new UnmatchedFile
                            {
                                File = new DiscoveredFileWithMetadata { Path = filePath, Size = 4, Modified = DateTime.UtcNow, AllTags = tagsService.Tags },
                                Reason = "No local match",
                                PotentialAuthors = new[]
                                {
                                    new AuthorSuggestion
                                    {
                                        ProviderId = "hc:123",
                                        AuthorName = "Test Author",
                                        Confidence = 0.8
                                    }
                                }
                            }
                        }
                    }
                };

                var importApproved = new RecordingImportApprovedBooks();

                var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
                ((BookServiceProxy)(object)bookService).Book = null;

                var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
                ((AuthorServiceProxy)(object)authorService).Author = null;

                var editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
                ((EditionServiceProxy)(object)editionService).Edition = null;
                ((EditionServiceProxy)(object)editionService).EditionsByBook = new List<Edition>();

                var authorLibraryService = new StubAuthorLibraryService
                {
                    ResultAuthor = new Author { Id = 7, Name = "Test Author" }
                };

                var rootFolderService = new StubRootFolderService
                {
                    RootFolders = new List<RootFolder>
                    {
                        new RootFolder { Path = "/library/audiobooks", FolderType = FolderType.Audiobook },
                        new RootFolder { Path = "/library/other-audiobooks", FolderType = FolderType.Audiobook }
                    }
                };

                var service = new DownloadedBooksImportService(
                    diskProvider,
                    diskScanService,
                    matchingService,
                    tagsService,
                    importApproved,
                    bookService,
                    authorService,
                    editionService,
                    DispatchProxy.Create<IImportOrchestrator, ThrowingProxy<IImportOrchestrator>>(),
                    authorLibraryService,
                    rootFolderService,
                    ConfigServiceTestProxy.Create(autoAddMissingAuthorsFromCompletedDownloads: true),
                    DispatchProxy.Create<IHistoryService, HistoryServiceProxy>(),
                    DispatchProxy.Create<IEventAggregator, ThrowingProxy<IEventAggregator>>(),
                    DispatchProxy.Create<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo, ThrowingProxy<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo>>(),
                    DispatchProxy.Create<IMediaInfoExtractor, ThrowingProxy<IMediaInfoExtractor>>(),
                    LogManager.GetCurrentClassLogger());

                _ = service.ProcessPath(filePath, ImportMode.Auto, author: null, downloadClientItem: new DownloadClientItem { DownloadId = "external-grab" });

                Assert.That(matchingService.Calls, Has.Count.EqualTo(1));
                Assert.That(matchingService.Calls[0].Context.AllowV5Identification, Is.True);
                Assert.That(authorLibraryService.AddCalls, Has.Count.EqualTo(0));
                Assert.That(importApproved.Decisions, Has.Count.EqualTo(1));
                Assert.That(importApproved.Decisions[0].Approved, Is.False);
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Test]
        public void should_auto_add_suggested_author_and_rematch_completed_download_when_enabled()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "chaptarr-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var filePath = Path.Combine(tempDir, "Test.m4b");
            File.WriteAllBytes(filePath, new byte[] { 1, 2, 3, 4 });

            try
            {
                var diskProvider = new StubDiskProvider();
                var diskScanService = new StubDiskScanService();
                var tagsService = new StubMetadataTagService
                {
                    Tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["artist"] = new List<string> { "Test Author" },
                        ["album"] = new List<string> { "Test Book" }
                    }
                };

                var matchingService = new StubFileMatchingService
                {
                    InitialResult = new FileMatchResult
                    {
                        MatchedFiles = Array.Empty<FileMatch>(),
                        UnmatchedFiles = new[]
                        {
                            new UnmatchedFile
                            {
                                File = new DiscoveredFileWithMetadata { Path = filePath, Size = 4, Modified = DateTime.UtcNow, AllTags = tagsService.Tags },
                                Reason = "No local match",
                                PotentialAuthors = new[]
                                {
                                    new AuthorSuggestion
                                    {
                                        ProviderId = "hc:123",
                                        AuthorName = "Test Author",
                                        Confidence = 0.8
                                    }
                                }
                            }
                        }
                    },
                    RematchResult = new FileMatchResult
                    {
                        MatchedFiles = new[]
                        {
                            new FileMatch
                            {
                                File = new DiscoveredFileWithMetadata { Path = filePath, Size = 4, Modified = DateTime.UtcNow, AllTags = tagsService.Tags },
                                AuthorId = 7,
                                AuthorName = "Test Author",
                                BookId = 10,
                                BookTitle = "Test Book",
                                EditionId = 3
                            }
                        },
                        UnmatchedFiles = Array.Empty<UnmatchedFile>()
                    },
                    UseRematchResultWhenAuthorRestricted = true
                };

                var importApproved = new RecordingImportApprovedBooks();

                var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
                ((BookServiceProxy)(object)bookService).Book = new Book { Id = 10, AuthorId = 7, Title = "Test Book" };

                var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
                ((AuthorServiceProxy)(object)authorService).Author = new Author { Id = 7, Name = "Test Author" };

                var editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
                ((EditionServiceProxy)(object)editionService).Edition = new Edition { Id = 3, BookId = 10, Monitored = true, ReadingFormatId = 2 };
                ((EditionServiceProxy)(object)editionService).EditionsByBook = new List<Edition> { ((EditionServiceProxy)(object)editionService).Edition };

                var rootFolderService = new StubRootFolderService
                {
                    RootFolders = new List<RootFolder>
                    {
                        new RootFolder
                        {
                            Path = "/library/audiobooks",
                            FolderType = FolderType.Audiobook,
                            AudiobookSettings = "{\"QualityProfileId\":1,\"MetadataProfileId\":2,\"MonitorExisting\":1,\"MonitorFuture\":true,\"Tags\":[5]}"
                        },
                        new RootFolder
                        {
                            Path = "/library/other-audiobooks",
                            FolderType = FolderType.Audiobook,
                            AudiobookSettings = "{\"QualityProfileId\":9,\"MetadataProfileId\":9,\"MonitorExisting\":0,\"MonitorFuture\":false}"
                        }
                    }
                };

                var authorLibraryService = new StubAuthorLibraryService
                {
                    ResultAuthor = new Author { Id = 7, Name = "Test Author" }
                };

                var service = new DownloadedBooksImportService(
                    diskProvider,
                    diskScanService,
                    matchingService,
                    tagsService,
                    importApproved,
                    bookService,
                    authorService,
                    editionService,
                    DispatchProxy.Create<IImportOrchestrator, ThrowingProxy<IImportOrchestrator>>(),
                    authorLibraryService,
                    rootFolderService,
                    ConfigServiceTestProxy.Create(autoAddMissingAuthorsFromCompletedDownloads: true, defaultAudiobookRootFolderPath: "/library/audiobooks"),
                    DispatchProxy.Create<IHistoryService, HistoryServiceProxy>(),
                    DispatchProxy.Create<IEventAggregator, ThrowingProxy<IEventAggregator>>(),
                    DispatchProxy.Create<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo, ThrowingProxy<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo>>(),
                    DispatchProxy.Create<IMediaInfoExtractor, ThrowingProxy<IMediaInfoExtractor>>(),
                    LogManager.GetCurrentClassLogger());

                _ = service.ProcessPath(filePath, ImportMode.Auto, author: null, downloadClientItem: new DownloadClientItem { DownloadId = "external-grab" });

                Assert.That(matchingService.Calls[0].Context.AllowV5Identification, Is.True);
                Assert.That(authorLibraryService.AddCalls, Has.Count.EqualTo(1));
                Assert.That(authorLibraryService.AddCalls[0].providerId, Is.EqualTo("hc:123"));
                Assert.That(authorLibraryService.AddCalls[0].config.AudiobookRootFolderPath, Is.EqualTo("/library/audiobooks"));
                Assert.That(authorLibraryService.AddCalls[0].config.AudiobookQualityProfileId, Is.EqualTo(1));
                Assert.That(authorLibraryService.AddCalls[0].config.AudiobookMetadataProfileId, Is.EqualTo(2));
                Assert.That(authorLibraryService.AddCalls[0].config.AudiobookTags, Does.Contain(5));
                Assert.That(authorLibraryService.AddCalls[0].config.EbookTags, Is.Null);
                Assert.That(matchingService.Calls.Select(c => c.RestrictToAuthorId).ToList(), Is.EqualTo(new List<int?> { null, 7 }));
                Assert.That(importApproved.Decisions, Has.Count.EqualTo(1));
                Assert.That(importApproved.Decisions[0].Approved, Is.True);
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Test]
        public void should_reject_unmatched_files_in_download_import()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "chaptarr-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var filePath = Path.Combine(tempDir, "Test.m4b");
            File.WriteAllBytes(filePath, new byte[] { 1, 2, 3, 4 });

            try
            {
                var diskProvider = new StubDiskProvider();
                var diskScanService = new StubDiskScanService();
                var tagsService = new StubMetadataTagService();

                var matchingService = new StubFileMatchingService();
                matchingService.InitialResult = new FileMatchResult
                {
                    MatchedFiles = Array.Empty<FileMatch>(),
                    UnmatchedFiles = new[]
                    {
                        new UnmatchedFile
                        {
                            File = new DiscoveredFileWithMetadata { Path = filePath, Size = 4, Modified = DateTime.UtcNow, AllTags = tagsService.Tags },
                            Reason = "No match",
                            PotentialAuthors = Array.Empty<AuthorSuggestion>()
                        }
                    }
                };

                var importApproved = new RecordingImportApprovedBooks();

                var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
                ((BookServiceProxy)(object)bookService).Book = null;

                var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
                ((AuthorServiceProxy)(object)authorService).Author = null;

                var editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
                ((EditionServiceProxy)(object)editionService).Edition = null;
                ((EditionServiceProxy)(object)editionService).EditionsByBook = new List<Edition>();

                var rootFolderService = new StubRootFolderService();
                var authorLibraryService = new StubAuthorLibraryService { ResultAuthor = null };

                var importOrchestrator = DispatchProxy.Create<IImportOrchestrator, ThrowingProxy<IImportOrchestrator>>();
                var configService = ConfigServiceTestProxy.Create();
                var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
                var eventAggregator = DispatchProxy.Create<IEventAggregator, ThrowingProxy<IEventAggregator>>();
                var runtimeInfo = DispatchProxy.Create<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo, ThrowingProxy<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo>>();

                var service = new DownloadedBooksImportService(
                    diskProvider,
                    diskScanService,
                    matchingService,
                    tagsService,
                    importApproved,
                    bookService,
                    authorService,
                    editionService,
                    importOrchestrator,
                    authorLibraryService,
                    rootFolderService,
                    configService,
                    historyService,
                    eventAggregator,
                    runtimeInfo,
                    DispatchProxy.Create<IMediaInfoExtractor, ThrowingProxy<IMediaInfoExtractor>>(),
                    LogManager.GetCurrentClassLogger());

                _ = service.ProcessPath(filePath, ImportMode.Auto, author: null, downloadClientItem: null);

                Assert.That(authorLibraryService.AddCalls, Has.Count.EqualTo(0));
                Assert.That(matchingService.Calls, Has.Count.EqualTo(1));

                Assert.That(importApproved.Decisions, Has.Count.EqualTo(1));
                Assert.That(importApproved.Decisions[0].Approved, Is.False);
                Assert.That(importApproved.Decisions[0].Rejections, Is.Not.Empty);
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Test]
        public void should_allow_path_fallback_for_single_target_completed_download_folder()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "chaptarr-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var filePath = Path.Combine(tempDir, "Cory Doctorow - Enshittification.m4b");
            File.WriteAllBytes(filePath, new byte[] { 1, 2, 3, 4 });

            try
            {
                var diskProvider = new StubDiskProvider();
                var diskScanService = new StubDiskScanService();
                var tagsService = new StubMetadataTagService
                {
                    Tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["MP4:©too"] = new List<string> { "lavf62.3.100" }
                    }
                };

                var matchingService = new StubFileMatchingService
                {
                    InitialResult = new FileMatchResult
                    {
                        MatchedFiles = Array.Empty<FileMatch>(),
                        UnmatchedFiles = new[]
                        {
                            new UnmatchedFile
                            {
                                File = new DiscoveredFileWithMetadata
                                {
                                    Path = filePath,
                                    Size = 4,
                                    Modified = DateTime.UtcNow,
                                    AllTags = tagsService.Tags
                                },
                                Reason = "No match",
                                PotentialAuthors = Array.Empty<AuthorSuggestion>()
                            }
                        }
                    }
                };

                var importApproved = new RecordingImportApprovedBooks();

                var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
                ((BookServiceProxy)(object)bookService).Book = null;

                var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
                ((AuthorServiceProxy)(object)authorService).Author = new Author { Id = 76, Name = "Cory Doctorow" };

                var editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
                ((EditionServiceProxy)(object)editionService).Edition = null;
                ((EditionServiceProxy)(object)editionService).EditionsByBook = new List<Edition>();

                var rootFolderService = new StubRootFolderService();
                var authorLibraryService = new StubAuthorLibraryService { ResultAuthor = null };

                var importOrchestrator = DispatchProxy.Create<IImportOrchestrator, ThrowingProxy<IImportOrchestrator>>();
                var configService = ConfigServiceTestProxy.Create();
                var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
                var eventAggregator = DispatchProxy.Create<IEventAggregator, ThrowingProxy<IEventAggregator>>();
                var runtimeInfo = DispatchProxy.Create<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo, ThrowingProxy<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo>>();

                var service = new DownloadedBooksImportService(
                    diskProvider,
                    diskScanService,
                    matchingService,
                    tagsService,
                    importApproved,
                    bookService,
                    authorService,
                    editionService,
                    importOrchestrator,
                    authorLibraryService,
                    rootFolderService,
                    configService,
                    historyService,
                    eventAggregator,
                    runtimeInfo,
                    DispatchProxy.Create<IMediaInfoExtractor, ThrowingProxy<IMediaInfoExtractor>>(),
                    LogManager.GetCurrentClassLogger());

                _ = service.ProcessPath(
                    tempDir,
                    ImportMode.Auto,
                    author: null,
                    downloadClientItem: new DownloadClientItem
                    {
                        DownloadId = "download-1",
                        Title = "Cory Doctorow - Enshittification",
                        DownloadClientInfo = new DownloadClientItemClientInfo
                        {
                            Name = "binhex",
                            Protocol = DownloadProtocol.Torrent
                        }
                    },
                    remoteBook: new RemoteBook
                    {
                        Books = new List<Book>
                        {
                            new()
                            {
                                Id = 4634,
                                AuthorId = 76,
                                Title = "Enshittification: Why Everything Suddenly Got Worse and What to Do About It",
                                MediaType = BookMediaType.Audiobook
                            }
                        }
                    });

                Assert.That(matchingService.Calls, Has.Count.EqualTo(1));
                Assert.That(matchingService.Calls[0].RestrictToAuthorId, Is.EqualTo(76));
                Assert.That(matchingService.Calls[0].Context, Is.Not.Null);
                Assert.That(matchingService.Calls[0].Context.DisablePathFallback, Is.False);
                Assert.That(matchingService.Calls[0].Context.TargetBookIds, Is.EqualTo(new List<int> { 4634 }));
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Test]
        public void should_reject_completed_download_match_that_does_not_belong_to_grabbed_target_book()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "chaptarr-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var filePath = Path.Combine(tempDir, "Unexpected.m4b");
            File.WriteAllBytes(filePath, new byte[] { 1, 2, 3, 4 });

            try
            {
                var diskProvider = new StubDiskProvider();
                var diskScanService = new StubDiskScanService();
                var tagsService = new StubMetadataTagService();

                var matchingService = new StubFileMatchingService
                {
                    InitialResult = new FileMatchResult
                    {
                        MatchedFiles = new[]
                        {
                            new FileMatch
                            {
                                File = new DiscoveredFileWithMetadata { Path = filePath, Size = 4, Modified = DateTime.UtcNow, AllTags = tagsService.Tags },
                                AuthorId = 7,
                                AuthorName = "Test Author",
                                BookId = 10,
                                BookTitle = "Unexpected Book",
                                EditionId = 3
                            }
                        },
                        UnmatchedFiles = Array.Empty<UnmatchedFile>()
                    }
                };

                var importApproved = new RecordingImportApprovedBooks();

                var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
                ((BookServiceProxy)(object)bookService).Book = new Book { Id = 10, AuthorId = 7, Title = "Unexpected Book" };

                var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
                ((AuthorServiceProxy)(object)authorService).Author = new Author { Id = 7, Name = "Test Author" };

                var editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
                ((EditionServiceProxy)(object)editionService).Edition = new Edition { Id = 3, BookId = 10, Monitored = true, ReadingFormatId = 2 };
                ((EditionServiceProxy)(object)editionService).EditionsByBook = new List<Edition> { ((EditionServiceProxy)(object)editionService).Edition };

                var importOrchestrator = DispatchProxy.Create<IImportOrchestrator, ThrowingProxy<IImportOrchestrator>>();
                var configService = ConfigServiceTestProxy.Create();
                var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
                var eventAggregator = DispatchProxy.Create<IEventAggregator, ThrowingProxy<IEventAggregator>>();
                var runtimeInfo = DispatchProxy.Create<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo, ThrowingProxy<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo>>();

                var service = new DownloadedBooksImportService(
                    diskProvider,
                    diskScanService,
                    matchingService,
                    tagsService,
                    importApproved,
                    bookService,
                    authorService,
                    editionService,
                    importOrchestrator,
                    new StubAuthorLibraryService(),
                    new StubRootFolderService(),
                    configService,
                    historyService,
                    eventAggregator,
                    runtimeInfo,
                    DispatchProxy.Create<IMediaInfoExtractor, ThrowingProxy<IMediaInfoExtractor>>(),
                    LogManager.GetCurrentClassLogger());

                var remoteBook = new RemoteBook
                {
                    Author = ((AuthorServiceProxy)(object)authorService).Author,
                    Books = new List<Book> { new Book { Id = 11, AuthorId = 7, Title = "Expected Book" } }
                };

                var downloadClientItem = new DownloadClientItem
                {
                    DownloadId = "download-1",
                    CanMoveFiles = false,
                    DownloadClientInfo = new DownloadClientItemClientInfo { Id = 1, Name = "qBittorrent", Type = "qBittorrent" }
                };

                _ = service.ProcessPath(filePath, ImportMode.Auto, remoteBook.Author, downloadClientItem, remoteBook);

                Assert.That(importApproved.Decisions, Has.Count.EqualTo(1));
                Assert.That(importApproved.Decisions[0].Approved, Is.False);
                Assert.That(importApproved.Decisions[0].Rejections.Select(r => r.Reason), Has.Some.Contains("Expected Book"));
                Assert.That(importApproved.Decisions[0].Rejections.Select(r => r.Reason), Has.Some.Contains("Unexpected Book"));
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Test]
        public void should_allow_completed_download_match_when_it_belongs_to_grabbed_target_book()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "chaptarr-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var filePath = Path.Combine(tempDir, "Expected.m4b");
            File.WriteAllBytes(filePath, new byte[] { 1, 2, 3, 4 });

            try
            {
                var diskProvider = new StubDiskProvider();
                var diskScanService = new StubDiskScanService();
                var tagsService = new StubMetadataTagService();

                var matchingService = new StubFileMatchingService
                {
                    InitialResult = new FileMatchResult
                    {
                        MatchedFiles = new[]
                        {
                            new FileMatch
                            {
                                File = new DiscoveredFileWithMetadata { Path = filePath, Size = 4, Modified = DateTime.UtcNow, AllTags = tagsService.Tags },
                                AuthorId = 7,
                                AuthorName = "Test Author",
                                BookId = 10,
                                BookTitle = "Expected Book",
                                EditionId = 3
                            }
                        },
                        UnmatchedFiles = Array.Empty<UnmatchedFile>()
                    }
                };

                var importApproved = new RecordingImportApprovedBooks();

                var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
                ((BookServiceProxy)(object)bookService).Book = new Book { Id = 10, AuthorId = 7, Title = "Expected Book" };

                var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
                ((AuthorServiceProxy)(object)authorService).Author = new Author { Id = 7, Name = "Test Author" };

                var editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
                ((EditionServiceProxy)(object)editionService).Edition = new Edition { Id = 3, BookId = 10, Monitored = true, ReadingFormatId = 2 };
                ((EditionServiceProxy)(object)editionService).EditionsByBook = new List<Edition> { ((EditionServiceProxy)(object)editionService).Edition };

                var importOrchestrator = DispatchProxy.Create<IImportOrchestrator, ThrowingProxy<IImportOrchestrator>>();
                var configService = ConfigServiceTestProxy.Create();
                var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
                var eventAggregator = DispatchProxy.Create<IEventAggregator, ThrowingProxy<IEventAggregator>>();
                var runtimeInfo = DispatchProxy.Create<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo, ThrowingProxy<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo>>();

                var service = new DownloadedBooksImportService(
                    diskProvider,
                    diskScanService,
                    matchingService,
                    tagsService,
                    importApproved,
                    bookService,
                    authorService,
                    editionService,
                    importOrchestrator,
                    new StubAuthorLibraryService(),
                    new StubRootFolderService(),
                    configService,
                    historyService,
                    eventAggregator,
                    runtimeInfo,
                    DispatchProxy.Create<IMediaInfoExtractor, ThrowingProxy<IMediaInfoExtractor>>(),
                    LogManager.GetCurrentClassLogger());

                var remoteBook = new RemoteBook
                {
                    Author = ((AuthorServiceProxy)(object)authorService).Author,
                    Books = new List<Book> { new Book { Id = 10, AuthorId = 7, Title = "Expected Book" } },
                    Release = new ReleaseInfo
                    {
                        Title = "Test Author - Expected Book - James Marsters [MP3]",
                        IndexerFlags = IndexerFlags.Freeleech,
                        Narrator = "James Marsters",
                        IsGraphicAudio = true
                    },
                    ParsedBookInfo = new ParsedBookInfo
                    {
                        AuthorName = "Test Author",
                        BookTitle = "Expected Book",
                        ReleaseTitle = "Test Author - Expected Book - James Marsters [MP3]",
                        ReleaseGroup = "NarratorGroup",
                        Quality = new NzbDrone.Core.Qualities.QualityModel(NzbDrone.Core.Qualities.Quality.MP3),
                        Narrator = "James Marsters",
                        IsGraphicAudio = true,
                        AudioProductionType = "Full Cast"
                    }
                };

                var downloadClientItem = new DownloadClientItem
                {
                    DownloadId = "download-2",
                    CanMoveFiles = false,
                    DownloadClientInfo = new DownloadClientItemClientInfo { Id = 1, Name = "qBittorrent", Type = "qBittorrent" }
                };

                _ = service.ProcessPath(filePath, ImportMode.Auto, remoteBook.Author, downloadClientItem, remoteBook);

                Assert.That(importApproved.Decisions, Has.Count.EqualTo(1));
                Assert.That(importApproved.Decisions[0].Approved, Is.True);
                Assert.Multiple(() =>
                {
                    var localBook = importApproved.Decisions[0].Item;
                    Assert.That(localBook.SceneName, Is.EqualTo(remoteBook.Release.Title));
                    Assert.That(localBook.ReleaseGroup, Is.EqualTo("NarratorGroup"));
                    Assert.That(localBook.IndexerFlags, Is.EqualTo(IndexerFlags.Freeleech));
                    Assert.That(localBook.Narrator, Is.EqualTo("James Marsters"));
                    Assert.That(localBook.IsGraphicAudio, Is.True);
                    Assert.That(localBook.AudioProductionType, Is.EqualTo("Full Cast"));
                });
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Test]
        public void should_retarget_same_work_sibling_match_to_grabbed_book_without_cloning()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "chaptarr-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var filePath = Path.Combine(tempDir, "Expected.m4b");
            File.WriteAllBytes(filePath, new byte[] { 1, 2, 3, 4 });

            try
            {
                var diskProvider = new StubDiskProvider();
                var diskScanService = new StubDiskScanService();
                var tagsService = new StubMetadataTagService();

                var matchingService = new StubFileMatchingService
                {
                    InitialResult = new FileMatchResult
                    {
                        MatchedFiles = new[]
                        {
                            new FileMatch
                            {
                                File = new DiscoveredFileWithMetadata { Path = filePath, Size = 4, Modified = DateTime.UtcNow, AllTags = tagsService.Tags },
                                AuthorId = 7,
                                AuthorName = "Test Author",
                                BookId = 5079,
                                BookTitle = "Expected Book",
                                EditionId = 30
                            }
                        },
                        UnmatchedFiles = Array.Empty<UnmatchedFile>()
                    }
                };

                var author = new Author { Id = 7, Name = "Test Author" };
                var matchedBook = new Book { Id = 5079, AuthorId = 7, Author = author, Title = "Expected Book", AnyEditionOk = true, MediaType = BookMediaType.Audiobook, HardcoverBookId = "hc:429306" };
                var targetBook = new Book { Id = 2015, AuthorId = 7, Author = author, Title = "Expected Book", AnyEditionOk = true, MediaType = BookMediaType.Audiobook, HardcoverBookId = "hc:429306" };
                var matchedEdition = new Edition { Id = 30, BookId = 5079, Title = "Jim Dale", AudibleASIN = "B000000123", ReadingFormatId = 2, Monitored = false, Book = matchedBook };
                var targetEdition = new Edition { Id = 40, BookId = 2015, Title = "Jim Dale", AudibleASIN = "B000000123", ReadingFormatId = 2, Monitored = false, Book = targetBook };
                matchedBook.Editions = new List<Edition> { matchedEdition };
                targetBook.Editions = new List<Edition> { targetEdition };

                var importApproved = new RecordingImportApprovedBooks();

                var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
                var bookProxy = (BookServiceProxy)(object)bookService;
                bookProxy.BooksById[5079] = matchedBook;
                bookProxy.BooksById[2015] = targetBook;

                var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
                ((AuthorServiceProxy)(object)authorService).Author = author;

                var editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
                var editionProxy = (EditionServiceProxy)(object)editionService;
                editionProxy.EditionsById[30] = matchedEdition;
                editionProxy.EditionsById[40] = targetEdition;
                editionProxy.EditionsByBookId[5079] = new List<Edition> { matchedEdition };
                editionProxy.EditionsByBookId[2015] = new List<Edition> { targetEdition };

                var importOrchestrator = DispatchProxy.Create<IImportOrchestrator, ThrowingProxy<IImportOrchestrator>>();
                var configService = ConfigServiceTestProxy.Create();
                var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
                var eventAggregator = DispatchProxy.Create<IEventAggregator, ThrowingProxy<IEventAggregator>>();
                var runtimeInfo = DispatchProxy.Create<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo, ThrowingProxy<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo>>();

                var service = new DownloadedBooksImportService(
                    diskProvider,
                    diskScanService,
                    matchingService,
                    tagsService,
                    importApproved,
                    bookService,
                    authorService,
                    editionService,
                    importOrchestrator,
                    new StubAuthorLibraryService(),
                    new StubRootFolderService(),
                    configService,
                    historyService,
                    eventAggregator,
                    runtimeInfo,
                    DispatchProxy.Create<IMediaInfoExtractor, ThrowingProxy<IMediaInfoExtractor>>(),
                    LogManager.GetCurrentClassLogger());

                var remoteBook = new RemoteBook
                {
                    Author = author,
                    Books = new List<Book> { new Book { Id = 2015, AuthorId = 7, Title = "Expected Book", AnyEditionOk = true, MediaType = BookMediaType.Audiobook, HardcoverBookId = "hc:429306" } }
                };

                var downloadClientItem = new DownloadClientItem
                {
                    DownloadId = "DOWNLOAD-SAME-WORK",
                    CanMoveFiles = false,
                    DownloadClientInfo = new DownloadClientItemClientInfo { Id = 1, Name = "qBittorrent", Type = "qBittorrent" }
                };

                _ = service.ProcessPath(filePath, ImportMode.Auto, author, downloadClientItem, remoteBook);

                Assert.That(importApproved.Decisions, Has.Count.EqualTo(1));
                Assert.That(importApproved.Decisions[0].Approved, Is.True);
                Assert.That(importApproved.Decisions[0].Item.Book.Id, Is.EqualTo(2015));
                Assert.That(importApproved.Decisions[0].Item.Edition.Id, Is.EqualTo(40));
                Assert.That(importApproved.Decisions[0].Item.Edition.Book, Is.SameAs(targetBook));
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Test]
        public void should_reject_same_work_sibling_match_when_grabbed_book_has_no_equivalent_edition()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "chaptarr-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var filePath = Path.Combine(tempDir, "Expected.m4b");
            File.WriteAllBytes(filePath, new byte[] { 1, 2, 3, 4 });

            try
            {
                var diskProvider = new StubDiskProvider();
                var diskScanService = new StubDiskScanService();
                var tagsService = new StubMetadataTagService();

                var matchingService = new StubFileMatchingService
                {
                    InitialResult = new FileMatchResult
                    {
                        MatchedFiles = new[]
                        {
                            new FileMatch
                            {
                                File = new DiscoveredFileWithMetadata { Path = filePath, Size = 4, Modified = DateTime.UtcNow, AllTags = tagsService.Tags },
                                AuthorId = 7,
                                AuthorName = "Test Author",
                                BookId = 5079,
                                BookTitle = "Expected Book",
                                EditionId = 30
                            }
                        },
                        UnmatchedFiles = Array.Empty<UnmatchedFile>()
                    }
                };

                var author = new Author { Id = 7, Name = "Test Author" };
                var matchedBook = new Book { Id = 5079, AuthorId = 7, Author = author, Title = "Expected Book", AnyEditionOk = true, MediaType = BookMediaType.Audiobook, HardcoverBookId = "hc:429306" };
                var targetBook = new Book { Id = 2015, AuthorId = 7, Author = author, Title = "Expected Book", AnyEditionOk = true, MediaType = BookMediaType.Audiobook, HardcoverBookId = "hc:429306" };
                var matchedEdition = new Edition { Id = 30, BookId = 5079, Title = "Jim Dale", AudibleASIN = "B000000123", ReadingFormatId = 2, Monitored = false, Book = matchedBook };
                var unrelatedTargetEdition = new Edition { Id = 40, BookId = 2015, Title = "Stephen Fry", AudibleASIN = "B000000999", ReadingFormatId = 2, Monitored = false, Book = targetBook };
                matchedBook.Editions = new List<Edition> { matchedEdition };
                targetBook.Editions = new List<Edition> { unrelatedTargetEdition };

                var importApproved = new RecordingImportApprovedBooks();

                var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
                var bookProxy = (BookServiceProxy)(object)bookService;
                bookProxy.BooksById[5079] = matchedBook;
                bookProxy.BooksById[2015] = targetBook;

                var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
                ((AuthorServiceProxy)(object)authorService).Author = author;

                var editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
                var editionProxy = (EditionServiceProxy)(object)editionService;
                editionProxy.EditionsById[30] = matchedEdition;
                editionProxy.EditionsById[40] = unrelatedTargetEdition;
                editionProxy.EditionsByBookId[5079] = new List<Edition> { matchedEdition };
                editionProxy.EditionsByBookId[2015] = new List<Edition> { unrelatedTargetEdition };

                var importOrchestrator = DispatchProxy.Create<IImportOrchestrator, ThrowingProxy<IImportOrchestrator>>();
                var configService = ConfigServiceTestProxy.Create();
                var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
                var eventAggregator = DispatchProxy.Create<IEventAggregator, ThrowingProxy<IEventAggregator>>();
                var runtimeInfo = DispatchProxy.Create<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo, ThrowingProxy<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo>>();

                var service = new DownloadedBooksImportService(
                    diskProvider,
                    diskScanService,
                    matchingService,
                    tagsService,
                    importApproved,
                    bookService,
                    authorService,
                    editionService,
                    importOrchestrator,
                    new StubAuthorLibraryService(),
                    new StubRootFolderService(),
                    configService,
                    historyService,
                    eventAggregator,
                    runtimeInfo,
                    DispatchProxy.Create<IMediaInfoExtractor, ThrowingProxy<IMediaInfoExtractor>>(),
                    LogManager.GetCurrentClassLogger());

                var remoteBook = new RemoteBook
                {
                    Author = author,
                    Books = new List<Book> { new Book { Id = 2015, AuthorId = 7, Title = "Expected Book", AnyEditionOk = true, MediaType = BookMediaType.Audiobook, HardcoverBookId = "hc:429306" } }
                };

                var downloadClientItem = new DownloadClientItem
                {
                    DownloadId = "DOWNLOAD-SAME-WORK-MISSING-EDITION",
                    CanMoveFiles = false,
                    DownloadClientInfo = new DownloadClientItemClientInfo { Id = 1, Name = "qBittorrent", Type = "qBittorrent" }
                };

                _ = service.ProcessPath(filePath, ImportMode.Auto, author, downloadClientItem, remoteBook);

                Assert.That(importApproved.Decisions, Has.Count.EqualTo(1));
                Assert.That(importApproved.Decisions[0].Approved, Is.False);
                Assert.That(importApproved.Decisions[0].Rejections.Select(r => r.Reason), Has.Some.Contains("no equivalent edition"));
                Assert.That(importApproved.Decisions[0].Rejections.Select(r => r.Reason), Has.Some.Contains("BookId 2015"));
                Assert.That(importApproved.Decisions[0].Rejections.Select(r => r.Reason), Has.Some.Contains("BookId 5079"));
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        // Issue #111: an author-bibliography re-discovery re-added the same real book under a different
        // metadata provider, so the duplicate row's work ID (gr:...) never intersects the grabbed row's
        // work ID (hc:...). Both rows still carry the same physical edition (same Audible ASIN), which is
        // globally unique, so the download must be retargeted onto the row that actually owns the grab.
        [Test]
        public void should_retarget_cross_provider_duplicate_row_match_to_grabbed_book_when_edition_identity_matches()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "chaptarr-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var filePath = Path.Combine(tempDir, "Fate Hollow Academy Term 1.m4b");
            File.WriteAllBytes(filePath, new byte[] { 1, 2, 3, 4 });

            try
            {
                var diskProvider = new StubDiskProvider();
                var diskScanService = new StubDiskScanService();
                var tagsService = new StubMetadataTagService();

                var matchingService = new StubFileMatchingService
                {
                    InitialResult = new FileMatchResult
                    {
                        MatchedFiles = new[]
                        {
                            new FileMatch
                            {
                                File = new DiscoveredFileWithMetadata { Path = filePath, Size = 4, Modified = DateTime.UtcNow, AllTags = tagsService.Tags },
                                AuthorId = 7,
                                AuthorName = "Test Author",
                                BookId = 29100,
                                BookTitle = "Fate Hollow Academy",
                                EditionId = 30
                            }
                        },
                        UnmatchedFiles = Array.Empty<UnmatchedFile>()
                    }
                };

                var author = new Author { Id = 7, Name = "Test Author" };

                // Never-monitored duplicate row, re-discovered from Goodreads.
                var matchedBook = new Book { Id = 29100, AuthorId = 7, Author = author, Title = "Fate Hollow Academy", AnyEditionOk = true, MediaType = BookMediaType.Audiobook, GoodreadsWorkId = "gr:200474660" };

                // The row that was actually grabbed, sourced from Hardcover. No work-ID overlap with the duplicate.
                var targetBook = new Book { Id = 29083, AuthorId = 7, Author = author, Title = "Fate Hollow Academy: Term 1", AnyEditionOk = true, MediaType = BookMediaType.Audiobook, HardcoverBookId = "hc:585413" };

                var matchedEdition = new Edition { Id = 30, BookId = 29100, Title = "Fate Hollow Academy", AudibleASIN = "B0DXYZ1234", ReadingFormatId = 2, Monitored = false, Book = matchedBook };
                var targetEdition = new Edition { Id = 40, BookId = 29083, Title = "Fate Hollow Academy: Term 1", AudibleASIN = "B0DXYZ1234", ReadingFormatId = 2, Monitored = false, Book = targetBook };
                matchedBook.Editions = new List<Edition> { matchedEdition };
                targetBook.Editions = new List<Edition> { targetEdition };

                var importApproved = new RecordingImportApprovedBooks();

                var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
                var bookProxy = (BookServiceProxy)(object)bookService;
                bookProxy.BooksById[29100] = matchedBook;
                bookProxy.BooksById[29083] = targetBook;

                var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
                ((AuthorServiceProxy)(object)authorService).Author = author;

                var editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
                var editionProxy = (EditionServiceProxy)(object)editionService;
                editionProxy.EditionsById[30] = matchedEdition;
                editionProxy.EditionsById[40] = targetEdition;
                editionProxy.EditionsByBookId[29100] = new List<Edition> { matchedEdition };
                editionProxy.EditionsByBookId[29083] = new List<Edition> { targetEdition };

                var importOrchestrator = DispatchProxy.Create<IImportOrchestrator, ThrowingProxy<IImportOrchestrator>>();
                var configService = ConfigServiceTestProxy.Create();
                var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
                var eventAggregator = DispatchProxy.Create<IEventAggregator, ThrowingProxy<IEventAggregator>>();
                var runtimeInfo = DispatchProxy.Create<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo, ThrowingProxy<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo>>();

                var service = new DownloadedBooksImportService(
                    diskProvider,
                    diskScanService,
                    matchingService,
                    tagsService,
                    importApproved,
                    bookService,
                    authorService,
                    editionService,
                    importOrchestrator,
                    new StubAuthorLibraryService(),
                    new StubRootFolderService(),
                    configService,
                    historyService,
                    eventAggregator,
                    runtimeInfo,
                    DispatchProxy.Create<IMediaInfoExtractor, ThrowingProxy<IMediaInfoExtractor>>(),
                    LogManager.GetCurrentClassLogger());

                var remoteBook = new RemoteBook
                {
                    Author = author,
                    Books = new List<Book> { new Book { Id = 29083, AuthorId = 7, Title = "Fate Hollow Academy: Term 1", AnyEditionOk = true, MediaType = BookMediaType.Audiobook, HardcoverBookId = "hc:585413" } }
                };

                var downloadClientItem = new DownloadClientItem
                {
                    DownloadId = "DOWNLOAD-CROSS-PROVIDER-DUPLICATE",
                    CanMoveFiles = false,
                    DownloadClientInfo = new DownloadClientItemClientInfo { Id = 1, Name = "qBittorrent", Type = "qBittorrent" }
                };

                _ = service.ProcessPath(filePath, ImportMode.Auto, author, downloadClientItem, remoteBook);

                Assert.That(importApproved.Decisions, Has.Count.EqualTo(1));
                Assert.That(importApproved.Decisions[0].Approved, Is.True);
                Assert.That(importApproved.Decisions[0].Item.Book.Id, Is.EqualTo(29083));
                Assert.That(importApproved.Decisions[0].Item.Edition.Id, Is.EqualTo(40));
                Assert.That(importApproved.Decisions[0].Item.Edition.Book, Is.SameAs(targetBook));
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        // Safety boundary for issue #111: same author and media type, but the two rows share no work-level
        // ID and no edition-level ID. Nothing proves they are the same book, so the file must NOT be moved
        // onto the grabbed row -- the download is rejected and the matched row is left untouched.
        [Test]
        public void should_not_retarget_cross_provider_row_match_when_no_shared_work_or_edition_identity()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "chaptarr-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var filePath = Path.Combine(tempDir, "Some Other Book.m4b");
            File.WriteAllBytes(filePath, new byte[] { 1, 2, 3, 4 });

            try
            {
                var diskProvider = new StubDiskProvider();
                var diskScanService = new StubDiskScanService();
                var tagsService = new StubMetadataTagService();

                var matchingService = new StubFileMatchingService
                {
                    InitialResult = new FileMatchResult
                    {
                        MatchedFiles = new[]
                        {
                            new FileMatch
                            {
                                File = new DiscoveredFileWithMetadata { Path = filePath, Size = 4, Modified = DateTime.UtcNow, AllTags = tagsService.Tags },
                                AuthorId = 7,
                                AuthorName = "Test Author",
                                BookId = 29100,
                                BookTitle = "A Different Book",
                                EditionId = 30
                            }
                        },
                        UnmatchedFiles = Array.Empty<UnmatchedFile>()
                    }
                };

                var author = new Author { Id = 7, Name = "Test Author" };
                var matchedBook = new Book { Id = 29100, AuthorId = 7, Author = author, Title = "A Different Book", AnyEditionOk = true, MediaType = BookMediaType.Audiobook, GoodreadsWorkId = "gr:200474660" };
                var targetBook = new Book { Id = 29083, AuthorId = 7, Author = author, Title = "Fate Hollow Academy: Term 1", AnyEditionOk = true, MediaType = BookMediaType.Audiobook, HardcoverBookId = "hc:585413" };

                var matchedEdition = new Edition { Id = 30, BookId = 29100, Title = "A Different Book", AudibleASIN = "B0DAAAAAAA", ReadingFormatId = 2, Monitored = false, Book = matchedBook };
                var targetEdition = new Edition { Id = 40, BookId = 29083, Title = "Fate Hollow Academy: Term 1", AudibleASIN = "B0DZZZZZZZ", ReadingFormatId = 2, Monitored = false, Book = targetBook };
                matchedBook.Editions = new List<Edition> { matchedEdition };
                targetBook.Editions = new List<Edition> { targetEdition };

                var importApproved = new RecordingImportApprovedBooks();

                var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
                var bookProxy = (BookServiceProxy)(object)bookService;
                bookProxy.BooksById[29100] = matchedBook;
                bookProxy.BooksById[29083] = targetBook;

                var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
                ((AuthorServiceProxy)(object)authorService).Author = author;

                var editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
                var editionProxy = (EditionServiceProxy)(object)editionService;
                editionProxy.EditionsById[30] = matchedEdition;
                editionProxy.EditionsById[40] = targetEdition;
                editionProxy.EditionsByBookId[29100] = new List<Edition> { matchedEdition };
                editionProxy.EditionsByBookId[29083] = new List<Edition> { targetEdition };

                var importOrchestrator = DispatchProxy.Create<IImportOrchestrator, ThrowingProxy<IImportOrchestrator>>();
                var configService = ConfigServiceTestProxy.Create();
                var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
                var eventAggregator = DispatchProxy.Create<IEventAggregator, ThrowingProxy<IEventAggregator>>();
                var runtimeInfo = DispatchProxy.Create<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo, ThrowingProxy<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo>>();

                var service = new DownloadedBooksImportService(
                    diskProvider,
                    diskScanService,
                    matchingService,
                    tagsService,
                    importApproved,
                    bookService,
                    authorService,
                    editionService,
                    importOrchestrator,
                    new StubAuthorLibraryService(),
                    new StubRootFolderService(),
                    configService,
                    historyService,
                    eventAggregator,
                    runtimeInfo,
                    DispatchProxy.Create<IMediaInfoExtractor, ThrowingProxy<IMediaInfoExtractor>>(),
                    LogManager.GetCurrentClassLogger());

                var remoteBook = new RemoteBook
                {
                    Author = author,
                    Books = new List<Book> { new Book { Id = 29083, AuthorId = 7, Title = "Fate Hollow Academy: Term 1", AnyEditionOk = true, MediaType = BookMediaType.Audiobook, HardcoverBookId = "hc:585413" } }
                };

                var downloadClientItem = new DownloadClientItem
                {
                    DownloadId = "DOWNLOAD-CROSS-PROVIDER-UNRELATED",
                    CanMoveFiles = false,
                    DownloadClientInfo = new DownloadClientItemClientInfo { Id = 1, Name = "qBittorrent", Type = "qBittorrent" }
                };

                _ = service.ProcessPath(filePath, ImportMode.Auto, author, downloadClientItem, remoteBook);

                Assert.That(importApproved.Decisions, Has.Count.EqualTo(1));
                Assert.That(importApproved.Decisions[0].Approved, Is.False);

                // The file must stay attached to the row the matcher chose; retargeting it would misfile it.
                Assert.That(importApproved.Decisions[0].Item.Book.Id, Is.EqualTo(29100));
                Assert.That(importApproved.Decisions[0].Rejections.Select(r => r.Reason), Has.Some.Contains("but import matched"));
                Assert.That(importApproved.Decisions[0].Rejections.Select(r => r.Reason), Has.None.Contains("no equivalent edition"));
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Test]
        public void should_reject_completed_download_when_latest_grabbed_edition_is_strict_and_match_hits_different_sibling()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "chaptarr-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var filePath = Path.Combine(tempDir, "Expected.m4b");
            File.WriteAllBytes(filePath, new byte[] { 1, 2, 3, 4 });

            try
            {
                var diskProvider = new StubDiskProvider();
                var diskScanService = new StubDiskScanService();
                var tagsService = new StubMetadataTagService();

                var matchingService = new StubFileMatchingService
                {
                    InitialResult = new FileMatchResult
                    {
                        MatchedFiles = new[]
                        {
                            new FileMatch
                            {
                                File = new DiscoveredFileWithMetadata { Path = filePath, Size = 4, Modified = DateTime.UtcNow, AllTags = tagsService.Tags },
                                AuthorId = 7,
                                AuthorName = "Test Author",
                                BookId = 10,
                                BookTitle = "Expected Book",
                                EditionId = 3
                            }
                        },
                        UnmatchedFiles = Array.Empty<UnmatchedFile>()
                    }
                };

                var importApproved = new RecordingImportApprovedBooks();

                var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
                ((BookServiceProxy)(object)bookService).Book = new Book { Id = 10, AuthorId = 7, Title = "Expected Book", AnyEditionOk = false };

                var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
                ((AuthorServiceProxy)(object)authorService).Author = new Author { Id = 7, Name = "Test Author" };

                var editionThree = new Edition { Id = 3, BookId = 10, Title = "Edition Three", Monitored = false, ManualAdd = false, ReadingFormatId = 2 };
                var editionFour = new Edition { Id = 4, BookId = 10, Title = "Edition Four", Monitored = true, ManualAdd = false, ReadingFormatId = 2 };
                var editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
                ((EditionServiceProxy)(object)editionService).Edition = editionThree;
                ((EditionServiceProxy)(object)editionService).EditionsByBook = new List<Edition> { editionThree, editionFour };

                var importOrchestrator = DispatchProxy.Create<IImportOrchestrator, ThrowingProxy<IImportOrchestrator>>();
                var configService = ConfigServiceTestProxy.Create();
                var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
                ((HistoryServiceProxy)(object)historyService).HistoryItems = new List<EntityHistory>
                {
                    new() { EventType = EntityHistoryEventType.Grabbed, BookId = 10, EditionId = 3, DownloadId = "DOWNLOAD-STRICT", Date = new DateTime(2026, 4, 11, 10, 0, 0, DateTimeKind.Utc) },
                    new() { EventType = EntityHistoryEventType.Grabbed, BookId = 10, EditionId = 4, DownloadId = "DOWNLOAD-STRICT", Date = new DateTime(2026, 4, 11, 11, 0, 0, DateTimeKind.Utc) }
                };
                var eventAggregator = DispatchProxy.Create<IEventAggregator, ThrowingProxy<IEventAggregator>>();
                var runtimeInfo = DispatchProxy.Create<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo, ThrowingProxy<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo>>();

                var service = new DownloadedBooksImportService(
                    diskProvider,
                    diskScanService,
                    matchingService,
                    tagsService,
                    importApproved,
                    bookService,
                    authorService,
                    editionService,
                    importOrchestrator,
                    new StubAuthorLibraryService(),
                    new StubRootFolderService(),
                    configService,
                    historyService,
                    eventAggregator,
                    runtimeInfo,
                    DispatchProxy.Create<IMediaInfoExtractor, ThrowingProxy<IMediaInfoExtractor>>(),
                    LogManager.GetCurrentClassLogger());

                var remoteBook = new RemoteBook
                {
                    Author = ((AuthorServiceProxy)(object)authorService).Author,
                    Books = new List<Book> { new Book { Id = 10, AuthorId = 7, Title = "Expected Book", AnyEditionOk = false } }
                };

                var downloadClientItem = new DownloadClientItem
                {
                    DownloadId = "DOWNLOAD-STRICT",
                    CanMoveFiles = false,
                    DownloadClientInfo = new DownloadClientItemClientInfo { Id = 1, Name = "qBittorrent", Type = "qBittorrent" }
                };

                _ = service.ProcessPath(filePath, ImportMode.Auto, remoteBook.Author, downloadClientItem, remoteBook);

                Assert.That(importApproved.Decisions, Has.Count.EqualTo(1));
                Assert.That(importApproved.Decisions[0].Approved, Is.False);
                Assert.That(importApproved.Decisions[0].Rejections.Select(r => r.Reason), Has.Some.Contains("Edition Four"));
                Assert.That(importApproved.Decisions[0].Rejections.Select(r => r.Reason), Has.Some.Contains("Edition Three"));
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Test]
        public void should_keep_matcher_edition_when_flexible_book_has_no_embedded_metadata_for_sibling_switch()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "chaptarr-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var filePath = Path.Combine(tempDir, "Expected.m4b");
            File.WriteAllBytes(filePath, new byte[] { 1, 2, 3, 4 });

            try
            {
                var diskProvider = new StubDiskProvider();
                var diskScanService = new StubDiskScanService();
                var tagsService = new StubMetadataTagService
                {
                    Tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["MP4:©too"] = new List<string> { "lavf62.3.100" },
                        ["TOTALDURATION"] = new List<string> { "27490" }
                    }
                };

                var matchingService = new StubFileMatchingService
                {
                    InitialResult = new FileMatchResult
                    {
                        MatchedFiles = new[]
                        {
                            new FileMatch
                            {
                                File = new DiscoveredFileWithMetadata { Path = filePath, Size = 4, Modified = DateTime.UtcNow, AllTags = tagsService.Tags },
                                AuthorId = 7,
                                AuthorName = "Test Author",
                                BookId = 10,
                                BookTitle = "Expected Book",
                                EditionId = 3
                            }
                        },
                        UnmatchedFiles = Array.Empty<UnmatchedFile>()
                    }
                };

                var importApproved = new RecordingImportApprovedBooks();

                var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
                ((BookServiceProxy)(object)bookService).Book = new Book { Id = 10, AuthorId = 7, Title = "Expected Book", AnyEditionOk = true };

                var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
                ((AuthorServiceProxy)(object)authorService).Author = new Author { Id = 7, Name = "Test Author" };

                var editionThree = new Edition { Id = 3, BookId = 10, Title = "Edition Three", Monitored = false, ManualAdd = false, ReadingFormatId = 2 };
                var editionFour = new Edition { Id = 4, BookId = 10, Title = "Edition Four", Monitored = true, ManualAdd = false, ReadingFormatId = 2 };
                var editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
                ((EditionServiceProxy)(object)editionService).Edition = editionThree;
                ((EditionServiceProxy)(object)editionService).EditionsByBook = new List<Edition> { editionThree, editionFour };

                var importOrchestrator = DispatchProxy.Create<IImportOrchestrator, ThrowingProxy<IImportOrchestrator>>();
                var configService = ConfigServiceTestProxy.Create();
                var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
                ((HistoryServiceProxy)(object)historyService).HistoryItems = new List<EntityHistory>
                {
                    new() { EventType = EntityHistoryEventType.Grabbed, BookId = 10, EditionId = 3, DownloadId = "DOWNLOAD-FLEX", Date = new DateTime(2026, 4, 11, 10, 0, 0, DateTimeKind.Utc) },
                    new() { EventType = EntityHistoryEventType.Grabbed, BookId = 10, EditionId = 4, DownloadId = "DOWNLOAD-FLEX", Date = new DateTime(2026, 4, 11, 11, 0, 0, DateTimeKind.Utc) }
                };
                var eventAggregator = DispatchProxy.Create<IEventAggregator, ThrowingProxy<IEventAggregator>>();
                var runtimeInfo = DispatchProxy.Create<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo, ThrowingProxy<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo>>();

                var service = new DownloadedBooksImportService(
                    diskProvider,
                    diskScanService,
                    matchingService,
                    tagsService,
                    importApproved,
                    bookService,
                    authorService,
                    editionService,
                    importOrchestrator,
                    new StubAuthorLibraryService(),
                    new StubRootFolderService(),
                    configService,
                    historyService,
                    eventAggregator,
                    runtimeInfo,
                    DispatchProxy.Create<IMediaInfoExtractor, ThrowingProxy<IMediaInfoExtractor>>(),
                    LogManager.GetCurrentClassLogger());

                var remoteBook = new RemoteBook
                {
                    Author = ((AuthorServiceProxy)(object)authorService).Author,
                    Books = new List<Book> { new Book { Id = 10, AuthorId = 7, Title = "Expected Book", AnyEditionOk = true } }
                };

                var downloadClientItem = new DownloadClientItem
                {
                    DownloadId = "DOWNLOAD-FLEX",
                    CanMoveFiles = false,
                    DownloadClientInfo = new DownloadClientItemClientInfo { Id = 1, Name = "qBittorrent", Type = "qBittorrent" }
                };

                _ = service.ProcessPath(filePath, ImportMode.Auto, remoteBook.Author, downloadClientItem, remoteBook);

                Assert.That(importApproved.Decisions, Has.Count.EqualTo(1));
                Assert.That(importApproved.Decisions[0].Approved, Is.True);
                Assert.That(importApproved.Decisions[0].Item.Edition, Is.Not.Null);
                Assert.That(importApproved.Decisions[0].Item.Edition.Id, Is.EqualTo(3));
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
        [Test]
        public void should_allow_flexible_book_to_keep_sibling_match_when_identity_metadata_is_present()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "chaptarr-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var filePath = Path.Combine(tempDir, "Expected.m4b");
            File.WriteAllBytes(filePath, new byte[] { 1, 2, 3, 4 });

            try
            {
                var diskProvider = new StubDiskProvider();
                var diskScanService = new StubDiskScanService();
                var tagsService = new StubMetadataTagService
                {
                    Tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["TITLE"] = new List<string> { "Edition Three" },
                        ["ARTIST"] = new List<string> { "Test Author" },
                        ["ALBUM"] = new List<string> { "Expected Book" }
                    }
                };

                var matchingService = new StubFileMatchingService
                {
                    InitialResult = new FileMatchResult
                    {
                        MatchedFiles = new[]
                        {
                            new FileMatch
                            {
                                File = new DiscoveredFileWithMetadata { Path = filePath, Size = 4, Modified = DateTime.UtcNow, AllTags = tagsService.Tags },
                                AuthorId = 7,
                                AuthorName = "Test Author",
                                BookId = 10,
                                BookTitle = "Expected Book",
                                EditionId = 3
                            }
                        },
                        UnmatchedFiles = Array.Empty<UnmatchedFile>()
                    }
                };

                var importApproved = new RecordingImportApprovedBooks();

                var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
                ((BookServiceProxy)(object)bookService).Book = new Book { Id = 10, AuthorId = 7, Title = "Expected Book", AnyEditionOk = true };

                var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
                ((AuthorServiceProxy)(object)authorService).Author = new Author { Id = 7, Name = "Test Author" };

                var editionThree = new Edition { Id = 3, BookId = 10, Title = "Edition Three", Monitored = false, ManualAdd = false, ReadingFormatId = 2 };
                var editionFour = new Edition { Id = 4, BookId = 10, Title = "Edition Four", Monitored = true, ManualAdd = false, ReadingFormatId = 2 };
                var editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
                ((EditionServiceProxy)(object)editionService).Edition = editionThree;
                ((EditionServiceProxy)(object)editionService).EditionsByBook = new List<Edition> { editionThree, editionFour };

                var importOrchestrator = DispatchProxy.Create<IImportOrchestrator, ThrowingProxy<IImportOrchestrator>>();
                var configService = ConfigServiceTestProxy.Create();
                var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
                ((HistoryServiceProxy)(object)historyService).HistoryItems = new List<EntityHistory>
                {
                    new() { EventType = EntityHistoryEventType.Grabbed, BookId = 10, EditionId = 4, DownloadId = "DOWNLOAD-FLEX-EVIDENCED", Date = new DateTime(2026, 4, 11, 11, 0, 0, DateTimeKind.Utc) }
                };
                var eventAggregator = DispatchProxy.Create<IEventAggregator, ThrowingProxy<IEventAggregator>>();
                var runtimeInfo = DispatchProxy.Create<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo, ThrowingProxy<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo>>();

                var service = new DownloadedBooksImportService(
                    diskProvider,
                    diskScanService,
                    matchingService,
                    tagsService,
                    importApproved,
                    bookService,
                    authorService,
                    editionService,
                    importOrchestrator,
                    new StubAuthorLibraryService(),
                    new StubRootFolderService(),
                    configService,
                    historyService,
                    eventAggregator,
                    runtimeInfo,
                    DispatchProxy.Create<IMediaInfoExtractor, ThrowingProxy<IMediaInfoExtractor>>(),
                    LogManager.GetCurrentClassLogger());

                var remoteBook = new RemoteBook
                {
                    Author = ((AuthorServiceProxy)(object)authorService).Author,
                    Books = new List<Book> { new Book { Id = 10, AuthorId = 7, Title = "Expected Book", AnyEditionOk = true } }
                };

                var downloadClientItem = new DownloadClientItem
                {
                    DownloadId = "DOWNLOAD-FLEX-EVIDENCED",
                    CanMoveFiles = false,
                    DownloadClientInfo = new DownloadClientItemClientInfo { Id = 1, Name = "qBittorrent", Type = "qBittorrent" }
                };

                _ = service.ProcessPath(filePath, ImportMode.Auto, remoteBook.Author, downloadClientItem, remoteBook);

                Assert.That(importApproved.Decisions, Has.Count.EqualTo(1));
                Assert.That(importApproved.Decisions[0].Approved, Is.True);
                Assert.That(importApproved.Decisions[0].Item.Edition, Is.Not.Null);
                Assert.That(importApproved.Decisions[0].Item.Edition.Id, Is.EqualTo(3));
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }


        [Test]
        public void should_not_repair_tracked_multipart_audio_download_when_two_parts_share_same_normalized_contract()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "chaptarr-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var fileOne = Path.Combine(tempDir, "Dune - House Harkonnen - Part 1.m4b");
            var fileTwo = Path.Combine(tempDir, "Dune - House Harkonnen - Part 2.m4b");
            var fileThree = Path.Combine(tempDir, "Dune - House Harkonnen - Part 3.m4b");
            File.WriteAllBytes(fileOne, new byte[] { 1, 2, 3, 4 });
            File.WriteAllBytes(fileTwo, new byte[] { 1, 2, 3, 4 });
            File.WriteAllBytes(fileThree, new byte[] { 1, 2, 3, 4 });

            try
            {
                var diskProvider = new StubDiskProvider();
                var diskScanService = new StubDiskScanService();
                var tagsService = new StubMetadataTagService();
                tagsService.TagsByPath[fileOne] = CreateAudioTags("House Harkonnen (Unabridged)", "Dune_House Harkonnen - Part 1", "Brian Herbert", "Scott Brick");
                tagsService.TagsByPath[fileTwo] = CreateAudioTags("House Harkonnen (Unabridged)", "Dune_House Harkonnen - Part 2", "Brian Herbert", "Scott Brick");
                tagsService.TagsByPath[fileThree] = CreateAudioTags("House Harkonnen (Unabridged)", "Dune_House Harkonnen - Part 3", "Brian Herbert", "Scott Brick");

                var author = new Author { Id = 7, Name = "Brian Herbert" };
                var targetBook = new Book { Id = 10, AuthorId = 7, Title = "House Harkonnen", AnyEditionOk = true, Author = author };
                var preferredEdition = new Edition { Id = 11, BookId = 10, Title = "House Harkonnen", Monitored = true, ManualAdd = false, ReadingFormatId = 2, Book = targetBook };
                targetBook.Editions = new List<Edition> { preferredEdition };

                var matchingService = new StubFileMatchingService
                {
                    InitialResult = new FileMatchResult
                    {
                        MatchedFiles = new[]
                        {
                            CreateMatchedFile(fileOne, tagsService.TagsByPath[fileOne], 7, "Brian Herbert", 10, "House Harkonnen", 11),
                            CreateMatchedFile(fileTwo, tagsService.TagsByPath[fileTwo], 7, "Brian Herbert", 10, "House Harkonnen", 11)
                        },
                        UnmatchedFiles = new[]
                        {
                            CreateUnmatchedFile(fileThree, tagsService.TagsByPath[fileThree], "No confident single-file match")
                        }
                    }
                };

                var importApproved = new RecordingImportApprovedBooks();
                var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
                ((BookServiceProxy)(object)bookService).Book = targetBook;

                var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
                ((AuthorServiceProxy)(object)authorService).Author = author;

                var editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
                ((EditionServiceProxy)(object)editionService).Edition = preferredEdition;
                ((EditionServiceProxy)(object)editionService).EditionsByBook = new List<Edition> { preferredEdition };

                var importOrchestrator = DispatchProxy.Create<IImportOrchestrator, ThrowingProxy<IImportOrchestrator>>();
                var configService = ConfigServiceTestProxy.Create();
                var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
                ((HistoryServiceProxy)(object)historyService).HistoryItems = new List<EntityHistory>
                {
                    new() { EventType = EntityHistoryEventType.Grabbed, BookId = 10, EditionId = 11, DownloadId = "DOWNLOAD-MULTIPART", Date = new DateTime(2026, 4, 12, 0, 0, 0, DateTimeKind.Utc) }
                };
                var eventAggregator = DispatchProxy.Create<IEventAggregator, ThrowingProxy<IEventAggregator>>();
                var runtimeInfo = DispatchProxy.Create<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo, ThrowingProxy<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo>>();

                var service = new DownloadedBooksImportService(
                    diskProvider,
                    diskScanService,
                    matchingService,
                    tagsService,
                    importApproved,
                    bookService,
                    authorService,
                    editionService,
                    importOrchestrator,
                    new StubAuthorLibraryService(),
                    new StubRootFolderService(),
                    configService,
                    historyService,
                    eventAggregator,
                    runtimeInfo,
                    DispatchProxy.Create<IMediaInfoExtractor, ThrowingProxy<IMediaInfoExtractor>>(),
                    LogManager.GetCurrentClassLogger());

                var remoteBook = new RemoteBook
                {
                    Author = author,
                    Books = new List<Book> { new Book { Id = 10, AuthorId = 7, Title = "House Harkonnen", AnyEditionOk = true } }
                };

                var downloadClientItem = new DownloadClientItem
                {
                    Title = "Dune - House Harkonnen (Unabridged)",
                    DownloadId = "DOWNLOAD-MULTIPART",
                    FilePaths = new List<string> { fileOne, fileTwo, fileThree },
                    CanMoveFiles = false,
                    DownloadClientInfo = new DownloadClientItemClientInfo { Id = 1, Name = "qBittorrent", Type = "qBittorrent" }
                };

                _ = service.ProcessPath(tempDir, ImportMode.Auto, author, downloadClientItem, remoteBook);

                Assert.That(importApproved.Decisions, Has.Count.EqualTo(3));
                Assert.That(importApproved.Decisions.Count(decision => decision.Approved), Is.EqualTo(2));
                Assert.That(importApproved.Decisions.Count(decision => !decision.Approved), Is.EqualTo(1));
                Assert.That(importApproved.Decisions.Single(decision => !decision.Approved).Item.Path, Is.EqualTo(fileThree));
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Test]
        public void should_not_repair_tracked_multipart_audio_download_when_only_one_seed_match_exists()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "chaptarr-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var fileOne = Path.Combine(tempDir, "Dune - House Harkonnen - Part 1.m4b");
            var fileTwo = Path.Combine(tempDir, "Dune - House Harkonnen - Part 2.m4b");
            File.WriteAllBytes(fileOne, new byte[] { 1, 2, 3, 4 });
            File.WriteAllBytes(fileTwo, new byte[] { 1, 2, 3, 4 });

            try
            {
                var diskProvider = new StubDiskProvider();
                var diskScanService = new StubDiskScanService();
                var tagsService = new StubMetadataTagService();
                tagsService.TagsByPath[fileOne] = CreateAudioTags("House Harkonnen (Unabridged)", "Dune_House Harkonnen - Part 1", "Brian Herbert", "Scott Brick");
                tagsService.TagsByPath[fileTwo] = CreateAudioTags("House Harkonnen (Unabridged)", "Dune_House Harkonnen - Part 2", "Brian Herbert", "Scott Brick");

                var author = new Author { Id = 7, Name = "Brian Herbert" };
                var targetBook = new Book { Id = 10, AuthorId = 7, Title = "House Harkonnen", AnyEditionOk = true, Author = author };
                var preferredEdition = new Edition { Id = 11, BookId = 10, Title = "House Harkonnen", Monitored = true, ManualAdd = false, ReadingFormatId = 2, Book = targetBook };
                targetBook.Editions = new List<Edition> { preferredEdition };

                var matchingService = new StubFileMatchingService
                {
                    InitialResult = new FileMatchResult
                    {
                        MatchedFiles = new[]
                        {
                            CreateMatchedFile(fileOne, tagsService.TagsByPath[fileOne], 7, "Brian Herbert", 10, "House Harkonnen", 11)
                        },
                        UnmatchedFiles = new[]
                        {
                            CreateUnmatchedFile(fileTwo, tagsService.TagsByPath[fileTwo], "No confident single-file match")
                        }
                    }
                };

                var importApproved = new RecordingImportApprovedBooks();
                var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
                ((BookServiceProxy)(object)bookService).Book = targetBook;

                var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
                ((AuthorServiceProxy)(object)authorService).Author = author;

                var editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
                ((EditionServiceProxy)(object)editionService).Edition = preferredEdition;
                ((EditionServiceProxy)(object)editionService).EditionsByBook = new List<Edition> { preferredEdition };

                var importOrchestrator = DispatchProxy.Create<IImportOrchestrator, ThrowingProxy<IImportOrchestrator>>();
                var configService = ConfigServiceTestProxy.Create();
                var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
                ((HistoryServiceProxy)(object)historyService).HistoryItems = new List<EntityHistory>
                {
                    new() { EventType = EntityHistoryEventType.Grabbed, BookId = 10, EditionId = 11, DownloadId = "DOWNLOAD-MULTIPART-ONE-SEED", Date = new DateTime(2026, 4, 12, 0, 0, 0, DateTimeKind.Utc) }
                };
                var eventAggregator = DispatchProxy.Create<IEventAggregator, ThrowingProxy<IEventAggregator>>();
                var runtimeInfo = DispatchProxy.Create<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo, ThrowingProxy<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo>>();

                var service = new DownloadedBooksImportService(
                    diskProvider,
                    diskScanService,
                    matchingService,
                    tagsService,
                    importApproved,
                    bookService,
                    authorService,
                    editionService,
                    importOrchestrator,
                    new StubAuthorLibraryService(),
                    new StubRootFolderService(),
                    configService,
                    historyService,
                    eventAggregator,
                    runtimeInfo,
                    DispatchProxy.Create<IMediaInfoExtractor, ThrowingProxy<IMediaInfoExtractor>>(),
                    LogManager.GetCurrentClassLogger());

                var remoteBook = new RemoteBook
                {
                    Author = author,
                    Books = new List<Book> { new Book { Id = 10, AuthorId = 7, Title = "House Harkonnen", AnyEditionOk = true } }
                };

                var downloadClientItem = new DownloadClientItem
                {
                    Title = "Dune - House Harkonnen (Unabridged)",
                    DownloadId = "DOWNLOAD-MULTIPART-ONE-SEED",
                    FilePaths = new List<string> { fileOne, fileTwo },
                    CanMoveFiles = false,
                    DownloadClientInfo = new DownloadClientItemClientInfo { Id = 1, Name = "qBittorrent", Type = "qBittorrent" }
                };

                _ = service.ProcessPath(tempDir, ImportMode.Auto, author, downloadClientItem, remoteBook);

                Assert.That(importApproved.Decisions, Has.Count.EqualTo(2));
                Assert.That(importApproved.Decisions.Count(decision => decision.Approved), Is.EqualTo(1));
                Assert.That(importApproved.Decisions.Count(decision => !decision.Approved), Is.EqualTo(1));
                Assert.That(importApproved.Decisions.Single(decision => !decision.Approved).Item.Path, Is.EqualTo(fileTwo));
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Test]
        public void should_not_absorb_bare_numeric_sibling_title_into_tracked_multipart_cluster()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "chaptarr-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var fileOne = Path.Combine(tempDir, "LitRPG 5 - Part 1.m4b");
            var fileTwo = Path.Combine(tempDir, "LitRPG 5 - Part 2.m4b");
            var fileThree = Path.Combine(tempDir, "LitRPG 6.m4b");
            File.WriteAllBytes(fileOne, new byte[] { 1, 2, 3, 4 });
            File.WriteAllBytes(fileTwo, new byte[] { 1, 2, 3, 4 });
            File.WriteAllBytes(fileThree, new byte[] { 1, 2, 3, 4 });

            try
            {
                var diskProvider = new StubDiskProvider();
                var diskScanService = new StubDiskScanService();
                var tagsService = new StubMetadataTagService();
                tagsService.TagsByPath[fileOne] = CreateAudioTags("LitRPG 5", "LitRPG 5 - Part 1", "Series Author", "Narrator One");
                tagsService.TagsByPath[fileTwo] = CreateAudioTags("LitRPG 5", "LitRPG 5 - Part 2", "Series Author", "Narrator One");
                tagsService.TagsByPath[fileThree] = CreateAudioTags("LitRPG 6", "LitRPG 6", "Series Author", "Narrator One");

                var author = new Author { Id = 9, Name = "Series Author" };
                var targetBook = new Book { Id = 20, AuthorId = 9, Title = "LitRPG 5", AnyEditionOk = true, Author = author };
                var preferredEdition = new Edition { Id = 21, BookId = 20, Title = "LitRPG 5", Monitored = true, ManualAdd = false, ReadingFormatId = 2, Book = targetBook };
                targetBook.Editions = new List<Edition> { preferredEdition };

                var matchingService = new StubFileMatchingService
                {
                    InitialResult = new FileMatchResult
                    {
                        MatchedFiles = new[]
                        {
                            CreateMatchedFile(fileOne, tagsService.TagsByPath[fileOne], 9, "Series Author", 20, "LitRPG 5", 21),
                            CreateMatchedFile(fileTwo, tagsService.TagsByPath[fileTwo], 9, "Series Author", 20, "LitRPG 5", 21)
                        },
                        UnmatchedFiles = new[]
                        {
                            CreateUnmatchedFile(fileThree, tagsService.TagsByPath[fileThree], "No confident single-file match")
                        }
                    }
                };

                var importApproved = new RecordingImportApprovedBooks();
                var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
                ((BookServiceProxy)(object)bookService).Book = targetBook;

                var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
                ((AuthorServiceProxy)(object)authorService).Author = author;

                var editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
                ((EditionServiceProxy)(object)editionService).Edition = preferredEdition;
                ((EditionServiceProxy)(object)editionService).EditionsByBook = new List<Edition> { preferredEdition };

                var importOrchestrator = DispatchProxy.Create<IImportOrchestrator, ThrowingProxy<IImportOrchestrator>>();
                var configService = ConfigServiceTestProxy.Create();
                var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
                ((HistoryServiceProxy)(object)historyService).HistoryItems = new List<EntityHistory>
                {
                    new() { EventType = EntityHistoryEventType.Grabbed, BookId = 20, EditionId = 21, DownloadId = "DOWNLOAD-NUMERIC-SIBLING", Date = new DateTime(2026, 4, 12, 0, 0, 0, DateTimeKind.Utc) }
                };
                var eventAggregator = DispatchProxy.Create<IEventAggregator, ThrowingProxy<IEventAggregator>>();
                var runtimeInfo = DispatchProxy.Create<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo, ThrowingProxy<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo>>();

                var service = new DownloadedBooksImportService(
                    diskProvider,
                    diskScanService,
                    matchingService,
                    tagsService,
                    importApproved,
                    bookService,
                    authorService,
                    editionService,
                    importOrchestrator,
                    new StubAuthorLibraryService(),
                    new StubRootFolderService(),
                    configService,
                    historyService,
                    eventAggregator,
                    runtimeInfo,
                    DispatchProxy.Create<IMediaInfoExtractor, ThrowingProxy<IMediaInfoExtractor>>(),
                    LogManager.GetCurrentClassLogger());

                var remoteBook = new RemoteBook
                {
                    Author = author,
                    Books = new List<Book> { new Book { Id = 20, AuthorId = 9, Title = "LitRPG 5", AnyEditionOk = true } }
                };

                var downloadClientItem = new DownloadClientItem
                {
                    Title = "LitRPG 5 multipart",
                    DownloadId = "DOWNLOAD-NUMERIC-SIBLING",
                    FilePaths = new List<string> { fileOne, fileTwo, fileThree },
                    CanMoveFiles = false,
                    DownloadClientInfo = new DownloadClientItemClientInfo { Id = 1, Name = "qBittorrent", Type = "qBittorrent" }
                };

                _ = service.ProcessPath(tempDir, ImportMode.Auto, author, downloadClientItem, remoteBook);

                Assert.That(importApproved.Decisions, Has.Count.EqualTo(3));
                Assert.That(importApproved.Decisions.Count(decision => decision.Approved), Is.EqualTo(2));
                Assert.That(importApproved.Decisions.Count(decision => !decision.Approved), Is.EqualTo(1));
                Assert.That(importApproved.Decisions.Single(decision => !decision.Approved).Item.Path, Is.EqualTo(fileThree));
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Test]
        public void should_not_strip_literal_part_word_without_adjacent_number_when_repairing_cluster()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "chaptarr-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var fileOne = Path.Combine(tempDir, "The Best Part - Part 1.m4b");
            var fileTwo = Path.Combine(tempDir, "The Best Part - Part 2.m4b");
            var fileThree = Path.Combine(tempDir, "The Best.m4b");
            File.WriteAllBytes(fileOne, new byte[] { 1, 2, 3, 4 });
            File.WriteAllBytes(fileTwo, new byte[] { 1, 2, 3, 4 });
            File.WriteAllBytes(fileThree, new byte[] { 1, 2, 3, 4 });

            try
            {
                var diskProvider = new StubDiskProvider();
                var diskScanService = new StubDiskScanService();
                var tagsService = new StubMetadataTagService();
                tagsService.TagsByPath[fileOne] = CreateAudioTags("The Best Part", "The Best Part - Part 1", "Author Example", "Narrator One");
                tagsService.TagsByPath[fileTwo] = CreateAudioTags("The Best Part", "The Best Part - Part 2", "Author Example", "Narrator One");
                tagsService.TagsByPath[fileThree] = CreateAudioTags("The Best", "The Best", "Author Example", "Narrator One");

                var author = new Author { Id = 30, Name = "Author Example" };
                var targetBook = new Book { Id = 31, AuthorId = 30, Title = "The Best Part", AnyEditionOk = true, Author = author };
                var preferredEdition = new Edition { Id = 32, BookId = 31, Title = "The Best Part", Monitored = true, ManualAdd = false, ReadingFormatId = 2, Book = targetBook };
                targetBook.Editions = new List<Edition> { preferredEdition };

                var matchingService = new StubFileMatchingService
                {
                    InitialResult = new FileMatchResult
                    {
                        MatchedFiles = new[]
                        {
                            CreateMatchedFile(fileOne, tagsService.TagsByPath[fileOne], 30, "Author Example", 31, "The Best Part", 32),
                            CreateMatchedFile(fileTwo, tagsService.TagsByPath[fileTwo], 30, "Author Example", 31, "The Best Part", 32)
                        },
                        UnmatchedFiles = new[]
                        {
                            CreateUnmatchedFile(fileThree, tagsService.TagsByPath[fileThree], "No confident single-file match")
                        }
                    }
                };

                var importApproved = new RecordingImportApprovedBooks();
                var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
                ((BookServiceProxy)(object)bookService).Book = targetBook;

                var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
                ((AuthorServiceProxy)(object)authorService).Author = author;

                var editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
                ((EditionServiceProxy)(object)editionService).Edition = preferredEdition;
                ((EditionServiceProxy)(object)editionService).EditionsByBook = new List<Edition> { preferredEdition };

                var importOrchestrator = DispatchProxy.Create<IImportOrchestrator, ThrowingProxy<IImportOrchestrator>>();
                var configService = ConfigServiceTestProxy.Create();
                var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
                ((HistoryServiceProxy)(object)historyService).HistoryItems = new List<EntityHistory>
                {
                    new() { EventType = EntityHistoryEventType.Grabbed, BookId = 31, EditionId = 32, DownloadId = "DOWNLOAD-LITERAL-PART", Date = new DateTime(2026, 4, 12, 0, 0, 0, DateTimeKind.Utc) }
                };
                var eventAggregator = DispatchProxy.Create<IEventAggregator, ThrowingProxy<IEventAggregator>>();
                var runtimeInfo = DispatchProxy.Create<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo, ThrowingProxy<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo>>();

                var service = new DownloadedBooksImportService(
                    diskProvider,
                    diskScanService,
                    matchingService,
                    tagsService,
                    importApproved,
                    bookService,
                    authorService,
                    editionService,
                    importOrchestrator,
                    new StubAuthorLibraryService(),
                    new StubRootFolderService(),
                    configService,
                    historyService,
                    eventAggregator,
                    runtimeInfo,
                    DispatchProxy.Create<IMediaInfoExtractor, ThrowingProxy<IMediaInfoExtractor>>(),
                    LogManager.GetCurrentClassLogger());

                var remoteBook = new RemoteBook
                {
                    Author = author,
                    Books = new List<Book> { new Book { Id = 31, AuthorId = 30, Title = "The Best Part", AnyEditionOk = true } }
                };

                var downloadClientItem = new DownloadClientItem
                {
                    Title = "The Best Part multipart",
                    DownloadId = "DOWNLOAD-LITERAL-PART",
                    FilePaths = new List<string> { fileOne, fileTwo, fileThree },
                    CanMoveFiles = false,
                    DownloadClientInfo = new DownloadClientItemClientInfo { Id = 1, Name = "qBittorrent", Type = "qBittorrent" }
                };

                _ = service.ProcessPath(tempDir, ImportMode.Auto, author, downloadClientItem, remoteBook);

                Assert.That(importApproved.Decisions, Has.Count.EqualTo(3));
                Assert.That(importApproved.Decisions.Count(decision => decision.Approved), Is.EqualTo(2));
                Assert.That(importApproved.Decisions.Count(decision => !decision.Approved), Is.EqualTo(1));
                Assert.That(importApproved.Decisions.Single(decision => !decision.Approved).Item.Path, Is.EqualTo(fileThree));
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        private static FileMatch CreateMatchedFile(string path, Dictionary<string, List<string>> tags, int authorId, string authorName, int bookId, string bookTitle, int editionId)
        {
            return new FileMatch
            {
                File = new DiscoveredFileWithMetadata
                {
                    Path = path,
                    Size = new FileInfo(path).Length,
                    Modified = File.GetLastWriteTimeUtc(path),
                    AllTags = tags
                },
                AuthorId = authorId,
                AuthorName = authorName,
                BookId = bookId,
                BookTitle = bookTitle,
                EditionId = editionId
            };
        }

        private static UnmatchedFile CreateUnmatchedFile(string path, Dictionary<string, List<string>> tags, string reason)
        {
            return new UnmatchedFile
            {
                File = new DiscoveredFileWithMetadata
                {
                    Path = path,
                    Size = new FileInfo(path).Length,
                    Modified = File.GetLastWriteTimeUtc(path),
                    AllTags = tags
                },
                Reason = reason,
                PotentialAuthors = Array.Empty<AuthorSuggestion>()
            };
        }

        private static Dictionary<string, List<string>> CreateAudioTags(string album, string title, string author, string narrator)
        {
            return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["ALBUM"] = new List<string> { album },
                ["ALBUMARTIST"] = new List<string> { author },
                ["ARTIST"] = new List<string> { author },
                ["COMMENT"] = new List<string> { $"Narrator: {narrator}" },
                ["TITLE"] = new List<string> { title }
            };
        }
    }
}
