using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using NLog;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Authors;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Manual;
using NzbDrone.Core.MediaFiles.BookImport.Services;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.MediaFiles.Commands
{
    public class RetryUnmappedMatchCommandHandler : IExecute<RetryUnmappedMatchCommand>
    {
        private readonly IMediaFileService _mediaFileService;
        private readonly IMetadataTagService _metadataTagService;
        private readonly IDiskProvider _diskProvider;
        private readonly IIngestQueueRepository _ingestQueueRepository;
        private readonly IFileMatchingService _fileMatching;
        private readonly IBookImportService _bookImport;
        private readonly IBookService _bookService;
        private readonly IEditionService _editionService;
        private readonly IBookUnitDestinationService _unitDestination;
        private readonly IAuthorLibraryService _authorLibraryService;
        private readonly IPendingAuthorImportService _pendingAuthorImportService;
        private readonly IRootFolderService _rootFolderService;
        private readonly IAuthorService _authorService;
        private readonly Logger _logger;

        public RetryUnmappedMatchCommandHandler(
            IMediaFileService mediaFileService,
            IMetadataTagService metadataTagService,
            IDiskProvider diskProvider,
            IIngestQueueRepository ingestQueueRepository,
            IFileMatchingService fileMatching,
            IBookImportService bookImport,
            IBookService bookService,
            IEditionService editionService,
            IBookUnitDestinationService unitDestination,
            IAuthorLibraryService authorLibraryService,
            IPendingAuthorImportService pendingAuthorImportService,
            IRootFolderService rootFolderService,
            IAuthorService authorService,
            Logger logger)
        {
            _mediaFileService = mediaFileService;
            _metadataTagService = metadataTagService;
            _diskProvider = diskProvider;
            _ingestQueueRepository = ingestQueueRepository;
            _fileMatching = fileMatching;
            _bookImport = bookImport;
            _bookService = bookService;
            _editionService = editionService;
            _unitDestination = unitDestination;
            _authorLibraryService = authorLibraryService;
            _pendingAuthorImportService = pendingAuthorImportService;
            _rootFolderService = rootFolderService;
            _authorService = authorService;
            _logger = logger;
        }

        public void Execute(RetryUnmappedMatchCommand message)
        {
            var selection = message.UnmappedFiles ?? new UnmappedFilesSelection { Scope = "all" };
            var files = ResolveUnmappedFiles(selection, message.MediaType);

            if (!files.Any())
            {
                _logger.Debug("[UNMAPPED-RETRY] No currently unmapped files matched scope '{0}' and mediaType '{1}'",
                    selection.Scope,
                    message.MediaType ?? "all");
                return;
            }

            var paths = files.Select(file => file.Path).Distinct(PathEqualityComparer.Instance).ToList();
            var purged = _ingestQueueRepository.PurgePaths(paths);
            _logger.Debug("[UNMAPPED-RETRY] Purged {0} stale staging rows before retrying {1} unmapped files", purged, files.Count);

            var byPath = files
                .Where(file => !string.IsNullOrWhiteSpace(file.Path))
                .GroupBy(file => file.Path, PathEqualityComparer.Instance)
                .ToDictionary(group => group.Key, group => group.First(), PathEqualityComparer.Instance);

            var discovered = new List<DiscoveredFileWithMetadata>();
            var skipped = 0;

            foreach (var file in files)
            {
                var metadata = BuildDiscoveredFile(file);
                if (metadata == null)
                {
                    skipped++;
                    continue;
                }

                discovered.Add(metadata);
            }

            if (!discovered.Any())
            {
                _logger.Debug("[UNMAPPED-RETRY] No retryable unmapped files remained after freshness checks (selected={0}, skipped={1})",
                    files.Count,
                    skipped);
                return;
            }

            _logger.Info("[UNMAPPED-RETRY] Matching {0} unmapped files from stored evidence ({1} skipped)", discovered.Count, skipped);

            var matchedPaths = new HashSet<string>(PathEqualityComparer.Instance);
            var alreadyLinkedPaths = new HashSet<string>(PathEqualityComparer.Instance);
            var applyFailedPaths = new HashSet<string>(PathEqualityComparer.Instance);
            var localImported = 0;
            var serverImported = 0;

            var localResult = _fileMatching
                .MatchFilesToLibraryAsync(discovered.ToArray(), restrictToAuthorId: null, MatchingContextPresets.ForScanLocal())
                .GetAwaiter()
                .GetResult();

            localImported += ImportMatches(localResult.MatchedFiles, matchedPaths, alreadyLinkedPaths, applyFailedPaths, "local");

            var remaining = (localResult.UnmatchedFiles ?? Array.Empty<UnmatchedFile>())
                .Where(unmatched => unmatched?.File?.Path != null && !matchedPaths.Contains(unmatched.File.Path))
                .Select(unmatched => unmatched.File)
                .ToArray();

            FileMatchResult serverResult = null;
            if (remaining.Any())
            {
                serverResult = _fileMatching
                    .MatchFilesToLibraryAsync(remaining, restrictToAuthorId: null, CreateServerRetryContext())
                    .GetAwaiter()
                    .GetResult();

                serverImported += ImportMatches(serverResult.MatchedFiles, matchedPaths, alreadyLinkedPaths, applyFailedPaths, "server");
            }

            var finalUnmatched = (serverResult?.UnmatchedFiles ?? localResult.UnmatchedFiles ?? Array.Empty<UnmatchedFile>())
                .Where(unmatched => unmatched?.File?.Path != null && byPath.ContainsKey(unmatched.File.Path) && !matchedPaths.Contains(unmatched.File.Path))
                .ToList();

            var existingAuthorImported = RematchExistingSuggestedAuthors(finalUnmatched, byPath, matchedPaths, alreadyLinkedPaths, applyFailedPaths);
            serverImported += existingAuthorImported;
            finalUnmatched = finalUnmatched
                .Where(unmatched => unmatched?.File?.Path != null && !matchedPaths.Contains(unmatched.File.Path))
                .ToList();

            MarkUnmatchedAttempts(finalUnmatched, byPath);
            var queuedAuthors = QueueSuggestedAuthorImports(finalUnmatched, byPath);

            _logger.Info("[UNMAPPED-RETRY] Retry match complete: localImported={0}, serverImported={1}, alreadyLinked={2}, unmatched={3}, applyFailed={4}, queuedAuthors={5}, skipped={6}",
                localImported,
                serverImported,
                alreadyLinkedPaths.Count,
                finalUnmatched.Count,
                applyFailedPaths.Count,
                queuedAuthors,
                skipped);
        }

        private DiscoveredFileWithMetadata BuildDiscoveredFile(BookFile file)
        {
            if (file == null || string.IsNullOrWhiteSpace(file.Path))
            {
                return null;
            }

            IFileInfo fileInfo;
            try
            {
                fileInfo = _diskProvider.GetFileInfo(file.Path);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[UNMAPPED-RETRY] Could not stat '{0}'", file.Path);
                return null;
            }

            if (fileInfo == null || !fileInfo.Exists)
            {
                _logger.Debug("[UNMAPPED-RETRY] Skipping missing file '{0}'", file.Path);
                return null;
            }

            var refreshed = UnmappedFileStoredEvidence.TryRefreshIfNeeded(
                file,
                fileInfo,
                _metadataTagService,
                _logger,
                "[UNMAPPED-RETRY]",
                out var evidence);

            if (evidence.FileChanged && !refreshed)
            {
                _logger.Debug("[UNMAPPED-RETRY] Skipping changed file with unavailable refreshed evidence: {0}", file.Path);
                return null;
            }

            if (refreshed && evidence.Mutated)
            {
                _mediaFileService.Update(file);
            }

            return new DiscoveredFileWithMetadata
            {
                Path = file.Path,
                Size = fileInfo.Length,
                Modified = MediaFileFreshness.GetLastWriteUtc(fileInfo),
                AllTags = evidence.Tags,
                DurationSeconds = evidence.DurationSeconds,
                Quality = UnmappedFileStoredEvidence.ResolveQuality(file, file.Path)
            };
        }

        private int ImportMatches(
            IEnumerable<FileMatch> matches,
            HashSet<string> matchedPaths,
            HashSet<string> alreadyLinkedPaths,
            HashSet<string> applyFailedPaths,
            string source)
        {
            var imported = 0;

            foreach (var match in matches ?? Array.Empty<FileMatch>())
            {
                if (match?.File?.Path == null || matchedPaths.Contains(match.File.Path))
                {
                    continue;
                }

                try
                {
                    var destination = ResolveDestination(match);

                    var applyResult = _bookImport
                        .ImportExistingFileAsync(
                            match.File,
                            destination.BookId,
                            destination.EditionId,
                            "Unknown",
                            match.Provenance)
                        .GetAwaiter()
                        .GetResult();

                    if (applyResult == null || !PathEqualityComparer.Instance.Equals(applyResult.Path, match.File.Path))
                    {
                        applyResult = BookImportFileResult.Failed(match.File.Path, "NO_APPLY_RESULT");
                    }

                    if (applyResult.IsHandled)
                    {
                        matchedPaths.Add(match.File.Path);
                        if (applyResult.IsApplied)
                        {
                            imported++;
                        }
                        else
                        {
                            alreadyLinkedPaths.Add(match.File.Path);
                        }
                    }
                    else
                    {
                        applyFailedPaths.Add(match.File.Path);
                        MarkApplyFailure(match.File.Path, applyResult.ReasonCode ?? "APPLY_NOT_COMPLETED");
                        _logger.Warn("[UNMAPPED-RETRY] {0} match was not applied for '{1}': outcome={2}, reason={3}",
                            source,
                            match.File.Path,
                            applyResult.Outcome,
                            applyResult.ReasonCode ?? "APPLY_NOT_COMPLETED");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "[UNMAPPED-RETRY] Failed to import {0} match for '{1}'", source, match.File.Path);
                    applyFailedPaths.Add(match.File.Path);
                    MarkApplyFailure(match.File.Path, "APPLY_EXCEPTION");
                }
            }

            return imported;
        }

        private int RematchExistingSuggestedAuthors(
            IEnumerable<UnmatchedFile> unmatchedFiles,
            IReadOnlyDictionary<string, BookFile> byPath,
            HashSet<string> matchedPaths,
            HashSet<string> alreadyLinkedPaths,
            HashSet<string> applyFailedPaths)
        {
            var imported = 0;

            foreach (var group in BuildSuggestedAuthorGroups(unmatchedFiles, byPath))
            {
                var existingAuthor = FindExistingSuggestedAuthor(group.Suggestion.ProviderId);
                if (existingAuthor == null || existingAuthor.Id <= 0)
                {
                    continue;
                }

                var eligibleItems = ResolveRootBoundRetryItems(group.Items, matchedPaths);
                foreach (var mediaGroup in eligibleItems.GroupBy(item => item.MediaType))
                {
                    foreach (var rootGroup in mediaGroup.GroupBy(item => item.RootFolder.Path, PathEqualityComparer.Instance))
                    {
                        foreach (var workGroup in rootGroup.GroupBy(item => item.ProviderWorkId ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                        {
                            var partition = workGroup.ToList();
                            var mediaType = partition[0].MediaType;
                            var rootFolder = partition[0].RootFolder;
                            var providerWorkId = partition[0].ProviderWorkId;
                            var filesForAuthor = partition
                                .Select(item => item.Item.Unmatched.File)
                                .Where(file => file?.Path != null && !matchedPaths.Contains(file.Path))
                                .ToArray();

                            if (filesForAuthor.Length == 0)
                            {
                                continue;
                            }

                            var existingBooks = _bookService.GetBooksByAuthor(existingAuthor.Id) ?? new List<Book>();
                            if (!existingBooks.Any(book => book.MediaType == mediaType))
                            {
                                if (!TryBuildExistingAuthorBackfillConfig(
                                        existingAuthor,
                                        mediaType,
                                        rootFolder,
                                        partition.Select(item => item.Item).ToList(),
                                        out var backfillConfig,
                                        out var configError))
                                {
                                    SetUnmatchedReason(
                                        partition,
                                        $"EXISTING_AUTHOR_MEDIA_BACKFILL_CONFIG_FAILED:{mediaType}:{configError}");
                                    continue;
                                }

                                try
                                {
                                    _authorLibraryService
                                        .AddAuthorAsync(group.Suggestion.ProviderId, backfillConfig)
                                        .GetAwaiter()
                                        .GetResult();
                                }
                                catch (Exception ex)
                                {
                                    _logger.Warn(
                                        ex,
                                        "[UNMAPPED-RETRY] Failed {0} catalog backfill for existing suggested author {1}",
                                        mediaType,
                                        group.Suggestion.ProviderId);
                                    SetUnmatchedReason(
                                        partition,
                                        $"EXISTING_AUTHOR_MEDIA_BACKFILL_FAILED:{mediaType}:{group.Suggestion.ProviderId}");
                                    continue;
                                }

                                existingBooks = _bookService.GetBooksByAuthor(existingAuthor.Id) ?? new List<Book>();
                                if (!existingBooks.Any(book => book.MediaType == mediaType))
                                {
                                    SetUnmatchedReason(
                                        partition,
                                        $"AUTHORITATIVE_MEDIA_CATALOG_EMPTY:{mediaType}:{group.Suggestion.ProviderId}");
                                    continue;
                                }
                            }

                            HashSet<int> allowedBookIds = null;
                            if (!string.IsNullOrWhiteSpace(providerWorkId))
                            {
                                if (!LocalProviderWorkBoundaryResolver.TryResolve(
                                        _bookService,
                                        existingAuthor,
                                        providerWorkId,
                                        mediaType,
                                        _logger,
                                        "UNMAPPED-RETRY",
                                        out var allowedBooks,
                                        out var boundaryReason))
                                {
                                    SetUnmatchedReason(
                                        partition,
                                        $"AUTHORITATIVE_WORK_NOT_LOCAL:{mediaType}:{providerWorkId}:{boundaryReason}");
                                    continue;
                                }

                                allowedBookIds = allowedBooks.Select(book => book.Id).ToHashSet();
                            }

                            try
                            {
                                var scopedContext = MatchingContextPresets.ForScanScopedRematch();
                                scopedContext.HardAllowedBookIds = allowedBookIds?.OrderBy(id => id).ToList();
                                if (allowedBookIds?.Count > 0)
                                {
                                    scopedContext.DisablePathFallback = false;
                                }

                                var scopedResult = _fileMatching
                                    .MatchFilesToLibraryAsync(filesForAuthor, existingAuthor.Id, scopedContext)
                                    .GetAwaiter()
                                    .GetResult();

                                var acceptedMatches = ValidateSuggestedMatches(
                                    scopedResult.MatchedFiles,
                                    partition,
                                    existingAuthor,
                                    allowedBookIds);
                                var scopedImported = ImportMatches(
                                    acceptedMatches,
                                    matchedPaths,
                                    alreadyLinkedPaths,
                                    applyFailedPaths,
                                    "server-author-local");
                                imported += scopedImported;
                                CopyScopedUnmatchedReasons(
                                    scopedResult.UnmatchedFiles,
                                    partition,
                                    existingAuthor,
                                    group.Suggestion.ProviderId);
                                CopyApplyFailureReasons(acceptedMatches, partition, applyFailedPaths);

                                _logger.Debug(
                                    "[UNMAPPED-RETRY] Existing suggested author {0} {1} rematch imported {2}/{3} files",
                                    group.Suggestion.ProviderId,
                                    mediaType,
                                    scopedImported,
                                    filesForAuthor.Length);
                            }
                            catch (Exception ex)
                            {
                                _logger.Warn(
                                    ex,
                                    "[UNMAPPED-RETRY] Failed existing-author {0} rematch for suggested author {1}",
                                    mediaType,
                                    group.Suggestion.ProviderId);
                                SetUnmatchedReason(
                                    partition,
                                    $"EXISTING_AUTHOR_SCOPED_REMATCH_FAILED:{mediaType}:{group.Suggestion.ProviderId}");
                            }
                        }
                    }
                }
            }

            return imported;
        }

        private List<RootBoundSuggestedAuthorRetryItem> ResolveRootBoundRetryItems(
            IEnumerable<SuggestedAuthorRetryItem> items,
            IReadOnlySet<string> matchedPaths)
        {
            var resolved = new List<RootBoundSuggestedAuthorRetryItem>();

            foreach (var item in items ?? Array.Empty<SuggestedAuthorRetryItem>())
            {
                var path = item?.Unmatched?.File?.Path;
                if (string.IsNullOrWhiteSpace(path) || matchedPaths.Contains(path))
                {
                    continue;
                }

                var mediaType = QualityMediaTypeHelper.GetMediaTypeFromPath(path);
                if (!mediaType.HasValue)
                {
                    item.Unmatched.Reason = "UNSUPPORTED_MEDIA_TYPE";
                    continue;
                }

                RootFolder rootFolder;
                try
                {
                    rootFolder = _rootFolderService.GetBestRootFolder(path);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "[UNMAPPED-RETRY] Failed resolving root folder for '{0}'", path);
                    item.Unmatched.Reason = "NO_ROOT_FOLDER";
                    continue;
                }

                if (rootFolder == null)
                {
                    item.Unmatched.Reason = "NO_ROOT_FOLDER";
                    continue;
                }

                if (!StagingQueueFileDispositionHelper.IsFileAllowedForRootFolderType(path, rootFolder))
                {
                    item.Unmatched.Reason = $"ROOT_FOLDER_TYPE_{rootFolder.FolderType}";
                    continue;
                }

                resolved.Add(new RootBoundSuggestedAuthorRetryItem
                {
                    Item = item,
                    MediaType = mediaType.Value,
                    RootFolder = rootFolder,
                    ProviderWorkId = GetSpecificBookProviderIds(item.Suggestion).FirstOrDefault()
                });
            }

            return resolved;
        }

        private bool TryBuildExistingAuthorBackfillConfig(
            Author existingAuthor,
            BookMediaType mediaType,
            RootFolder rootFolder,
            IReadOnlyCollection<SuggestedAuthorRetryItem> items,
            out MonitoringConfig config,
            out string error)
        {
            var request = new SuggestedAuthorImportConfigRequest
            {
                AuthorName = existingAuthor?.Name,
                FilePaths = items
                    .Select(item => item?.Unmatched?.File?.Path)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(PathEqualityComparer.Instance)
                    .ToList(),
                FixedRootFolder = rootFolder,
                ForceMediaType = mediaType,
                QueueIfUnavailable = false,
                RequestedBy = "RetryUnmappedMatch",
                ResolveRootFromFilePathFirst = true,
                AllowAmbiguousRootFallback = false,
                IncludeRootDefaultTags = true
            };

            return SuggestedAuthorImportCoordinator.TryBuildMonitoringConfig(
                request,
                _rootFolderService,
                null,
                null,
                out config,
                out error);
        }

        private List<FileMatch> ValidateSuggestedMatches(
            IEnumerable<FileMatch> matches,
            IReadOnlyCollection<RootBoundSuggestedAuthorRetryItem> partition,
            Author existingAuthor,
            IReadOnlySet<int> allowedBookIds)
        {
            var itemsByPath = partition
                .Where(item => item?.Item?.Unmatched?.File?.Path != null)
                .GroupBy(item => item.Item.Unmatched.File.Path, PathEqualityComparer.Instance)
                .ToDictionary(group => group.Key, group => group.First(), PathEqualityComparer.Instance);
            var accepted = new List<FileMatch>();

            foreach (var match in matches ?? Array.Empty<FileMatch>())
            {
                if (match?.File?.Path == null || !itemsByPath.TryGetValue(match.File.Path, out var item))
                {
                    continue;
                }

                var book = _bookService.GetBook(match.BookId);
                if (!ManualImportService.SuggestedLocalMatchMatchesSuggestion(
                        match,
                        existingAuthor,
                        book,
                        item.ProviderWorkId,
                        out var bookRejection,
                        allowedBookIds))
                {
                    item.Item.Unmatched.Reason = $"SUGGESTED_LOCAL_MATCH_REJECTED:{bookRejection}";
                    continue;
                }

                var edition = match.EditionId > 0 ? _editionService.GetEdition(match.EditionId) : null;
                if (!ManualImportService.SuggestedLocalMatchEditionMatchesBook(
                        edition,
                        book,
                        match.EditionId,
                        out var editionRejection))
                {
                    item.Item.Unmatched.Reason = $"SUGGESTED_LOCAL_MATCH_REJECTED:{editionRejection}";
                    continue;
                }

                accepted.Add(match);
            }

            return accepted;
        }

        private static void CopyScopedUnmatchedReasons(
            IEnumerable<UnmatchedFile> scopedUnmatched,
            IReadOnlyCollection<RootBoundSuggestedAuthorRetryItem> partition,
            Author existingAuthor,
            string authorProviderId)
        {
            var itemsByPath = partition
                .Where(item => item?.Item?.Unmatched?.File?.Path != null)
                .GroupBy(item => item.Item.Unmatched.File.Path, PathEqualityComparer.Instance)
                .ToDictionary(group => group.Key, group => group.First(), PathEqualityComparer.Instance);

            foreach (var unmatched in scopedUnmatched ?? Array.Empty<UnmatchedFile>())
            {
                if (unmatched?.File?.Path == null || !itemsByPath.TryGetValue(unmatched.File.Path, out var item))
                {
                    continue;
                }

                item.Item.Unmatched.Reason =
                    $"EXISTING_AUTHOR_NO_LOCAL_MATCH:{authorProviderId}:authorId={existingAuthor.Id}:{unmatched.Reason ?? "NO_MATCH"}";
            }
        }

        private void CopyApplyFailureReasons(
            IEnumerable<FileMatch> matches,
            IReadOnlyCollection<RootBoundSuggestedAuthorRetryItem> partition,
            IReadOnlySet<string> applyFailedPaths)
        {
            if (applyFailedPaths == null || applyFailedPaths.Count == 0)
            {
                return;
            }

            var itemsByPath = partition
                .Where(item => item?.Item?.Unmatched?.File?.Path != null)
                .GroupBy(item => item.Item.Unmatched.File.Path, PathEqualityComparer.Instance)
                .ToDictionary(group => group.Key, group => group.First(), PathEqualityComparer.Instance);

            foreach (var match in matches ?? Array.Empty<FileMatch>())
            {
                if (match?.File?.Path == null ||
                    !applyFailedPaths.Contains(match.File.Path) ||
                    !itemsByPath.TryGetValue(match.File.Path, out var item))
                {
                    continue;
                }

                var stored = _mediaFileService.GetFileWithPath(match.File.Path);
                item.Item.Unmatched.Reason = stored?.MatchDetails ?? "APPLY_NOT_COMPLETED";
            }
        }

        private static void SetUnmatchedReason(
            IEnumerable<RootBoundSuggestedAuthorRetryItem> items,
            string reason)
        {
            foreach (var item in items ?? Array.Empty<RootBoundSuggestedAuthorRetryItem>())
            {
                if (item?.Item?.Unmatched != null)
                {
                    item.Item.Unmatched.Reason = reason;
                }
            }
        }

        private (int BookId, int EditionId) ResolveDestination(FileMatch match)
        {
            var canonicalEdition = _editionService.GetEdition(match.EditionId);
            if (canonicalEdition == null)
            {
                throw new InvalidOperationException($"Matched edition not found: {match.EditionId}");
            }

            var canonicalBook = _bookService.GetBook(canonicalEdition.BookId);
            if (canonicalBook == null)
            {
                throw new InvalidOperationException($"Matched book not found: {canonicalEdition.BookId}");
            }

            var unitKey = _unitDestination.BuildRootUnitKeyWithExtension(match.File.Path, canonicalEdition.Title, canonicalBook.MediaType);
            return _unitDestination.ResolveDestinationForUnit(canonicalBook, canonicalEdition, unitKey);
        }

        private void MarkUnmatchedAttempts(IEnumerable<UnmatchedFile> unmatchedFiles, IReadOnlyDictionary<string, BookFile> byPath)
        {
            var now = DateTime.UtcNow;
            var updated = new List<BookFile>();

            foreach (var unmatched in unmatchedFiles ?? Array.Empty<UnmatchedFile>())
            {
                if (unmatched?.File?.Path == null || !byPath.TryGetValue(unmatched.File.Path, out var bookFile))
                {
                    continue;
                }

                bookFile.LastMatchAttempt = now;
                bookFile.MatchDetails = unmatched.Reason ?? "NO_MATCH";
                updated.Add(bookFile);
            }

            if (updated.Any())
            {
                _mediaFileService.Update(updated);
            }
        }

        private void MarkApplyFailure(string path, string reasonCode)
        {
            try
            {
                var bookFile = _mediaFileService.GetFileWithPath(path);
                if (bookFile == null || bookFile.EditionId > 0)
                {
                    return;
                }

                bookFile.LastMatchAttempt = DateTime.UtcNow;
                bookFile.MatchDetails = reasonCode;
                _mediaFileService.Update(bookFile);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[UNMAPPED-RETRY] Failed persisting apply failure for '{0}'", path);
            }
        }

        private int QueueSuggestedAuthorImports(IEnumerable<UnmatchedFile> unmatchedFiles, IReadOnlyDictionary<string, BookFile> byPath)
        {
            var unmatchedList = (unmatchedFiles ?? Array.Empty<UnmatchedFile>())
                .Where(unmatched => unmatched?.File?.Path != null && unmatched.PotentialAuthors?.Any() == true)
                .ToList();

            if (!unmatchedList.Any())
            {
                return 0;
            }

            var queued = 0;
            var now = DateTime.UtcNow;
            var updatedFiles = new List<BookFile>();

            foreach (var group in BuildSuggestedAuthorGroups(unmatchedList, byPath))
            {
                var suggestion = group.Suggestion;
                var suggestions = group.Items
                    .Select(item => item.Suggestion)
                    .ToList();
                var bookProviderEvidence = suggestions
                    .SelectMany(GetSpecificBookProviderIds)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var affectedFiles = group.Items
                    .Select(item => item.Unmatched.File.Path)
                    .Where(path => byPath.ContainsKey(path))
                    .Select(path => byPath[path])
                    .Distinct()
                    .ToList();

                if (!affectedFiles.Any())
                {
                    continue;
                }

                var existingAuthor = FindExistingSuggestedAuthor(suggestion.ProviderId);
                if (existingAuthor != null)
                {
                    _logger.Debug("[UNMAPPED-RETRY] Suggested author {0} already exists as ID {1}; not queueing pending import for {2} files",
                        suggestion.ProviderId,
                        existingAuthor.Id,
                        affectedFiles.Count);
                    continue;
                }

                if (!TryBuildPendingAuthorImportConfig(suggestions, affectedFiles, out var config, out var error))
                {
                    foreach (var file in affectedFiles)
                    {
                        file.LastMatchAttempt = now;
                        file.MatchDetails = $"AUTHOR_IMPORT_CONFIG_FAILED:{error}";
                        updatedFiles.Add(file);
                    }

                    _logger.Warn("[UNMAPPED-RETRY] Could not queue suggested author {0} for {1} files: {2}",
                        suggestion.ProviderId,
                        affectedFiles.Count,
                        error);
                    continue;
                }

                try
                {
                    var pendingId = _pendingAuthorImportService
                        .EnqueueAsync(suggestion.ProviderId, config, "RetryUnmappedMatch")
                        .GetAwaiter()
                        .GetResult();

                    queued++;

                    foreach (var file in affectedFiles)
                    {
                        file.LastMatchAttempt = now;
                        file.MatchDetails = BuildPendingAuthorImportMatchDetails(pendingId, suggestion.ProviderId, bookProviderEvidence);
                        updatedFiles.Add(file);
                    }

                    _logger.Info("[UNMAPPED-RETRY] Queued suggested author {0} for {1} unmapped files (pendingId={2})",
                        suggestion.ProviderId,
                        affectedFiles.Count,
                        pendingId);
                }
                catch (Exception ex)
                {
                    foreach (var file in affectedFiles)
                    {
                        file.LastMatchAttempt = now;
                        file.MatchDetails = $"AUTHOR_IMPORT_QUEUE_FAILED:{suggestion.ProviderId}";
                        updatedFiles.Add(file);
                    }

                    _logger.Warn(ex, "[UNMAPPED-RETRY] Failed to queue suggested author {0} for {1} unmapped files",
                        suggestion.ProviderId,
                        affectedFiles.Count);
                }
            }

            if (updatedFiles.Any())
            {
                _mediaFileService.Update(updatedFiles
                    .GroupBy(file => file.Id)
                    .Select(group => group.First())
                    .ToList());
            }

            return queued;
        }

        private static List<SuggestedAuthorRetryGroup> BuildSuggestedAuthorGroups(
            IEnumerable<UnmatchedFile> unmatchedFiles,
            IReadOnlyDictionary<string, BookFile> byPath)
        {
            return (unmatchedFiles ?? Array.Empty<UnmatchedFile>())
                .Where(unmatched => unmatched?.File?.Path != null && unmatched.PotentialAuthors?.Any() == true)
                .Select(unmatched => new SuggestedAuthorRetryItem
                {
                    Unmatched = unmatched,
                    Suggestion = unmatched.PotentialAuthors
                        .Where(s => !string.IsNullOrWhiteSpace(s?.ProviderId))
                        .OrderByDescending(s => s.Confidence)
                        .FirstOrDefault()
                })
                .Where(item => item.Suggestion != null && byPath.ContainsKey(item.Unmatched.File.Path))
                .GroupBy(item => BuildSuggestionKey(item.Suggestion), StringComparer.OrdinalIgnoreCase)
                .Select(group => new SuggestedAuthorRetryGroup
                {
                    Suggestion = group.First().Suggestion,
                    Items = group.ToList()
                })
                .ToList();
        }

        private Author FindExistingSuggestedAuthor(string providerId)
        {
            var normalized = NormalizeProviderId(providerId, null) ?? providerId?.Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            var colon = normalized.IndexOf(':');
            if (colon <= 0 || colon >= normalized.Length - 1)
            {
                return null;
            }

            try
            {
                return _authorService?.FindByProviderId(normalized.Substring(0, colon), normalized.Substring(colon + 1));
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[UNMAPPED-RETRY] Failed looking up suggested author {0}", normalized);
                return null;
            }
        }

        private bool TryBuildPendingAuthorImportConfig(IReadOnlyCollection<AuthorSuggestion> suggestions, IReadOnlyCollection<BookFile> files, out MonitoringConfig config, out string error)
        {
            var suggestion = suggestions?.FirstOrDefault();

            var paths = files?
                .Select(file => file.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(PathEqualityComparer.Instance)
                .ToList() ?? new List<string>();

            var request = new SuggestedAuthorImportConfigRequest
            {
                AuthorName = suggestion?.AuthorName,
                FilePaths = paths,
                QueueIfUnavailable = true,
                RequestedBy = "RetryUnmappedMatch",
                ResolveRootFromFilePathFirst = true,
                AllowAmbiguousRootFallback = false,
                IncludeRootDefaultTags = true
            };

            return SuggestedAuthorImportCoordinator.TryBuildMonitoringConfig(
                request,
                _rootFolderService,
                null,
                null,
                out config,
                out error);
        }

        private static IEnumerable<string> GetSpecificBookProviderIds(AuthorSuggestion suggestion)
        {
            var normalized = NormalizeProviderId(suggestion?.BookProviderId, "hc");
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                yield return normalized;
            }
        }

        private static string NormalizeProviderId(string value, string defaultPrefix)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            try
            {
                return ProviderIdHelper.Canonicalize(value.Trim(), defaultPrefix);
            }
            catch
            {
                return null;
            }
        }

        private static string BuildSuggestionKey(AuthorSuggestion suggestion)
        {
            return NormalizeProviderId(suggestion.ProviderId, null) ?? suggestion.ProviderId?.Trim();
        }

        private static string BuildPendingAuthorImportMatchDetails(int pendingId, string authorProviderId, IReadOnlyCollection<string> bookProviderIds)
        {
            var details = pendingId > 0
                ? $"PENDING_AUTHOR_IMPORT:{pendingId}:{authorProviderId}"
                : $"PENDING_AUTHOR_IMPORT:EXISTING_AUTHOR:{authorProviderId}";

            if (bookProviderIds?.Any() != true)
            {
                return details;
            }

            var shown = bookProviderIds.Take(3).ToList();
            var suffix = string.Join(",", shown);
            if (bookProviderIds.Count > shown.Count)
            {
                suffix += $",+{bookProviderIds.Count - shown.Count}";
            }

            return $"{details}:books={suffix}";
        }

        private List<BookFile> ResolveUnmappedFiles(UnmappedFilesSelection selection, string mediaType)
        {
            return UnmappedFileSelectionResolver.ResolveRows(
                _mediaFileService,
                selection,
                mediaType,
                _logger,
                "[UNMAPPED-RETRY]");
        }

        private static MatchingContext CreateServerRetryContext()
        {
            var context = MatchingContextPresets.ForScanV5();
            context.AllowGroupedV5Suggestions = true;
            return context;
        }

        private sealed class SuggestedAuthorRetryItem
        {
            public UnmatchedFile Unmatched { get; set; }
            public AuthorSuggestion Suggestion { get; set; }
        }

        private sealed class SuggestedAuthorRetryGroup
        {
            public AuthorSuggestion Suggestion { get; set; }
            public List<SuggestedAuthorRetryItem> Items { get; set; }
        }

        private sealed class RootBoundSuggestedAuthorRetryItem
        {
            public SuggestedAuthorRetryItem Item { get; set; }
            public BookMediaType MediaType { get; set; }
            public RootFolder RootFolder { get; set; }
            public string ProviderWorkId { get; set; }
        }
    }
}
