using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Services;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.MediaFiles.TagExtraction;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.MediaFiles
{
    /// <summary>
    /// Clean implementation of DownloadedBooksImportService that follows the import bible.
    /// This service handles the download folder workflow and passes everything to ImportOrchestrator.
    /// </summary>
    public class DownloadedBooksImportService : IDownloadedBooksImportService
    {
        private static readonly HashSet<string> EmbeddedIdentityEvidenceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "TITLE", "ALBUM", "ARTIST", "ALBUMARTIST", "AUTHOR", "BOOK", "NARRATOR", "READER", "ISBN", "ASIN", "AUDIBLE_ASIN"
        };

        private readonly IDiskProvider _diskProvider;
        private readonly IDiskScanService _diskScanService;
        private readonly IFileMatchingService _fileMatchingService;
        private readonly IMetadataTagService _metadataTagService;
        private readonly IImportApprovedBooks _importApprovedBooks;
        private readonly IBookService _bookService;
        private readonly IAuthorService _authorService;
        private readonly IEditionService _editionService;
        private readonly IImportOrchestrator _importOrchestrator;
        private readonly IAuthorLibraryService _authorLibraryService;
        private readonly IRootFolderService _rootFolderService;
        private readonly IConfigService _configService;
        private readonly IHistoryService _historyService;
        private readonly IEventAggregator _eventAggregator;
        private readonly IRuntimeInfo _runtimeInfo;
        private readonly IMediaInfoExtractor _mediaInfoExtractor;
        private readonly Logger _logger;

        public DownloadedBooksImportService(
            IDiskProvider diskProvider,
            IDiskScanService diskScanService,
            IFileMatchingService fileMatchingService,
            IMetadataTagService metadataTagService,
            IImportApprovedBooks importApprovedBooks,
            IBookService bookService,
            IAuthorService authorService,
            IEditionService editionService,
            IImportOrchestrator importOrchestrator,
            IAuthorLibraryService authorLibraryService,
            IRootFolderService rootFolderService,
            IConfigService configService,
            IHistoryService historyService,
            IEventAggregator eventAggregator,
            IRuntimeInfo runtimeInfo,
            IMediaInfoExtractor mediaInfoExtractor,
            Logger logger)
        {
            _diskProvider = diskProvider;
            _diskScanService = diskScanService;
            _fileMatchingService = fileMatchingService;
            _metadataTagService = metadataTagService;
            _importApprovedBooks = importApprovedBooks;
            _bookService = bookService;
            _authorService = authorService;
            _editionService = editionService;
            _importOrchestrator = importOrchestrator;
            _authorLibraryService = authorLibraryService;
            _rootFolderService = rootFolderService;
            _configService = configService;
            _historyService = historyService;
            _eventAggregator = eventAggregator;
            _runtimeInfo = runtimeInfo;
            _mediaInfoExtractor = mediaInfoExtractor;
            _logger = logger;
        }

        public List<ImportResult> ProcessRootFolder(IDirectoryInfo directoryInfo)
        {
            // ALL file discovery must go through ImportOrchestrator
            // This method should just delegate to ProcessPath
            return ProcessPath(directoryInfo.FullName, ImportMode.Auto, null, null);
        }

        public List<ImportResult> ProcessPath(string path, ImportMode importMode = ImportMode.Auto, Author author = null, DownloadClientItem downloadClientItem = null, RemoteBook remoteBook = null, bool requireDefaultRootFolderForMissingAuthors = false)
        {
            return ProcessPath(path, importMode, author, downloadClientItem, remoteBook, null, requireDefaultRootFolderForMissingAuthors);
        }

            private List<ImportResult> ProcessPath(string path, ImportMode importMode, Author author, DownloadClientItem downloadClientItem, RemoteBook remoteBook, ParsedBookInfo parsedBookInfo, bool requireDefaultRootFolderForMissingAuthors = false)
            {
                _logger.Debug("[DOWNLOAD-IMPORT] Processing path: {0}, mode: {1}, downloadClient: {2}",
                    path, importMode, downloadClientItem?.Title ?? "none");

                if (_diskProvider.FolderExists(path))
                {
                    var directoryInfo = _diskProvider.GetDirectoryInfo(path);
                    return ProcessFolder(directoryInfo, importMode, author, downloadClientItem, remoteBook, parsedBookInfo, requireDefaultRootFolderForMissingAuthors);
                }

                if (_diskProvider.FileExists(path))
                {
                    var fileInfo = _diskProvider.GetFileInfo(path);
                    return ProcessFile(fileInfo, importMode, author, downloadClientItem, remoteBook, parsedBookInfo, requireDefaultRootFolderForMissingAuthors);
                }

            LogInaccessiblePathError(path);
            _eventAggregator.PublishEvent(new TrackImportFailedEvent(null, null, true, downloadClientItem));
            return new List<ImportResult> { InaccessiblePathResult(path) };
        }

            private List<ImportResult> ProcessFolder(IDirectoryInfo directoryInfo, ImportMode importMode, Author author, DownloadClientItem downloadClientItem, RemoteBook remoteBook, ParsedBookInfo parsedBookInfo = null, bool requireDefaultRootFolderForMissingAuthors = false)
            {
                _logger.Debug("[DOWNLOAD-IMPORT] Processing folder: {0}", directoryInfo.FullName);

            // Build precise media file list (flat downloads)
            List<IFileInfo> visibleFiles;
            List<IFileInfo> mediaFiles;
            var fileListScoped = downloadClientItem?.FilePaths != null && downloadClientItem.FilePaths.Count > 0;
            if (fileListScoped)
            {
                visibleFiles = downloadClientItem.FilePaths
                    .Where(p => !string.IsNullOrWhiteSpace(p) && _diskProvider.FileExists(p))
                    .Select(p => _diskProvider.GetFileInfo(p))
                    .ToList();
            }
            else
            {
                visibleFiles = _diskProvider.GetFileInfos(directoryInfo.FullName, true);
            }

            mediaFiles = visibleFiles
                .Where(f => MediaFileExtensions.AllExtensions.Contains(f.Extension))
                .ToList();

            if (!mediaFiles.Any())
            {
                _logger.Debug("[DOWNLOAD-IMPORT] No media files found in: {0}", directoryInfo.FullName);
                return new List<ImportResult> { NoSupportedMediaFilesResult(directoryInfo.FullName, visibleFiles) };
            }

            var discovered = new List<DiscoveredFileWithMetadata>(mediaFiles.Count);
            var tagsByPath = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase);
            foreach (var fi in mediaFiles)
            {
                var (tags, durationSeconds) = _metadataTagService.ReadAllTagsAndDuration(fi);
                tags ??= new Dictionary<string, List<string>>();
                tagsByPath[fi.FullName] = tags;
                discovered.Add(new DiscoveredFileWithMetadata
                {
                    Path = fi.FullName,
                    Size = fi.Length,
                    Modified = fi.LastWriteTimeUtc,
                    AllTags = tags,
                    DurationSeconds = durationSeconds
                });
            }

            var targetBookIds = remoteBook?.Books?
                .Select(b => b?.Id ?? 0)
                .Where(id => id > 0)
                .Distinct()
                .ToList();
            var restrictToAuthorId = ResolveRestrictedAuthorId(author, remoteBook);
            var allowAutomaticAuthorImport = downloadClientItem != null && _configService.AutoAddMissingAuthorsFromCompletedDownloads;
            var matchCtx = CreateStrictMatchingContext(
                downloadClientItem == null || allowAutomaticAuthorImport,
                targetBookIds,
                allowPathFallback: ShouldAllowTrackedDownloadPathFallback(downloadClientItem, restrictToAuthorId, targetBookIds));
            var matchResult = _fileMatchingService.MatchFilesToLibraryAsync(discovered.ToArray(), restrictToAuthorId, matchCtx).GetAwaiter().GetResult();

            var decisions = new List<ImportDecision<LocalBook>>();
            var booksById = new Dictionary<int, Book>();
            var authorsById = new Dictionary<int, Author>();
            foreach (var fm in matchResult.MatchedFiles)
            {
                var decision = CreateDecisionForMatch(fm, tagsByPath, booksById, authorsById);
                if (decision != null)
                {
                    decisions.Add(decision);
                }
            }

            // When manually triggered or explicitly enabled for completed downloads, add suggested missing
            // authors and re-run matching restricted to each added author so files can import in one run.
            var rematchedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if ((downloadClientItem == null || allowAutomaticAuthorImport) && matchResult.UnmatchedFiles.Any())
            {
                var discoveredByPath = discovered.ToDictionary(d => d.Path, StringComparer.OrdinalIgnoreCase);

                var groups = matchResult.UnmatchedFiles
                    .Select(u => new
                    {
                        Unmatched = u,
                        Suggestion = u.PotentialAuthors?.FirstOrDefault()
                    })
                    .Where(x => x.Suggestion != null && !string.IsNullOrWhiteSpace(x.Suggestion.ProviderId))
                    .GroupBy(x => x.Suggestion.ProviderId, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var group in groups)
                {
                    var suggestion = group.First().Suggestion;
                    var authorProviderId = suggestion.ProviderId;
                    var authorName = suggestion.AuthorName;

                    var filePaths = group
                        .Select(x => x.Unmatched.File.Path)
                        .Where(p => !string.IsNullOrWhiteSpace(p))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (!TryBuildDownloadedImportMonitoringConfig(authorName, filePaths, out var config, out var configError, requireDefaultRoots: downloadClientItem != null || requireDefaultRootFolderForMissingAuthors))
                    {
                        _logger.Warn("[DOWNLOAD-IMPORT] Cannot auto-add author '{0}' ({1}): {2}",
                            authorName ?? "<unknown>", authorProviderId ?? "<unknown>", configError);
                        SetAutoAddRejectionReason(group.Select(x => x.Unmatched), authorName, configError);
                        continue;
                    }

                    try
                    {
                        _logger.Debug("[DOWNLOAD-IMPORT] Auto-adding author '{0}' ({1}) for {2} unmatched file(s)",
                            authorName ?? "<unknown>", authorProviderId, filePaths.Count);

                        var addedAuthor = _authorLibraryService.AddAuthorAsync(authorProviderId, config).GetAwaiter().GetResult();

                        // Pending imports (negative IDs) can't be imported immediately.
                        if (addedAuthor == null || addedAuthor.Id <= 0)
                        {
                            _logger.Warn("[DOWNLOAD-IMPORT] Author add returned no immediate author for '{0}' ({1}), id={2}",
                                authorName ?? "<unknown>", authorProviderId, addedAuthor?.Id ?? 0);
                            continue;
                        }

                        var discoveredGroup = filePaths
                            .Where(p => discoveredByPath.ContainsKey(p))
                            .Select(p => discoveredByPath[p])
                            .ToArray();

                        if (discoveredGroup.Length == 0)
                        {
                            continue;
                        }

                        var rematchCtx = CreateStrictMatchingContext(false, null);
                        var rematch = _fileMatchingService.MatchFilesToLibraryAsync(discoveredGroup, addedAuthor.Id, rematchCtx)
                            .GetAwaiter().GetResult();

                        foreach (var fm in rematch.MatchedFiles)
                        {
                            var decision = CreateDecisionForMatch(fm, tagsByPath, booksById, authorsById);
                            if (decision != null)
                            {
                                decisions.Add(decision);
                                rematchedPaths.Add(fm.File.Path);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "[DOWNLOAD-IMPORT] Failed to auto-add author '{0}' ({1}) during downloaded import",
                            authorName ?? "<unknown>", authorProviderId ?? "<unknown>");
                    }
                }
            }

            // Any unmatched files remaining should be rejected (do not route through ImportApprovedBooks' orchestrator path).
            foreach (var um in matchResult.UnmatchedFiles)
            {
                if (rematchedPaths.Contains(um.File.Path))
                {
                    continue;
                }

                tagsByPath.TryGetValue(um.File.Path, out var unmatchedTags);
                unmatchedTags ??= um.File.AllTags ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

                var local = new LocalBook
                {
                    Path = um.File.Path,
                    Size = um.File.Size,
                    Modified = um.File.Modified,
                    DurationSeconds = um.File.DurationSeconds,
                    Quality = um.File.Quality ?? GuessQualityByExtension(um.File.Path),
                    RawTags = new RawFileTags { AllTags = unmatchedTags }
                };
                var reason = BuildUnmatchedRejectionReason(um);
                decisions.Add(new ImportDecision<LocalBook>(local, new Rejection(reason)));
            }

            // Disabled deliberately: upstream arrs use grabbed release identity as a guardrail,
            // not as a post-match mutator. Normal edition matching should win unless a pinned
            // edition was rejected by the matching pipeline itself.
            // decisions = RepairTrackedMultipartAudioDecisions(decisions, remoteBook, downloadClientItem, author, downloadClientItem?.Title ?? directoryInfo.Name);
            ApplyTrackedReleaseEvidence(decisions, remoteBook, downloadClientItem);

            EnforceTrackedDownloadTargetBooks(decisions, remoteBook, downloadClientItem);
            EnforceTrackedDownloadExpectedEditions(decisions, downloadClientItem);

            // Derive effective import mode for downloads (Move if client can move files; otherwise Copy)
            var effectiveMode = importMode;
            if (importMode == ImportMode.Auto && downloadClientItem != null)
            {
                effectiveMode = downloadClientItem.CanMoveFiles ? ImportMode.Move : ImportMode.Copy;
            }

            var importResults = _importApprovedBooks.Import(decisions, replaceExisting: true, downloadClientItem: downloadClientItem, importMode: effectiveMode);
            var hasPendingConversion = importResults.Any(result => result.Result == ImportResultType.Pending);

            // Import extra files if configured
            if (!hasPendingConversion && _configService.ImportExtraFiles && importResults.Any(r => r.Result == ImportResultType.Imported))
            {
                ImportExtraFiles(importResults, directoryInfo, effectiveMode == ImportMode.Move, fileListScoped ? visibleFiles : null);
            }

            if (!hasPendingConversion && effectiveMode == ImportMode.Move && importResults.Any(i => i.Result == ImportResultType.Imported) && ShouldDeleteFolder(directoryInfo))
            {
                _logger.Debug("Deleting folder after importing valid files");
                try { _diskProvider.DeleteFolder(directoryInfo.FullName, true); } catch (IOException e) { _logger.Debug(e, "Unable to delete folder after importing: {0}", e.Message); }
            }

            return importResults;
        }

            private List<ImportResult> ProcessFile(IFileInfo fileInfo, ImportMode importMode, Author author, DownloadClientItem downloadClientItem, RemoteBook remoteBook, ParsedBookInfo parsedBookInfo = null, bool requireDefaultRootFolderForMissingAuthors = false)
            {
                _logger.Debug("[DOWNLOAD-IMPORT] Processing file: {0}", fileInfo.FullName);

            if (Path.GetFileNameWithoutExtension(fileInfo.Name).StartsWith("._"))
            {
                _logger.Debug("[{0}] starts with '._', skipping", fileInfo.FullName);
                var localBook = new LocalBook { Path = fileInfo.FullName };
                var rejection = new Rejection("Invalid file, filename starts with '._'");
                return new List<ImportResult>
                {
                    new ImportResult(new ImportDecision<LocalBook>(localBook, rejection), "Invalid file")
                };
            }

            if (downloadClientItem == null && _diskProvider.IsFileLocked(fileInfo.FullName))
            {
                return new List<ImportResult> { FileIsLockedResult(fileInfo.FullName) };
            }

            // Single-file download: build tags, match, and import
            var (tags, durationSeconds) = _metadataTagService.ReadAllTagsAndDuration(fileInfo);
            tags ??= new Dictionary<string, List<string>>();
            var discovered = new[]
            {
                new DiscoveredFileWithMetadata
                {
                    Path = fileInfo.FullName,
                    Size = fileInfo.Length,
                    Modified = fileInfo.LastWriteTimeUtc,
                    AllTags = tags,
                    DurationSeconds = durationSeconds
                }
            };
            var targetBookIds = remoteBook?.Books?
                .Select(b => b?.Id ?? 0)
                .Where(id => id > 0)
                .Distinct()
                .ToList();
            // Allow V5 identification for manual imports and for completed downloads when explicitly enabled.
            var allowAutomaticAuthorImport = downloadClientItem != null && _configService.AutoAddMissingAuthorsFromCompletedDownloads;
            var matchCtx = MatchingContextPresets.ForDownloaded(downloadClientItem == null || allowAutomaticAuthorImport, targetBookIds, allowPathFallback: true);
            var restrictToAuthorId = ResolveRestrictedAuthorId(author, remoteBook);
            var match = _fileMatchingService.MatchFilesToLibraryAsync(discovered, restrictToAuthorId, matchCtx).GetAwaiter().GetResult();

            var decisions = new List<ImportDecision<LocalBook>>();
            var booksById = new Dictionary<int, Book>();
            var authorsById = new Dictionary<int, Author>();
            foreach (var fm in match.MatchedFiles)
            {
                var decision = CreateDecisionForMatch(fm, new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase)
                {
                    [fm.File.Path] = tags
                }, booksById, authorsById);
                if (decision != null)
                {
                    decisions.Add(decision);
                }
            }

            var rematchedPath = false;
            if ((downloadClientItem == null || allowAutomaticAuthorImport) && match.UnmatchedFiles.Length == 1)
            {
                var suggestion = match.UnmatchedFiles[0].PotentialAuthors?.FirstOrDefault();
                if (suggestion != null && !string.IsNullOrWhiteSpace(suggestion.ProviderId))
                {
                    if (TryBuildDownloadedImportMonitoringConfig(suggestion.AuthorName, new[] { fileInfo.FullName }, out var config, out var configError, requireDefaultRoots: downloadClientItem != null || requireDefaultRootFolderForMissingAuthors))
                    {
                        try
                        {
                            var addedAuthor = _authorLibraryService.AddAuthorAsync(suggestion.ProviderId, config).GetAwaiter().GetResult();
                            if (addedAuthor != null && addedAuthor.Id > 0)
                            {
                                var rematchCtx = CreateStrictMatchingContext(false, targetBookIds);
                                var rematch = _fileMatchingService.MatchFilesToLibraryAsync(discovered, addedAuthor.Id, rematchCtx)
                                    .GetAwaiter().GetResult();

                                foreach (var fm in rematch.MatchedFiles)
                                {
                                    var decision = CreateDecisionForMatch(fm, new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase)
                                    {
                                        [fm.File.Path] = tags
                                    }, booksById, authorsById);
                                    if (decision != null)
                                    {
                                        decisions.Add(decision);
                                        rematchedPath = true;
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Error(ex, "[DOWNLOAD-IMPORT] Failed to auto-add author '{0}' ({1}) during downloaded import",
                                suggestion.AuthorName ?? "<unknown>", suggestion.ProviderId ?? "<unknown>");
                        }
                    }
                    else
                    {
                        _logger.Warn("[DOWNLOAD-IMPORT] Cannot auto-add author '{0}' ({1}): {2}",
                            suggestion.AuthorName ?? "<unknown>", suggestion.ProviderId ?? "<unknown>", configError);
                        SetAutoAddRejectionReason(match.UnmatchedFiles, suggestion.AuthorName, configError);
                    }
                }
            }

            foreach (var um in match.UnmatchedFiles)
            {
                if (rematchedPath)
                {
                    continue;
                }

                var local = new LocalBook { Path = um.File.Path };
                var reason = BuildUnmatchedRejectionReason(um);
                decisions.Add(new ImportDecision<LocalBook>(local, new Rejection(reason)));
            }

            ApplyTrackedReleaseEvidence(decisions, remoteBook, downloadClientItem);
            EnforceTrackedDownloadTargetBooks(decisions, remoteBook, downloadClientItem);
            EnforceTrackedDownloadExpectedEditions(decisions, downloadClientItem);

            var effectiveMode = importMode;
            if (importMode == ImportMode.Auto && downloadClientItem != null)
            {
                effectiveMode = downloadClientItem.CanMoveFiles ? ImportMode.Move : ImportMode.Copy;
            }

            return _importApprovedBooks.Import(decisions, replaceExisting: true, downloadClientItem: downloadClientItem, importMode: effectiveMode);
        }

        private ImportDecision<LocalBook> CreateDecisionForMatch(FileMatch match, Dictionary<string, Dictionary<string, List<string>>> tagsByPath, IDictionary<int, Book> booksById, IDictionary<int, Author> authorsById)
        {
            if (match == null)
            {
                return null;
            }

            var book = GetBookForMatch(match.BookId, booksById);
            var author = GetAuthorForMatch(match.AuthorId, authorsById);
            if (book == null || author == null)
            {
                return null;
            }

            // Ensure Book has the correct Author relationship for naming/token evaluation
            // (BookService.GetBook hydrates Author, but we want to keep it consistent with the match).
            book.Author = author;

            // Prefer a hydrated edition (from GetBook -> GetEditionsByBook join) so Edition.Book is populated.
            // This fixes missing series/year tokens during download import (rename later worked because it used hydrated objects).
            var edition = book.Editions?.FirstOrDefault(e => e.Id == match.EditionId) ??
                          _editionService.GetEdition(match.EditionId);

            // Safety: Match.EditionId should belong to Match.BookId. If it doesn't, do not attach the wrong edition
            // to this book for naming/token evaluation; fall back to a safe edition selection within this book.
            if (edition != null && edition.BookId != book.Id)
            {
                _logger.Debug("[DOWNLOAD-IMPORT] EditionId {0} belongs to BookId {1} (expected {2}); falling back to edition selection within the matched book.",
                    edition.Id, edition.BookId, book.Id);
                edition = null;
            }

            if (edition == null)
            {
                try
                {
                    var all = _editionService.GetEditionsByBook(book.Id) ?? new List<Edition>();
                    edition = all.FirstOrDefault(e => e.Monitored);
                    if (edition == null)
                    {
                        var ext = Path.GetExtension(match.File.Path);
                        var isAudio = MediaFileExtensions.AudioExtensions.Contains(ext);
                        // Prefer matching format, fall back within this book instance.
                        // Book.MediaType is authoritative; editions may include fallbacks when API lacks format-specific data.
                        if (isAudio)
                        {
                            // Audiobook: prefer audio (2) → ebook (3) → print (1) → anything
                            edition = all.FirstOrDefault(e => e.ReadingFormatId == 2)
                                ?? all.FirstOrDefault(e => e.ReadingFormatId == 3)
                                ?? all.FirstOrDefault(e => e.ReadingFormatId == 1)
                                ?? all.FirstOrDefault();
                        }
                        else
                        {
                            // Ebook: prefer ebook (3) → print (1) → anything (audiobook as last resort)
                            edition = all.FirstOrDefault(e => e.ReadingFormatId == 3)
                                ?? all.FirstOrDefault(e => e.ReadingFormatId == 1)
                                ?? all.FirstOrDefault();
                        }
                    }
                }
                catch
                {
                    // Fall through and let edition remain null
                }
            }

            if (edition == null)
            {
                return null;
            }

            // Naming tokens (Series/ReleaseYearFirst) depend on Edition.Book, but GetEdition(id) does not hydrate it.
            // Ensure it's available for organizer logic.
            if (edition.Book == null)
            {
                edition.Book = book;
            }
            else if (edition.Book.Author == null)
            {
                edition.Book.Author = author;
            }

            tagsByPath ??= new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase);
            tagsByPath.TryGetValue(match.File.Path, out var tags);
            tags ??= new Dictionary<string, List<string>>();

            var local = new LocalBook
            {
                Path = match.File.Path,
                Book = book,
                Author = author,
                Edition = edition,
                Size = _diskProvider.GetFileSize(match.File.Path),
                Modified = _diskProvider.FileGetLastWrite(match.File.Path),
                DurationSeconds = match.File.DurationSeconds,
                Quality = GuessQualityByExtension(match.File.Path),
                RawTags = new RawFileTags { AllTags = tags },
                MatchProvenance = match.Provenance
            };

            return new ImportDecision<LocalBook>(local);
        }

        private static void ApplyTrackedReleaseEvidence(
            IEnumerable<ImportDecision<LocalBook>> decisions,
            RemoteBook remoteBook,
            DownloadClientItem downloadClientItem)
        {
            if (downloadClientItem == null || remoteBook == null)
            {
                return;
            }

            var parsed = remoteBook.ParsedBookInfo;
            var release = remoteBook.Release;
            var sceneName = release?.Title;
            if (string.IsNullOrWhiteSpace(sceneName))
            {
                sceneName = downloadClientItem.Title;
            }

            if (string.IsNullOrWhiteSpace(sceneName))
            {
                sceneName = parsed?.ReleaseTitle;
            }

            sceneName = string.IsNullOrWhiteSpace(sceneName)
                ? null
                : NzbDrone.Core.Parser.Parser.RemoveFileExtension(sceneName);

            foreach (var decision in decisions ?? Enumerable.Empty<ImportDecision<LocalBook>>())
            {
                var localBook = decision?.Item;
                if (localBook == null)
                {
                    continue;
                }

                localBook.DownloadClientBookInfo = parsed;
                localBook.SceneSource = !string.IsNullOrWhiteSpace(sceneName);
                localBook.SceneName = sceneName ?? localBook.SceneName;
                if (!string.IsNullOrWhiteSpace(parsed?.ReleaseGroup))
                {
                    localBook.ReleaseGroup = parsed.ReleaseGroup;
                }

                localBook.IndexerFlags = release?.IndexerFlags ?? localBook.IndexerFlags;
                localBook.IsGraphicAudio = release?.IsGraphicAudio == true || parsed?.IsGraphicAudio == true;
                localBook.AudioProductionType = !string.IsNullOrWhiteSpace(parsed?.AudioProductionType)
                    ? parsed.AudioProductionType
                    : localBook.AudioProductionType;
                localBook.Narrator = !string.IsNullOrWhiteSpace(release?.Narrator)
                    ? release.Narrator
                    : !string.IsNullOrWhiteSpace(parsed?.Narrator) ? parsed.Narrator : localBook.Narrator;
            }
        }

        private Book GetBookForMatch(int bookId, IDictionary<int, Book> booksById)
        {
            if (booksById != null && booksById.TryGetValue(bookId, out var cachedBook))
            {
                return cachedBook;
            }

            var book = _bookService.GetBook(bookId);
            if (book != null && booksById != null)
            {
                booksById[bookId] = book;
            }

            return book;
        }

        private Author GetAuthorForMatch(int authorId, IDictionary<int, Author> authorsById)
        {
            if (authorsById != null && authorsById.TryGetValue(authorId, out var cachedAuthor))
            {
                return cachedAuthor;
            }

            var author = _authorService.GetAuthor(authorId);
            if (author != null && authorsById != null)
            {
                authorsById[authorId] = author;
            }

            return author;
        }

        private void EnforceTrackedDownloadTargetBooks(List<ImportDecision<LocalBook>> decisions, RemoteBook remoteBook, DownloadClientItem downloadClientItem)
        {
            if (downloadClientItem == null || decisions == null || decisions.Count == 0)
            {
                return;
            }

            var expectedBooks = remoteBook?.Books?
                .Where(book => book != null && book.Id > 0)
                .ToList();

            if (expectedBooks == null || expectedBooks.Count == 0)
            {
                return;
            }

            RetargetSameWorkMatchesToGrabbedBook(decisions, expectedBooks, remoteBook?.Author, downloadClientItem);

            var expectedBookIds = expectedBooks.Select(book => book.Id).ToHashSet();
            var expectedLabel = FormatBookListLabel(expectedBooks);

            foreach (var decision in decisions.Where(decision => decision?.Approved == true))
            {
                var matchedBook = decision.Item?.Book;
                if (matchedBook == null || !expectedBookIds.Contains(matchedBook.Id))
                {
                    decision.Reject(new Rejection($"Completed download was grabbed for {expectedLabel}, but import matched {FormatBookLabel(matchedBook)}."));
                }
            }
        }

        private void RetargetSameWorkMatchesToGrabbedBook(List<ImportDecision<LocalBook>> decisions, List<Book> expectedBooks, Author expectedAuthor, DownloadClientItem downloadClientItem)
        {
            if (expectedBooks?.Count != 1)
            {
                return;
            }

            var targetBook = expectedBooks[0];
            if (targetBook == null || targetBook.Id <= 0)
            {
                return;
            }

            var siblingDecisions = decisions
                .Where(decision => decision?.Approved == true)
                .Where(decision =>
                {
                    var matchedBook = decision.Item?.Book;
                    return matchedBook?.Id > 0 && matchedBook.Id != targetBook.Id;
                })
                .ToList();

            if (siblingDecisions.Count == 0)
            {
                return;
            }

            targetBook = HydrateExpectedBook(targetBook);

            foreach (var decision in siblingDecisions.Where(decision => decision?.Approved == true))
            {
                var localBook = decision.Item;
                var matchedBook = localBook?.Book;
                if (!SharesRetargetableRowIdentity(targetBook, matchedBook))
                {
                    continue;
                }

                var matchedEdition = localBook.Edition;

                // Two independent same-book signals are accepted here:
                //
                //  1. Work-level provider IDs intersect (hc/gr/ol work IDs). This is the original signal and
                //     covers duplicate rows catalogued through the same metadata provider.
                //
                //  2. The matched edition itself also exists under the grabbed book row, identified by an
                //     edition-level provider ID (ISBN-10/13, ASIN/Audible ASIN, or a Goodreads/Hardcover/
                //     OpenLibrary/Google Books edition ID). Duplicate rows created by author-bibliography
                //     re-discovery are frequently sourced from a *different* provider than the row that was
                //     grabbed, so their work-level ID sets never intersect even though they describe the same
                //     title. Signal 2 recognises those without weakening the guarantee: edition-level provider
                //     IDs are globally unique per physical edition, so a shared one means both rows carry the
                //     very same edition. Combined with the same-author and same-media-type guards in
                //     SharesRetargetableRowIdentity, this cannot collide two different books that merely share
                //     a title -- which is exactly why title similarity is deliberately NOT used as a signal.
                //
                // Both signals still have to land on a concrete equivalent edition below before anything is
                // retargeted, so a match on either one can only ever move the file to an edition the grabbed
                // book row already owns.
                var sameWorkProviderId = WorkIdMatcher.WorkProviderIdMatches(targetBook, matchedBook);
                var targetEdition = FindEquivalentEditionForTargetBook(targetBook, matchedEdition);

                if (targetEdition == null)
                {
                    if (!sameWorkProviderId)
                    {
                        // Neither signal fired: nothing establishes that these two rows are the same book.
                        // Leave the decision untouched so the caller's expected-book check rejects it.
                        continue;
                    }

                    decision.Reject(new Rejection(
                        $"Completed download was grabbed for {FormatBookLabel(targetBook)} and matched same-work sibling {FormatBookLabel(matchedBook)}, but no equivalent edition exists under the grabbed book row. Refresh metadata and retry."));
                    continue;
                }


                // The grabbed BookId owns the in-flight download. Flexible edition matching may
                // identify a same-work sibling row, but import must fill the row that triggered
                // the grab. Do not clone or fake editions here: only retarget when the equivalent
                // edition already exists under the grabbed book row. Strict edition validation
                // still runs after this and can reject the retargeted decision.
                localBook.Author = ResolveTargetAuthor(targetBook, matchedBook, expectedAuthor, localBook.Author);
                localBook.Book = targetBook;
                localBook.Edition = targetEdition;

                if (targetEdition.Book == null)
                {
                    targetEdition.Book = targetBook;
                }

                _logger.Debug("Retargeting completed download '{0}' from same-work sibling {1} to grabbed book {2} using edition {3}",
                    downloadClientItem?.Title ?? downloadClientItem?.DownloadId ?? "<unknown>",
                    FormatBookLabel(matchedBook),
                    FormatBookLabel(targetBook),
                    FormatEditionLabel(targetEdition));
            }
        }

        private Book HydrateExpectedBook(Book book)
        {
            if (book == null || book.Id <= 0)
            {
                return book;
            }

            try
            {
                var hydrated = _bookService.GetBook(book.Id);
                return hydrated?.Id == book.Id ? hydrated : book;
            }
            catch (NzbDrone.Core.Datastore.ModelNotFoundException)
            {
                return book;
            }
        }

        /// <summary>
        /// Baseline guards every retarget must clear, independent of how "same book" is established:
        /// two distinct persisted rows, the same media type, and the same (known) author. These are
        /// hard requirements -- retargeting across authors or across media types would misfile the download.
        /// </summary>
        private static bool SharesRetargetableRowIdentity(Book targetBook, Book matchedBook)
        {
            if (targetBook == null ||
                matchedBook == null ||
                targetBook.Id <= 0 ||
                matchedBook.Id <= 0 ||
                targetBook.Id == matchedBook.Id)
            {
                return false;
            }

            if (targetBook.MediaType != matchedBook.MediaType)
            {
                return false;
            }

            var targetAuthorId = GetAuthorId(targetBook);
            var matchedAuthorId = GetAuthorId(matchedBook);
            if (targetAuthorId <= 0 || matchedAuthorId <= 0 || targetAuthorId != matchedAuthorId)
            {
                return false;
            }

            return true;
        }

        private Edition FindEquivalentEditionForTargetBook(Book targetBook, Edition matchedEdition)
        {
            if (targetBook == null || targetBook.Id <= 0 || matchedEdition == null)
            {
                return null;
            }

            if (matchedEdition.BookId == targetBook.Id)
            {
                return matchedEdition;
            }

            var editions = targetBook.Editions;
            if (editions == null || editions.Count == 0)
            {
                editions = _editionService.GetEditionsByBook(targetBook.Id) ?? new List<Edition>();
            }

            return editions.FirstOrDefault(edition => BookEditionIdentity.EditionsMatch(edition, matchedEdition));
        }

        private static Author ResolveTargetAuthor(Book targetBook, Book matchedBook, Author expectedAuthor, Author localAuthor)
        {
            if (targetBook?.Author?.Id > 0)
            {
                return targetBook.Author;
            }

            if (expectedAuthor?.Id > 0 && expectedAuthor.Id == targetBook?.AuthorId)
            {
                return expectedAuthor;
            }

            if (matchedBook?.Author?.Id > 0 && matchedBook.Author.Id == targetBook?.AuthorId)
            {
                return matchedBook.Author;
            }

            return localAuthor;
        }

        private static int GetAuthorId(Book book)
        {
            if (book == null)
            {
                return 0;
            }

            return book.AuthorId > 0 ? book.AuthorId : book.Author?.Id ?? 0;
        }

        private static string FormatBookListLabel(IEnumerable<Book> books)
        {
            var labels = books?
                .Where(book => book != null)
                .Select(FormatBookLabel)
                .Distinct()
                .ToList() ?? new List<string>();

            return labels.Count > 0 ? string.Join(", ", labels) : "the grabbed book";
        }

        private static string FormatBookLabel(Book book)
        {
            if (book == null)
            {
                return "an unexpected book";
            }

            var label = !string.IsNullOrWhiteSpace(book.Title) ? $"'{book.Title}'" : $"BookId {book.Id}";
            var details = new List<string>();

            if (book.Id > 0)
            {
                details.Add($"BookId {book.Id}");
            }

            var workId = BookEditionIdentity.GetCanonicalWorkProviderIds(book).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(workId))
            {
                details.Add($"work {workId}");
            }

            return details.Count > 0 ? $"{label} ({string.Join(", ", details)})" : label;
        }

        private static string FormatEditionLabel(Edition edition)
        {
            if (edition == null)
            {
                return "unknown edition";
            }

            return !string.IsNullOrWhiteSpace(edition.Title)
                ? $"'{edition.Title}' (EditionId {edition.Id})"
                : $"EditionId {edition.Id}";
        }

        private void EnforceTrackedDownloadExpectedEditions(List<ImportDecision<LocalBook>> decisions, DownloadClientItem downloadClientItem)
        {
            if (downloadClientItem == null || decisions == null || decisions.Count == 0)
            {
                return;
            }

            var expectedEditionCache = new Dictionary<int, Edition>();

            foreach (var decision in decisions.Where(decision => decision?.Approved == true))
            {
                var localBook = decision.Item;
                var matchedBook = localBook?.Book;
                var matchedEdition = localBook?.Edition;

                if (matchedBook == null || matchedEdition == null || matchedBook.Id <= 0)
                {
                    continue;
                }

                if (!expectedEditionCache.TryGetValue(matchedBook.Id, out var expectedEdition))
                {
                    expectedEdition = ResolveExpectedTrackedEdition(matchedBook, downloadClientItem);
                    expectedEditionCache[matchedBook.Id] = expectedEdition;
                }

                if (expectedEdition == null || expectedEdition.Id <= 0 || matchedEdition.Id == expectedEdition.Id)
                {
                    continue;
                }

                var strictEditionMatch = !matchedBook.AnyEditionOk || expectedEdition.ManualAdd;
                if (!strictEditionMatch)
                {
                    // Upstream arrs use grabbed release identity as a guardrail, not as a
                    // post-match mutator. For flexible books, trust the matcher-selected
                    // edition instead of silently reverting to grab history/monitored state.
                    continue;
                }

                decision.Reject(new Rejection(
                    $"Completed download was grabbed for edition '{expectedEdition.Title}', but import matched edition '{matchedEdition.Title}'. Chaptarr could not verify the expected edition."));
            }
        }

        private Edition ResolveExpectedTrackedEdition(Book book, DownloadClientItem downloadClientItem)
        {
            return TrackedMultipartAudioRepairHelper.ResolveExpectedTrackedEdition(
                book,
                downloadClientItem?.DownloadId,
                _historyService,
                _editionService);
        }

        private List<ImportDecision<LocalBook>> RepairTrackedMultipartAudioDecisions(
            List<ImportDecision<LocalBook>> decisions,
            RemoteBook remoteBook,
            DownloadClientItem downloadClientItem,
            Author fallbackAuthor,
            string contextLabel)
        {
            if (downloadClientItem == null || decisions == null || decisions.Count < 2)
            {
                return decisions;
            }

            var targetBookIds = remoteBook?.Books?
                .Where(book => book != null && book.Id > 0)
                .Select(book => book.Id)
                .Distinct()
                .ToList();

            if (targetBookIds?.Count != 1)
            {
                return decisions;
            }

            var targetBook = _bookService.GetBook(targetBookIds[0]);
            if (targetBook == null)
            {
                return decisions;
            }

            var targetAuthor = targetBook.Author ?? fallbackAuthor ?? remoteBook?.Author;
            var preferredEdition = ResolveExpectedTrackedEdition(targetBook, downloadClientItem);

            return TrackedMultipartAudioRepairHelper.RepairTrackedSingleBookAudioDecisions(
                decisions,
                targetBook,
                targetAuthor,
                preferredEdition,
                _editionService,
                _logger,
                contextLabel);
        }

        private static bool HasEmbeddedMetadataEvidence(LocalBook localBook)
        {
            var rawTags = localBook?.RawTags?.AllTags;
            if (rawTags == null || rawTags.Count == 0)
            {
                return false;
            }

            var canonicalTags = rawTags.ToDictionary(
                kv => kv.Key,
                kv => kv.Value?
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList() ?? new List<string>(),
                StringComparer.OrdinalIgnoreCase);

            TagCanonicalizer.AddCanonicalKeys(canonicalTags);

            return canonicalTags.Any(tag =>
                IsEmbeddedIdentityEvidenceKey(tag.Key) &&
                tag.Value != null &&
                tag.Value.Any(value => !string.IsNullOrWhiteSpace(value)));
        }

        private static bool IsEmbeddedIdentityEvidenceKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            if (EmbeddedIdentityEvidenceKeys.Contains(key))
            {
                return true;
            }

            var lastSeparator = key.LastIndexOf(':');
            return lastSeparator > -1 &&
                   lastSeparator < key.Length - 1 &&
                   EmbeddedIdentityEvidenceKeys.Contains(key.Substring(lastSeparator + 1));
        }

        private static void HydrateEditionForImport(LocalBook localBook, Edition edition)
        {
            if (localBook == null || edition == null)
            {
                return;
            }

            localBook.Edition = edition;

            if (localBook.Book != null)
            {
                edition.Book ??= localBook.Book;
            }

            if (edition.Book?.Author == null && localBook.Author != null)
            {
                edition.Book.Author = localBook.Author;
            }
        }

        private bool TryBuildDownloadedImportMonitoringConfig(string authorName, IEnumerable<string> filePaths, out MonitoringConfig config, out string error, bool requireDefaultRoots = false)
        {
            var request = new SuggestedAuthorImportConfigRequest
            {
                AuthorName = authorName,
                FilePaths = filePaths,
                QueueIfUnavailable = false,
                RequestedBy = "DownloadedBooksImportService",
                UseConfiguredDefaultRoots = true,
                AllowAmbiguousRootFallback = !requireDefaultRoots,
                IncludeRootDefaultTags = false
            };

            return SuggestedAuthorImportCoordinator.TryBuildMonitoringConfig(
                request,
                _rootFolderService,
                _configService,
                null,
                out config,
                out error);
        }

        private string BuildUnmatchedRejectionReason(UnmatchedFile unmatched)
        {
            if (IsAutoAddRejectionReason(unmatched?.Reason))
            {
                return unmatched.Reason;
            }

            if (unmatched?.PotentialAuthors?.Any() == true)
            {
                var s = unmatched.PotentialAuthors.First();
                if (!string.IsNullOrWhiteSpace(s.AuthorName) && !string.IsNullOrWhiteSpace(s.ProviderId))
                {
                    return $"No match in local library. Suggested author: {s.AuthorName} ({s.ProviderId}).";
                }
            }

            return unmatched?.Reason ?? "No match in local library";
        }

        private static void SetAutoAddRejectionReason(IEnumerable<UnmatchedFile> unmatchedFiles, string authorName, string reason)
        {
            if (unmatchedFiles == null)
            {
                return;
            }

            var authorLabel = !authorName.IsNullOrWhiteSpace() ? $" '{authorName}'" : string.Empty;
            var detail = !reason.IsNullOrWhiteSpace() ? reason : "the destination root folder could not be resolved";
            var rejectionReason = $"Cannot add suggested author{authorLabel}: {detail}.";

            foreach (var unmatched in unmatchedFiles)
            {
                if (unmatched != null)
                {
                    unmatched.Reason = rejectionReason;
                }
            }
        }

        private static bool IsAutoAddRejectionReason(string reason)
        {
            return !reason.IsNullOrWhiteSpace() &&
                   reason.StartsWith("Cannot add suggested author", StringComparison.OrdinalIgnoreCase);
        }

        private QualityModel GuessQualityByExtension(string filePath)
        {
            try
            {
                var ext = Path.GetExtension(filePath);
                var mediaInfo = MediaFileExtensions.IsMatroskaAudioExtension(ext)
                    ? _mediaInfoExtractor.ExtractMediaInfo(filePath)
                    : null;
                var q = MediaFileExtensions.GetQualityForExtension(ext, mediaInfo);
                return new QualityModel { Quality = q };
            }
            catch { return new QualityModel { Quality = Qualities.Quality.Unknown }; }
        }

        private void ImportExtraFiles(List<ImportResult> importedBooks, IDirectoryInfo directoryInfo, bool moveFiles, List<IFileInfo> scopedFiles = null)
        {
            var allFiles = scopedFiles ?? _diskProvider.GetFileInfos(directoryInfo.FullName, false);
            var wantedExtensions = _configService.ExtraFileExtensions.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                                                     .Select(e => e.Trim(' ', '.'))
                                                                     .Select(e => $".{e}")
                                                                     .ToList();

            var extraFiles = allFiles.Where(f => !MediaFileExtensions.AllExtensions.Contains(f.Extension) &&
                                             wantedExtensions.Any(ext => f.Extension.Equals(ext, StringComparison.OrdinalIgnoreCase)))
                                     .ToList();

            foreach (var result in importedBooks.Where(r => r.Result == ImportResultType.Imported))
            {
                if (result.ImportDecision?.Item?.Path == null) continue;

                var bookPath = result.ImportDecision.Item.Path;
                var destinationDirectory = Path.GetDirectoryName(bookPath);
                var sourceFileName = Path.GetFileNameWithoutExtension(bookPath);

                foreach (var extraFile in extraFiles)
                {
                    try
                    {
                        var extraFileName = Path.GetFileNameWithoutExtension(extraFile.Name);

                        if (extraFileName.StartsWith(sourceFileName, StringComparison.OrdinalIgnoreCase) ||
                            sourceFileName.StartsWith(extraFileName, StringComparison.OrdinalIgnoreCase))
                        {
                            var destinationPath = Path.Combine(destinationDirectory, extraFile.Name);

                            if (!_diskProvider.FileExists(destinationPath))
                            {
                                if (moveFiles)
                                {
                                    _diskProvider.MoveFile(extraFile.FullName, destinationPath);
                                    _logger.Debug("Moved extra file: {0} to {1}", extraFile.Name, destinationPath);
                                }
                                else
                                {
                                    _diskProvider.CopyFile(extraFile.FullName, destinationPath);
                                    _logger.Debug("Copied extra file: {0} to {1}", extraFile.Name, destinationPath);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn(ex, "Failed to import extra file: {0}", extraFile.Name);
                    }
                }
            }
        }

        public bool ShouldDeleteFolder(IDirectoryInfo directoryInfo)
        {
            try
            {
                var bookFiles = _diskScanService.GetBookFiles(directoryInfo.FullName);
                var rarFiles = _diskProvider.GetFiles(directoryInfo.FullName, true)
                    .Where(f => Path.GetExtension(f).Equals(".rar", StringComparison.OrdinalIgnoreCase));

                foreach (var bookFile in bookFiles)
                {
                    var bookParseResult = Parser.Parser.ParseBookTitle(bookFile.Name);
                    if (bookParseResult == null)
                    {
                        _logger.Warn("Unable to parse file on import: [{0}]", bookFile);
                        return false;
                    }
                    _logger.Warn("Book file detected: [{0}]", bookFile);
                    return false;
                }

                if (rarFiles.Any(f => _diskProvider.GetFileSize(f) > 10.Megabytes()))
                {
                    _logger.Warn("RAR file detected, will require manual cleanup");
                    return false;
                }

                return true;
            }
            catch (DirectoryNotFoundException e)
            {
                _logger.Debug(e, "Folder {0} has already been removed", directoryInfo.FullName);
                return false;
            }
            catch (Exception e)
            {
                _logger.Debug(e, "Unable to determine whether folder {0} should be removed", directoryInfo.FullName);
                return false;
            }
        }

        private ImportResult FileIsLockedResult(string filePath)
        {
            _logger.Debug("[{0}] is currently locked by another process, skipping", filePath);
            var localBook = new LocalBook { Path = filePath };
            var rejection = new Rejection("Locked file, try again later");
            return new ImportResult(new ImportDecision<LocalBook>(localBook, rejection), "Locked file");
        }

        private ImportResult InaccessiblePathResult(string path)
        {
            var reason = $"Download path is not accessible to Chaptarr: {path}. Check the download client path, remote path mapping, and container volume mappings.";
            var localBook = new LocalBook { Path = path };
            var rejection = new Rejection(reason);
            return new ImportResult(new ImportDecision<LocalBook>(localBook, rejection), reason);
        }

        private ImportResult NoSupportedMediaFilesResult(string path, IReadOnlyCollection<IFileInfo> visibleFiles = null)
        {
            var observedFilesMessage = BuildObservedUnsupportedFilesMessage(path, visibleFiles);
            var reason = $"No supported audio or ebook files were found in {path}.{observedFilesMessage} Check that the download contains supported audio or ebook files and that Chaptarr can read the completed download folder.";
            var localBook = new LocalBook { Path = path };
            var rejection = new Rejection(reason);
            return new ImportResult(new ImportDecision<LocalBook>(localBook, rejection), reason);
        }

        private static string BuildObservedUnsupportedFilesMessage(string basePath, IReadOnlyCollection<IFileInfo> visibleFiles)
        {
            if (visibleFiles == null || visibleFiles.Count == 0)
            {
                return " Chaptarr did not see any files there.";
            }

            var unsupportedFiles = visibleFiles
                .Where(f => f != null && !MediaFileExtensions.AllExtensions.Contains(f.Extension))
                .OrderByDescending(f => f.Length)
                .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!unsupportedFiles.Any())
            {
                return string.Empty;
            }

            var listedFiles = unsupportedFiles
                .Take(5)
                .Select(f => FormatObservedFile(basePath, f))
                .ToList();

            var moreCount = unsupportedFiles.Count - listedFiles.Count;
            var moreMessage = moreCount > 0 ? $" and {moreCount} more" : string.Empty;

            var extensions = unsupportedFiles
                .Select(f => string.IsNullOrWhiteSpace(f.Extension) ? "(no extension)" : f.Extension)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return $" Chaptarr did see unsupported file(s): {string.Join(", ", listedFiles)}{moreMessage}. Unsupported extension(s): {string.Join(", ", extensions)}.";
        }

        private static string FormatObservedFile(string basePath, IFileInfo file)
        {
            if (file == null)
            {
                return string.Empty;
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(basePath) && !string.IsNullOrWhiteSpace(file.FullName))
                {
                    var relativePath = Path.GetRelativePath(basePath, file.FullName);
                    if (!string.IsNullOrWhiteSpace(relativePath) &&
                        relativePath != "." &&
                        !relativePath.StartsWith("..", StringComparison.Ordinal))
                    {
                        return relativePath;
                    }
                }
            }
            catch
            {
                // Fall back to the filename below if the paths cannot be relativized on this platform.
            }

            return string.IsNullOrWhiteSpace(file.Name) ? file.FullName : file.Name;
        }

        private void LogInaccessiblePathError(string path)
        {
            if (_runtimeInfo.IsWindowsService)
            {
                var mounts = _diskProvider.GetMounts();
                var mount = mounts.FirstOrDefault(m => m.RootDirectory == Path.GetPathRoot(path));

                if (mount == null)
                {
                    _logger.Error("Import failed, path does not exist or is not accessible by Chaptarr: {0}. Unable to find a volume mounted for the path.", path);
                    return;
                }

                if (mount.DriveType == DriveType.Network)
                {
                    _logger.Error("Import failed, path does not exist or is not accessible by Chaptarr: {0}. It's recommended to avoid mapped network drives when running as a Windows service.", path);
                    return;
                }
            }

            if (OsInfo.IsWindows && path.StartsWith(@"\\"))
            {
                _logger.Error("Import failed, path does not exist or is not accessible by Chaptarr: {0}. Ensure the user running Chaptarr has access to the network share", path);
                return;
            }

            _logger.Error("Import failed, path does not exist or is not accessible by Chaptarr: {0}. Ensure the path exists and the user running Chaptarr has the correct permissions", path);
        }

        private static MatchingContext CreateStrictMatchingContext(bool allowV5Identification, List<int> targetBookIds, bool allowPathFallback = false)
        {
            return MatchingContextPresets.ForDownloaded(allowV5Identification, targetBookIds, allowPathFallback);
        }

        private static int? ResolveRestrictedAuthorId(Author author, RemoteBook remoteBook)
        {
            if (author?.Id > 0)
            {
                return author.Id;
            }

            var distinctAuthorIds = remoteBook?.Books?
                .Where(book => book != null && book.AuthorId > 0)
                .Select(book => book.AuthorId)
                .Distinct()
                .ToList();

            if (distinctAuthorIds?.Count == 1)
            {
                return distinctAuthorIds[0];
            }

            return null;
        }

        private static bool ShouldAllowTrackedDownloadPathFallback(DownloadClientItem downloadClientItem, int? restrictToAuthorId, List<int> targetBookIds)
        {
            return downloadClientItem != null &&
                   restrictToAuthorId.HasValue &&
                   targetBookIds?.Count == 1;
        }

        public List<ImportResult> ProcessFolder(string path, ImportMode importMode = ImportMode.Auto, Author author = null, DownloadClientItem downloadClientItem = null, RemoteBook remoteBook = null, bool requireDefaultRootFolderForMissingAuthors = false)
        {
            if (!_diskProvider.FolderExists(path))
            {
                _logger.Error("Folder {0} doesn't exist", path);
                return new List<ImportResult>();
            }

            var directoryInfo = _diskProvider.GetDirectoryInfo(path);
            return ProcessFolder(directoryInfo, importMode, author, downloadClientItem, remoteBook, null, requireDefaultRootFolderForMissingAuthors);
        }

        public List<ImportResult> ProcessFile(string path, ImportMode importMode = ImportMode.Auto, Author author = null, DownloadClientItem downloadClientItem = null, RemoteBook remoteBook = null, bool requireDefaultRootFolderForMissingAuthors = false)
        {
            if (!_diskProvider.FileExists(path))
            {
                _logger.Error("File {0} doesn't exist", path);
                return new List<ImportResult>();
            }

            var fileInfo = _diskProvider.GetFileInfo(path);
            return ProcessFile(fileInfo, importMode, author, downloadClientItem, remoteBook, requireDefaultRootFolderForMissingAuthors: requireDefaultRootFolderForMissingAuthors);
        }
    }
}
