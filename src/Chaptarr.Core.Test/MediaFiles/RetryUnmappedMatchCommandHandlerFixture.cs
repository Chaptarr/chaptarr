using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Authors;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Services;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class RetryUnmappedMatchCommandHandlerFixture
    {
        private sealed class StubMediaFileService : IMediaFileService
        {
            public List<BookFile> UnmappedFiles { get; set; } = new List<BookFile>();
            public List<BookFile> Updated { get; } = new List<BookFile>();
            public List<string> Events { get; set; }
            public int SingleUpdateCalls { get; private set; }
            public int BatchUpdateCalls { get; private set; }

            public BookFile Add(BookFile bookFile) => throw new NotImplementedException();
            public void AddMany(List<BookFile> bookFiles) => throw new NotImplementedException();
            public void Update(BookFile bookFile)
            {
                SingleUpdateCalls++;
                Events?.Add($"update:{bookFile?.Path}");
                Updated.Add(bookFile);
            }

            public void Update(List<BookFile> bookFiles)
            {
                BatchUpdateCalls++;
                Events?.Add($"update-batch:{bookFiles?.Count ?? 0}");
                Updated.AddRange(bookFiles ?? new List<BookFile>());
            }

            public void Delete(BookFile bookFile, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public void DeleteMany(List<BookFile> bookFiles, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public List<IFileInfo> FilterUnchangedFiles(List<IFileInfo> files, FilterFilesType filter) => files;
            public List<BookFile> GetFilesByAuthor(int authorId) => throw new NotImplementedException();
            public List<BookFile> GetFilesByBook(int bookId) => throw new NotImplementedException();
            public List<BookFile> GetFilesByBooks(List<int> bookIds) => throw new NotImplementedException();
            public List<BookFile> GetFilesByEdition(int editionId) => throw new NotImplementedException();
            public List<BookFile> GetUnmappedFiles() => UnmappedFiles.Where(file => file.EditionId == 0).ToList();
            public List<BookFile> GetUnmappedFiles(string mediaType) => GetUnmappedFiles()
                .Where(file => string.IsNullOrWhiteSpace(mediaType) || string.Equals(file.MediaType, mediaType, StringComparison.OrdinalIgnoreCase))
                .ToList();
            public List<BookFile> GetUnmappedFiles(IEnumerable<int> ids, string mediaType)
            {
                var requested = ids?.ToHashSet() ?? new HashSet<int>();
                return GetUnmappedFiles(mediaType).Where(file => requested.Contains(file.Id)).ToList();
            }
            public BookFile Get(int id) => throw new NotImplementedException();
            public List<BookFile> Get(IEnumerable<int> ids) => throw new NotImplementedException();
            public List<BookFile> GetFilesWithBasePath(string path) => throw new NotImplementedException();
            public List<BookFile> GetFilesWithBasePath(string path, string mediaType) => throw new NotImplementedException();
            public BookFile GetFileWithPath(string path) => UnmappedFiles.FirstOrDefault(file => string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase));
            public List<BookFile> GetFileWithPath(List<string> path) => throw new NotImplementedException();
            public void UpdateMediaInfo(List<BookFile> bookFiles) => throw new NotImplementedException();
            public List<BookFile> GetFilesByAuthorAndMediaType(int authorId, string mediaType) => throw new NotImplementedException();
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

        private sealed class RecordingMatchingService : IFileMatchingService
        {
            public List<(DiscoveredFileWithMetadata[] Files, int? RestrictToAuthorId, MatchingContext Context)> Calls { get; } =
                new List<(DiscoveredFileWithMetadata[], int?, MatchingContext)>();
            public Func<DiscoveredFileWithMetadata[], int?, MatchingContext, int, FileMatchResult> OnMatch { get; set; }
            public List<string> Events { get; set; }

            public Task<FileMatchResult> MatchFilesToLibraryAsync(DiscoveredFileWithMetadata[] filesWithMetadata)
            {
                return MatchFilesToLibraryAsync(filesWithMetadata, null, new MatchingContext());
            }

            public Task<FileMatchResult> MatchFilesToLibraryAsync(DiscoveredFileWithMetadata[] filesWithMetadata, int? restrictToAuthorId)
            {
                return MatchFilesToLibraryAsync(filesWithMetadata, restrictToAuthorId, new MatchingContext());
            }

            public Task<FileMatchResult> MatchFilesToLibraryAsync(DiscoveredFileWithMetadata[] filesWithMetadata, int? restrictToAuthorId, bool forDownloads)
            {
                return MatchFilesToLibraryAsync(filesWithMetadata, restrictToAuthorId, new MatchingContext { PerFileMatching = forDownloads });
            }

            public Task<FileMatchResult> MatchFilesToLibraryAsync(DiscoveredFileWithMetadata[] filesWithMetadata, int? restrictToAuthorId, MatchingContext context)
            {
                Events?.Add($"match:{filesWithMetadata?.Length ?? 0}");
                Calls.Add((filesWithMetadata, restrictToAuthorId, context));
                return Task.FromResult(OnMatch?.Invoke(filesWithMetadata, restrictToAuthorId, context, Calls.Count) ?? new FileMatchResult());
            }

            public EditionFtsMatch HolyGrailMatch(int? authorId, IEnumerable<string> allTagTokens, BookMediaType mediaType) => throw new NotImplementedException();
            public FileMatch HolyGrailMatchFile(DiscoveredFileWithMetadata file, BookMediaType mediaType, int? restrictToAuthorId = null) => throw new NotImplementedException();
        }

        private sealed class RecordingBookImportService : IBookImportService
        {
            public List<(string Path, int BookId, int? EditionId, Dictionary<string, List<string>> Tags, int? DurationSeconds, long Size, DateTime Modified)> Imports { get; } =
                new List<(string, int, int?, Dictionary<string, List<string>>, int?, long, DateTime)>();
            public Func<string, int, int?, BookImportFileResult> ResultFactory { get; set; }

            public Task ImportFileAsync(string path, int bookId, string quality) => throw new NotImplementedException();
            public Task ImportFileAsync(string path, int bookId, string quality, Dictionary<string, List<string>> tags) => throw new NotImplementedException();
            public Task ImportFileAsync(string path, int bookId, int? editionId, string quality, Dictionary<string, List<string>> tags) => throw new NotImplementedException();
            public Task<BookImportFileResult> ImportExistingFileAsync(string path, int bookId, int? editionId, string quality, Dictionary<string, List<string>> tags)
            {
                return ImportExistingFileAsync(path, bookId, editionId, quality, tags, null);
            }

            public Task<BookImportFileResult> ImportExistingFileAsync(string path, int bookId, int? editionId, string quality, Dictionary<string, List<string>> tags, int? durationSeconds)
            {
                return ImportExistingFileAsync(path, bookId, editionId, quality, tags, durationSeconds, null);
            }

            public Task<BookImportFileResult> ImportExistingFileAsync(string path, int bookId, int? editionId, string quality, Dictionary<string, List<string>> tags, int? durationSeconds, MatchProvenance provenance)
            {
                Imports.Add((path, bookId, editionId, tags, durationSeconds, 0, default));
                return Task.FromResult(ResultFactory?.Invoke(path, bookId, editionId) ?? BookImportFileResult.Imported(path, 1000 + Imports.Count));
            }

            public Task<BookImportFileResult> ImportExistingFileAsync(DiscoveredFileWithMetadata file, int bookId, int? editionId, string quality, MatchProvenance provenance)
            {
                Imports.Add((file?.Path, bookId, editionId, file?.AllTags, file?.DurationSeconds, file?.Size ?? 0, file?.Modified ?? default));
                return Task.FromResult(ResultFactory?.Invoke(file?.Path, bookId, editionId) ?? BookImportFileResult.Imported(file?.Path, 1000 + Imports.Count));
            }

            public Task<IReadOnlyList<BookImportFileResult>> ImportFilesAsync(List<(string Path, int? EditionId, Dictionary<string, List<string>> Tags, int? DurationSeconds)> files, int bookId) => throw new NotImplementedException();
            public Task<IReadOnlyList<BookImportFileResult>> ImportFilesAsync(List<(string Path, int? EditionId, Dictionary<string, List<string>> Tags, int? DurationSeconds, MatchProvenance Provenance)> files, int bookId) => throw new NotImplementedException();
            public Task<IReadOnlyList<BookImportFileResult>> ImportFilesAsync(List<(DiscoveredFileWithMetadata File, int? EditionId, MatchProvenance Provenance)> files, int bookId) => throw new NotImplementedException();
            public Task<IReadOnlyList<BookImportFileResult>> ImportFilesAsync(List<(string Path, Dictionary<string, List<string>> Tags)> files, int bookId, int? editionId, string quality) => throw new NotImplementedException();
            public Task<IReadOnlyList<BookImportFileResult>> ImportFilesAsync(List<(string Path, Dictionary<string, List<string>> Tags, int? DurationSeconds)> files, int bookId, int? editionId, string quality) => throw new NotImplementedException();
        }

        private sealed class DestinationServiceStub : IBookUnitDestinationService
        {
            public string BuildRootUnitKeyWithExtension(string anyFilePathInUnit, string editionTitle, BookMediaType mediaType) => anyFilePathInUnit;
            public (int BookId, int EditionId) ResolveDestinationForUnit(Book canonicalBook, Edition canonicalEdition, string unitKey) => (canonicalBook.Id, canonicalEdition.Id);
        }

        private sealed class RecordingPendingAuthorImportService : IPendingAuthorImportService
        {
            public List<(string ProviderId, MonitoringConfig Config, string SourceApplication)> Enqueued { get; } =
                new List<(string, MonitoringConfig, string)>();

            public int NextId { get; set; } = 500;

            public Task<int> EnqueueAsync(string providerId, MonitoringConfig config, string sourceApplication)
            {
                Enqueued.Add((providerId, config, sourceApplication));
                return Task.FromResult(NextId++);
            }

            public List<PendingAuthorImport> GetAll() => throw new NotImplementedException();
            public List<PendingAuthorImport> GetDueForProcessing(int limit = 10) => throw new NotImplementedException();
            public PendingAuthorImport GetByProviderId(string providerId) => throw new NotImplementedException();
            public void UpdateStatus(PendingAuthorImport item, PendingImportStatus status, string error) => throw new NotImplementedException();
            public void ScheduleRetry(PendingAuthorImport item, string error) => throw new NotImplementedException();
            public void Cancel(int id) => throw new NotImplementedException();
            public void RetryNow(int id) => throw new NotImplementedException();
            public void CleanupOldCompleted() => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
        }

        private sealed class RecordingAuthorLibraryService : IAuthorLibraryService
        {
            public List<(string ProviderId, MonitoringConfig Config)> Adds { get; } =
                new List<(string, MonitoringConfig)>();
            public Func<string, MonitoringConfig, Task<Author>> OnAdd { get; set; }

            public Task<Author> AddAuthorAsync(string providerId, MonitoringConfig config = null)
            {
                Adds.Add((providerId, config));
                return OnAdd?.Invoke(providerId, config) ?? Task.FromResult<Author>(null);
            }

            public Task<Author> AddAuthorMonitoringBookAsync(string authorProviderId, string bookProviderId) =>
                throw new NotImplementedException();

            public Task<List<Author>> AddAuthorsMonitoringSeriesAsync(string[] authorProviderIds, string seriesProviderId) =>
                throw new NotImplementedException();

            public Task<Author> RefreshAuthorAsync(int authorId) => throw new NotImplementedException();
            public Task RemoveAuthorAsync(int authorId) => throw new NotImplementedException();

            public Task<UserSelectedEditionMaterialization> MaterializeUserSelectedEditionAsync(
                UserSelectedRemoteEdition selection,
                MonitoringConfig config) => throw new NotImplementedException();
        }

        private sealed class RootFolderServiceStub : IRootFolderService
        {
            public RootFolder RootFolder { get; set; } = CreateAudiobookRootFolder();

            public List<RootFolder> All() => RootFolder == null ? new List<RootFolder>() : new List<RootFolder> { RootFolder };
            public List<RootFolder> AllWithSpaceStats() => All();
            public RootFolder Add(RootFolder rootFolder) => throw new NotImplementedException();
            public RootFolder Update(RootFolder rootFolder) => throw new NotImplementedException();
            public void Remove(int id) => throw new NotImplementedException();
            public RootFolder Get(int id) => RootFolder;
            public List<RootFolder> AllForTag(int tagId) => All();
            public RootFolder GetBestRootFolder(string path) => RootFolder;
            public RootFolder GetBestRootFolder(string path, List<RootFolder> allRootFolders) => RootFolder;
            public string GetBestRootFolderPath(string path) => RootFolder?.Path;
            public string GetBestRootFolderPath(string path, List<RootFolder> allRootFolders) => RootFolder?.Path;
        }

        private class IngestQueueRepositoryProxy : DispatchProxy
        {
            public List<string> PurgedPaths { get; } = new List<string>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IIngestQueueRepository.PurgePaths))
                {
                    var paths = ((IEnumerable<string>)args[0]).ToList();
                    PurgedPaths.AddRange(paths);
                    return paths.Count;
                }

                return GetDefaultValue(targetMethod?.ReturnType);
            }
        }

        private class DiskProviderProxy : DispatchProxy
        {
            public Dictionary<string, (bool Exists, long Length, DateTime LastWriteUtc)> Files { get; } =
                new Dictionary<string, (bool, long, DateTime)>(StringComparer.OrdinalIgnoreCase);

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IDiskProvider.GetFileInfo))
                {
                    var path = (string)args[0];
                    Files.TryGetValue(path, out var info);
                    var fileInfo = DispatchProxy.Create<IFileInfo, FileInfoProxy>();
                    var proxy = (FileInfoProxy)(object)fileInfo;
                    proxy.FullName = path;
                    proxy.ExistsResult = info.Exists;
                    proxy.Length = info.Length;
                    proxy.LastWriteUtc = info.LastWriteUtc;
                    return fileInfo;
                }

                return GetDefaultValue(targetMethod?.ReturnType);
            }
        }

        private class FileInfoProxy : DispatchProxy
        {
            public string FullName { get; set; }
            public bool ExistsResult { get; set; }
            public long Length { get; set; }
            public DateTime LastWriteUtc { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    "get_Exists" => ExistsResult,
                    "get_Length" => Length,
                    "get_LastWriteTimeUtc" => LastWriteUtc,
                    "get_LastWriteTime" => LastWriteUtc.ToLocalTime(),
                    "get_FullName" => FullName,
                    "get_Name" => System.IO.Path.GetFileName(FullName),
                    "get_Extension" => System.IO.Path.GetExtension(FullName),
                    _ => GetDefaultValue(targetMethod?.ReturnType)
                };
            }
        }

        private class BookServiceProxy : DispatchProxy
        {
            public Dictionary<int, Book> Books { get; } = new Dictionary<int, Book>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IBookService.GetBook))
                {
                    return Books.TryGetValue((int)args[0], out var book) ? book : null;
                }

                if (targetMethod?.Name == nameof(IBookService.GetBooksByAuthor))
                {
                    var authorId = (int)args[0];
                    return Books.Values.Where(book => book.AuthorId == authorId).ToList();
                }

                if (targetMethod?.Name == nameof(IBookService.FindAllByWorkProviderId))
                {
                    var provider = args[0]?.ToString();
                    var rawProviderId = args[1]?.ToString();
                    var mediaType = (BookMediaType)args[2];
                    var providerId = rawProviderId?.Contains(':') == true
                        ? rawProviderId
                        : $"{provider}:{rawProviderId}";

                    return Books.Values
                        .Where(book => book.MediaType == mediaType &&
                                       BookEditionIdentity.HasCanonicalWorkProviderId(book, providerId))
                        .OrderBy(book => book.Id)
                        .ToList();
                }

                return GetDefaultValue(targetMethod?.ReturnType);
            }
        }

        private class EditionServiceProxy : DispatchProxy
        {
            public Dictionary<int, Edition> Editions { get; } = new Dictionary<int, Edition>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IEditionService.GetEdition))
                {
                    return Editions.TryGetValue((int)args[0], out var edition) ? edition : null;
                }

                return GetDefaultValue(targetMethod?.ReturnType);
            }
        }

        private class AuthorServiceProxy : DispatchProxy
        {
            public Dictionary<string, Author> AuthorsByProviderId { get; } =
                new Dictionary<string, Author>(StringComparer.OrdinalIgnoreCase);

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IAuthorService.FindByProviderId))
                {
                    var key = $"{args[0]}:{args[1]}";
                    return AuthorsByProviderId.TryGetValue(key, out var author) ? author : null;
                }

                return GetDefaultValue(targetMethod?.ReturnType);
            }
        }

        [Test]
        public void retry_match_uses_stored_evidence_without_refresh_for_fresh_file()
        {
            var modified = new DateTime(2026, 7, 4, 1, 0, 0, DateTimeKind.Utc);
            var file = CreateUnmappedFile(1, "/books/one.mp3", modified, 100, "Stored Title", 321);
            var context = CreateContext(file, modified, 100);

            context.Matching.OnMatch = (files, restrictToAuthorId, ctx, call) => new FileMatchResult
            {
                MatchedFiles = new[]
                {
                    new FileMatch { File = files[0], AuthorId = 5, BookId = 10, EditionId = 20 }
                }
            };

            context.Sut.Execute(new RetryUnmappedMatchCommand
            {
                MediaType = "audiobook",
                UnmappedFiles = new UnmappedFilesSelection { Scope = "all" }
            });

            Assert.That(context.Metadata.ReadAllTagsAndDurationCalls, Is.EqualTo(0));
            Assert.That(context.Matching.Calls, Has.Count.EqualTo(1));
            Assert.That(context.Matching.Calls[0].Context.AllowV5Identification, Is.False);
            Assert.That(context.Matching.Calls[0].Files[0].AllTags["TITLE"], Is.EqualTo(new[] { "Stored Title" }));
            Assert.That(context.Import.Imports, Has.Count.EqualTo(1));
            Assert.That(context.Import.Imports[0].DurationSeconds, Is.EqualTo(321));
            Assert.That(context.Import.Imports[0].Tags["TITLE"], Is.EqualTo(new[] { "Stored Title" }));
            Assert.That(context.Import.Imports[0].Size, Is.EqualTo(100));
            Assert.That(context.Import.Imports[0].Modified, Is.EqualTo(modified));
            Assert.That(context.Ingest.PurgedPaths, Is.EqualTo(new[] { "/books/one.mp3" }));
        }

        [Test]
        public void retry_match_does_not_count_a_match_whose_apply_result_failed()
        {
            var modified = new DateTime(2026, 7, 4, 1, 0, 0, DateTimeKind.Utc);
            var file = CreateUnmappedFile(1, "/books/one.mp3", modified, 100, "Stored Title", 321);
            var context = CreateContext(file, modified, 100);
            context.Import.ResultFactory = (path, _, _) => BookImportFileResult.Failed(path, "FILE_MISSING_AT_APPLY");

            context.Matching.OnMatch = (files, restrictToAuthorId, ctx, call) => new FileMatchResult
            {
                MatchedFiles = new[]
                {
                    new FileMatch { File = files[0], AuthorId = 5, BookId = 10, EditionId = 20 }
                }
            };

            context.Sut.Execute(new RetryUnmappedMatchCommand
            {
                MediaType = "audiobook",
                UnmappedFiles = new UnmappedFilesSelection { Scope = "all" }
            });

            Assert.That(context.Import.Imports, Has.Count.EqualTo(1));
            Assert.That(file.EditionId, Is.EqualTo(0));
            Assert.That(file.MatchDetails, Is.EqualTo("FILE_MISSING_AT_APPLY"));
            Assert.That(file.LastMatchAttempt, Is.Not.Null);
            Assert.That(context.Media.SingleUpdateCalls, Is.EqualTo(1));
        }

        [Test]
        public void retry_match_treats_already_linked_as_handled_without_failure_provenance()
        {
            var modified = new DateTime(2026, 7, 4, 1, 0, 0, DateTimeKind.Utc);
            var file = CreateUnmappedFile(1, "/books/one.mp3", modified, 100, "Stored Title", 321);
            var context = CreateContext(file, modified, 100);
            context.Import.ResultFactory = (path, _, _) => BookImportFileResult.AlreadyLinked(path, 1001);

            context.Matching.OnMatch = (files, restrictToAuthorId, ctx, call) => new FileMatchResult
            {
                MatchedFiles = new[]
                {
                    new FileMatch { File = files[0], AuthorId = 5, BookId = 10, EditionId = 20 }
                }
            };

            context.Sut.Execute(new RetryUnmappedMatchCommand
            {
                MediaType = "audiobook",
                UnmappedFiles = new UnmappedFilesSelection { Scope = "all" }
            });

            Assert.That(context.Import.Imports, Has.Count.EqualTo(1));
            Assert.That(context.Matching.Calls, Has.Count.EqualTo(1), "AlreadyLinked should suppress server and suggested-author rematches");
            Assert.That(file.MatchDetails, Is.Null);
            Assert.That(file.LastMatchAttempt, Is.Null);
            Assert.That(context.Media.SingleUpdateCalls, Is.EqualTo(0));
            Assert.That(context.PendingImports.Enqueued, Is.Empty);
        }

        [Test]
        public void retry_match_runs_server_retry_for_local_leftovers()
        {
            var modified = new DateTime(2026, 7, 4, 1, 0, 0, DateTimeKind.Utc);
            var file = CreateUnmappedFile(1, "/books/one.mp3", modified, 100, "Stored Title", 321);
            var context = CreateContext(file, modified, 100);

            context.Matching.OnMatch = (files, restrictToAuthorId, ctx, call) =>
            {
                if (call == 1)
                {
                    return new FileMatchResult
                    {
                        UnmatchedFiles = new[] { new UnmatchedFile { File = files[0], Reason = "NO_MATCH" } }
                    };
                }

                return new FileMatchResult
                {
                    MatchedFiles = new[]
                    {
                        new FileMatch { File = files[0], AuthorId = 5, BookId = 10, EditionId = 20 }
                    }
                };
            };

            context.Sut.Execute(new RetryUnmappedMatchCommand
            {
                MediaType = "audiobook",
                UnmappedFiles = new UnmappedFilesSelection { Scope = "all" }
            });

            Assert.That(context.Matching.Calls, Has.Count.EqualTo(2));
            Assert.That(context.Matching.Calls[1].Context.AllowV5Identification, Is.True);
            Assert.That(context.Matching.Calls[1].Context.AllowAuthorImport, Is.False);
            Assert.That(context.Matching.Calls[1].Context.PerFileMatching, Is.True);
            Assert.That(context.Matching.Calls[1].Context.AllowGroupedV5Suggestions, Is.True);
            Assert.That(context.Import.Imports, Has.Count.EqualTo(1));
        }

        [Test]
        public void retry_match_queues_suggested_author_import_from_server_unmatched_without_inline_author_import()
        {
            var modified = new DateTime(2026, 7, 4, 1, 0, 0, DateTimeKind.Utc);
            var files = Enumerable.Range(1, 250)
                .Select(i => CreateUnmappedFile(
                    i,
                    $"/books/Huge Book/{i:D3}.mp3",
                    modified,
                    100 + i,
                    "Huge Book",
                    60))
                .ToList();
            var context = CreateContext(files, modified);

            context.Matching.OnMatch = (matchedFiles, restrictToAuthorId, ctx, call) => new FileMatchResult
            {
                UnmatchedFiles = matchedFiles
                    .Select(file => new UnmatchedFile
                    {
                        File = file,
                        Reason = "NO_MATCH",
                        PotentialAuthors = call == 2
                            ? new[]
                            {
                                new AuthorSuggestion
                                {
                                    ProviderId = "hc:123",
                                    AuthorName = "Suggested Author",
                                    BookProviderId = "hc:999",
                                    BookTitle = "Huge Book",
                                    Confidence = 0.97
                                }
                            }
                            : Array.Empty<AuthorSuggestion>()
                    })
                    .ToArray()
            };

            context.Sut.Execute(new RetryUnmappedMatchCommand
            {
                MediaType = "audiobook",
                UnmappedFiles = new UnmappedFilesSelection { Scope = "all" }
            });

            Assert.That(context.Matching.Calls, Has.Count.EqualTo(2));
            Assert.That(context.Matching.Calls[1].Context.AllowAuthorImport, Is.False);
            Assert.That(context.PendingImports.Enqueued, Has.Count.EqualTo(1));

            var queued = context.PendingImports.Enqueued.Single();
            Assert.That(queued.ProviderId, Is.EqualTo("hc:123"));
            Assert.That(queued.SourceApplication, Is.EqualTo("RetryUnmappedMatch"));
            Assert.That(queued.Config.AuthorName, Is.EqualTo("Suggested Author"));
            Assert.That(queued.Config.CreateAudiobook, Is.True);
            Assert.That(queued.Config.CreateEbook, Is.False);
            Assert.That(queued.Config.AudiobookRootFolderPath, Is.EqualTo("/books"));
            Assert.That(queued.Config.AudiobookMonitored, Is.True);
            Assert.That(queued.Config.AudiobookBooksToMonitor, Is.Null);
            Assert.That(queued.Config.SpecificBookProviderIds, Is.Null);
            Assert.That(queued.Config.MonitorMode, Is.Null);

            var pendingStampedIds = context.Media.Updated
                .Where(file => file.MatchDetails?.StartsWith("PENDING_AUTHOR_IMPORT:", StringComparison.Ordinal) == true)
                .Select(file => file.Id)
                .Distinct()
                .ToList();
            Assert.That(pendingStampedIds, Has.Count.EqualTo(250));
            var bookEvidenceStampedIds = context.Media.Updated
                .Where(file => file.MatchDetails?.Contains("books=hc:999", StringComparison.Ordinal) == true)
                .Select(file => file.Id)
                .Distinct()
                .ToList();
            Assert.That(bookEvidenceStampedIds, Has.Count.EqualTo(250));
            Assert.That(context.Import.Imports, Is.Empty);
        }

        [Test]
        public void retry_match_keeps_many_track_books_in_one_retry_batch_and_enables_grouped_v5_suggestions()
        {
            var modified = new DateTime(2026, 7, 4, 1, 0, 0, DateTimeKind.Utc);
            var files = Enumerable.Range(1, 250)
                .Select(i => CreateUnmappedFile(
                    i,
                    $"/books/Huge Book/{i:D3}.mp3",
                    modified,
                    100 + i,
                    "Huge Book",
                    60))
                .ToList();
            var context = CreateContext(files, modified);

            context.Matching.OnMatch = (matchedFiles, restrictToAuthorId, ctx, call) => new FileMatchResult
            {
                UnmatchedFiles = matchedFiles
                    .Select(file => new UnmatchedFile { File = file, Reason = "NO_MATCH" })
                    .ToArray()
            };

            context.Sut.Execute(new RetryUnmappedMatchCommand
            {
                MediaType = "audiobook",
                UnmappedFiles = new UnmappedFilesSelection { Scope = "all" }
            });

            Assert.That(context.Metadata.ReadAllTagsAndDurationCalls, Is.EqualTo(0));
            Assert.That(context.Ingest.PurgedPaths, Has.Count.EqualTo(250));
            Assert.That(context.Matching.Calls, Has.Count.EqualTo(2));
            Assert.That(context.Matching.Calls[0].Files, Has.Length.EqualTo(250));
            Assert.That(context.Matching.Calls[1].Files, Has.Length.EqualTo(250));
            Assert.That(context.Matching.Calls[1].Context.AllowGroupedV5Suggestions, Is.True);
        }

        [Test]
        public void retry_match_refreshes_changed_file_before_matching()
        {
            var storedModified = new DateTime(2026, 7, 4, 1, 0, 0, DateTimeKind.Utc);
            var diskModified = storedModified.AddMinutes(5);
            var file = CreateUnmappedFile(1, "/books/one.mp3", storedModified, 100, "Stored Title", 321);
            var context = CreateContext(file, diskModified, 200);
            context.Metadata.Tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["TITLE"] = new List<string> { "Fresh Title" }
            };
            context.Metadata.DurationSeconds = 999;

            context.Matching.OnMatch = (files, restrictToAuthorId, ctx, call) => new FileMatchResult
            {
                MatchedFiles = new[]
                {
                    new FileMatch { File = files[0], AuthorId = 5, BookId = 10, EditionId = 20 }
                }
            };

            context.Sut.Execute(new RetryUnmappedMatchCommand
            {
                MediaType = "audiobook",
                UnmappedFiles = new UnmappedFilesSelection { Scope = "all" }
            });

            Assert.That(context.Metadata.ReadAllTagsAndDurationCalls, Is.EqualTo(1));
            Assert.That(file.Size, Is.EqualTo(200));
            Assert.That(file.DurationSeconds, Is.EqualTo(999));
            Assert.That(file.AllTags["TITLE"], Is.EqualTo(new[] { "Fresh Title" }));
            Assert.That(context.Matching.Calls[0].Files[0].AllTags["TITLE"], Is.EqualTo(new[] { "Fresh Title" }));
            Assert.That(context.Matching.Calls[0].Files[0].DurationSeconds, Is.EqualTo(999));
            Assert.That(context.Media.SingleUpdateCalls, Is.EqualTo(1));
            Assert.That(context.Media.BatchUpdateCalls, Is.EqualTo(0));
            Assert.That(context.Events.Take(2), Is.EqualTo(new[] { "update:/books/one.mp3", "match:1" }));
        }

        [Test]
        public void retry_match_backfills_quality_and_media_type_before_matching()
        {
            var storedModified = new DateTime(2026, 7, 4, 1, 0, 0, DateTimeKind.Utc);
            var diskModified = storedModified.AddMinutes(5);
            var file = CreateUnmappedFile(1, "/books/one.mp3", storedModified, 100, "Stored Title", 321);
            file.Quality = null;
            file.MediaType = null;
            var context = CreateContext(file, diskModified, 200);
            context.Metadata.Tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["TITLE"] = new List<string> { "Fresh Title" }
            };
            context.Metadata.DurationSeconds = 999;

            context.Matching.OnMatch = (files, restrictToAuthorId, ctx, call) => new FileMatchResult
            {
                MatchedFiles = new[]
                {
                    new FileMatch { File = files[0], AuthorId = 5, BookId = 10, EditionId = 20 }
                }
            };

            context.Sut.Execute(new RetryUnmappedMatchCommand
            {
                UnmappedFiles = new UnmappedFilesSelection { Scope = "all" }
            });

            Assert.That(file.Quality, Is.Not.Null);
            Assert.That(file.MediaType, Is.EqualTo("audiobook"));
            Assert.That(context.Media.SingleUpdateCalls, Is.EqualTo(1));
            Assert.That(context.Media.BatchUpdateCalls, Is.EqualTo(0));
            Assert.That(context.Events.Take(2), Is.EqualTo(new[] { "update:/books/one.mp3", "match:1" }));
        }

        [Test]
        public void retry_match_rematches_existing_server_suggested_author_locally_without_queueing_pending_import()
        {
            var modified = new DateTime(2026, 7, 4, 1, 0, 0, DateTimeKind.Utc);
            var file = CreateUnmappedFile(1, "/books/Suggested/01.mp3", modified, 100, "Suggested Book", 321);
            var context = CreateContext(file, modified, 100);
            context.Authors.AuthorsByProviderId["hc:123"] = new Author { Id = 42, Name = "Suggested Author" };
            context.Books.Books[10].AuthorId = 42;

            context.Matching.OnMatch = (files, restrictToAuthorId, ctx, call) =>
            {
                if (call == 1)
                {
                    return new FileMatchResult
                    {
                        UnmatchedFiles = new[] { new UnmatchedFile { File = files[0], Reason = "NO_MATCH" } }
                    };
                }

                if (call == 2)
                {
                    return new FileMatchResult
                    {
                        UnmatchedFiles = new[]
                        {
                            new UnmatchedFile
                            {
                                File = files[0],
                                Reason = "V5_AUTHOR_SUGGESTION",
                                PotentialAuthors = new[]
                                {
                                    new AuthorSuggestion
                                    {
                                        ProviderId = "hc:123",
                                        AuthorName = "Suggested Author",
                                        Confidence = 0.99
                                    }
                                }
                            }
                        }
                    };
                }

                Assert.That(restrictToAuthorId, Is.EqualTo(42));
                Assert.That(ctx.AllowV5Identification, Is.False);
                return new FileMatchResult
                {
                    MatchedFiles = new[]
                    {
                        new FileMatch { File = files[0], AuthorId = 42, BookId = 10, EditionId = 20 }
                    }
                };
            };

            context.Sut.Execute(new RetryUnmappedMatchCommand
            {
                MediaType = "audiobook",
                UnmappedFiles = new UnmappedFilesSelection { Scope = "all" }
            });

            Assert.That(context.Matching.Calls, Has.Count.EqualTo(3));
            Assert.That(context.Matching.Calls[2].RestrictToAuthorId, Is.EqualTo(42));
            Assert.That(context.Import.Imports, Has.Count.EqualTo(1));
            Assert.That(context.PendingImports.Enqueued, Is.Empty);
            Assert.That(context.AuthorLibrary.Adds, Is.Empty);
        }

        [TestCase(BookMediaType.Ebook, "/books/Freida McFadden/The Housemaid Is Watching.epub", "ebook", false)]
        [TestCase(BookMediaType.Audiobook, "/books/Freida McFadden/The Housemaid Is Watching.m4b", "audiobook", false)]
        [TestCase(BookMediaType.Ebook, "/books/Freida McFadden/The Housemaid Is Watching.epub", "ebook", true)]
        public void retry_match_backfills_existing_author_missing_media_side_and_imports_in_one_press(
            BookMediaType requestedMediaType,
            string path,
            string commandMediaType,
            bool alreadyLinked)
        {
            var modified = new DateTime(2026, 7, 23, 1, 0, 0, DateTimeKind.Utc);
            var storedDuration = requestedMediaType == BookMediaType.Audiobook ? 321 : (int?)null;
            var file = CreateUnmappedFile(1, path, modified, 100, "The Housemaid Is Watching", storedDuration);
            file.MediaType = commandMediaType;
            file.Quality = new QualityModel
            {
                Quality = requestedMediaType == BookMediaType.Ebook ? Quality.EPUB : Quality.M4B
            };

            var context = CreateContext(file, modified, 100);
            context.RootFolders.RootFolder = CreateMixedRootFolder();
            if (alreadyLinked)
            {
                context.Import.ResultFactory = (importPath, _, _) => BookImportFileResult.AlreadyLinked(importPath, 1001);
            }

            var author = new Author
            {
                Id = 42,
                Name = "Freida McFadden",
                AudiobookQualityProfileId = requestedMediaType == BookMediaType.Ebook ? 77 : null,
                AudiobookMetadataProfileId = requestedMediaType == BookMediaType.Ebook ? 78 : null,
                EbookQualityProfileId = requestedMediaType == BookMediaType.Audiobook ? 87 : null,
                EbookMetadataProfileId = requestedMediaType == BookMediaType.Audiobook ? 88 : null
            };
            context.Authors.AuthorsByProviderId["hc:297579"] = author;

            var existingMediaType = requestedMediaType == BookMediaType.Ebook
                ? BookMediaType.Audiobook
                : BookMediaType.Ebook;
            context.Books.Books[10].AuthorId = author.Id;
            context.Books.Books[10].MediaType = existingMediaType;
            context.Books.Books[10].HardcoverBookId = "hc:994242";
            context.Books.Books[10].Title = "Wrong-media representative";

            context.AuthorLibrary.OnAdd = (providerId, config) =>
            {
                context.Books.Books[11] = new Book
                {
                    Id = 11,
                    AuthorId = author.Id,
                    MediaType = requestedMediaType,
                    HardcoverBookId = "hc:994242",
                    Title = "The Housemaid Is Watching"
                };
                context.Editions.Editions[21] = new Edition
                {
                    Id = 21,
                    BookId = 11,
                    Title = "The Housemaid Is Watching"
                };
                return Task.FromResult(author);
            };

            context.Matching.OnMatch = (files, restrictToAuthorId, matchingContext, call) =>
            {
                if (call == 1)
                {
                    return new FileMatchResult
                    {
                        UnmatchedFiles = new[] { new UnmatchedFile { File = files[0], Reason = "NO_MATCH" } }
                    };
                }

                if (call == 2)
                {
                    return new FileMatchResult
                    {
                        UnmatchedFiles = new[]
                        {
                            new UnmatchedFile
                            {
                                File = files[0],
                                Reason = "V5_AUTHOR_SUGGESTION",
                                PotentialAuthors = new[]
                                {
                                    new AuthorSuggestion
                                    {
                                        ProviderId = "hc:297579",
                                        AuthorName = author.Name,
                                        BookProviderId = "hc:994242",
                                        BookTitle = "The Housemaid Is Watching",
                                        Confidence = 0.99
                                    }
                                }
                            }
                        }
                    };
                }

                Assert.That(restrictToAuthorId, Is.EqualTo(author.Id));
                Assert.That(matchingContext.AllowV5Identification, Is.False);
                Assert.That(matchingContext.HardAllowedBookIds, Is.EqualTo(new[] { 11 }));
                Assert.That(matchingContext.DisablePathFallback, Is.False);
                return new FileMatchResult
                {
                    MatchedFiles = new[]
                    {
                        new FileMatch
                        {
                            File = files[0],
                            AuthorId = author.Id,
                            AuthorName = author.Name,
                            BookId = 11,
                            BookTitle = "The Housemaid Is Watching",
                            EditionId = 21
                        }
                    }
                };
            };

            context.Sut.Execute(new RetryUnmappedMatchCommand
            {
                MediaType = commandMediaType,
                UnmappedFiles = new UnmappedFilesSelection { Scope = "all" }
            });

            Assert.That(context.AuthorLibrary.Adds, Has.Count.EqualTo(1));
            var add = context.AuthorLibrary.Adds.Single();
            Assert.Multiple(() =>
            {
                Assert.That(add.ProviderId, Is.EqualTo("hc:297579"));
                Assert.That(add.Config.QueueIfUnavailable, Is.False);
                Assert.That(add.Config.CreateEbook, Is.EqualTo(requestedMediaType == BookMediaType.Ebook));
                Assert.That(add.Config.CreateAudiobook, Is.EqualTo(requestedMediaType == BookMediaType.Audiobook));
                Assert.That(add.Config.MonitorMode, Is.Null);
                Assert.That(add.Config.SpecificBookProviderIds, Is.Null);
                Assert.That(context.Matching.Calls, Has.Count.EqualTo(3));
                Assert.That(context.Metadata.ReadAllTagsAndDurationCalls, Is.EqualTo(0));
                Assert.That(context.Import.Imports, Has.Count.EqualTo(1));
                Assert.That(context.Import.Imports.Single().BookId, Is.EqualTo(11));
                Assert.That(context.PendingImports.Enqueued, Is.Empty);
            });

            if (requestedMediaType == BookMediaType.Ebook)
            {
                Assert.Multiple(() =>
                {
                    Assert.That(add.Config.EbookRootFolderPath, Is.EqualTo("/books"));
                    Assert.That(add.Config.EbookQualityProfileId, Is.EqualTo(3));
                    Assert.That(add.Config.EbookMetadataProfileId, Is.EqualTo(4));
                    Assert.That(add.Config.EbookMonitored, Is.False);
                    Assert.That(add.Config.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
                });
            }
            else
            {
                Assert.Multiple(() =>
                {
                    Assert.That(add.Config.AudiobookRootFolderPath, Is.EqualTo("/books"));
                    Assert.That(add.Config.AudiobookQualityProfileId, Is.EqualTo(1));
                    Assert.That(add.Config.AudiobookMetadataProfileId, Is.EqualTo(2));
                    Assert.That(add.Config.AudiobookMonitored, Is.True);
                    Assert.That(add.Config.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.All));
                });
            }

            if (alreadyLinked)
            {
                Assert.That(file.MatchDetails, Is.Null);
                Assert.That(file.LastMatchAttempt, Is.Null);
                Assert.That(context.Media.SingleUpdateCalls, Is.EqualTo(0));
                Assert.That(context.PendingImports.Enqueued, Is.Empty);
            }
        }

        [Test]
        public void retry_match_does_not_backfill_existing_author_through_incompatible_root()
        {
            var modified = new DateTime(2026, 7, 23, 1, 0, 0, DateTimeKind.Utc);
            var file = CreateUnmappedFile(1, "/books/Freida McFadden/The Boyfriend.epub", modified, 100, "The Boyfriend", null);
            file.MediaType = "ebook";
            file.Quality = new QualityModel { Quality = Quality.EPUB };
            var context = CreateContext(file, modified, 100);
            var author = new Author { Id = 42, Name = "Freida McFadden" };
            context.Authors.AuthorsByProviderId["hc:297579"] = author;
            context.Books.Books[10].AuthorId = author.Id;

            context.Matching.OnMatch = (files, restrictToAuthorId, matchingContext, call) => new FileMatchResult
            {
                UnmatchedFiles = new[]
                {
                    new UnmatchedFile
                    {
                        File = files[0],
                        Reason = call == 1 ? "NO_MATCH" : "V5_AUTHOR_SUGGESTION",
                        PotentialAuthors = call == 2
                            ? new[] { new AuthorSuggestion { ProviderId = "hc:297579", AuthorName = author.Name, Confidence = 0.99 } }
                            : Array.Empty<AuthorSuggestion>()
                    }
                }
            };

            context.Sut.Execute(new RetryUnmappedMatchCommand
            {
                MediaType = "ebook",
                UnmappedFiles = new UnmappedFilesSelection { Scope = "all" }
            });

            Assert.That(context.Matching.Calls, Has.Count.EqualTo(2));
            Assert.That(context.AuthorLibrary.Adds, Is.Empty);
            Assert.That(context.Import.Imports, Is.Empty);
            Assert.That(file.MatchDetails, Is.EqualTo("ROOT_FOLDER_TYPE_Audiobook"));
        }

        [Test]
        public void retry_match_keeps_file_unmapped_when_authoritative_backfill_excludes_suggested_work()
        {
            var modified = new DateTime(2026, 7, 23, 1, 0, 0, DateTimeKind.Utc);
            var file = CreateUnmappedFile(1, "/books/Freida McFadden/The Boyfriend.epub", modified, 100, "The Boyfriend", null);
            file.MediaType = "ebook";
            file.Quality = new QualityModel { Quality = Quality.EPUB };
            var context = CreateContext(file, modified, 100);
            context.RootFolders.RootFolder = CreateMixedRootFolder();
            var author = new Author { Id = 42, Name = "Freida McFadden" };
            context.Authors.AuthorsByProviderId["hc:297579"] = author;
            context.Books.Books[10].AuthorId = author.Id;

            context.AuthorLibrary.OnAdd = (providerId, config) =>
            {
                context.Books.Books[11] = new Book
                {
                    Id = 11,
                    AuthorId = author.Id,
                    MediaType = BookMediaType.Ebook,
                    HardcoverBookId = "hc:different-work",
                    Title = "Different retained work"
                };
                return Task.FromResult(author);
            };

            context.Matching.OnMatch = (files, restrictToAuthorId, matchingContext, call) => new FileMatchResult
            {
                UnmatchedFiles = new[]
                {
                    new UnmatchedFile
                    {
                        File = files[0],
                        Reason = call == 1 ? "NO_MATCH" : "V5_AUTHOR_SUGGESTION",
                        PotentialAuthors = call == 2
                            ? new[]
                            {
                                new AuthorSuggestion
                                {
                                    ProviderId = "hc:297579",
                                    AuthorName = author.Name,
                                    BookProviderId = "hc:1325795",
                                    Confidence = 0.99
                                }
                            }
                            : Array.Empty<AuthorSuggestion>()
                    }
                }
            };

            context.Sut.Execute(new RetryUnmappedMatchCommand
            {
                MediaType = "ebook",
                UnmappedFiles = new UnmappedFilesSelection { Scope = "all" }
            });

            Assert.That(context.AuthorLibrary.Adds, Has.Count.EqualTo(1));
            Assert.That(context.Matching.Calls, Has.Count.EqualTo(2));
            Assert.That(context.Import.Imports, Is.Empty);
            Assert.That(file.MatchDetails, Is.EqualTo("AUTHORITATIVE_WORK_NOT_LOCAL:Ebook:hc:1325795:V5_WORK_NOT_LOCAL"));
            Assert.That(context.PendingImports.Enqueued, Is.Empty);
        }

        [Test]
        public void retry_match_rejects_a_scoped_match_outside_the_v5_work_boundary()
        {
            var modified = new DateTime(2026, 7, 23, 1, 0, 0, DateTimeKind.Utc);
            var file = CreateUnmappedFile(1, "/books/Freida McFadden/The Boyfriend.epub", modified, 100, "The Boyfriend", null);
            file.MediaType = "ebook";
            file.Quality = new QualityModel { Quality = Quality.EPUB };
            var context = CreateContext(file, modified, 100);
            context.RootFolders.RootFolder = CreateMixedRootFolder();
            var author = new Author { Id = 42, Name = "Freida McFadden" };
            context.Authors.AuthorsByProviderId["hc:297579"] = author;
            context.Books.Books[11] = new Book
            {
                Id = 11,
                AuthorId = author.Id,
                MediaType = BookMediaType.Ebook,
                HardcoverBookId = "hc:1325795",
                Title = "The Boyfriend"
            };
            context.Books.Books[12] = new Book
            {
                Id = 12,
                AuthorId = author.Id,
                MediaType = BookMediaType.Ebook,
                HardcoverBookId = "hc:different-work",
                Title = "Different Work"
            };
            context.Editions.Editions[22] = new Edition
            {
                Id = 22,
                BookId = 12,
                Title = "Different Work"
            };

            context.Matching.OnMatch = (files, restrictToAuthorId, matchingContext, call) =>
            {
                if (call == 1)
                {
                    return new FileMatchResult
                    {
                        UnmatchedFiles = new[] { new UnmatchedFile { File = files[0], Reason = "NO_MATCH" } }
                    };
                }

                if (call == 2)
                {
                    return new FileMatchResult
                    {
                        UnmatchedFiles = new[]
                        {
                            new UnmatchedFile
                            {
                                File = files[0],
                                Reason = "V5_AUTHOR_SUGGESTION",
                                PotentialAuthors = new[]
                                {
                                    new AuthorSuggestion
                                    {
                                        ProviderId = "hc:297579",
                                        AuthorName = author.Name,
                                        BookProviderId = "hc:1325795",
                                        Confidence = 0.99
                                    }
                                }
                            }
                        }
                    };
                }

                Assert.That(matchingContext.HardAllowedBookIds, Is.EqualTo(new[] { 11 }));
                return new FileMatchResult
                {
                    MatchedFiles = new[]
                    {
                        new FileMatch
                        {
                            File = files[0],
                            AuthorId = author.Id,
                            BookId = 12,
                            BookTitle = "Different Work",
                            EditionId = 22
                        }
                    }
                };
            };

            context.Sut.Execute(new RetryUnmappedMatchCommand
            {
                MediaType = "ebook",
                UnmappedFiles = new UnmappedFilesSelection { Scope = "all" }
            });

            Assert.That(context.Matching.Calls, Has.Count.EqualTo(3));
            Assert.That(context.AuthorLibrary.Adds, Is.Empty);
            Assert.That(context.Import.Imports, Is.Empty);
            Assert.That(file.MatchDetails, Does.StartWith("SUGGESTED_LOCAL_MATCH_REJECTED:"));
            Assert.That(file.MatchDetails, Does.Contain("outside the provider-resolved work boundary"));
        }

        private static BookFile CreateUnmappedFile(int id, string path, DateTime modified, long size, string title, int? durationSeconds)
        {
            return new BookFile
            {
                Id = id,
                Path = path,
                EditionId = 0,
                MediaType = "audiobook",
                Size = size,
                Modified = modified,
                AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["TITLE"] = new List<string> { title }
                },
                DurationSeconds = durationSeconds,
                Quality = new QualityModel { Quality = Quality.MP3 }
            };
        }

        private static TestContext CreateContext(BookFile file, DateTime diskModified, long diskSize)
        {
            return CreateContext(new[] { file }, current => (diskModified, diskSize));
        }

        private static TestContext CreateContext(List<BookFile> files, DateTime diskModified)
        {
            return CreateContext(files, current => (diskModified, current.Size));
        }

        private static TestContext CreateContext(IEnumerable<BookFile> files, Func<BookFile, (DateTime Modified, long Size)> diskInfo)
        {
            var fileList = files.ToList();
            var media = new StubMediaFileService { UnmappedFiles = fileList };
            var metadata = new StubMetadataTagService();
            var disk = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var diskProxy = (DiskProviderProxy)(object)disk;
            foreach (var file in fileList)
            {
                var info = diskInfo(file);
                diskProxy.Files[file.Path] = (true, info.Size, info.Modified);
            }

            var ingest = DispatchProxy.Create<IIngestQueueRepository, IngestQueueRepositoryProxy>();
            var ingestProxy = (IngestQueueRepositoryProxy)(object)ingest;
            var matching = new RecordingMatchingService();
            var events = new List<string>();
            media.Events = events;
            matching.Events = events;
            var import = new RecordingBookImportService();
            var pendingImports = new RecordingPendingAuthorImportService();
            var authorLibrary = new RecordingAuthorLibraryService();
            var rootFolders = new RootFolderServiceStub();

            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var books = (BookServiceProxy)(object)bookService;
            books.Books[10] = new Book { Id = 10, Title = "Book", MediaType = BookMediaType.Audiobook };

            var editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
            var editions = (EditionServiceProxy)(object)editionService;
            editions.Editions[20] = new Edition { Id = 20, BookId = 10, Title = "Edition" };

            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            var authorServiceProxy = (AuthorServiceProxy)(object)authorService;

            var sut = new RetryUnmappedMatchCommandHandler(
                media,
                metadata,
                disk,
                ingest,
                matching,
                import,
                bookService,
                editionService,
                new DestinationServiceStub(),
                authorLibrary,
                pendingImports,
                rootFolders,
                authorService,
                LogManager.GetCurrentClassLogger());

            return new TestContext(
                sut, media, metadata, ingestProxy, matching, import, authorLibrary, pendingImports,
                rootFolders, books, editions, authorServiceProxy, events);
        }

        private static RootFolder CreateAudiobookRootFolder()
        {
            var rootFolder = new RootFolder
            {
                Path = "/books",
                FolderType = FolderType.Audiobook,
                DefaultTags = new HashSet<int> { 7 }
            };

            rootFolder.SetAudiobookSettings(new MediaTypeSettings
            {
                QualityProfileId = 1,
                MetadataProfileId = 2,
                Monitored = true,
                MonitorExistingBooks = true,
                MonitorNewItems = NewItemMonitorTypes.All,
                Tags = new List<int> { 8 }
            });

            return rootFolder;
        }

        private static RootFolder CreateMixedRootFolder()
        {
            var rootFolder = new RootFolder
            {
                Path = "/books",
                FolderType = FolderType.Mixed,
                DefaultTags = new HashSet<int> { 7 }
            };

            rootFolder.SetAudiobookSettings(new MediaTypeSettings
            {
                QualityProfileId = 1,
                MetadataProfileId = 2,
                Monitored = true,
                MonitorExistingBooks = true,
                MonitorNewItems = NewItemMonitorTypes.All,
                Tags = new List<int> { 8 }
            });

            rootFolder.SetEbookSettings(new MediaTypeSettings
            {
                QualityProfileId = 3,
                MetadataProfileId = 4,
                Monitored = false,
                MonitorExistingBooks = false,
                MonitorNewItems = NewItemMonitorTypes.None,
                Tags = new List<int> { 9 }
            });

            return rootFolder;
        }

        private static object GetDefaultValue(Type type)
        {
            if (type == null || type == typeof(void))
            {
                return null;
            }

            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }

        private sealed class TestContext
        {
            public TestContext(
                RetryUnmappedMatchCommandHandler sut,
                StubMediaFileService media,
                StubMetadataTagService metadata,
                IngestQueueRepositoryProxy ingest,
                RecordingMatchingService matching,
                RecordingBookImportService import,
                RecordingAuthorLibraryService authorLibrary,
                RecordingPendingAuthorImportService pendingImports,
                RootFolderServiceStub rootFolders,
                BookServiceProxy books,
                EditionServiceProxy editions,
                AuthorServiceProxy authors,
                List<string> events)
            {
                Sut = sut;
                Media = media;
                Metadata = metadata;
                Ingest = ingest;
                Matching = matching;
                Import = import;
                AuthorLibrary = authorLibrary;
                PendingImports = pendingImports;
                RootFolders = rootFolders;
                Books = books;
                Editions = editions;
                Authors = authors;
                Events = events;
            }

            public RetryUnmappedMatchCommandHandler Sut { get; }
            public StubMediaFileService Media { get; }
            public StubMetadataTagService Metadata { get; }
            public IngestQueueRepositoryProxy Ingest { get; }
            public RecordingMatchingService Matching { get; }
            public RecordingBookImportService Import { get; }
            public RecordingPendingAuthorImportService PendingImports { get; }
            public RecordingAuthorLibraryService AuthorLibrary { get; }
            public RootFolderServiceStub RootFolders { get; }
            public AuthorServiceProxy Authors { get; }
            public BookServiceProxy Books { get; }
            public EditionServiceProxy Editions { get; }
            public List<string> Events { get; }
        }
    }
}
