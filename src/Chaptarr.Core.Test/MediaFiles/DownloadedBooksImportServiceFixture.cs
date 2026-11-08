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

        private static IEnumerable<TestCaseData> SharedGrabTransferCases()
        {
            foreach (var postgres in new[] { false, true })
            foreach (var media in new[] { BookMediaType.Audiobook, BookMediaType.Ebook })
            foreach (var alternative in new[] { false, true })
            foreach (var mode in new[] { ImportMode.Copy, ImportMode.Move })
            foreach (var extras in new[] { "disabled", "default", "custom", "disabled-custom", "disabled-missing-companion" })
            {
                if (extras == "disabled-missing-companion" && mode == ImportMode.Move) continue;
                yield return new TestCaseData(postgres, media, alternative, mode, extras);
            }
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void profile_disallowed_ebook_alternative_should_not_block_durable_eligible_import(bool postgres, bool alternative)
        {
            captured_grab_should_import_only_its_book_files_and_honor_existing_extra_file_settings(postgres, BookMediaType.Ebook, alternative, ImportMode.Copy, "disabled-quality");
        }

        [TestCaseSource(nameof(SharedGrabTransferCases))]
        [TestCase(false, BookMediaType.Audiobook, false, ImportMode.Copy, "disabled-unscoped")]
        [TestCase(true, BookMediaType.Audiobook, false, ImportMode.Copy, "disabled-unscoped")]
        public void captured_grab_should_import_only_its_book_files_and_honor_existing_extra_file_settings(
            bool postgres, BookMediaType media, bool alternative, ImportMode mode, string extras)
        {
            Chaptarr.Core.Test.Common.DualDatabaseFixture.Run(postgres, database =>
            {
                var temp = Path.Combine(Path.GetTempPath(), "chaptarr-grab-extras-" + Guid.NewGuid().ToString("N"));
                var source = Path.Combine(temp, "download");
                var destination = Path.Combine(temp, "library");
                Directory.CreateDirectory(source); Directory.CreateDirectory(destination);
                try
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var noop = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IEventAggregator>((method, args) => null);
                    var author = new AuthorRepository(database, noop, logger).Insert(new Author { Name = "Test Writer", CleanName = "testwriter",
                        SortName = "testwriter", TitleSlug = "test-writer", Path = destination, Added = DateTime.UtcNow });
                    var profile = new NzbDrone.Core.Profiles.Qualities.QualityProfile { Id = 1, Name = "Native formats", UpgradeAllowed = true,
                        ProfileType = media == BookMediaType.Audiobook ? NzbDrone.Core.Profiles.Qualities.ProfileType.Audiobook : NzbDrone.Core.Profiles.Qualities.ProfileType.Ebook,
                        Items = (media == BookMediaType.Audiobook ? new[] { NzbDrone.Core.Qualities.Quality.M4B } :
                            new[] { NzbDrone.Core.Qualities.Quality.PDF, NzbDrone.Core.Qualities.Quality.MOBI, NzbDrone.Core.Qualities.Quality.EPUB })
                            .Select(quality => new NzbDrone.Core.Profiles.Qualities.QualityProfileQualityItem { Allowed = true, Quality = quality }).ToList() };
                    if (extras == "disabled-quality") profile.Items.Single(item => item.Quality == NzbDrone.Core.Qualities.Quality.MOBI).Allowed = false;
                    if (media == BookMediaType.Audiobook) { author.AudiobookQualityProfileId = 1; author.AudiobookQualityProfile = profile; }
                    else { author.EbookQualityProfileId = 1; author.EbookQualityProfile = profile; }
                    var books = new BookRepository(database, noop);
                    var editions = new EditionRepository(database, noop);
                    Book Seed(string slug)
                    {
                        var book = books.Insert(new Book { AuthorId = author.Id, Author = author, Title = "Paper Lanterns", CleanTitle = "paperlanterns",
                            TitleSlug = slug, MediaType = media, Added = DateTime.UtcNow, AnyEditionOk = false });
                        var edition = editions.Insert(new Edition { BookId = book.Id, Book = book, Title = book.Title,
                            ForeignEditionId = "local:" + slug, ReadingFormatId = media == BookMediaType.Audiobook ? 2 : 3, Monitored = true,
                            Narrator = "First Reader", NarratorNames = new() { "First Reader" } });
                        book.Editions = new() { edition }; return book;
                    }
                    var chosen = Seed("chosen"); var other = Seed("other");
                    var ownPaths = media == BookMediaType.Audiobook
                        ? Enumerable.Range(1, 3).Select(index => Path.Combine(source, "Book" + index + ".m4b")).ToList()
                        : new[] { ".epub", ".mobi", ".pdf" }.Select(extension => Path.Combine(source, "Book" + extension)).ToList();
                    var companion = Path.Combine(source, media == BookMediaType.Audiobook ? "Book1.epub" : "Book.m4b");
                    var cover = Path.Combine(source, "cover.jpg");
                    var matchingJpeg = Path.Combine(source, media == BookMediaType.Audiobook ? "Book1.jpg" : "Book.jpg");
                    var junk = ownPaths.Select(path => Path.Combine(source, "._" + Path.GetFileName(path))).ToList();
                    foreach (var path in ownPaths.Concat(junk).Append(companion).Append(cover).Append(matchingJpeg)
                        .Where(path => extras != "disabled-missing-companion" || path != companion)) File.WriteAllText(path, Path.GetFileName(path));
                    var fileSystem = new System.IO.Abstractions.FileSystem();
                    var transfers = new List<(string Path, string Target, bool Copy, bool Extra)>();
                    var disk = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IDiskProvider>((method, args) => method.Name switch
                    {
                        nameof(IDiskProvider.FolderExists) => Directory.Exists((string)args[0]),
                        nameof(IDiskProvider.FileExists) or nameof(IDiskProvider.FileExistsCanonical) => File.Exists((string)args[0]),
                        nameof(IDiskProvider.GetFileInfo) => fileSystem.FileInfo.FromFileName((string)args[0]),
                        nameof(IDiskProvider.GetDirectoryInfo) => fileSystem.DirectoryInfo.FromDirectoryName((string)args[0]),
                        nameof(IDiskProvider.GetFileSize) => new FileInfo((string)args[0]).Length,
                        nameof(IDiskProvider.FileGetLastWrite) => File.GetLastWriteTimeUtc((string)args[0]),
                        nameof(IDiskProvider.GetParentFolder) => Path.GetDirectoryName((string)args[0]),
                        nameof(IDiskProvider.GetFiles) => Directory.GetFiles((string)args[0], "*", (bool)args[1] ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly),
                        nameof(IDiskProvider.GetFileInfos) => Directory.GetFiles((string)args[0]).Select(path => (IFileInfo)fileSystem.FileInfo.FromFileName(path)).ToList(),
                        nameof(IDiskProvider.DeleteFolder) => DeleteFolder((string)args[0]),
                        _ => null
                    });
                    object DeleteFolder(string path) { Directory.Delete(path, true); return null; }
                    var scan = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IDiskScanService>((method, args) =>
                        Directory.GetFiles((string)args[0], "*", SearchOption.AllDirectories).Where(path => MediaFileExtensions.AllExtensions.Contains(Path.GetExtension(path)))
                            .Select(path => (IFileInfo)fileSystem.FileInfo.FromFileName(path)).ToArray());
                    var extraExtensions = extras.Contains("custom") ? (media == BookMediaType.Audiobook ? "epub" : "m4b") : "jpg,png,cue,m3u,opf";
                    var config = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IConfigService>((method, args) => method.Name switch
                    {
                        "get_ImportExtraFiles" => !extras.StartsWith("disabled", StringComparison.Ordinal),
                        "get_ExtraFileExtensions" => extraExtensions,
                        "get_CopyUsingHardlinks" => false,
                        "get_DownloadPropersAndRepacks" => NzbDrone.Core.Qualities.ProperDownloadTypes.PreferAndUpgrade,
                        "get_BookMatchingStrictness" => BookMatchingStrictness.Balanced,
                        _ => method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null
                    });
                    var bookService = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IBookService>((method, args) => method.Name switch
                    {
                        nameof(IBookService.GetBook) => (int)args[0] == chosen.Id ? chosen : other,
                        nameof(IBookService.GetExistingBooks) => new List<Book> { chosen, other }.Where(book => ((IEnumerable<int>)args[0]).Contains(book.Id)).ToList(),
                        _ => null
                    });
                    var authorService = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IAuthorService>((method, args) => author);
                    var editionService = new EditionService(editions, noop, logger);
                    var files = new MediaFileRepository(database, noop);
                    var mediaFiles = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IMediaFileService>((method, args) =>
                    {
                        if (method.Name == nameof(IMediaFileService.ReplaceMany)) { files.ReplaceMany((List<BookFile>)args[0], (List<BookFile>)args[1]); return null; }
                        if (method.Name == nameof(IMediaFileService.GetFilesByBook)) return files.GetFilesByBook((int)args[0]);
                        if (method.Name == nameof(IMediaFileService.GetFilesByEdition)) return files.GetFilesByEdition((int)args[0]);
                        if (method.Name == nameof(IMediaFileService.GetFileWithPath)) return files.GetFileWithPath((string)args[0]);
                        throw new InvalidOperationException("Unexpected media operation " + method.Name);
                    });
                    var history = new NzbDrone.Core.History.HistoryService(new NzbDrone.Core.History.HistoryRepository(database, noop), logger);
                    var downloadHistory = new NzbDrone.Core.Download.History.DownloadHistoryService(
                        new NzbDrone.Core.Download.History.DownloadHistoryRepository(database, noop), history);
                    var events = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IEventAggregator>((method, args) =>
                    {
                        if (args[0] is NzbDrone.Core.MediaFiles.Events.TrackImportedEvent imported) history.Handle(imported);
                        if (args[0] is NzbDrone.Core.Download.DownloadCompletedEvent completed) downloadHistory.Handle(completed);
                        return null;
                    });
                    var extraRows = new NzbDrone.Core.Extras.Others.OtherExtraFileRepository(database, noop);
                    var extraWriter = new NzbDrone.Core.Extras.Others.OtherExtraFileService(extraRows, authorService, disk, null, logger);
                    var transfer = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IDiskTransferService>((method, args) =>
                    {
                        var copy = (TransferMode)args[2] != TransferMode.Move;
                        transfers.Add(((string)args[0], (string)args[1], copy, true));
                        if (copy) File.Copy((string)args[0], (string)args[1], true); else File.Move((string)args[0], (string)args[1], true);
                        return args[2];
                    });
                    var extraService = new NzbDrone.Core.Extras.ExtraService(mediaFiles, editionService, disk, config,
                        new[] { new NzbDrone.Core.Extras.Others.OtherExtraService(config, disk, transfer, extraWriter,
                            Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IMediaFileAttributeService>((method, args) => null), null, logger) }, logger);
                    var mover = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IMoveBookFiles>((method, args) =>
                    {
                        var file = (BookFile)args[0]; var local = (LocalBook)args[1];
                        var target = Path.Combine(destination, Path.GetFileName(local.Path));
                        if (method.Name == nameof(IMoveBookFiles.GetImportDestinationPath)) return target;
                        Assert.That(method.Name, Is.AnyOf(nameof(IMoveBookFiles.CopyBookFile), nameof(IMoveBookFiles.MoveBookFile)));
                        var copy = method.Name == nameof(IMoveBookFiles.CopyBookFile);
                        file.Path = target;
                        transfers.Add((local.Path, file.Path, copy, false));
                        if (copy) File.Copy(local.Path, file.Path); else File.Move(local.Path, file.Path);
                        return file;
                    });
                    var dependencies = new Dictionary<Type, object> { [typeof(IMediaFileService)] = mediaFiles, [typeof(IMetadataTagService)] = new StubMetadataTagService(),
                        [typeof(IMediaInfoExtractor)] = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IMediaInfoExtractor>((method, args) =>
                            method.Name == nameof(IMediaInfoExtractor.GetDuration) ? TimeSpan.Zero : null),
                        [typeof(IBookService)] = bookService, [typeof(IAuthorService)] = authorService, [typeof(IEditionService)] = editionService,
                        [typeof(NzbDrone.Core.Extras.IExtraService)] = extraService, [typeof(IMoveBookFiles)] = mover,
                        [typeof(NzbDrone.Core.History.IHistoryService)] = history, [typeof(IEventAggregator)] = events, [typeof(Logger)] = logger,
                        [typeof(IDiskProvider)] = disk, [typeof(IConfigService)] = config };
                    var constructor = typeof(ImportApprovedBooks).GetConstructors().Single();
                    var writer = (ImportApprovedBooks)constructor.Invoke(constructor.GetParameters().Select(parameter => dependencies.GetValueOrDefault(parameter.ParameterType)).ToArray());
                    var submitted = new List<string>();
                    var matching = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IFileMatchingService>((method, args) =>
                    {
                        var input = (DiscoveredFileWithMetadata[])args[0]; submitted.AddRange(input.Select(file => file.Path));
                        return Task.FromResult(new FileMatchResult
                        {
                            MatchedFiles = input.Where(file => ownPaths.Contains(file.Path)).Select(file => new FileMatch { File = file, BookId = chosen.Id,
                                EditionId = chosen.Editions[0].Id, AuthorId = author.Id, AuthorName = author.Name, BookTitle = chosen.Title }).ToArray(),
                            UnmatchedFiles = input.Where(file => !ownPaths.Contains(file.Path)).Select(file => new UnmatchedFile { File = file, Reason = "Companion" }).ToArray()
                        });
                    });
                    var captured = new Dictionary<int, int> { [chosen.Id] = chosen.Editions[0].Id, [other.Id] = other.Editions[0].Id };
                    var grabbed = DateTime.UtcNow.AddMinutes(-1);
                    var client = new DownloadClientItem { DownloadId = "grab", Title = "Captured grab", CanMoveFiles = mode == ImportMode.Move,
                        Status = DownloadItemStatus.Completed, DownloadClientInfo = new DownloadClientItemClientInfo { Name = "Native test client", Type = "Test" },
                        FilePaths = ownPaths.Concat(junk).Append(Path.Combine(source, "._Missing" + Path.GetExtension(ownPaths[0])))
                            .Append(companion).Append(cover).Append(matchingJpeg).ToList(), FileListConfidence = DownloadClientFileListConfidence.Authoritative };
                    var remote = new RemoteBook { Author = author, Books = alternative ? new() { chosen, other } : new() { chosen },
                        AlternativeCopies = alternative, AlternativeCopyGrabDate = grabbed, TargetEditionIds = captured };
                    if (extras == "disabled-unscoped") client.FilePaths = null;
                    var importer = new DownloadedBooksImportService(disk, scan, matching, new StubMetadataTagService(), writer, bookService, authorService,
                        editionService, null, null, null, config, history, events, null, null, logger, queueService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.EmptyQueue(), conversionJobService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoConversionJobs());
                    var results = importer.ProcessPath(source, mode, author, client, remote);
                    TestContext.WriteLine(string.Join("; ", results.SelectMany(result => result.Errors)));
                    var expectedCount = media == BookMediaType.Audiobook ? 3 : 1;
                    Assert.Multiple(() =>
                    {
                        Assert.That(submitted, Is.EquivalentTo(ownPaths));
                        Assert.That(results, Has.Count.EqualTo(ownPaths.Count));
                        Assert.That(results.Count(result => result.Result == ImportResultType.Imported), Is.EqualTo(expectedCount),
                            string.Join("; ", results.SelectMany(result => result.Errors)));
                        Assert.That(files.All(), Has.Count.EqualTo(expectedCount));
                        Assert.That(files.All().All(file => file.EditionId == chosen.Editions[0].Id), Is.True);
                        Assert.That(files.GetFilesByBook(other.Id), Is.Empty);
                        Assert.That(transfers.Count(item => !item.Extra), Is.EqualTo(expectedCount));
                        Assert.That(transfers.Where(item => !item.Extra).All(item => item.Copy == (mode == ImportMode.Copy)), Is.True);
                        Assert.That(transfers.Any(item => item.Path == cover), Is.False);
                    });
                    var savedHistory = history.FindByDownloadId(client.DownloadId);
                    Assert.That(savedHistory, Has.Count.EqualTo(expectedCount));
                    Assert.That(savedHistory.All(row => row.BookId == chosen.Id && row.EditionId == chosen.Editions[0].Id), Is.True);
                    Assert.That(transfers.Count(item => item.Path == companion), Is.Zero);
                    Assert.That(transfers.Count(item => item.Path == matchingJpeg), Is.EqualTo(extras == "default" ? 1 : 0));
                    Assert.That(extraRows.All(), Has.Count.EqualTo(extras == "default" ? 1 : 0));
                    Assert.That(File.Exists(companion), Is.EqualTo(extras != "disabled-missing-companion"));
                    Assert.That(File.Exists(cover), Is.True);
                    Assert.That(Directory.Exists(source), Is.True);
                    Assert.That(File.Exists(Path.Combine(destination, Path.GetFileName(companion))), Is.False);
                    var tracked = new NzbDrone.Core.Download.TrackedDownloads.TrackedDownload { DownloadItem = client, RemoteBook = remote };
                    if (extras == "disabled-quality")
                    {
                        var immediate = NzbDrone.Core.Download.TrackedDownloads.TrackedDownloadAlreadyImported.Evaluate(tracked, results).Complete;
                        var strictReplay = new NzbDrone.Core.Download.TrackedDownloads.TrackedDownloadAlreadyImported(logger).IsImported(tracked, savedHistory);
                        var snapshotService = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<NzbDrone.Core.Download.IDownloadClientFileSnapshotService>((method, args) => null);
                        var completion = new NzbDrone.Core.Download.CompletedDownloadService(events, history, null, null, null,
                            new NzbDrone.Core.Download.TrackedDownloads.TrackedDownloadAlreadyImported(logger), logger, snapshotService);
                        var verified = completion.VerifyImport(tracked, results);
                        var latest = downloadHistory.GetLatestDownloadHistoryItem(client.DownloadId);
                        var tracker = new NzbDrone.Core.Download.TrackedDownloads.TrackedDownloadService(
                            Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<NzbDrone.Core.Parser.IParsingService>((method, args) => remote),
                            new NzbDrone.Common.Cache.CacheManager(), history, noop, downloadHistory,
                            Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<NzbDrone.Core.CustomFormats.ICustomFormatCalculationService>((method, args) =>
                                new List<NzbDrone.Core.CustomFormats.CustomFormat>()), logger, snapshotService);
                        var restoredDownload = tracker.TrackDownload(new NzbDrone.Core.Download.DownloadClientDefinition { Id = 1 }, client);
                        TestContext.Progress.WriteLine($"QUALITY_COMPLETION database={database.DatabaseType} alternative={alternative} imported={results.Count(result => result.Result == ImportResultType.Imported)} rejects={string.Join(';', results.Where(result => result.Result != ImportResultType.Imported).Select(result => System.IO.Path.GetExtension(result.ImportDecision.Item.Path) + ':' + string.Join(',', result.ImportDecision.Rejections.Select(rejection => rejection.Category))))} mobiCopied={transfers.Any(transfer => transfer.Path.EndsWith(".mobi", StringComparison.OrdinalIgnoreCase))} historyRows={savedHistory.Count} importedBookIds={string.Join(',', results.Where(result => result.Result == ImportResultType.Imported).Select(result => result.ImportDecision.Item.Book.Id).Distinct())} immediate={immediate} verified={verified} strictReplay={strictReplay} aggregate={latest?.EventType.ToString() ?? "none"} restored={restoredDownload?.State}");
                        Assert.That(results.Count(result => result.ImportDecision.Rejections.Any(rejection => rejection.IsQualityFilter)), Is.EqualTo(1));
                        Assert.That(transfers.Any(transfer => transfer.Path.EndsWith(".mobi", StringComparison.OrdinalIgnoreCase)), Is.False);
                        Assert.That(immediate, Is.True);
                        Assert.That(verified, Is.True);
                        Assert.That(strictReplay, Is.True);
                        Assert.That(latest.EventType, Is.EqualTo(NzbDrone.Core.Download.History.DownloadHistoryEventType.DownloadImported));
                        Assert.That(restoredDownload.State, Is.EqualTo(NzbDrone.Core.Download.TrackedDownloads.TrackedDownloadState.Imported));
                        return;
                    }
                    Assert.That(NzbDrone.Core.Download.TrackedDownloads.TrackedDownloadAlreadyImported.Evaluate(tracked, results).Complete, Is.True);
                    if (extras == "disabled-unscoped")
                    {
                        Assert.That(new NzbDrone.Core.Download.TrackedDownloads.TrackedDownloadAlreadyImported(logger).IsImported(tracked, savedHistory),
                            Is.False, "Without a saved source inventory, history alone cannot prove that every source was imported");
                        return;
                    }
                    Assert.That(new NzbDrone.Core.Download.TrackedDownloads.TrackedDownloadAlreadyImported(logger).IsImported(tracked, savedHistory),
                        Is.True);
                    var snapshots = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<NzbDrone.Core.Download.IDownloadClientFileSnapshotService>((method, args) => null);
                    var completedService = new NzbDrone.Core.Download.CompletedDownloadService(events, history, null, null, null,
                        new NzbDrone.Core.Download.TrackedDownloads.TrackedDownloadAlreadyImported(logger), logger, snapshots);
                    Assert.That(completedService.VerifyImport(tracked, results), Is.True);
                    Assert.That(downloadHistory.GetLatestDownloadHistoryItem(client.DownloadId).EventType,
                        Is.EqualTo(NzbDrone.Core.Download.History.DownloadHistoryEventType.DownloadImported));
                    var freshTracker = new NzbDrone.Core.Download.TrackedDownloads.TrackedDownloadService(
                        Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<NzbDrone.Core.Parser.IParsingService>((method, args) => remote),
                        new NzbDrone.Common.Cache.CacheManager(), history, noop, downloadHistory,
                        Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<NzbDrone.Core.CustomFormats.ICustomFormatCalculationService>((method, args) =>
                            new List<NzbDrone.Core.CustomFormats.CustomFormat>()), logger, snapshots);
                    var restored = freshTracker.TrackDownload(new NzbDrone.Core.Download.DownloadClientDefinition { Id = 1 }, client);
                    Assert.That(restored.State, Is.EqualTo(NzbDrone.Core.Download.TrackedDownloads.TrackedDownloadState.Imported));
                    TestContext.Progress.WriteLine($"GRAB_EXTRAS db={database.DatabaseType} media={media} alternative={alternative} mode={mode} extras={extras} nativeFiles={files.All().Count()} history={savedHistory.Count} extraRows={extraRows.All().Count()} companionTransfers={transfers.Count(item => item.Path == companion)}");
                }
                finally { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
            });
        }

        [TestCase(".mkv", false, false)]
        // Confirmed wrong output stops for manual import; incomplete folders keep waiting.
        [TestCase(".mkv", true, true)]
        [TestCase(".nfo", true, true)]
        [TestCase(".nfo", false, false)]
        [TestCase(".epub", false, true)]
        [TestCase(".epub", true, true)]
        [TestCase(".m4b.partial~", true, false)]
        [TestCase(".exe", false, true)]
        [TestCase(".EXE", true, true)]
        [TestCase(".ps1", false, true)]
        [TestCase(".ps1", true, true)]
        [TestCase(".zip", false, true)]
        [TestCase(".zip", true, true)]
        [TestCase(".rar", false, true)]
        [TestCase(".rar", true, true)]
        public void should_classify_downloads_without_target_media(string extension, bool singleFile, bool requiresAction)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "chaptarr-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var filePath = Path.Combine(tempDir, "The Five People You Meet in Heaven (2004)" + extension);
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
                    LogManager.GetCurrentClassLogger(), queueService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.EmptyQueue(), conversionJobService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoConversionJobs());

                var results = service.ProcessPath(singleFile ? filePath : tempDir, ImportMode.Auto,
                    remoteBook: new RemoteBook { Books = new List<Book> { new Book { MediaType = BookMediaType.Audiobook } } });

                Assert.That(results, Has.Count.EqualTo(1));
                Assert.That(results[0].ImportDecision.Rejections.Single().Type, Is.EqualTo(requiresAction ? RejectionType.Permanent : RejectionType.Temporary));
                Assert.That(File.Exists(filePath), Is.True, "A refusal must leave the download for the user.");
                if (requiresAction || extension == ".mkv")
                {
                    Assert.That(results[0].Errors.Single(), Does.Contain(Path.GetFileName(filePath)));
                }
                else
                {
                    Assert.That(results[0].Errors.Single(), Does.Contain("No supported audiobook files"));
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Test]
        public void should_keep_waiting_when_supported_media_is_not_available()
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
                    Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IEventAggregator>((method, args) => null),
                    Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo>((method, args) => false),
                    DispatchProxy.Create<IMediaInfoExtractor, ThrowingProxy<IMediaInfoExtractor>>(),
                    LogManager.GetCurrentClassLogger(), queueService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.EmptyQueue(), conversionJobService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoConversionJobs());

                RejectionType Classify(DownloadClientItem downloadClientItem, string path = null)
                {
                    return service.ProcessPath(path ?? tempDir, ImportMode.Auto, author: null, downloadClientItem: downloadClientItem)
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
                    FilePaths = new List<string> { existingArchive, missingNfo, Path.Combine(tempDir, "._Missing.m4b") },
                    FileListConfidence = DownloadClientFileListConfidence.Authoritative
                };
                var partiallyVisibleAuthoritativeMediaList = new DownloadClientItem
                {
                    FilePaths = new List<string> { existingMedia, missingMedia },
                    FileListConfidence = DownloadClientFileListConfidence.Authoritative
                };

                for (var pass = 0; pass < 5; pass++)
                {
                    Assert.That(Classify(new DownloadClientItem(), Path.Combine(tempDir, "Still arriving")),
                        Is.EqualTo(RejectionType.Temporary), $"Missing path must still wait on pass {pass + 1}");
                }

                Assert.That(Classify(authoritativeMediaList), Is.EqualTo(RejectionType.Temporary));
                Assert.That(Classify(degradedMediaList), Is.EqualTo(RejectionType.Temporary));
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
                    LogManager.GetCurrentClassLogger(), queueService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.EmptyQueue(), conversionJobService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoConversionJobs());

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
                    LogManager.GetCurrentClassLogger(), queueService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.EmptyQueue(), conversionJobService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoConversionJobs());

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
                    LogManager.GetCurrentClassLogger(), queueService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.EmptyQueue(), conversionJobService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoConversionJobs());

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
                    LogManager.GetCurrentClassLogger(), queueService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.EmptyQueue(), conversionJobService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoConversionJobs());

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
                    LogManager.GetCurrentClassLogger(), queueService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.EmptyQueue(), conversionJobService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoConversionJobs());

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
                    LogManager.GetCurrentClassLogger(), queueService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.EmptyQueue(), conversionJobService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoConversionJobs());

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
                    LogManager.GetCurrentClassLogger(), queueService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.EmptyQueue(), conversionJobService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoConversionJobs());

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
                    LogManager.GetCurrentClassLogger(), queueService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.EmptyQueue(), conversionJobService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoConversionJobs());

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
                    LogManager.GetCurrentClassLogger(), queueService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.EmptyQueue(), conversionJobService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoConversionJobs());

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
                    LogManager.GetCurrentClassLogger(), queueService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.EmptyQueue(), conversionJobService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoConversionJobs());

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
                    LogManager.GetCurrentClassLogger(), queueService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.EmptyQueue(), conversionJobService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoConversionJobs());

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
                    LogManager.GetCurrentClassLogger(), queueService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.EmptyQueue(), conversionJobService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoConversionJobs());

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
                    LogManager.GetCurrentClassLogger(), queueService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.EmptyQueue(), conversionJobService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoConversionJobs());

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
                    LogManager.GetCurrentClassLogger(), queueService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.EmptyQueue(), conversionJobService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoConversionJobs());

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
                    LogManager.GetCurrentClassLogger(), queueService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.EmptyQueue(), conversionJobService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoConversionJobs());

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
                    LogManager.GetCurrentClassLogger(), queueService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.EmptyQueue(), conversionJobService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoConversionJobs());

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
                    LogManager.GetCurrentClassLogger(), queueService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.EmptyQueue(), conversionJobService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoConversionJobs());

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
                    LogManager.GetCurrentClassLogger(), queueService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.EmptyQueue(), conversionJobService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoConversionJobs());

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
                    LogManager.GetCurrentClassLogger(), queueService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.EmptyQueue(), conversionJobService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoConversionJobs());

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
                    LogManager.GetCurrentClassLogger(), queueService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.EmptyQueue(), conversionJobService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoConversionJobs());

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
                    LogManager.GetCurrentClassLogger(), queueService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.EmptyQueue(), conversionJobService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoConversionJobs());

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
