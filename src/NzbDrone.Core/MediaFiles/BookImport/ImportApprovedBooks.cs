using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Data.Sqlite;
using Npgsql;
using NLog;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Utilities;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Extras;
using NzbDrone.Core.History;
using NzbDrone.Core.Instrumentation;
using NzbDrone.Core.MediaFiles; // for IMoveBookFiles
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.MediaFiles.BookImport.Services;
using NzbDrone.Core.MediaFiles.BookImport.Manual;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
    using NzbDrone.Core.Parser;
    using NzbDrone.Core.Parser.Model;
    using NzbDrone.Core.Profiles.Qualities;
    using NzbDrone.Core.Qualities;


namespace NzbDrone.Core.MediaFiles.BookImport
{
    /// <summary>
    /// ImportApprovedBooks is responsible ONLY for importing approved files to existing library items.
    /// It does NOT create authors, books, or series. Files must be matched to existing items.
    /// </summary>
    public class ImportApprovedBooks : IImportApprovedBooks
    {
        internal const string EbookAlternativeRejectionCategory = "EbookAlternative";

        private const string ConversionArtifactManifestFileName = "conversion-artifact.json";
        private static readonly string[] SourceCoverBaseNames = { "cover", "front", "folder", "album", "albumart" };
        private static readonly string[] SourceCoverExtensions = { ".jpg", ".jpeg", ".png" };

        private sealed class ReplacementRefusedException : Exception
        {
            public ReplacementRefusedException(string message) : base(message) { }
        }

        public sealed class IncludedImportException : Exception
        {
            public IReadOnlyList<ImportResult> Results { get; }

            public IncludedImportException(Exception innerException, IReadOnlyList<ImportResult> results)
                : base(innerException.Message, innerException)
            {
                Results = results;
            }
        }

        private sealed class PendingFileCommit
        {
            public BookFile BookFile { get; init; }
            public LocalBook LocalBook { get; init; }
            public Author Author { get; init; }
            public string SourcePath { get; init; }
            public string DestinationPath { get; set; }
            public bool CopyOnly { get; init; }
            public bool DatabaseCommitted { get; set; }
            public bool NewDownload { get; init; } = true;
            public bool ImportAssociatedFiles { get; init; } = true;
            public bool PublishAddedEvent { get; init; }
            public string RenamedFrom { get; init; }
            public List<(BookFile OldFile, string BackupPath)> StagedReplacements { get; init; } = new();
            public List<BookFile> DatabaseRowsToReplace { get; } = new();
        }

        private readonly IMediaFileService _mediaFileService;
        private readonly IMetadataTagService _metadataTagService;
        private readonly IMediaInfoExtractor _mediaInfoExtractor;
        private readonly IAuthorService _authorService;
        private readonly IBookService _bookService;
        private readonly IEditionService _editionService;
        private readonly IRecycleBinProvider _recycleBinProvider;
        private readonly IExtraService _extraService;
        private readonly IMoveBookFiles _bookFileMover;
        private readonly IHistoryService _historyService;
        private readonly NzbDrone.Core.Download.History.IDownloadHistoryService _downloadHistoryService;
        private readonly IEventAggregator _eventAggregator;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly ISeriesBookLinkService _seriesBookLinkService;
            private readonly ISeriesService _seriesService;
            private readonly IQualityProfileService _qualityProfileService;
            private readonly IM4bConversionService _m4bConversionService;
            private readonly IConversionTrackingService _conversionTrackingService;
            private readonly IConversionJobService _conversionJobService;
            private readonly IDiskProvider _diskProvider;
            private readonly IConfigService _configService;
            private readonly IContainmentValidator _containmentValidator;
            private readonly IMapCoversToLocal _coverMapper;
            private readonly ICustomFormatCalculationService _customFormatCalculationService;
            private readonly Logger _logger;

        public ImportApprovedBooks(
            IMediaFileService mediaFileService,
            IMetadataTagService metadataTagService,
            IMediaInfoExtractor mediaInfoExtractor,
            IAuthorService authorService,
            IBookService bookService,
            IEditionService editionService,
            IRecycleBinProvider recycleBinProvider,
            IExtraService extraService,
            IMoveBookFiles bookFileMover,
            IHistoryService historyService,
            NzbDrone.Core.Download.History.IDownloadHistoryService downloadHistoryService,
            IEventAggregator eventAggregator,
            IManageCommandQueue commandQueueManager,
                ISeriesBookLinkService seriesBookLinkService,
                ISeriesService seriesService,
                IQualityProfileService qualityProfileService,
                IM4bConversionService m4bConversionService,
                Logger logger,
                IConversionTrackingService conversionTrackingService = null,
                IDiskProvider diskProvider = null,
                IConfigService configService = null,
                IContainmentValidator containmentValidator = null,
                IMapCoversToLocal coverMapper = null,
                ICustomFormatCalculationService customFormatCalculationService = null,
                IConversionJobService conversionJobService = null)
            {
            _mediaFileService = mediaFileService;
            _metadataTagService = metadataTagService;
            _mediaInfoExtractor = mediaInfoExtractor;
            _authorService = authorService;
            _bookService = bookService;
            _editionService = editionService;
            _recycleBinProvider = recycleBinProvider;
            _extraService = extraService;
            _bookFileMover = bookFileMover;
            _historyService = historyService;
            _downloadHistoryService = downloadHistoryService;
            _eventAggregator = eventAggregator;
            _commandQueueManager = commandQueueManager;
                _seriesBookLinkService = seriesBookLinkService;
                _seriesService = seriesService;
                _qualityProfileService = qualityProfileService;
                _m4bConversionService = m4bConversionService;
                _conversionTrackingService = conversionTrackingService;
                _conversionJobService = conversionJobService;
                _diskProvider = diskProvider;
                _configService = configService;
                _containmentValidator = containmentValidator;
                _coverMapper = coverMapper;
                _customFormatCalculationService = customFormatCalculationService;
                _logger = logger;
            }

        public List<ImportResult> Import(
            List<ImportDecision<LocalBook>> decisions,
            bool replaceExisting,
            DownloadClientItem downloadClientItem = null,
            ImportMode importMode = ImportMode.Auto,
            CancellationToken cancellationToken = default)
        {
            _logger.Debug("[CLEAN-IMPORT] Starting import of {0} files", decisions.Count);
            var downloadForced = downloadClientItem?.DownloadForced == true;
            if (_logger.IsDebugEnabled)
            {
                _logger.Debug("[MEMORY] Clean import start ({0} decisions): {1}", decisions.Count, MemorySnapshot.CaptureDetailed());
            }

            var importResults = new List<ImportResult>();
            var importedBookEventContexts = new List<(Author Author, Book Book, BookFile File, bool NewDownload)>();
            var replacedBookFilesByBook = new Dictionary<int, List<BookFile>>();
            var bookFileConvertedEvents = new List<BookFileConvertedEvent>();
            var importHistory = new ImportHistoryContext(downloadClientItem?.DownloadId);
            var recordingSourcePaths = new Dictionary<LocalBook, IReadOnlyCollection<string>>();
            var conversionJob = downloadClientItem?.DownloadId.IsNotNullOrWhiteSpace() == true
                ? _conversionJobService?.Get(downloadClientItem.DownloadId) : null;
            if (conversionJob != null && decisions.All(decision => decision.Item?.IsManualImport != true) &&
                ConversionJobService.IsWaitingForOutput(conversionJob, _conversionJobService))
                return decisions.Select(decision => new ImportResult(decision, ImportResultType.Pending, "Waiting for conversion to finish.")).ToList();

            if (conversionJob?.CleanupPending == true && decisions.All(decision => decision.Item?.IsManualImport != true))
                return decisions.Select(decision => new ImportResult(decision, ImportResultType.Pending,
                    "Waiting for conversion cleanup to finish before retrying import. Cleanup failures are shown in System > Status.")).ToList();

            var acceptedRequest = conversionJob?.GetAcceptedRequest();
            var acceptedConversionParts = new HashSet<LocalBook>();
            foreach (var recording in RecordingCustomFormatHelper.GroupByRecording(decisions.Select(decision => decision.Item)))
            {
                var paths = recording.SelectMany(TrackedDownloadAlreadyImported.GetSourcePaths)
                    .Distinct(PathEqualityComparer.Instance).ToArray();
                foreach (var part in recording) recordingSourcePaths[part] = paths;
                if (recording.All(part => !part.IsManualImport) &&
                    acceptedRequest?.MatchesSources(recording.First().Book?.Id ?? 0, paths) == true &&
                    recording.All(part => part.Edition?.Id == acceptedRequest.EditionId))
                {
                    acceptedConversionParts.UnionWith(recording);
                }
            }

            var selectedManualPaths = decisions.Where(decision => decision.Item?.IsManualImport == true)
                .Select(decision => decision.Item.Path).ToHashSet(PathEqualityComparer.Instance);
            foreach (var output in selectedManualPaths.Where(ConversionWorkPath.IsWorkPath))
            {
                var artifact = ConversionWorkPath.GetSuccessfulArtifact(output);
                var source = artifact?.Sources?.FirstOrDefault(file => selectedManualPaths.Contains(file.Path));
                if (source != null)
                {
                    var error = $"Select either the converted file '{output}' or its original files, not both. Original file also selected: '{source.Path}'. Nothing was imported.";
                    _logger.Warn(error);
                    return decisions.Select(decision => new ImportResult(decision, error)).ToList();
                }
            }

            // Filter to only approved decisions
            var approvedDecisions = decisions.Where(c => c.Approved).ToList();
            _logger.Debug("[CLEAN-IMPORT] {0} approved decisions out of {1} total", approvedDecisions.Count, decisions.Count);

            // Add rejected decisions to results
            foreach (var decision in decisions.Where(c => !c.Approved))
            {
                importResults.Add(new ImportResult(decision, "Rejected: " + string.Join(", ", decision.Rejections.Select(r => r.Reason))));
            }

            // Separate matched and unmatched files
            var matchedDecisions = approvedDecisions.Where(d => d.Item?.Book?.Id > 0).ToList();
            BookSeries.LoadLinks(matchedDecisions.Select(decision => decision.Item.Book), ids => _seriesBookLinkService.GetLinksByBook(ids));
            var unmatchedDecisions = approvedDecisions.Where(d => d.Item?.Book?.Id == 0 || d.Item?.Book == null).ToList();

                // Enforce QualityProfile gating before any moves: if not allowed, reject and do not import.
                // Manual imports are an explicit user choice and must not be silently gated.
                var gatedMatched = new List<ImportDecision<LocalBook>>();
                var authorCache = new Dictionary<int, Author>();
                foreach (var md in matchedDecisions)
                {
                    var local = md.Item;
                    // Included files are checked together before any part is imported.
                    if (local?.IsIncludedMedia == true || acceptedConversionParts.Contains(local))
                    {
                        gatedMatched.Add(md);
                        continue;
                    }

                    if (local?.IsManualImport == true || downloadForced)
                    {
                        _logger.Debug("[QUALITY-GATE] Skipping quality gating for explicit user import/grab: '{0}'", local.Path);
                        gatedMatched.Add(md);
                        continue;
                    }

                    try
                    {
                        Author author = null;
                        if (local?.Author != null)
                        {
                            author = local.Author;
                        }
                        else if (local?.Book?.AuthorId > 0)
                        {
                            if (!authorCache.TryGetValue(local.Book.AuthorId, out author))
                            {
                                author = _authorService.GetAuthor(local.Book.AuthorId);
                                authorCache[local.Book.AuthorId] = author;
                            }
                        }

                        if (author == null)
                        {
                            md.Reject(new Rejection("Author not found"));
                            importResults.Add(new ImportResult(md, "Author not found"));
                            continue;
                        }

                        var quality = local?.Quality?.Quality ?? Qualities.Quality.Unknown;
                        var profile = author.GetQualityProfileForQuality(quality);
                        if (profile == null)
                        {
                            gatedMatched.Add(md); // no profile → do not gate
                            continue;
                        }

                        var allowed = profile.IsQualityAllowed(quality.Id);
                        if (!allowed)
                        {
                            var reason = $"Quality '{quality.Name}' not allowed by profile '{profile.Name}'";

                            // Retain the category for ebook alternative selection and the refusal for manual review.
                            // Read back via Rejection.IsQualityFilter — never by matching this message.
                            md.Reject(new Rejection(reason, RejectionType.Permanent, canBypass: false, category: "Quality", severity: 3));
                            importResults.AddRange(PolicyRefusals(new List<ImportDecision<LocalBook>> { md }, downloadClientItem,
                                reason, recordingSourcePaths.GetValueOrDefault(local)));
                            _logger.Debug("[QUALITY-GATE] Rejecting '{0}' — {1}", local?.Path, reason);
                        }
                        else
                        {
                            gatedMatched.Add(md);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn(ex, "[QUALITY-GATE] Error evaluating quality for '{0}'", local?.Path);
                        md.Reject(new Rejection("Quality not allowed by profile"));
                        importResults.Add(new ImportResult(md, "Quality not allowed by profile"));
                    }
                }
                matchedDecisions = gatedMatched;

                // Unmatched (approved-but-not-associated) decisions should never be routed through ImportApprovedBooks.
                // This class is responsible for importing files that are already matched to existing library items.
                if (unmatchedDecisions.Any())
                {
                    _logger.Debug("[CLEAN-IMPORT] {0} approved decisions were missing book matches; skipping", unmatchedDecisions.Count);
                    foreach (var decision in unmatchedDecisions)
                    {
                        importResults.Add(new ImportResult(decision, "File was not matched to an existing book"));
                    }
                }

                var matchedByItem = matchedDecisions.ToLookup(decision => decision.Item);
                var bookGroups = matchedDecisions.GroupBy(decision => decision.Item.Book.Id)
                    .SelectMany(group => group.First().Item.Book.MediaType == BookMediaType.Audiobook &&
                        !(replaceExisting && group.Any(decision => decision.Item.IsManualImport))
                        ? RecordingCustomFormatHelper.GroupByRecording(group.Select(decision => decision.Item))
                            .Select(recording => recording.SelectMany(part => matchedByItem[part]).ToList())
                        : new[] { group.ToList() })
                    .ToList();

            if (!bookGroups.Any())
            {
                _logger.Debug("[CLEAN-IMPORT] No files matched to existing books after orchestrator processing");
                return importResults;
            }

            // Readarr-conformant history tracking: publish TrackImportedEvent per imported file.
            // This powers Activity -> History "Book Imported" rows for both download imports and manual imports.
            var trackImportedEvents = new List<TrackImportedEvent>();
            Dictionary<int, List<string>> previouslyImportedFileSources = null;
            var excludedSources = new Dictionary<int, List<string>>();
            void CaptureEbookAlternatives(IEnumerable<ImportResult> results)
            {
                foreach (var result in results.Where(IsSkippedEbookAlternative).Where(result => result.ImportDecision.Item.Edition?.Id > 0))
                {
                    var id = result.ImportDecision.Item.Edition.Id;
                    if (!excludedSources.TryGetValue(id, out var paths))
                    {
                        paths = new List<string>();
                        excludedSources.Add(id, paths);
                    }

                    paths.AddRange(TrackedDownloadAlreadyImported.GetSourcePaths(result.ImportDecision.Item));
                }
            }

            CaptureEbookAlternatives(importResults);

            void PublishCommittedImportEvents()
            {
                if (bookFileConvertedEvents.Any())
                {
                    foreach (var bookFileConvertedEvent in bookFileConvertedEvents)
                    {
                        _eventAggregator.PublishEvent(bookFileConvertedEvent);
                    }
                }

                if (trackImportedEvents.Any())
                {
                    foreach (var trackImportedEvent in trackImportedEvents)
                    {
                        _eventAggregator.PublishEvent(trackImportedEvent);
                    }
                }

                // Publish events for imported files
                if (importedBookEventContexts.Any())
                {
                    var eventStopwatch = Stopwatch.StartNew();
                    var totalFiles = 0;

                    foreach (var bookGroup in importedBookEventContexts
                        .Where(x => x.Book != null && x.Book.Id > 0 && x.File != null)
                        .GroupBy(x => x.Book.Id))
                    {
                        var importedFiles = bookGroup
                            .Select(x => x.File)
                            .Where(x => x != null)
                            .DistinctBy(x => x.Id > 0 ? $"id:{x.Id}" : $"path:{x.Path}")
                            .ToList();

                        if (!importedFiles.Any())
                        {
                            continue;
                        }

                        var firstContext = bookGroup.First();
                        var oldFiles = replacedBookFilesByBook.TryGetValue(bookGroup.Key, out var replacedFiles)
                            ? replacedFiles
                            : new List<BookFile>();
                        totalFiles += importedFiles.Count;
                        var newDownload = downloadClientItem != null || bookGroup.Any(context => context.NewDownload);

                        _eventAggregator.PublishEvent(new BookImportedEvent(
                            firstContext.Author,
                            firstContext.Book,
                            importedFiles,
                            oldFiles,
                            newDownload,
                            downloadClientItem));

                        // Info deliberately: default-log evidence that the import-to-notification pipeline fired.
                        _logger.Info("[NOTIFY] Published BookImportedEvent for '{0}' (BookId={1}, Files={2}, OldFiles={3}, NewDownload={4})",
                            firstContext.Book.Title,
                            firstContext.Book.Id,
                            importedFiles.Count,
                            oldFiles.Count,
                            newDownload);
                    }

                    eventStopwatch.Stop();
                    _logger.Debug("[PERFORMANCE] Event publishing took {0}ms for {1} files",
                        eventStopwatch.ElapsedMilliseconds, totalFiles);
                }

                bookFileConvertedEvents.Clear();
                trackImportedEvents.Clear();
                importedBookEventContexts.Clear();
                replacedBookFilesByBook.Clear();
            }

            var failedReplacementFiles = importResults
                .Where(result => result.ImportDecision.Item?.IsManualImport == true || !IsSkippedEbookAlternative(result))
                .ToLookup(result => result.ImportDecision.Item?.Book?.Id ?? 0);
            var manualSources = replaceExisting && decisions.Any(decision => decision.Item?.IsManualImport == true)
                ? _mediaFileService.GetFileWithPath(decisions.Where(decision => decision.Item != null)
                    .Select(decision => decision.Item.Path).Distinct(PathEqualityComparer.Instance).ToList())
                : new List<BookFile>();
            var manualSourcePaths = (manualSources ?? new List<BookFile>()).Select(file => file.Path)
                .ToHashSet(PathEqualityComparer.Instance);

            foreach (var bookGroup in bookGroups)
            {
                var pendingFileCommits = new List<PendingFileCommit>();
                var groupRolledBack = false;
                try
                {
                var __bookGroupSw = Stopwatch.StartNew();
                var __route = "existing";
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.Debug("[CLEAN-IMPORT] Import cancelled");
                    break;
                }

                var bookDecisions = bookGroup.ToList();
                var book = bookDecisions.First().Item.Book;
                var author = book.Author ?? _authorService.GetAuthor(book.AuthorId);

                if (author == null)
                {
                    _logger.Error("[CLEAN-IMPORT] Could not find author for book {0} (AuthorId: {1})", book.Title, book.AuthorId);
                    foreach (var decision in bookDecisions)
                    {
                        importResults.Add(new ImportResult(decision, "Author not found"));
                    }
                    continue;
                }

                var previousResultCount = importResults.Count;
                bookDecisions = SelectSingleEbookAlternative(bookDecisions, author, importResults);
                CaptureEbookAlternatives(importResults.GetRange(previousResultCount, importResults.Count - previousResultCount));
                if (!bookDecisions.Any())
                {
                    _logger.Debug("[EBOOK-DUPLICATE-FORMAT] No ebook import candidates remain for book: {0}", book.Title);
                    continue;
                }

                _logger.Debug("[CLEAN-IMPORT] Importing {0} files for book: {1} by {2}",
                    bookDecisions.Count, book.Title, author.Name);

                var conversionWorkFolder = (string)null;

                // Check if we need to create a new book instance for this batch
                // (when quality profile doesn't allow upgrades)
                var firstDecision = bookDecisions.First();
                var localBook = firstDecision.Item;
                var qualityProfile = author.GetQualityProfileForQuality(localBook.Quality.Quality);
                var manualImport = bookDecisions.All(d => d.Item.IsManualImport);
                var manualReplaceExisting = replaceExisting && manualImport;
                var acceptedConversion = bookDecisions.All(decision => acceptedConversionParts.Contains(decision.Item))
                    ? acceptedRequest : null;
                var recordingPaths = bookDecisions.Select(decision => recordingSourcePaths.GetValueOrDefault(decision.Item)
                        ?? TrackedDownloadAlreadyImported.GetSourcePaths(decision.Item).ToArray())
                    .Distinct().SelectMany(paths => paths).Distinct(PathEqualityComparer.Instance).ToArray();
                var failedRecordingFiles = failedReplacementFiles[book.Id].Where(result => manualReplaceExisting ||
                    book.MediaType != BookMediaType.Audiobook || result.ImportDecision.Item?.Edition?.Id is not > 0 ||
                    result.ImportDecision.Item.Edition.Id == localBook.Edition?.Id).ToList();

                // Check if any existing files would be replaced
                var existingFiles = _mediaFileService.GetFilesByBook(book.Id);
                if (existingFiles.Count > 0 && downloadClientItem?.DownloadId.IsNotNullOrWhiteSpace() == true && previouslyImportedFileSources == null)
                {
                    var history = importHistory.Get(downloadClientItem.DownloadId, _historyService.FindByDownloadId);
                    previouslyImportedFileSources = TrackedDownloadAlreadyImported.GetImportedFileSources(history);
                }

                var sameGrabFiles = existingFiles.Where(file => TrackedDownloadAlreadyImported.IsFileFromDownload(
                    file, downloadClientItem?.DownloadId, previouslyImportedFileSources, localBook.AlternativeCopyGrabDate)).ToList();
                recordingPaths = recordingPaths.Concat(sameGrabFiles.Where(file => file.EditionId == localBook.Edition?.Id)
                        .SelectMany(file => file.MatchProvenance?.ImportSourcePaths?.Count > 0
                            ? file.MatchProvenance.ImportSourcePaths
                            : previouslyImportedFileSources?.GetValueOrDefault(file.Id) ?? new List<string>()))
                    .Distinct(PathEqualityComparer.Instance).ToArray();
                var replacementCandidates = manualReplaceExisting
                    ? ManualImportReplacementFiles.Get(existingFiles, bookDecisions.Select(decision => decision.Item).ToList(), manualSources)
                    : existingFiles.Except(sameGrabFiles).ToList();
                if (localBook.IsIncludedMedia && (replacementCandidates.Count > 0 ||
                    EditionPinPolicy.FindConflictingProtectedEdition(book, book.Editions, localBook.Edition.Id) != null))
                {
                    importResults.AddRange(PolicyRefusals(bookDecisions, downloadClientItem,
                        "The book is no longer missing or conflicts with the selected edition. Existing files and selection were kept.", recordingPaths));
                    continue;
                }

                var replacesRecording = replaceExisting && replacementCandidates.Count > 0;
                var atomicBookImport = replacesRecording || manualReplaceExisting;
                // Fresh imports can retain successful parts. Replacing an old recording or
                // changing selected tracked rows still requires the complete selected set.
                var requiresCompleteReplacement = replacesRecording || (manualReplaceExisting &&
                    bookDecisions.Any(decision => manualSourcePaths.Contains(decision.Item.Path)));
                if (requiresCompleteReplacement && failedRecordingFiles.Count > 0)
                {
                    const string reason = "Replacement cancelled because a selected file was rejected. Existing files were kept.";
                    importResults.AddRange(bookDecisions.Select(decision => new ImportResult(decision, reason)));
                    continue;
                }

                if (manualReplaceExisting && localBook.ConfirmedReplacementFiles != null &&
                    (localBook.ConfirmedReplacementFiles.Count(file => file.BookId == book.Id) != replacementCandidates.Count ||
                     replacementCandidates.Any(file => !ManualImportReplacementFiles.IsConfirmed(file, localBook.ConfirmedReplacementFiles))))
                {
                    var reason = localBook.MatchProvenance?.SelectionSource == "user_metadata"
                        ? $"This selection resolved to local book '{book.Title}' (Book ID {book.Id}), which already has files. Select that local book in Manual Import and review its replacement files."
                        : "Existing files changed since confirmation. Review the replacement files again.";
                    importResults.AddRange(bookDecisions.Select(decision => new ImportResult(decision, reason)));
                    continue;
                }
                try
                {
                    if (manualReplaceExisting && localBook.ConfirmedReplacementFiles != null)
                    {
                        _mediaFileService.ValidateReplacementFiles(replacementCandidates, localBook.ConfirmedReplacementFiles);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "[IMPORT-CONFIRMATION] Replacement preflight refused BookId={0}, DownloadId={1}", book.Id, downloadClientItem?.DownloadId);
                    importResults.AddRange(bookDecisions.Select(decision => new ImportResult(decision, ex.Message)));
                    continue;
                }

                var sameGrabSources = sameGrabFiles.ToDictionary(file => file.Id, file =>
                    file.MatchProvenance?.ImportSourcePaths?.Count > 0 ? file.MatchProvenance.ImportSourcePaths :
                        previouslyImportedFileSources?.GetValueOrDefault(file.Id) ?? new List<string>());
                var completedPaths = sameGrabFiles.Where(file => File.Exists(file.Path))
                    .SelectMany(file => sameGrabSources[file.Id].Select(path => (Path: path, file.EditionId)))
                    .ToLookup(source => source.Path, source => source.EditionId, PathEqualityComparer.Instance);
                var completedDecisions = bookDecisions.Where(decision => !manualReplaceExisting).Where(decision =>
                {
                    var sources = TrackedDownloadAlreadyImported.GetSourcePaths(decision.Item).ToList();
                    return sources.Count > 0 && sources.All(path => decision.Item.IsManualImport
                        ? completedPaths[path].Contains(decision.Item.Edition.Id)
                        : completedPaths.Contains(path));
                }).ToList();
                var completedImportResults = new List<ImportResult>();
                foreach (var group in completedDecisions.GroupBy(decision => (decision.Item.IsManualImport, decision.Item.Edition?.Id)))
                {
                    // The requested association is already durable; finish settings once per edition without retransferring files or replaying import events.
                    var item = group.First().Item;
                    var error = item.IsManualImport
                        ? SelectImportedEdition(item, book, item.Edition)
                        : "Source file was already imported from this download";
                    foreach (var decision in group)
                    {
                        var result = new ImportResult(decision, item.IsManualImport ? ImportResultType.Imported : ImportResultType.Skipped);
                        if (error != null)
                        {
                            result.Errors.Add(error);
                        }

                        importResults.Add(result);
                        completedImportResults.Add(result);
                    }
                }

                FinishManualImportSettings(completedImportResults);

                bookDecisions = bookDecisions.Except(completedDecisions).ToList();
                if (bookDecisions.Count == 0)
                {
                    continue;
                }

                var completedPartsStarted = Stopwatch.GetTimestamp();
                var completedParts = RecordingCustomFormatHelper.GetCompletedParts(
                    bookDecisions.Select(decision => decision.Item).ToList(), sameGrabFiles,
                    downloadClientItem?.DownloadId, previouslyImportedFileSources,
                    completedDecisions.Select(decision => decision.Item), replacementCandidates.Select(file => file.Id).ToHashSet());
                _logger.Debug("[IMPORT][COMPLETED-PARTS] selected={0}, stored={1}, completed={2}, elapsedMs={3}",
                    bookDecisions.Count, sameGrabFiles.Count, completedParts.Count, Stopwatch.GetElapsedTime(completedPartsStarted).TotalMilliseconds);
                PartAssignmentHelper.NormalizeLocalBooksByEdition(bookDecisions.Select(decision => decision.Item).Concat(completedParts).ToList());

                var sourcePolicyInputs = bookDecisions.Select(decision =>
                {
                    var input = decision.Item.ShallowClone();
                    input.Author ??= author;
                    if (downloadClientItem != null && !manualImport)
                    {
                        input.Quality = acceptedConversion != null
                            ? new QualityModel((Qualities.Quality)acceptedConversion.TargetQualityId)
                            : QualityConversionHelper.GetEffectiveQualityAfterPlannedConversion(author, input.Quality);
                    }

                    return input;
                }).ToList();
                var policyRejection = acceptedConversion == null
                    ? GetBookGroupQualityRejection(sourcePolicyInputs, author, downloadForced) : null;
                if (policyRejection != null)
                {
                    importResults.AddRange(PolicyRefusals(bookDecisions, downloadClientItem, policyRejection, recordingPaths));
                    continue;
                }

                var currentItems = sourcePolicyInputs.ToHashSet();
                var policyInputs = RecordingCustomFormatHelper.GroupForScoring(sourcePolicyInputs.Concat(completedParts))
                    .Where(recording => recording.Any(currentItems.Contains))
                    .Select(recording => RecordingCustomFormatHelper.CreateInput(recording)).ToList();
                var incomingFormats = new Dictionary<LocalBook, List<CustomFormat>>();
                policyRejection = acceptedConversion == null
                    ? policyInputs.Select(input => GetCustomFormatImportRejectionReason(input, author,
                            author.GetQualityProfileForQuality(input.Quality.Quality), downloadForced, incomingFormats))
                        .FirstOrDefault(reason => reason != null)
                    : null;
                if (policyRejection != null)
                {
                    importResults.AddRange(PolicyRefusals(bookDecisions, downloadClientItem, policyRejection, recordingPaths));
                    continue;
                }

                if (!manualImport && replaceExisting)
                {
                    var replacementDecision = CheckReplacement(policyInputs,
                        replacementCandidates, qualityProfile, downloadForced, incomingFormats, existingFiles);
                    if (replacementDecision.Reason != null)
                    {
                        _logger.Info("[IMPORT-REPLACEMENT] BookId={0}, DownloadId={1}: {2}",
                            book.Id, downloadClientItem?.DownloadId, replacementDecision.Reason);
                        importResults.AddRange(PolicyRefusals(bookDecisions, downloadClientItem,
                            replacementDecision.Reason, recordingPaths, replacementDecision.Result));
                        continue;
                    }
                }

                // Check before conversion or pin protection can create another physical copy.
                var additionalCopy = qualityProfile?.UpgradeAllowed == false && replaceExisting && replacementCandidates.Count > 0 && !manualReplaceExisting;
                if (additionalCopy)
                {
                    var (duplicateReason, retryComparison) = GetAdditionalCopyDuplicateReason(bookDecisions, existingFiles, downloadClientItem?.DownloadId);
                    if (duplicateReason != null)
                    {
                        importResults.AddRange(retryComparison
                            ? bookDecisions.Select(decision => new ImportResult(decision, ImportResultType.Pending, duplicateReason))
                            : PolicyRefusals(bookDecisions, downloadClientItem, duplicateReason, recordingPaths));
                        continue;
                    }
                }

                var approvedReplacementIds = !manualImport && replaceExisting && qualityProfile?.UpgradeAllowed == true
                    ? replacementCandidates.Where(file => bookDecisions.Any(decision => decision.Item.Edition.Id == file.EditionId))
                        .Select(file => file.Id).ToHashSet()
                    : new HashSet<int>();
                foreach (var decision in bookDecisions)
                {
                    decision.Item.ApprovedReplacementFileIds = approvedReplacementIds;
                }

                // Log the existing files situation
                _logger.Debug("[UPGRADE-CHECK] Book '{0}' (ID: {1}) has {2} existing files",
                    book.Title, book.Id, existingFiles.Count);
                if (existingFiles.Any())
                {
                    foreach (var file in existingFiles)
                    {
                        _logger.Debug("[UPGRADE-CHECK]   Existing file: {0} (Quality: {1})",
                            file.Path, file.Quality?.Quality?.Name ?? "Unknown");
                    }
                }

                // CRITICAL BUG FIX: Only consider it a replacement if there are ACTUAL files to replace
                // If the book is "Missing" (no files), we should import to it, not create a duplicate
                var filesToReplace = replacementCandidates.Any() && replaceExisting;

                _logger.Debug("[UPGRADE-CHECK] replaceExisting={0}, existingFiles.Any()={1}, filesToReplace={2}, UpgradeAllowed={3}",
                    replaceExisting, existingFiles.Any(), filesToReplace, qualityProfile?.UpgradeAllowed ?? true);

                    var conversionFailures = downloadClientItem == null ? new List<ImportResult>()
                        : failedReplacementFiles[0].Concat(failedRecordingFiles).ToList();
                    var conversionResult = ConvertBookGroupIfNeeded(bookDecisions, book, author, downloadClientItem, replaceExisting, conversionFailures, conversionJob, acceptedConversion);
                    conversionWorkFolder = conversionResult.WorkFolder;
                    if (!conversionResult.Failed && conversionResult.Decisions == null)
                    {
                        foreach (var decision in bookDecisions)
                        {
                            importResults.Add(new ImportResult(decision, ImportResultType.Pending, conversionResult.Error ?? "Waiting for conversion to finish."));
                        }

                        __bookGroupSw.Stop();
                        _logger.Debug("[BOOK-TIMING] BookGroup deferred route=conversion-job title='{0}' elapsed={1}ms", book.Title, __bookGroupSw.ElapsedMilliseconds);
                        continue;
                    }

                    if (conversionResult.Failed)
                    {
                        if (_conversionJobService?.Get(downloadClientItem?.DownloadId)?.Status == ConversionJobStatus.ReadyToImport)
                        {
                            _conversionJobService.Fail(downloadClientItem.DownloadId, conversionResult.Error);
                        }

                        foreach (var decision in bookDecisions)
                        {
                            importResults.Add(new ImportResult(decision, conversionResult.Error));
                        }

                        CleanupConversionWorkFolder(conversionWorkFolder);
                        __bookGroupSw.Stop();
                        _logger.Debug("[BOOK-TIMING] BookGroup skipped route=conversion-failed title='{0}' elapsed={1}ms", book.Title, __bookGroupSw.ElapsedMilliseconds);
                        continue;
                    }

                    bookDecisions = conversionResult.Decisions;
                    var bypassedConversionJob = conversionResult.BypassedJob;
                    var conversionDownloadId = bookDecisions.Any(d => d.Item?.IsGeneratedConversion == true) ? downloadClientItem?.DownloadId : null;
                    // A successful conversion supplies different bytes/paths from its source set.
                    if (additionalCopy && conversionDownloadId != null)
                    {
                        var (duplicateReason, retryComparison) = GetAdditionalCopyDuplicateReason(bookDecisions, existingFiles, downloadClientItem?.DownloadId);
                        if (duplicateReason != null)
                        {
                            if (retryComparison)
                            {
                                importResults.AddRange(bookDecisions.Select(decision => new ImportResult(decision, ImportResultType.Pending, duplicateReason)));
                                continue;
                            }
                            importResults.AddRange(bookDecisions.Select(decision => new ImportResult(decision, duplicateReason)));
                            FailConvertedImport(bookFileConvertedEvents, bookDecisions, author, book, downloadClientItem, duplicateReason);
                            CleanupOrRetainConversionWorkFolder(conversionWorkFolder, bookDecisions, duplicateReason);
                            continue;
                        }
                    }

                    Book newBookInstance = null;
                    Edition newEdition = null;
                    var createdNewBookInstance = false;

                // Automatic imports preserve pins by creating a separate copy when necessary.
                // An explicit manual selection keeps its chosen destination in either import mode.
                if (!manualImport && newBookInstance == null && !bookDecisions.All(decision => decision.Item.AlternativeCopyGrabDate.HasValue))
                {
                    var allEditionsForBook = _editionService.GetEditionsByBook(book.Id);
                    var firstMismatch = bookDecisions.FirstOrDefault(decision =>
                        decision?.Item?.Edition?.Id > 0 &&
                        EditionPinPolicy.FindConflictingProtectedEdition(book, allEditionsForBook, decision.Item.Edition.Id) != null);

                    if (firstMismatch != null)
                    {
                        var protectedEdition = EditionPinPolicy.FindConflictingProtectedEdition(
                            book,
                            allEditionsForBook,
                            firstMismatch.Item.Edition.Id);

                        if (protectedEdition != null)
                        {
                            _logger.Debug("[MANUAL-EDITION-PROTECTION] User pinned edition {0} ('{1}') but file(s) matched to different edition(s). Creating clone to preserve user's selection.",
                                protectedEdition.Id, protectedEdition.Title);

                            var createResult = CreateNewBookInstanceForBatch(firstMismatch, book, author, anyEditionOk: true);
                            if (createResult?.NewBook == null || createResult.NewEdition == null)
                            {
                                foreach (var decision in bookDecisions)
                                {
                                    importResults.Add(new ImportResult(decision, "PINNED_EDITION_DESTINATION_CONFLICT"));
                                }

                                FailConvertedImport(bookFileConvertedEvents, bookDecisions, author, book, downloadClientItem, "PINNED_EDITION_DESTINATION_CONFLICT");
                                CleanupOrRetainConversionWorkFolder(conversionWorkFolder, bookDecisions, "PINNED_EDITION_DESTINATION_CONFLICT");
                                __bookGroupSw.Stop();
                                _logger.Warn("[MANUAL-EDITION-PROTECTION] Could not create a safe destination for BookId={0}; import was not applied", book.Id);
                                continue;
                            }

                            newBookInstance = createResult.NewBook;
                            newEdition = createResult.NewEdition;
                            createdNewBookInstance = true;
                            _logger.Debug("[MANUAL-EDITION-PROTECTION] Created new book instance ID: {0} for files not matching pinned edition",
                                newBookInstance.Id);
                            __route = "manual-edition-protection";
                        }
                    }
                }

                if (additionalCopy)
                {
                    var additionalCopyCollisionReason = GetAdditionalCopyPathCollisionReason(bookDecisions, book, author);
                    if (!string.IsNullOrWhiteSpace(additionalCopyCollisionReason))
                    {
                        _logger.Warn("[ADDITIONAL-COPY] {0}", additionalCopyCollisionReason);
                        FailConvertedImport(bookFileConvertedEvents, bookDecisions, author, book, downloadClientItem, additionalCopyCollisionReason);
                        CleanupOrRetainConversionWorkFolder(conversionWorkFolder, bookDecisions, additionalCopyCollisionReason);
                        __bookGroupSw.Stop();
                        _logger.Debug("[BOOK-TIMING] BookGroup skipped route=additional-copy-collision title='{0}' elapsed={1}ms", book.Title, __bookGroupSw.ElapsedMilliseconds);

                        foreach (var decision in bookDecisions)
                        {
                            importResults.Add(new ImportResult(decision, additionalCopyCollisionReason));
                        }

                        continue;
                    }

                    // These are NEW files for the same book - create a new book instance for multiple copies
                    _logger.Debug("[ADDITIONAL-COPY] Creating new book instance for additional physical copy of '{0}'",
                        book.Title);
                    var createResult = CreateNewBookInstanceForBatch(bookDecisions.First(), book, author, anyEditionOk: true);
                    if (createResult.NewBook != null)
                    {
                        newBookInstance = createResult.NewBook;
                        newEdition = createResult.NewEdition;
                        createdNewBookInstance = true;
                        _logger.Debug("[ADDITIONAL-COPY] Created new book instance with ID: {0} for additional copy",
                            newBookInstance.Id);
                        __route = "additional-copy";
                    }
                }

                    if (conversionDownloadId.IsNotNullOrWhiteSpace())
                    {
                        _conversionTrackingService?.Progress(conversionDownloadId, 99m, "Importing M4B");
                    }

                    // Collect all BookFile objects for this book to batch insert them
                    var bookFilesToAdd = new List<BookFile>();
                    var stagedReplacementIds = new HashSet<int>();
                    var bookImportResults = new List<ImportResult>();
                    // Track all successfully imported book files (including ones already present in the DB)
                    // so BookImportedEvent accurately reflects the work performed.
                    var importedBookFilesForBook = new List<BookFile>();

                _logger.Debug("[BATCH-DEBUG] Processing {0} files for book ID {1}, using {2}",
                    bookDecisions.Count,
                    newBookInstance?.Id ?? book.Id,
                    newBookInstance != null ? "new book instance" : "existing book");

                // Process each file in the book group
                foreach (var decision in bookDecisions)
                {
                    var fileStopwatch = Stopwatch.StartNew();
                    try
                    {
                        ImportResult result;
                        BookFile bookFile;
                        PendingFileCommit pendingFileCommit;
                        Book importedTargetBook;

                        if (newBookInstance != null)
                        {
                            // Import to the new book instance we created for this batch
                            var fileLocalBook = decision.Item;
                            fileLocalBook.Book = newBookInstance;
                            fileLocalBook.Edition = newEdition;
                            importedTargetBook = newBookInstance;
                            (result, bookFile) = ImportFile(decision, newBookInstance, author, false, downloadClientItem, importMode, downloadForced, Array.Empty<BookFile>(), stagedReplacementIds, excludedSources, out pendingFileCommit);
                        }
                        else
                        {
                            // Normal import
                            importedTargetBook = book;
                            (result, bookFile) = ImportFile(decision, book, author, replaceExisting, downloadClientItem, importMode, downloadForced, replacementCandidates, stagedReplacementIds, excludedSources, out pendingFileCommit);
                        }

                            if (result.Result == ImportResultType.Imported && bookFile != null)
                            {
                                if (pendingFileCommit != null && !pendingFileCommit.DatabaseCommitted)
                                {
                                    pendingFileCommits.Add(pendingFileCommit);
                                    stagedReplacementIds.UnionWith(pendingFileCommit.DatabaseRowsToReplace.Select(file => file.Id));
                                    if (bookFile.Id > 0)
                                    {
                                        stagedReplacementIds.Add(bookFile.Id);
                                    }
                                    replacementCandidates.RemoveAll(file => stagedReplacementIds.Contains(file.Id));
                                }

                                importedBookFilesForBook.Add(bookFile);
                                if (importedTargetBook != null)
                                {
                                    importedBookEventContexts.Add((author, importedTargetBook, bookFile, !decision.Item.ExistingFile));
                                }

                                // Ensure TrackImportedEvent has the author relationship required by HistoryService.
                                decision.Item.Author ??= author;

                                var convertedEvent = CreateGeneratedConversionEvent(decision.Item, bookFile, author, importedTargetBook, downloadClientItem);
                                if (convertedEvent != null)
                                {
                                    bookFileConvertedEvents.Add(convertedEvent);
                                }

                            // TrackImportedEvent needs the BookFile object reference; its Id will be populated after AddMany.
                            // Use "!ExistingFile" to match Readarr behavior: existing library files don't create history rows.
                            trackImportedEvents.Add(new TrackImportedEvent(
                                decision.Item,
                                bookFile,
                                new List<BookFile>(),
                                !decision.Item.ExistingFile || decision.Item.IsManualImport,
                                downloadClientItem) { HistoryContext = importHistory });

                            // Include deferred stale-row updates in the same commit as new files.
                            if (bookFile.Id == 0 || pendingFileCommit?.DatabaseCommitted == false)
                            {
                                // Collect the file for batch insertion
                                bookFilesToAdd.Add(bookFile);
                            }
                        }

                        importResults.Add(result);
                        bookImportResults.Add(result);
                        fileStopwatch.Stop();
                        _logger.Debug("[PERFORMANCE] File import for '{0}' took {1}ms",
                            Path.GetFileName(decision.Item.Path), fileStopwatch.ElapsedMilliseconds);
                    }
                    catch (Exception ex)
                    {
                        fileStopwatch.Stop();
                        _logger.Error(ex, "[CLEAN-IMPORT] Failed to import file: {0} after {1}ms",
                            decision.Item.Path, fileStopwatch.ElapsedMilliseconds);
                        var failedResult = new ImportResult(decision, "Import failed: " + ex.Message);
                        importResults.Add(failedResult);
                        bookImportResults.Add(failedResult);
                    }
                }

                if (requiresCompleteReplacement && newBookInstance == null && bookImportResults.Any(result => result.Result != ImportResultType.Imported))
                {
                    _logger.Warn("[IMPORT-ROLLBACK] Restoring BookId={0}, DownloadId={1}, preparedFiles={2}, failedSelections={3}",
                        book.Id, downloadClientItem?.DownloadId, pendingFileCommits.Count,
                        bookImportResults.Count(result => result.Result != ImportResultType.Imported));
                    RollbackPendingFileCommits(pendingFileCommits);
                    var rolledBackFiles = bookFilesToAdd.ToHashSet();
                    var rolledBackInputs = trackImportedEvents.Where(message => rolledBackFiles.Contains(message.ImportedBook))
                        .Select(message => message.BookInfo).ToHashSet();
                    trackImportedEvents.RemoveAll(message => rolledBackFiles.Contains(message.ImportedBook));
                    importedBookEventContexts.RemoveAll(context => rolledBackFiles.Contains(context.File));
                    var rolledBackPaths = rolledBackFiles.Select(file => file.Path).ToHashSet(PathEqualityComparer.Instance);
                    bookFileConvertedEvents.RemoveAll(message => message.ImportedPath != null && rolledBackPaths.Contains(message.ImportedPath));
                    var rolledBackResults = bookImportResults.Where(result => result.Result == ImportResultType.Imported &&
                        rolledBackInputs.Contains(result.ImportDecision.Item)).ToList();
                    foreach (var result in rolledBackResults)
                    {
                        importResults.Remove(result);
                        importResults.Add(new ImportResult(result.ImportDecision,
                            "Replacement cancelled because another selected file failed. Existing files were restored."));
                    }

                    FailConvertedImport(bookFileConvertedEvents, bookDecisions, author, book, downloadClientItem, "Replacement failed; existing files were restored.");
                    CleanupOrRetainConversionWorkFolder(conversionWorkFolder, bookDecisions, "Replacement failed; existing files were restored.");
                    PublishCommittedImportEvents();
                    continue;
                }

                // Batch insert all files for this book at once
                if (bookFilesToAdd.Any())
                {
                    _logger.Debug("[BATCH-DEBUG] Collected {0} files to insert", bookFilesToAdd.Count);
                    for (int i = 0; i < Math.Min(3, bookFilesToAdd.Count); i++)
                    {
                        var f = bookFilesToAdd[i];
                        _logger.Debug("[BATCH-DEBUG] File {0}: EditionId={1}, Path={2}",
                            i, f.EditionId, Path.GetFileName(f.Path));
                    }

                        var batchStopwatch = Stopwatch.StartNew();
                        _logger.Debug("[BATCH-INSERT] Adding {0} files for book '{1}' to database", bookFilesToAdd.Count, book.Title);
                        try
                        {
                            CommitPreparedBookFiles(bookFilesToAdd, pendingFileCommits, book, atomicBookImport && newBookInstance == null);
                        }
                        catch (ReplacementRefusedException ex)
                        {
                            _logger.Warn("[IMPORT-CONFIRMATION] BookId={0}: {1}", book.Id, ex.Message);
                            RollbackPendingFileCommits(pendingFileCommits);
                            var refusedFiles = bookFilesToAdd.ToHashSet();
                            trackImportedEvents.RemoveAll(message => refusedFiles.Contains(message.ImportedBook));
                            importedBookEventContexts.RemoveAll(context => refusedFiles.Contains(context.File));
                            var refusedPaths = refusedFiles.Select(file => file.Path).ToHashSet(PathEqualityComparer.Instance);
                            bookFileConvertedEvents.RemoveAll(message => message.ImportedPath != null && refusedPaths.Contains(message.ImportedPath));
                            foreach (var result in bookImportResults)
                            {
                                importResults.Remove(result);
                            }

                            importResults.AddRange(bookDecisions.Select(decision => new ImportResult(decision, ex.Message)));
                            FailConvertedImport(bookFileConvertedEvents, bookDecisions, author, book, downloadClientItem, ex.Message);
                            CleanupOrRetainConversionWorkFolder(conversionWorkFolder, bookDecisions, ex.Message);
                            continue;
                        }
                        catch (Exception ex)
                        {
                            _logger.Warn(ex, "[IMPORT-ROLLBACK] Primary commit failed for BookId={0}, DownloadId={1}, preparedFiles={2}",
                                book.Id, downloadClientItem?.DownloadId, pendingFileCommits.Count);
                            RollbackPendingFileCommits(pendingFileCommits);
                            groupRolledBack = true;
                            try
                            {
                                FailConvertedImport(bookFileConvertedEvents, bookDecisions, author, book, downloadClientItem, ex.Message, publishImmediately: true);
                            }
                            catch (Exception stateError)
                            {
                                _logger.Error(stateError, "Failed to record converted import failure for download {0}; original import error: {1}", downloadClientItem?.DownloadId, ex.Message);
                            }

                            throw;
                        }

                        var replacedFilesForBook = pendingFileCommits
                            .SelectMany(commit => commit.StagedReplacements)
                            .Select(replacement => replacement.OldFile)
                            .Where(file => file?.Id > 0)
                            .DistinctBy(file => file.Id)
                            .ToList();

                        if (replacedFilesForBook.Any())
                        {
                            replacedBookFilesByBook[(newBookInstance ?? book).Id] = replacedFilesForBook;
                        }

                        FinalizePendingFileCommits(pendingFileCommits);
                        batchStopwatch.Stop();
                        _logger.Debug("[BATCH-INSERT] Successfully inserted {0} files in {1}ms", bookFilesToAdd.Count, batchStopwatch.ElapsedMilliseconds);
                    }
                    else
                    {
                        _logger.Warn("[BATCH-DEBUG] No files collected for batch insertion for book '{0}'", book.Title);
                    }
    
                    if (!atomicBookImport || newBookInstance != null)
                    {
                        var insertedEditions = bookFilesToAdd.Where(file => file.Id > 0).Select(file => file.EditionId).ToHashSet();
                        foreach (var group in bookImportResults
                            .Where(result => result.Result == ImportResultType.Imported && insertedEditions.Contains(result.ImportDecision.Item.Edition.Id))
                            .GroupBy(result => result.ImportDecision.Item.Edition.Id))
                        {
                            var imported = group.First().ImportDecision.Item;
                            var error = SelectImportedEdition(imported, imported.Book ?? book, imported.Edition);
                            if (error != null)
                            {
                                foreach (var result in group)
                                {
                                    result.Errors.Add(error);
                                }
                            }
                        }
                    }

                    if (!atomicBookImport || newBookInstance != null)
                    {
                        FinishManualImportSettings(bookImportResults);
                    }

                    WriteTagsForCommittedFiles(pendingFileCommits);
                    foreach (var commit in pendingFileCommits.Where(commit => commit.DatabaseCommitted && commit.ImportAssociatedFiles))
                    {
                        ImportExtras(commit.LocalBook, commit.BookFile, commit.CopyOnly, replaceExisting);
                    }

                    // If we created a new book instance for this batch but ended up importing nothing into it,
                    // clean it up to avoid accumulating orphan duplicates on retries.
                    if (createdNewBookInstance && newBookInstance != null)
                    {
                        CleanupNewBookInstanceIfEmpty(createdNewBookInstance, newBookInstance, newEdition, importedBookFilesForBook);
                    }

                    __bookGroupSw.Stop();
                    _logger.Debug("[BOOK-TIMING] BookGroup route={0} bookId={1} title='{2}' files={3} elapsed={4}ms",
                    __route,
                    newBookInstance?.Id ?? book.Id,
                    book.Title,
                    bookDecisions.Count,
                    __bookGroupSw.ElapsedMilliseconds);

                    var retainConversionWorkFolder = false;
                    if (conversionDownloadId.IsNotNullOrWhiteSpace())
                    {
                        var generatedResults = bookImportResults
                            .Where(r => r.ImportDecision?.Item?.IsGeneratedConversion == true)
                            .ToList();
                        var generatedImport = generatedResults.FirstOrDefault(r => r.Result == ImportResultType.Imported);

                        if (generatedImport != null)
                        {
                            _conversionTrackingService?.Progress(conversionDownloadId, 100m, "Converted M4B imported");
                            try
                            {
                                _conversionJobService?.Complete(conversionDownloadId);
                            }
                            catch (Exception ex)
                            {
                                _logger.Error(ex, "M4B import committed for download {0}, but conversion completion could not be saved; committed file provenance will recover it", conversionDownloadId);
                                retainConversionWorkFolder = true;
                            }
                            _conversionTrackingService?.Complete(conversionDownloadId);
                        }
                        else
                        {
                            var error = generatedResults
                                .SelectMany(r => r.Errors)
                                .FirstOrDefault(e => e.IsNotNullOrWhiteSpace()) ?? "Converted M4B was not imported.";

                            FailConvertedImport(bookFileConvertedEvents, bookDecisions, author, book, downloadClientItem, error);
                            retainConversionWorkFolder = true;
                        }
                    }

                    if (retainConversionWorkFolder)
                    {
                        CleanupOrRetainConversionWorkFolder(conversionWorkFolder, bookDecisions, "Converted M4B was not imported.");
                    }
                    else
                    {
                        CleanupConversionWorkFolder(conversionWorkFolder);
                    }
                    if (bypassedConversionJob != null && bookImportResults.Count > 0 &&
                        bookImportResults.All(result => result.Result == ImportResultType.Imported))
                    {
                        try
                        {
                            // Originals are durable; retire only the captured inactive conversion
                            // attempt, without claiming a successful M4B encode.
                            var discardedPath = _conversionJobService.Reset(downloadClientItem.DownloadId, bypassedConversionJob);
                            if (discardedPath.IsNotNullOrWhiteSpace())
                            {
                                var message = $"Original files imported. Discarded retained conversion workspace: {discardedPath}";
                                var originalItems = bookDecisions.Select(decision => decision.Item).ToHashSet();
                                foreach (var imported in trackImportedEvents.Where(imported => originalItems.Contains(imported.BookInfo)))
                                {
                                    imported.Message = imported.Message.IsNullOrWhiteSpace() ? message : $"{imported.Message} {message}";
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Error(ex, "Original files imported for download {0}, but prior conversion cleanup/state could not be retired", downloadClientItem.DownloadId);
                        }
                    }

                    PublishCommittedImportEvents();
                }
                catch (Exception ex) when (decisions.All(decision => decision.Item?.IsIncludedMedia == true))
                {
                    if (!groupRolledBack) RollbackPendingFileCommits(pendingFileCommits);
                    var uncommittedFiles = pendingFileCommits.Where(commit => !commit.DatabaseCommitted)
                        .Select(commit => commit.BookFile).ToHashSet();
                    var uncommittedInputs = pendingFileCommits.Where(commit => !commit.DatabaseCommitted)
                        .Select(commit => commit.LocalBook).ToHashSet();
                    trackImportedEvents.RemoveAll(message => uncommittedFiles.Contains(message.ImportedBook));
                    importedBookEventContexts.RemoveAll(context => uncommittedFiles.Contains(context.File));
                    var uncommittedPaths = uncommittedFiles.Select(file => file.Path).ToHashSet(PathEqualityComparer.Instance);
                    bookFileConvertedEvents.RemoveAll(message => message.ImportedPath != null && uncommittedPaths.Contains(message.ImportedPath));
                    importResults.RemoveAll(result => result.Result == ImportResultType.Imported && uncommittedInputs.Contains(result.ImportDecision.Item));
                    var reported = importResults.Select(result => result.ImportDecision.Item).ToHashSet();
                    importResults.AddRange(decisions.Where(decision => !reported.Contains(decision.Item))
                        .Select(decision => new ImportResult(decision, "Import interrupted: " + ex.Message)));
                    _logger.Warn(ex, "[INCLUDED-MEDIA] Import interrupted for download {0}; preserving {1} committed results without discarding conversion work",
                        downloadClientItem?.DownloadId, importResults.Count(result => result.Result == ImportResultType.Imported));
                    throw new IncludedImportException(ex, importResults.ToArray());
                }
            }

            // Unmapped files are now handled at the beginning through ImportOrchestratorV2

            PublishCommittedImportEvents();

            _logger.Debug("[CLEAN-IMPORT] Import complete. Imported: {0}, Failed: {1}",
                importResults.Count(r => r.Result == ImportResultType.Imported),
                importResults.Count(r => r.Result != ImportResultType.Imported && !r.IsIncludedMediaSkip));
            if (_logger.IsDebugEnabled)
            {
                _logger.Debug("[MEMORY] Clean import complete ({0} decisions): {1}", decisions.Count, MemorySnapshot.CaptureDetailed());
            }

            return importResults;
        }

        internal List<ImportDecision<LocalBook>> SelectSingleEbookAlternative(
            List<ImportDecision<LocalBook>> bookDecisions,
            Author author,
            List<ImportResult> importResults)
        {
            var book = bookDecisions.FirstOrDefault()?.Item?.Book;
            if (book?.MediaType != BookMediaType.Ebook)
            {
                return bookDecisions;
            }

            var ebookCandidates = bookDecisions
                .Where(IsAutomaticEbookImportDecision)
                .ToList();

            if (ebookCandidates.Count <= 1)
            {
                return bookDecisions;
            }

            // The profile comparer ranks purely by Items position and never reads
            // Allowed, so a disallowed format placed above the preferred one would
            // win this pick. Prefer allowed formats; fall back to the ranked pick
            // only when the download contains no allowed format at all.
            var allowedCandidates = ebookCandidates
                .Where(candidate => IsQualityAllowedInProfile(author, candidate.Item?.Quality?.Quality))
                .ToList();
            var selectionPool = allowedCandidates.Any() ? allowedCandidates : ebookCandidates;

            var winner = selectionPool.Aggregate((best, candidate) =>
                CompareEbookImportCandidate(candidate, best, author) > 0 ? candidate : best);

            var skipped = ebookCandidates.Where(d => !ReferenceEquals(d, winner)).ToList();
            var selectedQuality = winner.Item?.Quality?.Quality?.Name ?? Quality.Unknown.Name;

            foreach (var decision in skipped)
            {
                var skippedQuality = decision.Item?.Quality?.Quality?.Name ?? Quality.Unknown.Name;
                var reason = $"Skipped duplicate ebook format from the same download; selected {selectedQuality} over {skippedQuality}";
                decision.Reject(new Rejection(reason) { Category = EbookAlternativeRejectionCategory });
                importResults.Add(new ImportResult(decision, reason));
                _logger.Debug("[EBOOK-DUPLICATE-FORMAT] {0}: {1}", book.Title, reason);
            }

            return bookDecisions
                .Where(d => !skipped.Contains(d))
                .ToList();
        }

        internal static bool IsSkippedEbookAlternative(ImportResult result)
        {
            // Completion still requires an imported winner for the same edition before ignoring an ineligible format.
            return result?.Result == ImportResultType.Rejected && result.ImportDecision.Item?.Book?.MediaType == BookMediaType.Ebook &&
                result.ImportDecision.Rejections.Count == 1 && (result.ImportDecision.Rejections[0].Category == EbookAlternativeRejectionCategory ||
                    result.ImportDecision.Rejections[0].IsQualityFilter);
        }

        private static bool IsAutomaticEbookImportDecision(ImportDecision<LocalBook> decision)
        {
            var localBook = decision?.Item;
            if (localBook == null || localBook.IsManualImport)
            {
                return false;
            }

            var quality = localBook.Quality?.Quality;
            if (quality == null)
            {
                return false;
            }

            return QualityMediaTypeHelper.IsEbookQuality(quality) ||
                   (quality == Qualities.Quality.Unknown && localBook.Book?.MediaType == BookMediaType.Ebook);
        }

        private static bool IsQualityAllowedInProfile(Author author, Qualities.Quality quality)
        {
            if (quality == null || quality == Qualities.Quality.Unknown)
            {
                return true;
            }

            var profile = author?.GetQualityProfileForQuality(quality);
            return profile == null ||
                   profile.IsQualityAllowed(quality.Id);
        }

        private static int CompareEbookImportCandidate(
            ImportDecision<LocalBook> left,
            ImportDecision<LocalBook> right,
            Author author)
        {
            var leftQuality = left.Item?.Quality ?? new QualityModel(Qualities.Quality.Unknown);
            var rightQuality = right.Item?.Quality ?? new QualityModel(Qualities.Quality.Unknown);
            var qualityProfile = author?.GetQualityProfileForQuality(leftQuality.Quality) ??
                                 author?.GetQualityProfileForQuality(rightQuality.Quality);

            var qualityCompare = qualityProfile?.Items?.Any() == true
                ? new QualityModelComparer(qualityProfile).Compare(leftQuality, rightQuality)
                : leftQuality.CompareTo(rightQuality);

            if (qualityCompare != 0)
            {
                return qualityCompare;
            }

            var sizeCompare = (left.Item?.Size ?? 0).CompareTo(right.Item?.Size ?? 0);
            if (sizeCompare != 0)
            {
                return sizeCompare;
            }

            return string.Compare(
                right.Item?.Path ?? string.Empty,
                left.Item?.Path ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
        }

        private BookFileConvertedEvent CreateGeneratedConversionEvent(LocalBook localBook, BookFile importedBook, Author author, Book book, DownloadClientItem downloadClientItem, string message = null)
        {
            if (localBook?.IsGeneratedConversion != true)
            {
                return null;
            }

            return new BookFileConvertedEvent(localBook, importedBook, author, book, downloadClientItem, message);
        }

        private void FailConvertedImport(List<BookFileConvertedEvent> events, List<ImportDecision<LocalBook>> bookDecisions, Author author, Book book, DownloadClientItem downloadClientItem, string message, bool publishImmediately = false)
        {
            var convertedLocalBook = bookDecisions
                ?.Select(d => d.Item)
                .FirstOrDefault(i => i?.IsGeneratedConversion == true);

            message = AppendRetainedConversionPath(message, convertedLocalBook);
            var convertedEvent = CreateGeneratedConversionEvent(convertedLocalBook, null, author, book, downloadClientItem, message);
            if (convertedEvent == null)
            {
                return;
            }

            if (_conversionJobService != null)
            {
                _conversionJobService.Fail(downloadClientItem?.DownloadId, message);
            }
            else
            {
                _conversionTrackingService?.Fail(downloadClientItem?.DownloadId, message);
            }

            var hasExisting = events.Any(e =>
                e.ImportedPath.IsNullOrWhiteSpace() &&
                string.Equals(e.ConvertedPath, convertedEvent.ConvertedPath, StringComparison.OrdinalIgnoreCase));

            if (publishImmediately)
            {
                _eventAggregator.PublishEvent(convertedEvent);
            }
            else if (!hasExisting)
            {
                events.Add(convertedEvent);
            }
        }

        private void CleanupNewBookInstanceIfEmpty(bool createdNewBookInstance, Book newBookInstance, Edition newEdition, List<BookFile> importedBookFilesForBook)
        {
            if (!createdNewBookInstance || newBookInstance == null)
            {
                return;
            }

            var importedToNewInstance = importedBookFilesForBook?.Any(bf =>
                bf?.EditionId > 0 && bf.EditionId == newEdition?.Id) == true;

            if (importedToNewInstance)
            {
                return;
            }

            try
            {
                _logger.Warn("[CLEAN-IMPORT] Deleting orphan book instance {0} (no files imported)", newBookInstance.Id);
                _bookService.DeleteBook(newBookInstance.Id, deleteFiles: false);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "[CLEAN-IMPORT] Failed deleting orphan book instance {0}", newBookInstance.Id);
            }
        }

        private (ImportResult result, BookFile bookFile) ImportFile(
            ImportDecision<LocalBook> decision,
            Book book,
            Author author,
            bool replaceExisting,
            DownloadClientItem downloadClientItem,
            ImportMode importMode,
            bool downloadForced,
            IReadOnlyCollection<BookFile> existingFiles,
            HashSet<int> alreadyStagedIds,
            IReadOnlyDictionary<int, List<string>> excludedSources,
            out PendingFileCommit pendingFileCommit)
        {
            pendingFileCommit = null;
            var localBook = decision.Item;
            BookFile relocateExistingFile = null;
            string relocateOriginalPath = null;
            var existingFileAlreadyAtDestination = false;
            var existingFileWasUnmapped = false;
            int? existingFileOriginalEditionId = null;

            try
            {
                    var totalStopwatch = Stopwatch.StartNew();

                // Ensure we have the full book object, not just a partial one
                var bookLoadStopwatch = Stopwatch.StartNew();
                if (book.Id > 0 && string.IsNullOrEmpty(book.CleanTitle))
                {
                    _logger.Debug("[EDITION-IMPORT] Loading full book object for ID {0}", book.Id);
                    book = _bookService.GetBook(book.Id);
                    if (book == null)
                    {
                        _logger.Error("[EDITION-IMPORT] Could not find book with ID {0}", decision.Item.Book.Id);
                        return (new ImportResult(decision, "Book not found in database"), null);
                    }
                }
                bookLoadStopwatch.Stop();
                _logger.Debug("[PERFORMANCE] Book loading took {0}ms", bookLoadStopwatch.ElapsedMilliseconds);
                // Get the edition to import to
                var edition = localBook.Edition;
                if (edition == null)
                {
                    _logger.Warn("[CLEAN-IMPORT] No edition specified for file: {0}", localBook.Path);
                    return (new ImportResult(decision, "No edition specified"), null);
                }

                // Ensure we have the full edition data
                if (edition.Id > 0 && edition.Book == null)
                {
                    edition = _editionService.GetEdition(edition.Id);
                }

                // Edition selection is complete before apply. Import persists the matcher/user decision unchanged.
                var decisionEdition = edition;

                // Get the quality profile for this file type
                var qualityProfile = author.GetQualityProfileForQuality(localBook.Quality.Quality);
                    if (qualityProfile != null)
                    {
                        _logger.Debug("[QUALITY-PROFILE] Using quality profile '{0}' (ID: {1}) for file. UpgradeAllowed: {2}",
                            qualityProfile.Name, qualityProfile.Id, qualityProfile.UpgradeAllowed);
                    }
                    else
                    {
                        _logger.Debug("[QUALITY-PROFILE] Explicit manual grab has no profile for this media type; profile gates are bypassed");
                    }

                    // Check if this file already exists ANYWHERE in the database (not just for this book)
                    var existingFileAtPath = _mediaFileService.GetFileWithPath(localBook.Path);
                    if (existingFileAtPath == null && localBook.IsManualImport && ConversionWorkPath.HasSuccessfulArtifact(localBook.Path))
                    {
                        localBook.ExistingFile = false;
                    }

                    if (existingFileAtPath != null)
                    {
                        // Check if this is an orphaned file (EditionId = 0) that can be relinked
                        if (existingFileAtPath.EditionId == 0)
                        {
                            _logger.Debug("[ORPHAN-RELINK] Found orphaned file at path: {0}; adopting existing row and linking to edition {1}",
                                localBook.Path, edition.Id);

                            relocateExistingFile = existingFileAtPath;
                            relocateOriginalPath = existingFileAtPath.Path;
                            existingFileOriginalEditionId = existingFileAtPath.EditionId;
                            existingFileWasUnmapped = true;
                            existingFileAlreadyAtDestination = true;
                        }
                        else
                        {
                            // Non-orphan file already tracked at this path.
                            // For explicit organize operations we must treat this as a relocation within the same book,
                            // updating the existing DB row's Path after transfer (no "delete and recreate").
                            var existingEdition = existingFileAtPath.Edition ?? _editionService.GetEdition(existingFileAtPath.EditionId);
                            if (existingEdition != null && existingEdition.BookId == book.Id)
                            {
                                relocateExistingFile = existingFileAtPath;
                                relocateOriginalPath = existingFileAtPath.Path;
                                existingFileOriginalEditionId = existingFileAtPath.EditionId;

                                if (localBook.ExistingFile)
                                {
                                    existingFileAlreadyAtDestination = true;
                                    _logger.Debug("[TRACKED-RELINK] Found existing tracked file for same book at destination; will update row in place: {0}", localBook.Path);
                                }
                                else
                                {
                                    _logger.Debug("[RELOCATE] Found existing tracked file for same book at path; will relocate and update row: {0}", localBook.Path);
                                }
                            }
                            else
                            {
                                // File already exists but is linked to a different book/edition - genuine conflict
                                return (new ImportResult(decision, "File already exists in database and is linked to a different book - import blocked"), null);
                            }
                        }
                    }

                // Check if we're replacing files (upgrade from different path)
                // Check for files to replace
                _logger.Debug("[MULTI-FILE] Checking for files to replace. Existing files: {0}",
                    existingFiles.Count);

                    var manualReplaceExisting = replaceExisting && localBook.IsManualImport;
                    var filesToReplace = localBook.IsManualImport && !replaceExisting
                        ? new List<BookFile>()
                        : GetReplacementFiles(existingFiles, localBook, edition.Id, manualReplaceExisting);

                    // Relocation is not an "upgrade/replacement"; do not block or stage/delete other files.
                    if (relocateExistingFile != null)
                    {
                        filesToReplace.Clear();
                    }

                    if (filesToReplace.Any())
                    {
                        _logger.Debug("[MULTI-FILE] Found {0} files to potentially replace for edition {1}",
                            filesToReplace.Count, edition.Id);
                    foreach (var f in filesToReplace)
                    {
                        _logger.Debug("[MULTI-FILE]   File to replace: {0} (ID: {1})", f.Path, f.Id);
                    }
                }

                    if (filesToReplace.Any() && !replaceExisting)
                    {
                        _logger.Debug("[CLEAN-IMPORT] Existing files found for edition but replaceExisting is false: {0}", localBook.Path);
                        return (new ImportResult(decision, "Edition already has files"), null);
                    }

                    // Create the book file
                    // Narrator is derived from the matched edition (metadata server is canonical).
                    var isAudioMedia = BookFile.DetermineMediaType(localBook.Quality) == "audiobook";
                    string narratorName = null;

                    if (isAudioMedia && edition.NarratorNames != null && edition.NarratorNames.Any())
                    {
                        var topTwo = edition.NarratorNames.Take(2).ToList();
                        narratorName = string.Join(" + ", topTwo);
                        _logger.Debug("[CLEAN-IMPORT] Setting narrator from edition: {0}", narratorName);
                    }

                _logger.Debug("[EDITION-IMPORT] Creating BookFile record:");
                _logger.Debug("[EDITION-IMPORT]   Path: {0}", localBook.Path);
                _logger.Debug("[EDITION-IMPORT]   EditionId: {0}", edition.Id);
                _logger.Debug("[EDITION-IMPORT]   Edition Title: '{0}'", edition.Title);
                _logger.Debug("[EDITION-IMPORT]   Narrator from edition: '{0}'", narratorName ?? "NONE");
                _logger.Debug("[EDITION-IMPORT]   MediaType: {0}", BookFile.DetermineMediaType(localBook.Quality));
                _logger.Debug("[EDITION-IMPORT]   Quality: {0}", localBook.Quality);

                var durationSeconds = GetDurationSeconds(localBook);
                    var hasTrackedReleaseEvidence = localBook.DownloadClientBookInfo != null || localBook.SceneSource;
                    // Store observed evidence separately from the nullable correction, so Auto can restore it.
                    var isGraphicAudio = isAudioMedia && AudioProductionDetector.DetectRecording(localBook);
                    var audioProductionType = isAudioMedia ? AudioProductionDetector.GetRecordingProductionType(localBook) : null;
                    var persistedNarrator = localBook.Narrator.IsNotNullOrWhiteSpace() ? localBook.Narrator : narratorName;

                    BookFile bookFile;
                    if (relocateExistingFile != null)
                    {
                        // Prepare a detached row; no persisted or shared in-memory state changes before the group commits.
                        bookFile = relocateExistingFile.CloneForImport();
                        bookFile.EditionId = edition.Id;
                        bookFile.Edition = edition;
                        if (existingFileWasUnmapped)
                        {
                            bookFile.DateAdded = DateTime.UtcNow;
                        }

                        bookFile.Part = localBook.Part > 0 ? localBook.Part : 1;
                        bookFile.PartCount = localBook.PartCount;
                        bookFile.Size = localBook.Size;
                        bookFile.Modified = localBook.Modified;
                        bookFile.Quality = localBook.Quality;
                        bookFile.MediaType = BookFile.DetermineMediaType(localBook.Quality);
                        if (hasTrackedReleaseEvidence)
                        {
                            bookFile.SceneName = localBook.SceneName;
                            bookFile.IndexerFlags = localBook.IndexerFlags;
                        }

                        if (hasTrackedReleaseEvidence || (localBook.IsManualImport && localBook.ReleaseGroup.IsNotNullOrWhiteSpace()))
                        {
                            bookFile.ReleaseGroup = localBook.ReleaseGroup;
                        }

                        if (localBook.AudioProductionOverrideSpecified)
                        {
                            bookFile.AudioProductionOverride = localBook.AudioProductionOverride;
                        }
                        bookFile.IsGraphicAudio = isGraphicAudio || relocateExistingFile.IsGraphicAudio;
                        if (audioProductionType.IsNotNullOrWhiteSpace())
                        {
                            bookFile.AudioProductionType = audioProductionType;
                        }

                        if (persistedNarrator.IsNotNullOrWhiteSpace())
                        {
                            bookFile.Narrator = persistedNarrator;
                        }

                        bookFile.Author = author;
                        bookFile.AllTags = localBook.RawTags?.AllTags ?? bookFile.AllTags;
                        bookFile.DurationSeconds = durationSeconds ?? bookFile.DurationSeconds;
                        bookFile.MediaInfo = MediaDuration.ApplyToMediaInfo(bookFile.MediaInfo, durationSeconds);
                    }
                    else
                    {
                        bookFile = new BookFile
                        {
                            Path = localBook.Path,
                            CalibreId = localBook.CalibreId,
                            Part = localBook.Part > 0 ? localBook.Part : 1,
                            PartCount = localBook.PartCount,
                            Size = localBook.Size,
                            Modified = localBook.Modified,
                            DateAdded = DateTime.UtcNow,
                            Quality = localBook.Quality,
                            SceneName = localBook.SceneName,
                            ReleaseGroup = localBook.ReleaseGroup,
                            IndexerFlags = localBook.IndexerFlags,
                            MediaInfo = MediaDuration.CreateMediaInfo(durationSeconds),
                            EditionId = edition.Id,
                            Edition = edition,
                            MediaType = BookFile.DetermineMediaType(localBook.Quality),
                            IsGraphicAudio = isGraphicAudio,
                            AudioProductionOverride = localBook.AudioProductionOverride,
                            AudioProductionType = audioProductionType,
                            Narrator = persistedNarrator,
                            Author = author,
                            AllTags = localBook.RawTags?.AllTags,
                            DurationSeconds = durationSeconds
                        };
                    }

                    var successfulMatchProvenance = localBook.MatchProvenance;
                    if (successfulMatchProvenance == null && localBook.IsManualImport)
                    {
                        successfulMatchProvenance = MatchProvenance.ManualSelection(author, book, decisionEdition);
                    }

                    if (successfulMatchProvenance != null)
                    {
                        var finalizedProvenance = successfulMatchProvenance.CloneForDestination(author, book, edition);
                        bookFile.MatchProvenance = finalizedProvenance;
                    }

                    // A manually selected conversion can outlive its tracked download. Its
                    // verified receipt preserves the same durable source proof as an automatic import.
                    var receipt = downloadClientItem?.DownloadId.IsNotNullOrWhiteSpace() == true
                        ? null : ConversionWorkPath.GetSuccessfulArtifact(localBook.Path);
                    var importedDownloadId = downloadClientItem?.DownloadId.IsNotNullOrWhiteSpace() == true
                        ? downloadClientItem.DownloadId : receipt?.DownloadId;
                    if (importedDownloadId.IsNotNullOrWhiteSpace())
                    {
                        // Commit source coverage with the file so recovery does not depend on later history events.
                        bookFile.MatchProvenance ??= new MatchProvenance();
                        bookFile.MatchProvenance.ImportDownloadId = importedDownloadId;
                        bookFile.MatchProvenance.ImportSourcePaths = receipt?.Sources?.Select(source => source.Path).ToList()
                            ?? TrackedDownloadAlreadyImported.GetSourcePaths(localBook).ToList();
                        bookFile.MatchProvenance.ImportExcludedSourcePaths = excludedSources.GetValueOrDefault(localBook.Edition.Id) ?? new List<string>();
                    }

                    // A successful link supersedes the why-unmapped/apply-failure scratchpad.
                    bookFile.LastMatchAttempt = null;
                    bookFile.MatchDetails = null;

                // Respect legacy Readarr behavior: if this is an existing file discovered during a library scan,
                // do not move/copy it. Only update DB state and tags.
                if (localBook.ExistingFile && !existingFileAlreadyAtDestination)
                {
                    _logger.Debug("[SCAN] Existing file detected during scan, will not move: {0}", localBook.Path);

                    // Any row at this exact path was already classified above as unmapped,
                    // same-book, or a hard conflict. Re-querying and deleting it here was
                    // unreachable for a valid scan candidate and made the invariant harder
                    // to reason about.
                    pendingFileCommit = new PendingFileCommit
                    {
                        BookFile = bookFile, LocalBook = localBook, Author = author,
                        SourcePath = localBook.Path, DestinationPath = localBook.Path,
                        NewDownload = false, ImportAssociatedFiles = false
                    };

                    // Return the BookFile to be batch inserted by the caller
                    // No file movement, no extras import for existing files
                    _logger.Debug("[SCAN] Tracked existing file without moving: {0}", localBook.Path);
                    return (new ImportResult(decision, ImportResultType.Imported), bookFile);
                }

                if (existingFileAlreadyAtDestination)
                {
                    var editionLinkChanged = existingFileOriginalEditionId.HasValue &&
                                             existingFileOriginalEditionId.Value != bookFile.EditionId;

                    if (existingFileWasUnmapped)
                    {
                        _logger.Debug("[ORPHAN-RELINK] Updating existing unmapped file in place without moving: {0}", localBook.Path);
                    }
                    else if (editionLinkChanged)
                    {
                        _logger.Debug("[TRACKED-RELINK] Updating existing file in place from edition {0} to edition {1}: {2}",
                            existingFileOriginalEditionId.Value,
                            bookFile.EditionId,
                            localBook.Path);
                    }
                    else
                    {
                        _logger.Debug("[TRACKED-RELINK] Refreshing existing file in place without changing its edition: {0}", localBook.Path);
                    }

                    pendingFileCommit = new PendingFileCommit
                    {
                        BookFile = bookFile, LocalBook = localBook, Author = author,
                        SourcePath = localBook.Path, DestinationPath = localBook.Path,
                        NewDownload = false, ImportAssociatedFiles = false,
                        PublishAddedEvent = editionLinkChanged
                    };
                }
                else
                {
                    // Handle file move/copy for new downloads
                    bool copyOnly = !localBook.IsGeneratedConversion &&
                                    (importMode == ImportMode.Copy || !ShouldMoveFile(localBook, author));
                    // Relocation of an already-tracked library file must not create duplicates; force move semantics.
                    if (relocateExistingFile != null)
                    {
                        copyOnly = false;
                    }

                    // A destination created after the group decision cannot acquire replacement permission.
                    {
                        var destinationPath = _bookFileMover.GetImportDestinationPath(bookFile, localBook);
                        if (destinationPath.IsNotNullOrWhiteSpace())
                        {
                            var destinationOwner = _mediaFileService.GetFileWithPath(destinationPath);
                            if (destinationOwner != null && destinationOwner.Id != bookFile.Id)
                            {
                                EnsureDestinationBelongsToBook(destinationOwner, book.Id);
                                EnsureReplacementConfirmed(destinationOwner, localBook);
                            }
                        }
                    }

                    // Upgrade safety: never delete/recycle existing files before the new file is successfully transferred.
                    // Stage existing files by renaming them out of the way, transfer the new file, then recycle/delete DB records.
                    var stagedReplacements = new List<(BookFile OldFile, string BackupPath)>();
                    if (filesToReplace.Any())
                    {
                        var stageStopwatch = Stopwatch.StartNew();
                        foreach (var oldFile in filesToReplace)
                        {
                            // Preserve CalibreId from the replaced file when applicable.
                            if (bookFile.CalibreId == 0 && oldFile.CalibreId != 0)
                            {
                                bookFile.CalibreId = oldFile.CalibreId;
                            }

                            if (!File.Exists(oldFile.Path))
                            {
                                continue;
                            }

                            var backupPath = GetUniqueUpgradeBackupPath(oldFile.Path);
                            try
                            {
                                File.Move(oldFile.Path, backupPath);
                                stagedReplacements.Add((oldFile, backupPath));
                            }
                            catch (Exception ex)
                            {
                                _logger.Error(ex, "[CLEAN-IMPORT] Failed to stage existing file for upgrade: {0}", oldFile.Path);
                                RollbackStagedReplacements(stagedReplacements);
                                return (new ImportResult(decision, $"Failed to stage existing file for upgrade: {ex.Message}"), null);
                            }
                        }
                        stageStopwatch.Stop();
                        _logger.Debug("[PERFORMANCE] Old file staging took {0}ms", stageStopwatch.ElapsedMilliseconds);
                    }

                    try
                    {
                        var transferStopwatch = Stopwatch.StartNew();
                        if (copyOnly)
                        {
                            bookFile = _bookFileMover.CopyBookFile(bookFile, localBook);
                        }
                        else
                        {
                            bookFile = _bookFileMover.MoveBookFile(bookFile, localBook);
                        }
                        transferStopwatch.Stop();
                        _logger.Debug("[PERFORMANCE] File transfer took {0}ms", transferStopwatch.ElapsedMilliseconds);
                    }
                    catch
                    {
                        RollbackStagedReplacements(stagedReplacements);
                        throw;
                    }

                    pendingFileCommit = new PendingFileCommit
                    {
                        BookFile = bookFile,
                        LocalBook = localBook,
                        Author = author,
                        SourcePath = localBook.Path,
                        DestinationPath = bookFile.Path,
                        CopyOnly = copyOnly,
                        NewDownload = relocateExistingFile == null,
                        RenamedFrom = relocateOriginalPath,
                        StagedReplacements = stagedReplacements
                    };

                    pendingFileCommit.DatabaseRowsToReplace.AddRange(filesToReplace);

                    // If the destination path is already tracked, either include it in the staged
                    // upgrade transaction or preserve the established stale-row adoption path.
                    bookFile = ResolveBookFilePathConflict(bookFile, localBook, pendingFileCommit, alreadyStagedIds);
                    if (pendingFileCommit != null)
                    {
                        pendingFileCommit.DestinationPath = bookFile.Path;
                    }

                }

                // Persistence, settings, tags and events share the successful group boundary.
                var importResult = new ImportResult(decision, ImportResultType.Imported);

                totalStopwatch.Stop();
                _logger.Debug("[PERFORMANCE] Total ImportFile took {0}ms for '{1}'",
                    totalStopwatch.ElapsedMilliseconds, Path.GetFileName(localBook.Path));
                _logger.Debug("[EDITION-IMPORT] IMPORT COMPLETE: File '{0}' imported to edition {1} with narrator '{2}'",
                    Path.GetFileName(localBook.Path), edition.Id, narratorName ?? "NONE");

                return (importResult, bookFile);
            }
            catch (Exception ex)
            {
                if (pendingFileCommit != null && !pendingFileCommit.DatabaseCommitted)
                {
                    RollbackPendingFileCommit(pendingFileCommit);
                    pendingFileCommit = null;
                }

                if (ex is ReplacementRefusedException)
                {
                    _logger.Warn("[IMPORT-CONFIRMATION] {0}: {1}", localBook.Path, ex.Message);
                    return (new ImportResult(decision, ex.Message), null);
                }

                _logger.Error(ex, "[CLEAN-IMPORT] Failed to import file: {0}", localBook.Path);
                return (new ImportResult(decision, "Import failed: " + ex.Message), null);
            }
        }

        private static bool RequiresManualImportSettings(LocalBook item) => item.IsManualImport && !item.AlternativeCopyGrabDate.HasValue &&
            (item.DisableReleaseSwitching || item.MatchProvenance?.SelectionSource == "user_metadata");

        private static void ApplyManualImportSettings(Book book, bool metadataSelection)
        {
            book.AnyEditionOk = false;
            if (metadataSelection)
            {
                book.SetMonitored(true);
                book.AddOptions ??= new AddBookOptions();
                book.AddOptions.AddType = BookAddType.Manual;
                book.AddOptions.SearchForNewBook = false;
            }
        }

        private void FinishManualImportSettings(IReadOnlyCollection<ImportResult> results)
        {
            foreach (var group in results
                .Where(result => result.Result == ImportResultType.Imported && result.Errors.Count == 0 &&
                    RequiresManualImportSettings(result.ImportDecision.Item))
                .GroupBy(result => result.ImportDecision.Item.Book.Id))
            {
                var bookId = group.Key;
                if (bookId <= 0)
                {
                    continue;
                }

                try
                {
                    var book = _bookService.GetBook(bookId);
                    if (book == null)
                    {
                        throw new InvalidOperationException($"Files imported but the book could not be found to disable edition switching (BookId={bookId}).");
                    }

                    ApplyManualImportSettings(book, group.Any(result => result.ImportDecision.Item.MatchProvenance?.SelectionSource == "user_metadata"));
                    _bookService.UpdateBook(book);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Files imported but automatic edition switching could not be disabled for BookId={0}", bookId);
                    foreach (var result in group)
                    {
                        result.Errors.Add($"Files imported but automatic edition switching could not be disabled for book {bookId}: {ex.Message}");
                    }
                }
            }
        }

        private static bool RequiresImportedEditionSelection(LocalBook localBook, Edition edition)
        {
            var explicitMetadataSelection = localBook.IsManualImport && localBook.MatchProvenance?.SelectionSource == "user_metadata";
            return !localBook.AlternativeCopyGrabDate.HasValue &&
                !(edition.Monitored && edition.Id == localBook.Edition.Id && (!explicitMetadataSelection || edition.ManualAdd));
        }

        private string SelectImportedEdition(LocalBook localBook, Book book, Edition edition)
        {
            var explicitMetadataSelection = localBook.IsManualImport && localBook.MatchProvenance?.SelectionSource == "user_metadata";
            if (!RequiresImportedEditionSelection(localBook, edition))
            {
                return null;
            }

            try
            {
                book.Editions = _editionService.SetMonitored(edition, explicitMetadataSelection);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "[EDITION-IMPORT] Failed to select imported edition {0} for book {1}", edition.Id, edition.BookId);
                if (localBook.IsManualImport)
                {
                    return $"Files imported but edition {edition.Id} could not be selected for book {edition.BookId}: {ex.Message}";
                }
            }

            return null;
        }

        private IEnumerable<ImportResult> PolicyRefusals(List<ImportDecision<LocalBook>> decisions, DownloadClientItem downloadClientItem,
            string reason, IReadOnlyCollection<string> sourcePaths = null, ImportResultType? requestedResult = null)
        {
            // Optional outcomes are cleaned up together after all recording results are known.
            if (decisions.All(decision => decision.Item.IsIncludedMedia))
                return decisions.Select(decision => PolicyRefusal(decision, reason));

            var retention = downloadClientItem?.DownloadId.IsNotNullOrWhiteSpace() == true
                ? _conversionJobService?.RetainCompletedOutput(downloadClientItem.DownloadId,
                    sourcePaths ?? decisions.SelectMany(decision => TrackedDownloadAlreadyImported.GetSourcePaths(decision.Item)).ToArray(), reason)
                : null;
            retention ??= new ConversionRetentionResult(ConversionRetentionStatus.None, reason);
            if (retention.Status == ConversionRetentionStatus.Pending)
            {
                return decisions.Select(decision => new ImportResult(decision, ImportResultType.Pending, retention.Message));
            }

            if (retention.Status == ConversionRetentionStatus.Blocked)
            {
                return decisions.Select(decision => new ImportResult(decision, retention.Message));
            }

            return decisions.Select(decision => decision.Item.IsIncludedMedia || !requestedResult.HasValue
                ? PolicyRefusal(decision, retention.Message)
                : new ImportResult(decision, requestedResult.Value, retention.Message));
        }

        private ImportResult PolicyRefusal(ImportDecision<LocalBook> decision, string reason)
        {
            if (decision.Item.IsIncludedMedia)
            {
                _logger.Info("[INCLUDED-MEDIA] Skipping optional file '{0}': {1}", decision.Item.Path, reason);
                return ImportResult.SkipIncludedMedia(decision, reason);
            }

            return new ImportResult(decision, reason);
        }

        private string GetBookGroupQualityRejection(List<LocalBook> policyInputs, Author author, bool downloadForced)
        {
            foreach (var input in policyInputs)
            {
                var quality = input.Quality;
                var profile = author.GetQualityProfileForQuality(quality.Quality);
                if (profile == null && !downloadForced)
                {
                    return "No quality profile configured for this file type";
                }

                if (input.IsIncludedMedia && profile != null && !profile.IsQualityAllowed(quality.Quality.Id))
                {
                    return $"The quality is not allowed by profile '{profile.Name}': {quality.Quality.Name}";
                }

            }

            return null;
        }

        private List<CustomFormat> GetRecordingCustomFormats(LocalBook recording,
            IDictionary<LocalBook, List<CustomFormat>> cache)
        {
            if (!cache.TryGetValue(recording, out var formats))
            {
                formats = _customFormatCalculationService?.ParseCustomFormat(recording) ?? new List<CustomFormat>();
                cache[recording] = formats;
            }

            return formats;
        }

        private string GetCustomFormatImportRejectionReason(LocalBook localBook, Author author, QualityProfile qualityProfile, bool downloadForced,
            IDictionary<LocalBook, List<CustomFormat>> incomingFormats)
        {
            if (_customFormatCalculationService == null || localBook?.ExistingFile == true || qualityProfile == null || downloadForced)
            {
                return null;
            }

            if (localBook.Author == null)
            {
                localBook.Author = author;
            }

            var formats = GetRecordingCustomFormats(localBook, incomingFormats);
            var score = qualityProfile.CalculateCustomFormatScore(formats);

            if (score >= qualityProfile.MinFormatScore)
            {
                return null;
            }

            var dramatizedFormat = formats.FirstOrDefault(f =>
                f.BuiltInKey == BuiltInCustomFormats.DramatizedAudioKey ||
                f.Name == BuiltInCustomFormats.DramatizedAudioName);
            var dramatizedReason = dramatizedFormat != null
                ? $"; matched {dramatizedFormat.Name} custom format on file tags"
                : string.Empty;

            return string.Format("Custom Formats {0} have score {1} below Author profile minimum {2}{3}",
                formats.ConcatToString(),
                score,
                qualityProfile.MinFormatScore,
                dramatizedReason);
        }

            private (List<ImportDecision<LocalBook>> Decisions, string WorkFolder, bool Failed, string Error, ConversionJob BypassedJob) ConvertBookGroupIfNeeded(
                List<ImportDecision<LocalBook>> bookDecisions,
                Book book,
                Author author,
                DownloadClientItem downloadClientItem,
                bool replaceExisting,
                IReadOnlyCollection<ImportResult> rejectedTrackedDownloadFiles,
                ConversionJob conversionJob,
                ConversionJobRequest acceptedRequest)
            {
                if (downloadClientItem == null || bookDecisions == null || bookDecisions.Count == 0)
                {
                    return (bookDecisions, null, false, null, null);
                }

                var first = bookDecisions.First().Item;
                var inputFiles = bookDecisions
                    .Select(d => d.Item?.Path)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(p => p, NaturalSortComparer.Instance)
                    .ToArray();

                if (inputFiles.Length == 0)
                {
                    return (bookDecisions, null, false, null, null);
                }

                var manualImport = bookDecisions.All(decision => decision.Item?.IsManualImport == true);
                if (!manualImport && acceptedRequest != null)
                {
                    if (conversionJob.Status is ConversionJobStatus.Queued or ConversionJobStatus.Converting ||
                        _conversionJobService.IsRunning(downloadClientItem.DownloadId))
                    {
                        return (null, conversionJob.WorkFolder, false, null, null);
                    }

                    var completedRequest = _conversionJobService.GetCompletedConversion(downloadClientItem.DownloadId);
                    if (completedRequest != null)
                    {
                        var converted = ReuseConversionOutput(first, bookDecisions, completedRequest.OutputPath,
                            completedRequest.Sources.Select(source => source.Path).ToArray(),
                            new QualityModel((Qualities.Quality)completedRequest.TargetQualityId),
                            completedRequest.TagOptions ?? new ConversionTagOptions(), downloadClientItem.DownloadId);
                        return (new List<ImportDecision<LocalBook>> { new(converted) }, completedRequest.WorkFolder, false, null, null);
                    }

                    const string error = "The completed conversion artifact could not be verified. Retry conversion or use Manual Import.";
                    _conversionJobService.Fail(downloadClientItem.DownloadId, error);
                    PublishConversionFailed(first, inputFiles, book, author,
                        new QualityModel((Qualities.Quality)acceptedRequest.TargetQualityId), acceptedRequest.OutputPath, error, downloadClientItem);
                    return (bookDecisions, null, true, error, null);
                }

                var targetQuality = QualityConversionHelper.GetPlannedConversionTarget(author, first?.Quality);
                if (!manualImport && targetQuality != Qualities.Quality.M4B)
                    return (bookDecisions, null, false, null, null);

                var existingJob = conversionJob;
                ConversionJobRequest existingRequest = null;
                if (existingJob != null)
                {
                    try
                    {
                        existingRequest = JsonSerializer.Deserialize<ConversionJobRequest>(existingJob.RequestJson ?? "{}");
                        if (existingRequest?.DiscardRequested == true)
                        {
                            return (bookDecisions, null, true, "Conversion is waiting for cleanup of the previous discarded attempt. It will resume automatically after cleanup succeeds; check the cleanup error in the logs.", null);
                        }
                    }
                    catch (JsonException) { /* An unreadable ordinary job is handled by the worker. */ }
                }

                var sameGroup = existingRequest?.MatchesSources(book.Id, inputFiles, requireBookIdentity: !manualImport) == true;
                var sharesFiles = existingRequest == null || inputFiles.Any(path => path.PathEquals(existingRequest.OutputPath) ||
                    existingRequest.Sources?.Any(source => path.PathEquals(source.Path)) == true);
                if (existingJob != null && (!manualImport || sharesFiles) &&
                    (existingJob.Status == ConversionJobStatus.Queued || existingJob.Status == ConversionJobStatus.Converting ||
                     existingJob.Status == ConversionJobStatus.Cancelling || _conversionJobService.IsRunning(downloadClientItem.DownloadId)))
                {
                    if (manualImport)
                    {
                        return (bookDecisions, null, true, "This download has an active conversion. Stop it and wait for the worker to finish before importing original files.", null);
                    }

                    return (null, existingJob.WorkFolder, false, null, null);
                }

                // Manual Import imports the selected files. Retry Import owns conversion.
                if (manualImport)
                {
                    if (inputFiles.Length == 1 && inputFiles[0].PathEquals(existingRequest?.OutputPath) && existingRequest?.Sources?.Count > 0)
                    {
                        var converted = CreateGeneratedConversionLocalBook(first, bookDecisions, inputFiles[0],
                            existingRequest.Sources.Select(source => source.Path).ToArray(), first.Quality,
                            existingRequest.TagOptions ?? new ConversionTagOptions());
                        return (new List<ImportDecision<LocalBook>> { new(converted) }, existingJob.WorkFolder, false, null, null);
                    }
                    return (bookDecisions, null, false, null, sameGroup ? existingJob : null);
                }

                if (inputFiles.All(p => Path.GetExtension(p).Equals(".m4b", StringComparison.OrdinalIgnoreCase)))
                {
                    return (bookDecisions, null, false, null, null);
                }

                // Keep a ready attempt with changed source signatures visible to the invalid-artifact
                // refusal below; otherwise Enqueue keeps returning the same ready job indefinitely.
                if (!sameGroup && !(existingJob?.Status == ConversionJobStatus.ReadyToImport && sharesFiles)) existingJob = null;

                if (rejectedTrackedDownloadFiles.Count > 0)
                {
                    var failures = string.Join("; ", rejectedTrackedDownloadFiles.Select(result =>
                        $"{result.ImportDecision.Item?.Path}: {string.Join("; ", result.Errors)}"));
                    var error = $"Conversion skipped because a required file could not be imported or identified: {failures}. Resolve the reported file failure, then retry import. Manual Import imports the files you select as-is.";
                    _conversionTrackingService?.Fail(downloadClientItem.DownloadId, error);
                    PublishConversionFailed(first, inputFiles, book, author, new QualityModel(Qualities.Quality.M4B), null, error, downloadClientItem);
                    return (bookDecisions, null, true, error, null);
                }

                var convertedQuality = new QualityModel(Qualities.Quality.M4B);

                if (!_m4bConversionService.CanConvert(inputFiles))
                {
                    var error = "Conversion to M4B is enabled, but one or more files are not compatible with the M4B converter.";
                    _conversionTrackingService?.Fail(downloadClientItem.DownloadId, error);
                    PublishConversionFailed(first, inputFiles, book, author, convertedQuality, null, error, downloadClientItem);
                    return (bookDecisions, null, true, error, null);
                }

                var outputName = ConversionWorkPath.SafeName(inputFiles.Length == 1 ? Path.GetFileNameWithoutExtension(inputFiles[0]) : book.Title);
                string workRoot = null;
                string workFolder = null;
                string outputPath = null;

                try
                {
                    var finalDestinationPath = GetConvertedImportDestinationPath(first, book, author, outputName, inputFiles, convertedQuality);
                    var finalDestinationFolder = Path.GetDirectoryName(finalDestinationPath);
                    if (finalDestinationFolder.IsNullOrWhiteSpace())
                    {
                        var error = "Unable to determine destination folder for converted M4B.";
                        _conversionTrackingService?.Fail(downloadClientItem.DownloadId, error);
                        PublishConversionFailed(first, inputFiles, book, author, convertedQuality, null, error, downloadClientItem);
                        return (bookDecisions, null, true, error, null);
                    }

                    var destinationConflict = GetConversionDestinationConflictReason(finalDestinationPath, first, replaceExisting);
                    if (destinationConflict.IsNotNullOrWhiteSpace())
                    {
                        _conversionTrackingService?.Fail(downloadClientItem.DownloadId, destinationConflict);
                        PublishConversionFailed(first, inputFiles, book, author, convertedQuality, null, destinationConflict, downloadClientItem);
                        return (bookDecisions, null, true, destinationConflict, null);
                    }

                    var downloadFolderName = ConversionWorkPath.SafeName(downloadClientItem.DownloadId ?? Guid.NewGuid().ToString("N"));
                    workRoot = Path.Combine(finalDestinationFolder, ".chaptarr-conversions", downloadFolderName);
                    workFolder = Path.Combine(workRoot, Guid.NewGuid().ToString("N"));

                    var outputFileName = Path.GetFileName(finalDestinationPath);
                    if (outputFileName.IsNullOrWhiteSpace())
                    {
                        outputFileName = outputName + ".m4b";
                    }

                    outputPath = Path.Combine(workFolder, outputFileName);
                    var tagOptions = ConversionTagProposalBuilder.BuildOptions(
                        bookDecisions.Select(d => d.Item),
                        book,
                        author,
                        first.Edition,
                        _containmentValidator,
                        _configService?.AudiobookConversionTagMode);
                    var sourceDuration = GetAudiobookConversionSourceDuration(inputFiles);
                    TryApplyMatchedEditionChapters(tagOptions, first.Edition, sourceDuration, book);
                    TryApplySourceSidecarCover(tagOptions, inputFiles, book, first.Edition);
                    ApplyConversionCoverFallback(tagOptions, book, first.Edition);
                    ConversionTagProposalBuilder.RefreshManifestJson(tagOptions, bookDecisions.Select(d => d.Item));
                    var audioBitrate = GetAudiobookConversionBitrate(inputFiles);
                    var audioChannels = GetAudiobookConversionAudioChannels();

                    if (TryFindReusableConversionArtifact(downloadClientItem.DownloadId, workRoot, inputFiles, convertedQuality, tagOptions, audioBitrate, audioChannels, out var reusableOutputPath))
                    {
                        var reusableLocalBook = ReuseConversionOutput(first, bookDecisions, reusableOutputPath,
                            inputFiles, convertedQuality, tagOptions, downloadClientItem.DownloadId);
                        return (new List<ImportDecision<LocalBook>> { new ImportDecision<LocalBook>(reusableLocalBook) }, Path.GetDirectoryName(reusableOutputPath), false, null, null);
                    }

                    if (existingJob != null && (existingJob.Status == ConversionJobStatus.Failed || existingJob.Status == ConversionJobStatus.Cancelled))
                    {
                        var error = existingJob.Error ?? existingJob.Message ?? "M4B conversion did not complete.";
                        PublishConversionFailed(first, inputFiles, book, author, convertedQuality, existingJob.OutputPath, error, downloadClientItem);
                        return (bookDecisions, null, true, error, null);
                    }

                    if (existingJob?.Status == ConversionJobStatus.ReadyToImport)
                    {
                        var error = "The completed conversion artifact cannot be reused because the sources or conversion settings changed. Waiting to evaluate the current files and rebuild the conversion.";
                        _conversionJobService.Reset(downloadClientItem.DownloadId, existingJob);
                        _logger.Warn("Download {0}: {1}", downloadClientItem.DownloadId, error);
                        return (null, null, false, error, null);
                    }

                    var estimate = EstimateConversionWorkspace(inputFiles);
                    if (!HasEnoughConversionWorkspaceSpace(finalDestinationFolder, estimate, out var freeSpaceError))
                    {
                        _conversionTrackingService?.Fail(downloadClientItem.DownloadId, freeSpaceError);
                        PublishConversionFailed(first, inputFiles, book, author, convertedQuality, outputPath, freeSpaceError, downloadClientItem);
                        return (bookDecisions, null, true, freeSpaceError, null);
                    }

                    // Previous successful attempts remain available until replacement/import/discard succeeds.
                    Directory.CreateDirectory(workFolder);

                    if (_conversionJobService != null)
                    {
                        var sources = BuildConversionArtifactSourceSignatures(inputFiles);
                        if (sources == null || sources.Count == 0)
                        {
                            var sourceIdentityError = "Unable to capture stable source-file identities for M4B conversion.";
                            _conversionJobService.Fail(downloadClientItem.DownloadId, sourceIdentityError);
                            return (bookDecisions, workFolder, true, sourceIdentityError, null);
                        }

                        var request = new ConversionJobRequest
                        {
                            DownloadId = downloadClientItem.DownloadId,
                            DownloadClientId = downloadClientItem.DownloadClientInfo?.Id ?? 0,
                            BookId = book.Id,
                            EditionId = first.Edition.Id,
                            BookTitle = book.Title,
                            WorkRoot = workRoot,
                            WorkFolder = workFolder,
                            OutputPath = outputPath,
                            Sources = sources.Select(source => new ConversionArtifactSource
                            {
                                Path = source.Path,
                                Size = source.Size,
                                ModifiedUtcTicks = source.ModifiedUtcTicks
                            }).ToList(),
                            TargetQualityId = convertedQuality.Quality.Id,
                            TargetQualityName = convertedQuality.Quality.Name,
                            AudioBitrate = audioBitrate,
                            AudioChannels = audioChannels,
                            ExpectedSourceDurationTicks = sourceDuration.Ticks,
                            TagSignature = GetConversionTagSignature(tagOptions),
                            TagOptions = tagOptions
                        };

                        var queued = _conversionJobService.Enqueue(request);
                        return (null, queued.WorkFolder, false, null, null);
                    }

                    throw new InvalidOperationException("The conversion job service is unavailable; conversion cannot start safely.");
                }
                catch (OperationCanceledException)
                {
                    var targetQualityName = convertedQuality.Quality.Name;
                    var error = $"{targetQualityName} conversion was cancelled.";
                    _logger.Info("[CONVERSION] Cancelled {0} conversion for '{1}'. Import stopped.", targetQualityName, book.Title);
                    CleanupConversionWorkFolder(workFolder);
                    _conversionTrackingService?.Cancelled(downloadClientItem.DownloadId, error);
                    return (bookDecisions, null, true, error, null);
                }
                catch (Exception ex) when (ex is not DbException)
                {
                    _logger.Error(ex, "[CONVERSION] Failed converting files to M4B for '{0}'", book.Title);
                    _conversionTrackingService?.Fail(downloadClientItem.DownloadId, ex.Message);
                    PublishConversionFailed(first, inputFiles, book, author, convertedQuality, outputPath, "M4B conversion failed: " + ex.Message, downloadClientItem);
                    if (!ConversionWorkPath.HasSuccessfulArtifact(outputPath)) CleanupConversionWorkFolder(workFolder);
                    return (bookDecisions, null, true, "M4B conversion failed: " + ex.Message, null);
                }
            }

            private LocalBook ReuseConversionOutput(LocalBook first, List<ImportDecision<LocalBook>> decisions,
                string outputPath, string[] sourcePaths, QualityModel quality, ConversionTagOptions tagOptions, string downloadId)
            {
                _conversionTrackingService?.Start(downloadId, quality.Quality.Id, quality.Quality.Name, "Using retained M4B");
                _conversionTrackingService?.Progress(downloadId, 97m, "Using retained M4B");
                var converted = CreateGeneratedConversionLocalBook(first, decisions, outputPath, sourcePaths, quality, tagOptions);
                _logger.Info("[CONVERSION] Reusing retained converted M4B for '{0}': {1}", first.Book.Title, outputPath);
                return converted;
            }

            private void TryApplyMatchedEditionChapters(ConversionTagOptions tagOptions, Edition edition, TimeSpan sourceDuration, Book book)
            {
                if (tagOptions == null || edition?.Chapters == null || edition.Chapters.Count < 2)
                {
                    return;
                }

                var chapters = edition.Chapters
                    .Where(chapter => chapter != null &&
                                      chapter.StartOffsetMs >= 0 &&
                                      chapter.Title.IsNotNullOrWhiteSpace())
                    .GroupBy(chapter => chapter.StartOffsetMs)
                    .Select(group => group.First())
                    .OrderBy(chapter => chapter.StartOffsetMs)
                    .ToList();

                if (chapters.Count < 2 || sourceDuration <= TimeSpan.Zero)
                {
                    return;
                }

                var referenceDuration = GetMatchedEditionChapterReferenceDuration(edition, chapters);
                if (referenceDuration <= TimeSpan.Zero)
                {
                    return;
                }

                var allowedDifference = AudiobookDurationTolerance.ForMatchingSeconds((int)Math.Round(referenceDuration.TotalSeconds, MidpointRounding.AwayFromZero));
                var actualDifference = (sourceDuration - referenceDuration).Duration();
                if (actualDifference > TimeSpan.FromSeconds(allowedDifference))
                {
                    _logger.Debug(
                        "[CONVERSION] Skipping provider chapters for '{0}' because source duration {1} does not match provider chapter duration {2}; allowed difference is {3}.",
                        book?.Title ?? edition.Title,
                        FormatDuration(sourceDuration),
                        FormatDuration(referenceDuration),
                        FormatDuration(TimeSpan.FromSeconds(allowedDifference)));
                    return;
                }

                var lastStart = TimeSpan.FromMilliseconds(chapters.Last().StartOffsetMs);
                if (lastStart >= sourceDuration)
                {
                    _logger.Debug(
                        "[CONVERSION] Skipping provider chapters for '{0}' because the last chapter starts after the source audio ends.",
                        book?.Title ?? edition.Title);
                    return;
                }

                tagOptions.ChaptersTxtContent = BuildChaptersTxt(chapters, sourceDuration);
                tagOptions.ProviderChapterCount = chapters.Count;
                _logger.Debug("[CONVERSION] Matched provider chapters will be stamped into converted M4B for '{0}' ({1} chapters).", book?.Title ?? edition.Title, chapters.Count);
            }

            private void ApplyConversionCoverFallback(ConversionTagOptions tagOptions, Book book, Edition matchedEdition)
            {
                if (tagOptions == null)
                {
                    return;
                }

                var fallbackPath = tagOptions.Cover.IsNotNullOrWhiteSpace()
                    ? tagOptions.Cover
                    : TryResolveMatchedBookCoverPath(book, matchedEdition);

                tagOptions.CoverPolicySignature = BuildConversionCoverPolicySignature(fallbackPath);

                if (fallbackPath.IsNullOrWhiteSpace() || tagOptions.Cover.IsNotNullOrWhiteSpace())
                {
                    return;
                }

                tagOptions.Cover = fallbackPath;
                _logger.Debug("[CONVERSION] Using matched book cover as fallback for '{0}': {1}", book?.Title ?? matchedEdition?.Title ?? "unknown book", fallbackPath);
            }

            private void TryApplySourceSidecarCover(ConversionTagOptions tagOptions, string[] inputFiles, Book book, Edition matchedEdition)
            {
                if (tagOptions == null || tagOptions.Cover.IsNotNullOrWhiteSpace())
                {
                    return;
                }

                var coverPath = TryFindSourceSidecarCover(inputFiles, book, matchedEdition);
                if (coverPath.IsNullOrWhiteSpace())
                {
                    return;
                }

                tagOptions.Cover = coverPath;
                tagOptions.CoverIsSource = true;
                _logger.Debug("[CONVERSION] Using source sidecar cover art for '{0}': {1}", book?.Title ?? matchedEdition?.Title ?? "unknown book", coverPath);
            }

            private static string TryFindSourceSidecarCover(IEnumerable<string> inputFiles, Book book, Edition matchedEdition)
            {
                var directories = inputFiles?
                    .Where(path => path.IsNotNullOrWhiteSpace())
                    .Select(Path.GetDirectoryName)
                    .Where(path => path.IsNotNullOrWhiteSpace())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList() ?? new List<string>();
                var titleKeys = BuildSourceCoverTitleKeys(book, matchedEdition);

                foreach (var directory in directories)
                {
                    foreach (var baseName in SourceCoverBaseNames)
                    {
                        foreach (var extension in SourceCoverExtensions)
                        {
                            var candidate = Path.Combine(directory, baseName + extension);
                            if (IsUsableSourceCover(candidate))
                            {
                                return candidate;
                            }
                        }
                    }

                    var looseCandidate = TryFindLooseSourceCover(directory, titleKeys);
                    if (looseCandidate.IsNotNullOrWhiteSpace())
                    {
                        return looseCandidate;
                    }
                }

                return null;
            }

            private static string TryFindLooseSourceCover(string directory, IReadOnlyCollection<string> titleKeys)
            {
                try
                {
                    return Directory
                        .EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
                        .Where(path => IsM4bToolCoverExtension(Path.GetExtension(path)))
                        .Where(IsUsableSourceCover)
                        .Select(path => new { Path = path, Rank = SourceCoverRank(path, titleKeys) })
                        .Where(candidate => candidate.Rank < int.MaxValue)
                        .OrderBy(candidate => candidate.Rank)
                        .ThenBy(candidate => Path.GetFileName(candidate.Path), StringComparer.OrdinalIgnoreCase)
                        .Select(candidate => candidate.Path)
                        .FirstOrDefault();
                }
                catch
                {
                    return null;
                }
            }

            private static IReadOnlyCollection<string> BuildSourceCoverTitleKeys(Book book, Edition matchedEdition)
            {
                return new[] { book?.Title, matchedEdition?.Title }
                    .Where(value => value.IsNotNullOrWhiteSpace())
                    .Select(UnicodeComparisonNormalizer.NormalizeKey)
                    .Where(value => value.Length >= 8)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            private static int SourceCoverRank(string path, IReadOnlyCollection<string> titleKeys)
            {
                var name = Path.GetFileNameWithoutExtension(path) ?? string.Empty;

                for (var i = 0; i < SourceCoverBaseNames.Length; i++)
                {
                    if (name.Equals(SourceCoverBaseNames[i], StringComparison.OrdinalIgnoreCase))
                    {
                        return i;
                    }
                }

                if (name.IndexOf("cover", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return SourceCoverBaseNames.Length;
                }

                if (name.IndexOf("front", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return SourceCoverBaseNames.Length + 1;
                }

                var normalizedName = UnicodeComparisonNormalizer.NormalizeKey(name);
                if (normalizedName.Length >= 8 &&
                    titleKeys != null &&
                    titleKeys.Any(titleKey =>
                        titleKey.Contains(normalizedName, StringComparison.OrdinalIgnoreCase) ||
                        normalizedName.Contains(titleKey, StringComparison.OrdinalIgnoreCase)))
                {
                    return SourceCoverBaseNames.Length + 2;
                }

                return int.MaxValue;
            }

            private static bool IsUsableSourceCover(string path)
            {
                if (path.IsNullOrWhiteSpace() || !IsM4bToolCoverExtension(Path.GetExtension(path)))
                {
                    return false;
                }

                try
                {
                    var info = new FileInfo(path);
                    return info.Exists && info.Length > 0;
                }
                catch
                {
                    return false;
                }
            }

            private string TryResolveMatchedBookCoverPath(Book book, Edition matchedEdition)
            {
                if (_coverMapper == null || book == null || book.Id <= 0)
                {
                    return null;
                }

                EnsureCoverEditionsLoaded(book, matchedEdition);

                foreach (var cover in EnumerateConversionCoverCandidates(book, matchedEdition))
                {
                    var coverPath = GetExistingBookCoverPath(book.Id, cover.Extension);
                    if (coverPath.IsNotNullOrWhiteSpace())
                    {
                        return coverPath;
                    }
                }

                var existingCoverPath = TryFindAnyExistingBookCoverPath(book.Id);
                if (existingCoverPath.IsNotNullOrWhiteSpace())
                {
                    return existingCoverPath;
                }

                try
                {
                    _coverMapper.EnsureBookCoversForImport(book);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "[CONVERSION] Unable to ensure matched book cover before M4B conversion for '{0}'", book.Title);
                }

                foreach (var cover in EnumerateConversionCoverCandidates(book, matchedEdition))
                {
                    var coverPath = GetExistingBookCoverPath(book.Id, cover.Extension);
                    if (coverPath.IsNotNullOrWhiteSpace())
                    {
                        return coverPath;
                    }
                }

                // Last-resort sweep for sparse rows where image metadata is missing but a cached cover exists.
                return TryFindAnyExistingBookCoverPath(book.Id);
            }

            private void EnsureCoverEditionsLoaded(Book book, Edition matchedEdition)
            {
                var editions = book.Editions?.Where(e => e != null).ToList() ?? new List<Edition>();

                if (matchedEdition != null && !editions.Any(e => e.Id == matchedEdition.Id))
                {
                    editions.Insert(0, matchedEdition);
                }

                if (editions.Count == 0)
                {
                    try
                    {
                        editions = _editionService.GetEditionsByBook(book.Id)?.Where(e => e != null).ToList() ?? new List<Edition>();
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "[CONVERSION] Unable to load editions for matched book cover lookup: {0}", book.Title);
                    }

                    if (matchedEdition != null && !editions.Any(e => e.Id == matchedEdition.Id))
                    {
                        editions.Insert(0, matchedEdition);
                    }
                }

                book.Editions = editions;
            }

            private IEnumerable<NzbDrone.Core.MediaCover.MediaCover> EnumerateConversionCoverCandidates(Book book, Edition matchedEdition)
            {
                foreach (var cover in GetUsableCoverImages(matchedEdition?.Images))
                {
                    yield return cover;
                }

                foreach (var cover in GetUsableCoverImages(book?.Images))
                {
                    yield return cover;
                }

                foreach (var edition in book?.Editions ?? new List<Edition>())
                {
                    if (matchedEdition != null && edition.Id == matchedEdition.Id)
                    {
                        continue;
                    }

                    foreach (var cover in GetUsableCoverImages(edition.Images))
                    {
                        yield return cover;
                    }
                }
            }

            private static IEnumerable<NzbDrone.Core.MediaCover.MediaCover> GetUsableCoverImages(IEnumerable<NzbDrone.Core.MediaCover.MediaCover> covers)
            {
                return covers?
                    .Where(c => c != null &&
                                c.CoverType == MediaCoverTypes.Cover &&
                                c.Url.IsNotNullOrWhiteSpace() &&
                                IsM4bToolCoverExtension(c.Extension))
                    .GroupBy(c => c.Url, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First()) ?? Enumerable.Empty<NzbDrone.Core.MediaCover.MediaCover>();
            }

            private string TryFindAnyExistingBookCoverPath(int bookId)
            {
                foreach (var extension in new[] { ".jpg", ".jpeg", ".png" })
                {
                    var coverPath = GetExistingBookCoverPath(bookId, extension);
                    if (coverPath.IsNotNullOrWhiteSpace())
                    {
                        return coverPath;
                    }
                }

                return null;
            }

            private static bool IsM4bToolCoverExtension(string extension)
            {
                return extension.IsNotNullOrWhiteSpace() &&
                       (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                       extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                       extension.Equals(".png", StringComparison.OrdinalIgnoreCase));
            }

            private string GetExistingBookCoverPath(int bookId, string extension)
            {
                if (extension.IsNullOrWhiteSpace())
                {
                    return null;
                }

                try
                {
                    var coverPath = _coverMapper.GetCoverPath(bookId, MediaCoverEntity.Book, MediaCoverTypes.Cover, extension);
                    if (coverPath.IsNotNullOrWhiteSpace() && File.Exists(coverPath) && new FileInfo(coverPath).Length > 0)
                    {
                        return coverPath;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "[CONVERSION] Unable to resolve local book cover path for book {0}", bookId);
                }

                return null;
            }

            private static string BuildConversionCoverPolicySignature(string fallbackPath)
            {
                var signature = new StringBuilder("source-cover-v1");

                if (fallbackPath.IsNullOrWhiteSpace())
                {
                    return signature.Append("|db:none").ToString();
                }

                signature.Append("|db:").Append(fallbackPath);

                try
                {
                    var info = new FileInfo(fallbackPath);
                    if (info.Exists)
                    {
                        signature
                            .Append('|')
                            .Append(info.Length.ToString(CultureInfo.InvariantCulture))
                            .Append('|')
                            .Append(info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
                    }
                }
                catch
                {
                    // Best-effort signature only; the path itself still invalidates older coverless artifacts.
                }

                return signature.ToString();
            }

            private static TimeSpan GetMatchedEditionChapterReferenceDuration(Edition edition, IReadOnlyList<EditionChapter> chapters)
            {
                var chapterEndMs = chapters
                    .Where(chapter => chapter.LengthMs > 0)
                    .Select(chapter => chapter.StartOffsetMs + chapter.LengthMs)
                    .DefaultIfEmpty(0)
                    .Max();

                if (chapterEndMs > 0)
                {
                    return TimeSpan.FromMilliseconds(chapterEndMs);
                }

                if (MediaDuration.HasDuration(edition?.DurationSeconds))
                {
                    return TimeSpan.FromSeconds(edition.DurationSeconds.Value);
                }

                return TimeSpan.Zero;
            }

            private static string BuildChaptersTxt(IReadOnlyList<EditionChapter> chapters, TimeSpan sourceDuration)
            {
                var lines = new List<string>
                {
                    "## total-length " + FormatChapterTime(sourceDuration)
                };

                lines.AddRange(chapters.Select(chapter =>
                    FormatChapterTime(TimeSpan.FromMilliseconds(chapter.StartOffsetMs)) + " " + SanitizeChapterTitle(chapter.Title)));

                return string.Join(Environment.NewLine, lines);
            }

            private static string FormatChapterTime(TimeSpan value)
            {
                var hours = (int)Math.Floor(value.TotalHours);
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:D2}:{1:D2}:{2:D2}.{3:D3}",
                    hours,
                    value.Minutes,
                    value.Seconds,
                    value.Milliseconds);
            }

            private static string FormatDuration(TimeSpan value)
            {
                return FormatChapterTime(value);
            }

            private static string SanitizeChapterTitle(string title)
            {
                return string.Join(" ", (title ?? string.Empty)
                    .Replace('\r', ' ')
                    .Replace('\n', ' ')
                    .Replace('\t', ' ')
                    .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            }

            private void PublishConversionFailed(LocalBook source, IEnumerable<string> inputFiles, Book book, Author author, QualityModel targetQuality, string outputPath, string error, DownloadClientItem downloadClientItem)
            {
                _eventAggregator.PublishEvent(new BookFileConversionFailedEvent(source, inputFiles, book, author, targetQuality, outputPath, error, downloadClientItem));
            }

            private LocalBook CreateGeneratedConversionLocalBook(LocalBook first, IReadOnlyList<ImportDecision<LocalBook>> bookDecisions, string outputPath, string[] inputFiles, QualityModel convertedQuality, ConversionTagOptions tagOptions)
            {
                var outputInfo = new FileInfo(outputPath);
                var generated = PlanGeneratedConversionLocalBook(first, outputPath, inputFiles, convertedQuality);
                generated.Size = outputInfo.Length;
                generated.Modified = outputInfo.LastWriteTimeUtc;
                generated.GeneratedConversionOutputSize = outputInfo.Length;
                generated.GeneratedConversionTagMode = tagOptions.Mode;
                generated.GeneratedConversionTagManifestJson = tagOptions.ManifestJson;
                return generated;
            }

            private static LocalBook PlanGeneratedConversionLocalBook(LocalBook first, string outputPath, string[] inputFiles, QualityModel convertedQuality)
            {
                return new LocalBook
                {
                    Path = outputPath,
                    RawTags = first.RawTags,
                    FolderTrackInfo = first.FolderTrackInfo,
                    DownloadClientBookInfo = first.DownloadClientBookInfo,
                    AcoustIdResults = first.AcoustIdResults,
                    Author = first.Author,
                    Book = first.Book,
                    Edition = first.Edition,
                    Quality = convertedQuality,
                    IndexerFlags = first.IndexerFlags,
                    ExistingFile = false,
                    IsGeneratedConversion = true,
                    GeneratedConversionSourcePaths = inputFiles.ToList(),
                    GeneratedConversionOutputPath = outputPath,
                    GeneratedConversionSourceQuality = first.Quality,
                    AdditionalFile = first.AdditionalFile,
                    SceneSource = first.SceneSource,
                    ReleaseGroup = first.ReleaseGroup,
                    IsGraphicAudio = first.IsGraphicAudio,
                    AudioProductionOverride = first.AudioProductionOverride,
                    AudioProductionOverrideSpecified = first.AudioProductionOverrideSpecified,
                    ImportRoot = first.ImportRoot,
                    AudioProductionType = first.AudioProductionType,
                    SceneName = first.SceneName,
                    Narrator = first.Narrator,
                    IsInitialImport = first.IsInitialImport,
                    IsManualImport = first.IsManualImport,
                    IsIncludedMedia = first.IsIncludedMedia,
                    DisableReleaseSwitching = first.DisableReleaseSwitching,
                    ConfirmedReplacementFiles = first.ConfirmedReplacementFiles,
                    ApprovedReplacementFileIds = first.ApprovedReplacementFileIds,
                    MatchProvenance = first.MatchProvenance,
                    AlternativeCopyIncompatibleEditionIds = first.AlternativeCopyIncompatibleEditionIds,
                    AlternativeCopyGrabDate = first.AlternativeCopyGrabDate,
                    CommonTags = first.CommonTags
                };
            }

            private string GetConvertedImportDestinationPath(LocalBook source, Book book, Author author, string outputName, string[] inputFiles, QualityModel convertedQuality)
            {
                if (source?.Edition == null)
                {
                    throw new InvalidOperationException("Unable to determine converted M4B destination because the matched edition is missing.");
                }

                source.Edition.Book ??= book;

                var sourceFolder = Path.GetDirectoryName(source.Path);
                var previewPath = sourceFolder.IsNotNullOrWhiteSpace()
                    ? Path.Combine(sourceFolder, outputName + ".m4b")
                    : Path.GetFullPath(outputName + ".m4b");

                var previewLocalBook = PlanGeneratedConversionLocalBook(source, previewPath, inputFiles, convertedQuality);
                previewLocalBook.Author = author;
                previewLocalBook.Book = book;

                var previewBookFile = new BookFile
                {
                    Path = previewLocalBook.Path.CleanFilePath(),
                    Quality = convertedQuality,
                    EditionId = source.Edition.Id,
                    Edition = source.Edition,
                    Author = author,
                    Part = previewLocalBook.Part > 0 ? previewLocalBook.Part : 1,
                    PartCount = previewLocalBook.PartCount,
                    MediaType = BookFile.DetermineMediaType(convertedQuality),
                    IsGraphicAudio = AudioProductionDetector.DetectRecording(source),
                    AudioProductionOverride = source.AudioProductionOverride,
                    AudioProductionType = AudioProductionDetector.GetRecordingProductionType(source)
                };

                return _bookFileMover.GetImportDestinationPath(previewBookFile, previewLocalBook);
            }

            private string GetConversionDestinationConflictReason(string finalDestinationPath, LocalBook conversionSource, bool replaceExisting)
            {
                if (finalDestinationPath.IsNullOrWhiteSpace())
                {
                    return null;
                }

                var existingTracked = _mediaFileService.GetFileWithPath(finalDestinationPath);
                if (!DestinationFileExists(finalDestinationPath))
                {
                    if (existingTracked != null)
                    {
                        _logger.Warn("[CONVERSION] Preserving tracked BookFile row for currently-missing conversion destination. A successful import will update it in place; a trustworthy root scan owns missing-file cleanup. BookFileId={0}, EditionId={1}, Path={2}",
                            existingTracked.Id,
                            existingTracked.EditionId,
                            finalDestinationPath);
                    }

                    return null;
                }

                if (replaceExisting && existingTracked != null && CanReplaceDestination(existingTracked, conversionSource))
                {
                    return null;
                }

                if (existingTracked != null)
                {
                    if (existingTracked.EditionId <= 0)
                    {
                        return $"Conversion skipped because an unmapped file already exists at the destination: {finalDestinationPath}. It may not appear in this author's Files tab. Import or delete the unmapped file, or change naming settings, then retry.";
                    }

                    return $"Conversion skipped because the destination is already occupied: {finalDestinationPath}. Remove or rename the existing file, or change naming settings, then retry.";
                }

                return $"Conversion skipped because an untracked file already exists at the destination: {finalDestinationPath}. It will not appear in Chaptarr's Files tab. Remove or rename the file, or change naming settings, then retry.";
            }

            private bool TryFindReusableConversionArtifact(string downloadId, string workRoot, string[] inputFiles, QualityModel targetQuality, ConversionTagOptions tagOptions, int audioBitrate, int audioChannels, out string outputPath)
            {
                outputPath = null;
                if (workRoot.IsNullOrWhiteSpace() || !Directory.Exists(workRoot))
                {
                    return false;
                }

                var currentSources = BuildConversionArtifactSourceSignatures(inputFiles);
                if (currentSources == null || currentSources.Count == 0)
                {
                    return false;
                }

                var tagSignature = GetConversionTagSignature(tagOptions);
                IEnumerable<string> manifests;
                try
                {
                    manifests = Directory
                        .EnumerateFiles(workRoot, ConversionArtifactManifestFileName, SearchOption.AllDirectories)
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .ToList();
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "[CONVERSION] Failed checking retained conversion artifacts under {0}", workRoot);
                    return false;
                }

                foreach (var manifestPath in manifests)
                {
                    try
                    {
                        var manifest = JsonSerializer.Deserialize<ConversionArtifactManifest>(File.ReadAllText(manifestPath));
                        if (manifest == null ||
                            (manifest.DownloadId.IsNotNullOrWhiteSpace() && !string.Equals(manifest.DownloadId, downloadId, StringComparison.Ordinal)) ||
                            manifest.TargetQualityId != targetQuality?.Quality?.Id ||
                            manifest.AudioBitrate != audioBitrate ||
                            manifest.AudioChannels != audioChannels ||
                            !string.Equals(manifest.TagSignature, tagSignature, StringComparison.Ordinal) ||
                            !ConversionWorkPath.SourcesMatch(currentSources, manifest.Sources))
                        {
                            continue;
                        }

                        var candidateOutput = manifest.OutputPath;
                        if (!candidateOutput.IsNullOrWhiteSpace() &&
                            Path.GetDirectoryName(candidateOutput).PathEquals(Path.GetDirectoryName(manifestPath)) &&
                            Path.GetDirectoryName(ConversionWorkPath.RetainedFolder(downloadId, candidateOutput)).PathEquals(workRoot) &&
                            ConversionWorkPath.IsSuccessfulArtifact(manifest, candidateOutput))
                        {
                            outputPath = candidateOutput;
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn(ex, "[CONVERSION] Ignoring unreadable retained conversion artifact manifest: {0}", manifestPath);
                    }
                }

                return false;
            }

            private static List<ConversionArtifactSource> BuildConversionArtifactSourceSignatures(IEnumerable<string> inputFiles)
            {
                var sources = new List<ConversionArtifactSource>();
                foreach (var inputFile in inputFiles.Where(p => !p.IsNullOrWhiteSpace()).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!File.Exists(inputFile))
                    {
                        return null;
                    }

                    var info = new FileInfo(inputFile);
                    sources.Add(new ConversionArtifactSource
                    {
                        Path = inputFile,
                        Size = info.Length,
                        ModifiedUtcTicks = info.LastWriteTimeUtc.Ticks
                    });
                }

                return sources;
            }

            private static string GetConversionTagSignature(ConversionTagOptions tagOptions)
            {
                if (tagOptions == null)
                {
                    return string.Empty;
                }

                return string.Join("\u001f",
                    tagOptions.Mode,
                    tagOptions.Name,
                    tagOptions.Album,
                    tagOptions.Artist,
                    tagOptions.AlbumArtist,
                    tagOptions.Writer,
                    tagOptions.Year,
                    tagOptions.Genre,
                    tagOptions.Comment,
                    tagOptions.Copyright,
                    tagOptions.Grouping,
                    tagOptions.Series,
                    tagOptions.SeriesPart,
                    tagOptions.Cover,
                    tagOptions.CoverIsSource.ToString(CultureInfo.InvariantCulture),
                    tagOptions.EncodedBy,
                    tagOptions.UseFilenamesAsChapters.ToString(CultureInfo.InvariantCulture),
                    tagOptions.IgnoreSourceTags.ToString(CultureInfo.InvariantCulture),
                    tagOptions.ProviderChapterCount?.ToString(CultureInfo.InvariantCulture),
                    tagOptions.CoverPolicySignature,
                    HashString(tagOptions.ChaptersTxtContent));
            }

            private static string HashString(string value)
            {
                if (value.IsNullOrWhiteSpace())
                {
                    return string.Empty;
                }

                return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
            }

            private ConversionEstimate EstimateConversionWorkspace(string[] inputFiles)
            {
                try
                {
                    return _m4bConversionService.EstimateConversion(inputFiles);
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "[CONVERSION] Unable to estimate conversion workspace size; continuing without size estimate");
                    return null;
                }
            }

            private int GetAudiobookConversionBitrate(string[] inputFiles)
            {
                var maxBitrate = Clamp(_configService?.AudiobookConversionMaxBitrate ?? 64, 16, 320);
                if (_configService?.AudiobookConversionNoUpscale != true)
                {
                    return maxBitrate;
                }

                var sourceBitrate = GetLowestSourceAudioBitrate(inputFiles);
                if (!sourceBitrate.HasValue)
                {
                    return maxBitrate;
                }

                return Math.Min(maxBitrate, Clamp(sourceBitrate.Value, 16, 320));
            }

            private int GetAudiobookConversionAudioChannels()
            {
                return string.Equals(_configService?.AudiobookConversionAudioChannels, "mono", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            }

            private int? GetLowestSourceAudioBitrate(string[] inputFiles)
            {
                var bitrates = new List<int>();

                foreach (var inputFile in inputFiles ?? Array.Empty<string>())
                {
                    try
                    {
                        var mediaInfo = _mediaInfoExtractor.ExtractMediaInfo(inputFile);
                        if (mediaInfo?.AudioBitrate > 0)
                        {
                            bitrates.Add(mediaInfo.AudioBitrate);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "[CONVERSION] Unable to read source bitrate for {0}", inputFile);
                    }
                }

                return bitrates.Count > 0 ? bitrates.Min() : null;
            }

            private TimeSpan GetAudiobookConversionSourceDuration(string[] inputFiles)
            {
                var total = TimeSpan.Zero;

                foreach (var inputFile in inputFiles ?? Array.Empty<string>())
                {
                    try
                    {
                        var duration = _mediaInfoExtractor.GetDuration(inputFile);
                        if (duration <= TimeSpan.Zero)
                        {
                            return TimeSpan.Zero;
                        }

                        total += duration;
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "[CONVERSION] Unable to read source duration for {0}", inputFile);
                        return TimeSpan.Zero;
                    }
                }

                return total;
            }

            private static int Clamp(int value, int min, int max)
            {
                return Math.Min(max, Math.Max(min, value));
            }

            private bool HasEnoughConversionWorkspaceSpace(string destinationFolder, ConversionEstimate estimate, out string error)
            {
                error = null;

                if (_configService?.SkipFreeSpaceCheckWhenImporting == true)
                {
                    _logger.Debug("[CONVERSION] Skipping destination free space check because import free space checks are disabled");
                    return true;
                }

                if (_diskProvider == null || _configService == null)
                {
                    _logger.Debug("[CONVERSION] Skipping destination free space check because disk/config services are unavailable");
                    return true;
                }

                try
                {
                    var checkPath = GetExistingFolderForFreeSpaceCheck(destinationFolder);
                    var freeSpace = _diskProvider.GetAvailableSpace(checkPath);
                    if (!freeSpace.HasValue)
                    {
                        _logger.Debug("[CONVERSION] Free space check returned no result for conversion destination: {0}", checkPath);
                        return true;
                    }

                    var estimatedInputSize = Math.Max(0, estimate?.TotalInputSize ?? 0);
                    var estimatedOutputSize = Math.Max(estimate?.EstimatedOutputSize ?? 0, estimatedInputSize);
                    var minimumFreeSpace = Math.Max(0, _configService.MinimumFreeSpaceWhenImporting).Megabytes();
                    var requiredFreeSpace = estimatedInputSize + estimatedOutputSize + minimumFreeSpace;
                    if (freeSpace.Value >= requiredFreeSpace)
                    {
                        return true;
                    }

                    error = $"Not enough free space in conversion destination folder. Need about {FormatBytes(requiredFreeSpace)} available at {checkPath}; {FormatBytes(freeSpace.Value)} is available.";
                    _logger.Warn("[CONVERSION] {0}", error);
                    return false;
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "[CONVERSION] Unable to check free disk space for conversion destination: {0}", destinationFolder);
                    return true;
                }
            }

            private static string GetExistingFolderForFreeSpaceCheck(string folder)
            {
                var current = folder;
                while (current.IsNotNullOrWhiteSpace() && !Directory.Exists(current))
                {
                    var parent = Directory.GetParent(current);
                    if (parent == null)
                    {
                        return current;
                    }

                    current = parent.FullName;
                }

                return current;
            }

            private static string FormatBytes(long bytes)
            {
                var value = (double)Math.Max(0, bytes);
                var suffixIndex = 0;
                var suffixes = new[] { "B", "KB", "MB", "GB", "TB" };

                while (value >= 1024 && suffixIndex < suffixes.Length - 1)
                {
                    value /= 1024;
                    suffixIndex++;
                }

                return string.Format(CultureInfo.InvariantCulture, "{0:0.#} {1}", value, suffixes[suffixIndex]);
            }

            private static string GetUniqueFileName(string baseName, string extension, HashSet<string> usedNames)
            {
                var candidate = baseName + extension;
                var index = 2;

                while (!usedNames.Add(candidate))
                {
                    candidate = $"{baseName}-{index++}{extension}";
                }

                return candidate;
            }

            private void CleanupConversionWorkFolder(string workFolder)
            {
                ConversionWorkPath.Cleanup(workFolder, _logger);
            }

            private void CleanupOrRetainConversionWorkFolder(string workFolder, List<ImportDecision<LocalBook>> bookDecisions, string reason)
            {
                if (TryGetRetainableGeneratedConversionPath(bookDecisions, out var retainedPath))
                {
                    _logger.Warn("[CONVERSION] Retaining converted M4B after import failure. Path={0} Reason={1}", retainedPath, reason);
                    return;
                }

                CleanupConversionWorkFolder(workFolder);
            }

            private string AppendRetainedConversionPath(string message, LocalBook convertedLocalBook)
            {
                return TryGetRetainableGeneratedConversionPath(convertedLocalBook, out var retainedPath)
                    ? ConversionRetentionResult.AppendRetainedConversionPath(message, retainedPath)
                    : message;
            }

            private static bool TryGetRetainableGeneratedConversionPath(IEnumerable<ImportDecision<LocalBook>> bookDecisions, out string retainedPath)
            {
                retainedPath = null;
                var convertedLocalBook = bookDecisions?
                    .Select(d => d?.Item)
                    .FirstOrDefault(i => i?.IsGeneratedConversion == true);

                return TryGetRetainableGeneratedConversionPath(convertedLocalBook, out retainedPath);
            }

            private static bool TryGetRetainableGeneratedConversionPath(LocalBook convertedLocalBook, out string retainedPath)
            {
                retainedPath = convertedLocalBook?.GeneratedConversionOutputPath ?? convertedLocalBook?.Path;
                return !retainedPath.IsNullOrWhiteSpace() &&
                       ConversionWorkPath.IsWorkPath(retainedPath) &&
                       File.Exists(retainedPath);
            }

            private bool DestinationFileExists(string path)
            {
                if (path.IsNullOrWhiteSpace())
                {
                    return false;
                }

                try
                {
                    return _diskProvider?.FileExists(path) ?? File.Exists(path);
                }
                catch
                {
                    return File.Exists(path);
                }
            }

            private static bool IsBookFilesPathUniqueViolation(SqliteException ex)
            {
                const int sqliteConstraintUnique = 2067; // SQLITE_CONSTRAINT_UNIQUE
                if (ex.SqliteExtendedErrorCode != sqliteConstraintUnique)
                {
                    return false;
                }

                // Example: "UNIQUE constraint failed: BookFiles.Path"
                return ex.Message.Contains("BookFiles.Path", StringComparison.OrdinalIgnoreCase);
            }

            private static bool IsBookFilesPathUniqueViolation(PostgresException ex)
            {
                if (!string.Equals(ex.SqlState, "23505", StringComparison.Ordinal))
                {
                    return false;
                }

                var blob = $"{ex.ConstraintName} {ex.Detail}";
                return blob.Contains("IX_BookFiles_Path_Unique", StringComparison.OrdinalIgnoreCase) ||
                       blob.Contains("BookFiles", StringComparison.OrdinalIgnoreCase) && blob.Contains("Path", StringComparison.OrdinalIgnoreCase);
            }

            private void CommitPreparedBookFiles(List<BookFile> bookFilesToAdd, List<PendingFileCommit> pendingFileCommits, Book book, bool commitSettings)
            {
                var persist = commitSettings ? PrepareImportSettingsCommit(book, pendingFileCommits) : null;
                var rowsToReplace = GetDatabaseRowsToReplace(pendingFileCommits);
                PrepareReplacementRows(bookFilesToAdd, rowsToReplace);
                try
                {
                    PersistPreparedBookFiles(bookFilesToAdd, rowsToReplace, pendingFileCommits, persist);
                }
                catch (SqliteException ex) when (IsBookFilesPathUniqueViolation(ex))
                {
                    RetryPreparedBookFilesAfterPathConflict(bookFilesToAdd, pendingFileCommits, ex, persist);
                }
                catch (PostgresException ex) when (IsBookFilesPathUniqueViolation(ex))
                {
                    RetryPreparedBookFilesAfterPathConflict(bookFilesToAdd, pendingFileCommits, ex, persist);
                }

                foreach (var commit in pendingFileCommits)
                {
                    commit.DatabaseCommitted = true;
                }
            }

            private void RetryPreparedBookFilesAfterPathConflict(
                List<BookFile> bookFilesToAdd,
                List<PendingFileCommit> pendingFileCommits,
                Exception ex,
                Action<Action<IDbConnection, IDbTransaction>> persist)
            {
                _logger.Warn(ex, "[IMPORT-PATH-CONFLICT] UNIQUE constraint hit during the atomic BookFile swap; refreshing destination rows and retrying once");

                var rowsToReplace = GetDatabaseRowsToReplace(pendingFileCommits);
                var knownIds = rowsToReplace.Where(file => file?.Id > 0).Select(file => file.Id).ToHashSet();

                foreach (var bookFile in bookFilesToAdd.Where(file => file?.Path.IsNotNullOrWhiteSpace() == true))
                {
                    var existing = _mediaFileService.GetFileWithPath(bookFile.Path);
                    if (existing?.Id > 0 && existing.Id != bookFile.Id && knownIds.Add(existing.Id))
                    {
                        EnsureDestinationBelongsToBook(existing, bookFile.Edition.BookId);
                        var selection = pendingFileCommits.FirstOrDefault(commit => ReferenceEquals(commit.BookFile, bookFile))?.LocalBook;
                        EnsureReplacementConfirmed(existing, selection);
                        _logger.Warn("[IMPORT-PATH-CONFLICT] Including concurrently-created BookFileId={0} in the atomic replacement. Path={1}",
                            existing.Id,
                            bookFile.Path);
                        rowsToReplace.Add(existing);
                    }
                }

                PrepareReplacementRows(bookFilesToAdd, rowsToReplace);
                PersistPreparedBookFiles(bookFilesToAdd, rowsToReplace, pendingFileCommits, persist);
            }

            private void PersistPreparedBookFiles(List<BookFile> bookFiles, List<BookFile> replacedFiles,
                List<PendingFileCommit> commits, Action<Action<IDbConnection, IDbTransaction>> persist)
            {
                var confirmed = commits.FirstOrDefault()?.LocalBook.ConfirmedReplacementFiles;
                if (persist == null)
                {
                    _mediaFileService.ReplaceMany(bookFiles, replacedFiles, DeleteMediaFileReason.Upgrade, confirmed);
                }
                else
                {
                    _mediaFileService.ReplaceMany(bookFiles, replacedFiles, DeleteMediaFileReason.Upgrade, confirmed, persist);
                }
            }

            private Action<Action<IDbConnection, IDbTransaction>> PrepareImportSettingsCommit(Book target, List<PendingFileCommit> commits)
            {
                var inputs = commits.Select(commit => commit.LocalBook).ToList();
                var selections = inputs.Where(item => RequiresImportedEditionSelection(item, item.Edition))
                    .GroupBy(item => item.Edition.Id).Select(group => group.First()).ToList();
                var pin = inputs.Any(RequiresManualImportSettings);
                if (selections.Count == 0 && !pin)
                {
                    return null;
                }

                var book = _bookService.GetBook(target.Id) ?? throw new InvalidOperationException($"Book {target.Id} disappeared before replacement settings could be saved.");
                var selected = selections.LastOrDefault();
                var editionUpdates = new List<Edition>();
                if (selected != null)
                {
                    var editions = book.Editions ?? throw new InvalidOperationException("The replacement book has no loaded edition catalog.");
                    var chosen = editions.Single(edition => edition.Id == selected.Edition.Id);
                    var previous = editions.ToDictionary(edition => edition.Id, edition => (edition.Monitored, edition.ManualAdd));
                    EditionPinPolicy.ApplyExplicitSelection(editions, chosen.Id,
                        editions.Where(edition => edition.Monitored).Select(edition => edition.Id).ToHashSet(),
                        selected.IsManualImport && selected.MatchProvenance?.SelectionSource == "user_metadata");
                    editionUpdates = editions.Where(edition => previous[edition.Id] != (edition.Monitored, edition.ManualAdd)).ToList();
                }
                if (pin) ApplyManualImportSettings(book, inputs.Any(item => RequiresManualImportSettings(item) && item.MatchProvenance?.SelectionSource == "user_metadata"));
                return writeFiles =>
                {
                    _bookService.UpdateBook(book, editionUpdates, writeRelated: writeFiles);
                    target.Editions = book.Editions;
                    target.AnyEditionOk = book.AnyEditionOk;
                    foreach (var item in inputs)
                    {
                        var stored = book.Editions?.FirstOrDefault(edition => edition.Id == item.Edition.Id);
                        if (stored != null)
                        {
                            item.Edition.Monitored = stored.Monitored;
                            item.Edition.ManualAdd = stored.ManualAdd;
                        }
                    }
                };
            }

            private static void PrepareReplacementRows(List<BookFile> bookFiles, List<BookFile> replacedFiles)
            {
                var updatedIds = bookFiles.Where(file => file.Id > 0).Select(file => file.Id).ToHashSet();
                replacedFiles.RemoveAll(file => updatedIds.Contains(file.Id));
                var replicaPathsByBook = replacedFiles.Where(file => file.MediaType == "ebook" && file.Edition?.BookId > 0)
                    .ToLookup(file => file.Edition.BookId, file => file.ReplicaPaths ?? new List<string>());
                foreach (var file in bookFiles.Where(file => file.MediaType == "ebook" && file.Edition?.BookId > 0))
                {
                    file.ReplicaPaths = (file.ReplicaPaths ?? new List<string>())
                        .Concat(replicaPathsByBook[file.Edition.BookId].SelectMany(paths => paths))
                        .Where(path => path.IsNotNullOrWhiteSpace() && !path.PathEquals(file.Path))
                        .Distinct(PathEqualityComparer.Instance).ToList();
                }
            }

            private static List<BookFile> GetDatabaseRowsToReplace(IEnumerable<PendingFileCommit> pendingFileCommits)
            {
                return pendingFileCommits?
                    .SelectMany(commit => commit?.DatabaseRowsToReplace ?? new List<BookFile>())
                    .Where(file => file?.Id > 0)
                    .DistinctBy(file => file.Id)
                    .ToList() ?? new List<BookFile>();
            }

            private BookFile ResolveBookFilePathConflict(BookFile bookFile, LocalBook localBook, PendingFileCommit pendingFileCommit, HashSet<int> alreadyStagedIds)
            {
                if (bookFile == null || bookFile.Path.IsNullOrWhiteSpace())
                {
                    return bookFile;
                }

                // Destination path uniqueness is enforced at the DB level, but stale rows can remain after aborted imports,
                // root folder changes, or manual user moves.
                var existingAtDestination = _mediaFileService.GetFileWithPath(bookFile.Path);
                if (existingAtDestination == null || existingAtDestination.Id == bookFile.Id)
                {
                    return bookFile;
                }

                EnsureDestinationBelongsToBook(existingAtDestination, localBook.Book.Id);
                EnsureReplacementConfirmed(existingAtDestination, localBook);

                if (bookFile.Id > 0)
                {
                    pendingFileCommit.DatabaseRowsToReplace.Add(existingAtDestination);
                    return bookFile;
                }

                if (bookFile.CalibreId == 0 && existingAtDestination.CalibreId != 0)
                {
                    bookFile.CalibreId = existingAtDestination.CalibreId;
                }

                if (pendingFileCommit == null)
                {
                    throw new InvalidOperationException("A new destination-path replacement must have a pending file commit");
                }

                if (pendingFileCommit.StagedReplacements.Any() || alreadyStagedIds.Contains(existingAtDestination.Id))
                {
                    _logger.Warn("[IMPORT-PATH-CONFLICT] Upgrade also found destination BookFileId={0}. Including every displaced row in the atomic replacement with EditionId={1}. Source={2} Dest={3}",
                        existingAtDestination.Id,
                        bookFile.EditionId,
                        localBook?.Path,
                        bookFile.Path);
                    pendingFileCommit.DatabaseRowsToReplace.Add(existingAtDestination);
                    return bookFile;
                }

                // Preserve the stale row's identity, but save it with the other prepared parts.
                _logger.Warn("[IMPORT-PATH-CONFLICT] Destination path already tracked by BookFileId={0} (EditionId={1}). Updating that row to EditionId={2}. Source={3} Dest={4}",
                    existingAtDestination.Id,
                    existingAtDestination.EditionId,
                    bookFile.EditionId,
                    localBook?.Path,
                    bookFile.Path);

                bookFile.Id = existingAtDestination.Id;
                if ((bookFile.ReplicaPaths == null || bookFile.ReplicaPaths.Count == 0) && existingAtDestination.ReplicaPaths?.Count > 0)
                {
                    bookFile.ReplicaPaths = existingAtDestination.ReplicaPaths;
                }

                bookFile.DurationSeconds ??= existingAtDestination.DurationSeconds;
                pendingFileCommit.DatabaseRowsToReplace.RemoveAll(file => file.Id == existingAtDestination.Id);

                return bookFile;
            }

        private void EnsureDestinationBelongsToBook(BookFile existingFile, int bookId)
        {
            if (existingFile.EditionId <= 0)
            {
                return;
            }

            var edition = existingFile.Edition ?? _editionService.GetEdition(existingFile.EditionId);
            if (edition?.BookId != bookId)
            {
                throw new ReplacementRefusedException($"Destination is tracked by another book. Import was not applied: {existingFile.Path}");
            }
        }

        private static string GetUniqueUpgradeBackupPath(string originalPath)
        {
            var directory = Path.GetDirectoryName(originalPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = Path.GetDirectoryName(Path.GetFullPath(originalPath)) ?? ".";
            }

            var fileName = Path.GetFileName(originalPath);

            // Same directory ensures same volume so rename is atomic and fast.
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var candidate = Path.Combine(directory, $"{fileName}.chaptarr-upgrade~{Guid.NewGuid():N}");
                if (!File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return Path.Combine(directory, $"{fileName}.chaptarr-upgrade~{Guid.NewGuid():N}");
        }

        private static bool CanReplaceDestination(BookFile existingFile, LocalBook selection) =>
            selection?.IsManualImport == true
                ? ManualImportReplacementFiles.IsConfirmed(existingFile, selection.ConfirmedReplacementFiles)
                : selection?.ApprovedReplacementFileIds?.Contains(existingFile.Id) == true;

        private static void EnsureReplacementConfirmed(BookFile existingFile, LocalBook selection)
        {
            if (!CanReplaceDestination(existingFile, selection))
            {
                throw new ReplacementRefusedException("Existing files changed since the replacement was checked. Review the replacement files in Manual Import.");
            }
        }

        private void ImportExtras(LocalBook localBook, BookFile bookFile, bool copyOnly, bool replaceExisting)
        {
            try
            {
                var previousReplicaPaths = (bookFile.ReplicaPaths ?? new List<string>()).ToArray();
                if (localBook.PendingReplicaPaths != null && _bookFileMover.CompleteImportReplicas(bookFile, localBook) &&
                    !previousReplicaPaths.SequenceEqual(bookFile.ReplicaPaths ?? new List<string>(), PathEqualityComparer.Instance))
                {
                    _mediaFileService.Update(bookFile);
                }

                _extraService.ImportTrack(localBook, bookFile, copyOnly, replaceExisting && localBook.ConfirmedReplacementFiles == null);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "[CLEAN-IMPORT] Failed to import extras for: {0}", localBook.Path);
            }
        }

        private void FinalizePendingFileCommits(IEnumerable<PendingFileCommit> pendingFileCommits)
        {
            var stopwatch = Stopwatch.StartNew();
            var finalized = 0;

            foreach (var commit in pendingFileCommits ?? Enumerable.Empty<PendingFileCommit>())
            {
                foreach (var (oldFile, backupPath) in commit.StagedReplacements)
                {
                    try
                    {
                        if (!File.Exists(backupPath))
                        {
                            continue;
                        }

                        var subfolder = string.Empty;
                        try
                        {
                            var rootFolderPath = commit.Author?.GetRootFolderForQuality(commit.LocalBook.Quality.Quality);
                            var oldFileDirectory = Path.GetDirectoryName(backupPath);

                            if (!string.IsNullOrWhiteSpace(rootFolderPath) && !string.IsNullOrWhiteSpace(oldFileDirectory))
                            {
                                subfolder = rootFolderPath.GetRelativePath(oldFileDirectory);
                            }
                        }
                        catch
                        {
                            subfolder = string.Empty;
                        }

                        _recycleBinProvider.DeleteFile(backupPath, subfolder);
                        finalized++;
                    }
                    catch (Exception ex)
                    {
                        // The database now points at the new file. Retaining a clearly-named
                        // backup is safer than turning cleanup trouble into a false import failure.
                        _logger.Error(ex, "[CLEAN-IMPORT] New BookFile committed, but old backup could not be recycled and was retained: {0}", backupPath);
                    }
                }
            }

            stopwatch.Stop();
            if (finalized > 0)
            {
                _logger.Debug("[PERFORMANCE] Finalized {0} committed upgrade backups in {1}ms", finalized, stopwatch.ElapsedMilliseconds);
            }
        }

        private void WriteTagsForCommittedFiles(IEnumerable<PendingFileCommit> pendingFileCommits)
        {
            foreach (var commit in pendingFileCommits ?? Enumerable.Empty<PendingFileCommit>())
            {
                if (!commit.DatabaseCommitted)
                {
                    continue;
                }

                if (commit.PublishAddedEvent)
                {
                    _eventAggregator.PublishEvent(new BookFileAddedEvent(commit.BookFile));
                }

                if (commit.RenamedFrom.IsNotNullOrWhiteSpace() && !commit.RenamedFrom.PathEquals(commit.BookFile.Path))
                {
                    _eventAggregator.PublishEvent(new BookFileRenamedEvent(commit.Author, commit.BookFile, commit.RenamedFrom));
                }

                TryWriteTags(commit.BookFile, commit.NewDownload, "COMMITTED-IMPORT");
            }
        }

        private void RollbackPendingFileCommits(IEnumerable<PendingFileCommit> pendingFileCommits)
        {
            foreach (var commit in (pendingFileCommits ?? Enumerable.Empty<PendingFileCommit>()).Reverse())
            {
                RollbackPendingFileCommit(commit);
            }
        }

        private void RollbackPendingFileCommit(PendingFileCommit pendingFileCommit)
        {
            if (pendingFileCommit == null || pendingFileCommit.DatabaseCommitted)
            {
                return;
            }

            var destinationCleared = RollbackTransferredFile(pendingFileCommit);
            RollbackStagedReplacements(
                pendingFileCommit.StagedReplacements,
                destinationCleared ? null : pendingFileCommit.DestinationPath);

            if (!destinationCleared && _diskProvider != null)
            {
                // The failed transfer still exists at its destination and could not be
                // returned without overwriting a newly occupied source path. Keep that
                // real file visible to the user instead of leaving it row-less.
                BookImportUnmappedFileHelper.TryEnsureUnmapped(
                    _mediaFileService,
                    _diskProvider,
                    pendingFileCommit.DestinationPath,
                    _logger,
                    "[CLEAN-IMPORT-ROLLBACK]",
                    pendingFileCommit.BookFile?.AllTags,
                    pendingFileCommit.BookFile?.DurationSeconds);
            }
        }

        private bool RollbackTransferredFile(PendingFileCommit pendingFileCommit)
        {
            var sourcePath = pendingFileCommit.SourcePath;
            var destinationPath = pendingFileCommit.DestinationPath;
            if (sourcePath.IsNullOrWhiteSpace() ||
                destinationPath.IsNullOrWhiteSpace() ||
                sourcePath.PathEquals(destinationPath) ||
                !File.Exists(destinationPath))
            {
                return true;
            }

            try
            {
                if (pendingFileCommit.CopyOnly)
                {
                    File.Delete(destinationPath);
                }
                else
                {
                    var sourceDirectory = Path.GetDirectoryName(sourcePath);
                    if (sourceDirectory.IsNotNullOrWhiteSpace())
                    {
                        Directory.CreateDirectory(sourceDirectory);
                    }

                    if (File.Exists(sourcePath))
                    {
                        _logger.Error("[CLEAN-IMPORT] Cannot move failed import back because the original source path is occupied. Retaining destination for recovery: {0}", destinationPath);
                        return false;
                    }

                    File.Move(destinationPath, sourcePath);
                }

                return !File.Exists(destinationPath);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[CLEAN-IMPORT] Failed to reverse transferred file after database failure: {0} -> {1}", destinationPath, sourcePath);
                return false;
            }
        }

        private void RollbackStagedReplacements(
            List<(BookFile OldFile, string BackupPath)> stagedReplacements,
            string blockedDestinationPath = null)
        {
            if (stagedReplacements == null || stagedReplacements.Count == 0)
            {
                return;
            }

            for (var i = stagedReplacements.Count - 1; i >= 0; i--)
            {
                var (oldFile, backupPath) = stagedReplacements[i];
                try
                {
                    if (oldFile == null || string.IsNullOrWhiteSpace(oldFile.Path) || string.IsNullOrWhiteSpace(backupPath))
                    {
                        continue;
                    }

                    if (!File.Exists(backupPath))
                    {
                        continue;
                    }

                    if (blockedDestinationPath.IsNotNullOrWhiteSpace() && oldFile.Path.PathEquals(blockedDestinationPath))
                    {
                        _logger.Error("[CLEAN-IMPORT] Retaining staged backup because the failed destination could not be cleared: {0}", backupPath);
                        continue;
                    }

                    // Overwrite any partially transferred file at the original path.
                    File.Move(backupPath, oldFile.Path, true);
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "[CLEAN-IMPORT] Failed to rollback staged replacement: {0} -> {1}", backupPath, oldFile?.Path);
                }
            }
        }

        private void TryWriteTags(BookFile bookFile, bool newDownload, string route)
        {
            try
            {
                if (newDownload && bookFile?.CalibreId > 0 && bookFile.Edition?.Book != null)
                {
                    try
                    {
                        _coverMapper?.EnsureBookCoversForImport(bookFile.Edition.Book);
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "[{0}] Unable to ensure the imported Calibre book cover", route);
                    }
                }

                _metadataTagService.WriteTags(bookFile, newDownload);
            }
            catch (Exception ex)
            {
                // Tags are optional post-processing. They cannot be part of the disk/database
                // commit and must never turn an otherwise valid import into invisible inventory.
                _logger.Warn(ex, "[{0}] Failed writing tags; keeping the BookFile import valid: {1}", route, bookFile?.Path);
            }
        }

        // Transfer logic is handled by IMoveBookFiles (BookFileMovingService)

        private bool ShouldMoveFile(LocalBook localBook, Author author)
        {
            // Only used for new downloads. If file already resides under the configured root
            // folder for its media type, don't move it.
            var fileDirectory = System.IO.Path.GetDirectoryName(localBook.Path);
            var rootFolder = author?.GetRootFolderForQuality(localBook.Quality.Quality);

            if (!string.IsNullOrWhiteSpace(rootFolder))
            {
                if (rootFolder.PathEquals(fileDirectory) || rootFolder.IsParentPath(localBook.Path))
                {
                    _logger.Debug("File already under root folder, not moving: {0}", localBook.Path);
                    return false;
                }
            }

            return true;
        }

        private static List<BookFile> GetReplacementFiles(IEnumerable<BookFile> existingFiles, LocalBook incoming, int editionId, bool manualReplace) =>
            existingFiles.Where(file => !file.Path.PathEquals(incoming.Path) &&
                (file.EditionId == editionId || manualReplace)).ToList();

        private (string Reason, bool Retry) GetAdditionalCopyDuplicateReason(List<ImportDecision<LocalBook>> incoming, List<BookFile> existing, string downloadId)
        {
            try
            {
                if (!AllFilesAlreadyInLibrary(incoming, existing, downloadId)) return (null, false);
                const string reason = "Already in your library: every selected file is identical to an existing file for this book. Nothing was imported.";
                _logger.Info("[IMPORT-IDENTICAL] BookId={0}, DownloadId={1}: {2}", incoming[0].Item.Book.Id, downloadId, reason);
                return (reason, false);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _logger.Warn(ex, "Unable to compare the additional copy for BookId={0}; existing files were kept", incoming[0].Item.Book.Id);
                return ("Unable to compare this download with the existing library files: " + ex.Message, ex is IOException);
            }
        }

        private bool AllFilesAlreadyInLibrary(List<ImportDecision<LocalBook>> incoming, List<BookFile> existing, string downloadId)
        {
            var timer = Stopwatch.StartNew();
            var comparisons = 0;
            // GetFilesByBook already scopes this inventory to the target book and media side.
            var remaining = existing.Select(file => file.Path).Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(PathEqualityComparer.Instance).ToList();
            try
            {
                if (incoming.Count > remaining.Count) return false;

                // Preserve the in-place import check without re-querying the DB for each part.
                var unmatched = new List<string>();
                foreach (var decision in incoming)
                {
                    var index = remaining.FindIndex(path => path.PathEquals(decision.Item.Path));
                    if (index >= 0) remaining.RemoveAt(index);
                    else unmatched.Add(decision.Item.Path);
                }

                if (unmatched.Count == 0) return true;
                if (_diskProvider == null) return false;

                var bySize = remaining.Where(path => _diskProvider.FileExists(path))
                    .GroupBy(path => _diskProvider.GetFileSize(path)).ToDictionary(group => group.Key, group => group.ToList());
                var sourcesBySize = new Dictionary<long, List<string>>();
                foreach (var source in unmatched)
                {
                    if (!_diskProvider.FileExists(source)) return false;
                    var size = _diskProvider.GetFileSize(source);
                    if (!bySize.TryGetValue(size, out var available)) return false;
                    if (!sourcesBySize.TryGetValue(size, out var sources)) sourcesBySize[size] = sources = new List<string>();
                    sources.Add(source);
                    if (sources.Count > available.Count) return false;
                }

                foreach (var group in sourcesBySize)
                foreach (var source in group.Value)
                {
                    var candidates = bySize[group.Key];
                    var index = candidates.FindIndex(path =>
                    {
                        comparisons++;
                        return _diskProvider.FilesEqual(source, path);
                    });
                    if (index < 0) return false;
                    candidates.RemoveAt(index);
                }

                return true;
            }
            finally
            {
                _logger.Debug("[IDENTICAL-COPY] {0} incoming, {1} existing files, {2} byte comparisons in {3}ms",
                    incoming.Count, existing.Count, comparisons, timer.ElapsedMilliseconds);
                if (timer.ElapsedMilliseconds >= 1000)
                {
                    _logger.Warn("[IMPORT-IDENTICAL] BookId={0}, DownloadId={1}: comparison took {2}ms for {3} incoming and {4} existing files ({5} byte comparisons)",
                        incoming[0].Item.Book.Id, downloadId, timer.ElapsedMilliseconds, incoming.Count, existing.Count, comparisons);
                }
            }
        }

        private (ImportResultType Result, string Reason) CheckReplacement(IReadOnlyCollection<LocalBook> incoming,
            IReadOnlyCollection<BookFile> existing, QualityProfile profile, bool handPicked,
            IDictionary<LocalBook, List<CustomFormat>> cachedFormats,
            IReadOnlyCollection<BookFile> storedRowFiles = null)
        {
            var timer = Stopwatch.StartNew();
            var comparisons = 0;
            var candidates = existing.Where(file => incoming.Any(item => item.Edition?.Id == file.EditionId &&
                !file.Path.PathEquals(item.Path))).ToList();

            (ImportResultType Result, string Reason) Finish(ImportResultType result, string reason = null)
            {
                _logger.Debug("[REPLACEMENT-CHECK] {0} incoming, {1} existing files, {2} comparisons in {3}ms: {4}",
                    incoming.Count, candidates.Count, comparisons, timer.ElapsedMilliseconds, result);
                if (timer.ElapsedMilliseconds >= 1000)
                {
                    _logger.Warn("[REPLACEMENT-CHECK] Took {0}ms for {1} incoming and {2} existing files ({3} comparisons)",
                        timer.ElapsedMilliseconds, incoming.Count, candidates.Count, comparisons);
                }

                return (result, reason);
            }

            if (candidates.Count == 0) return Finish(ImportResultType.Imported);
            if (profile == null) return Finish(ImportResultType.Skipped, "No quality profile is configured. Existing files were kept.");
            if (!profile.UpgradeAllowed) return Finish(ImportResultType.Imported); // Existing additional-copy path keeps both.

            var missing = candidates.Where(file => !DestinationFileExists(file.Path)).ToList();
            if (missing.Count > 0)
            {
                _logger.Warn("[REPLACEMENT-CHECK] {0} existing files are missing; omitted from replacement: {1}",
                    missing.Count, string.Join(", ", missing.Select(file => file.Path)));
                candidates = candidates.Except(missing).ToList();
            }

            var editions = incoming.Where(item => item.Edition != null).GroupBy(item => item.Edition.Id)
                .ToDictionary(group => group.Key, group => group.First());
            var narrators = editions.ToDictionary(pair => pair.Key,
                pair => ReleaseNarratorEvidenceExtractor.GetKnownNarratorNames(pair.Value.Edition).ToList());
            bool HasNarratorEvidence(int editionId, IDictionary<string, List<string>> tags) =>
                narrators.TryGetValue(editionId, out var names) && names.Count > 0 && names.All(name =>
                    !AudioProductionDetector.IsCastLabel(name) &&
                    ExactMatchEvidenceBuilder.FindNarratorEvidenceFields(name, editions[editionId].Author?.Name, tags).Count > 0);

            var candidateIds = candidates.Select(file => file.Id).ToHashSet();
            var scoringFiles = (storedRowFiles ?? existing).Where(file => candidateIds.Contains(file.Id) || DestinationFileExists(file.Path)).Select(file =>
            {
                var clone = file.CloneForImport();
                if (editions.TryGetValue(file.EditionId, out var target))
                {
                    clone.Edition ??= target.Edition;
                    clone.Author ??= target.Author;
                }
                return clone;
            }).ToList();
            var storedUnits = RecordingCustomFormatHelper.GroupStoredForScoring(scoringFiles)
                .Where(files => files.Any(file => candidateIds.Contains(file.Id)))
                .Select(files => (Files: files, Input: RecordingCustomFormatHelper.CreateStoredInput(files))).ToList();
            var storedScores = storedUnits.ToDictionary(unit => unit.Input, unit => profile.CalculateCustomFormatScore(
                _customFormatCalculationService?.ParseCustomFormat(unit.Input) ?? new List<CustomFormat>()));
            var details = new HashSet<string>();
            var differentRecording = false;
            string notAnUpgrade = null;
            foreach (var files in RecordingCustomFormatHelper.GroupForScoring(incoming))
            {
                var item = files.Count == 1 ? files[0] : RecordingCustomFormatHelper.CreateInput(files);
                var replaced = storedUnits.Where(unit => unit.Files.Any(previous => candidateIds.Contains(previous.Id) &&
                    previous.EditionId == item.Edition.Id && !previous.Path.PathEquals(item.Path))).ToList();
                if (replaced.Count == 0) continue;
                var incomingFormats = GetRecordingCustomFormats(item, cachedFormats);
                var incomingScore = profile.CalculateCustomFormatScore(incomingFormats);
                var incomingProduction = AudioProductionDetector.IsDramatizedRecording(item);
                var incomingNarrators = HasNarratorEvidence(item.Edition.Id, item.RawTags?.AllTags);
                foreach (var unit in replaced)
                {
                    var previous = unit.Input;
                    var score = storedScores[previous];
                    foreach (var incomingQuality in files.Select(file => file.Quality).Distinct())
                    foreach (var storedQuality in unit.Files.Select(file => file.Quality).Distinct())
                    {
                        comparisons++;
                        var comparison = QualityProfilePreferenceComparer.CompareCandidateToStored(profile, incomingQuality,
                            incomingScore, storedQuality, score,
                            _configService?.DownloadPropersAndRepacks ?? ProperDownloadTypes.PreferAndUpgrade);
                        if (comparison.Result < 0 || (comparison.Result == 0 && !handPicked))
                        {
                            notAnUpgrade ??= $"Not an upgrade. Existing: {storedQuality} (score {score}). New: {incomingQuality} (score {incomingScore}). Existing files were kept.";
                        }

                        details.Add($"{storedQuality} (score {score}) → {incomingQuality} (score {incomingScore})");
                    }

                    differentRecording |= item.Book?.MediaType == BookMediaType.Audiobook &&
                        AudioProductionDetector.IsDramatizedRecording(previous) != incomingProduction &&
                        !(incomingNarrators && HasNarratorEvidence(item.Edition.Id, previous.AllTags));
                }
            }

            if (differentRecording && !handPicked)
            {
                return Finish(ImportResultType.WaitingForConfirmation,
                    "The recording type differs. Existing files were kept. Open Manual Import to review and confirm Replace.");
            }

            if (notAnUpgrade != null) return Finish(ImportResultType.Skipped, notAnUpgrade);
            if (details.Count > 0 && !handPicked && profile.ConfirmAutomaticUpgrades)
            {
                return Finish(ImportResultType.WaitingForConfirmation,
                    $"Waiting for you to confirm replacing {candidates.Count} file(s): {string.Join("; ", details)}. Open Manual Import to review and confirm Replace.");
            }

            return Finish(ImportResultType.Imported);
        }

        private int? GetDurationSeconds(LocalBook localBook)
        {
            if (MediaDuration.HasDuration(localBook?.DurationSeconds))
            {
                return localBook.DurationSeconds;
            }

            if (string.IsNullOrWhiteSpace(localBook?.Path))
            {
                return null;
            }

            return MediaDuration.FromTimeSpan(_mediaInfoExtractor.GetDuration(localBook.Path));
        }

        private static string BuildImportUnitKeyHash(LocalBook localBook, Edition edition, BookMediaType mediaType)
        {
            if (localBook == null || string.IsNullOrWhiteSpace(localBook.Path))
            {
                return null;
            }

            try
            {
                var baseKey = BookCoalescingHelper.BuildRootUnitKey(localBook.Path, edition?.Title, mediaType);
                var extension = Path.GetExtension(localBook.Path) ?? string.Empty;
                var unitKey = BookCoalescingHelper.IsStandaloneUnitExtension(extension)
                    ? baseKey
                    : $"{baseKey}|{extension}";

                if (string.IsNullOrWhiteSpace(unitKey))
                {
                    return null;
                }

                var hash = SHA256.HashData(Encoding.UTF8.GetBytes(unitKey.Trim()));
                return Convert.ToHexString(hash).ToLowerInvariant();
            }
            catch
            {
                return null;
            }
        }

        private class BatchBookResult
        {
            public Book NewBook { get; set; }
            public Edition NewEdition { get; set; }
        }

        private BatchBookResult CreateNewBookInstanceForBatch(ImportDecision<LocalBook> decision, Book originalBook, Author author, bool anyEditionOk)
        {
            var localBook = decision.Item;
            var originalEdition = localBook.Edition;

            Book duplicateBook = null;
            try
            {

                // Determine narrator identifier for unique slug (prefer edition narrators if available)
                string narratorIdentifier = null;
                if (originalEdition?.NarratorNames != null && originalEdition.NarratorNames.Any())
                {
                    narratorIdentifier = string.Join("_", originalEdition.NarratorNames).CleanAuthorName().ToLowerInvariant().Replace(" ", "_");
                }

                var mediaType = localBook.Quality.Quality.Id <= 4 ? BookMediaType.Ebook : BookMediaType.Audiobook;
                var copySuffix = DateTime.UtcNow.Ticks.ToString().Substring(10);
                var unitKeyHash = BuildImportUnitKeyHash(localBook, originalEdition, mediaType);

                // Generate unique copy slug. Numeric suffixes look like canonical sibling rows, so clone paths
                // must carry the same explicit marker as the unit-destination clone path.
                var existingBooks = _bookService.GetBooksByAuthorId(author.Id);
                var baseSlug = originalBook.TitleSlug;
                if (string.IsNullOrEmpty(baseSlug))
                {
                    // Keep normalization consistent with AuthorImportService.cs:725
                    baseSlug = originalBook.Title?.ToLowerInvariant().Replace(" ", "-") ?? $"book-{originalBook.Id}";
                    _logger.Warn("Book {0} (ID: {1}) has null or empty TitleSlug, generated: {2}", 
                                originalBook.Title, originalBook.Id, baseSlug);
                }
                
                // Strip existing numeric suffix if present (avoids foo_2_3 problem)
                var lastUnderscore = baseSlug.LastIndexOf('_');
                if (lastUnderscore > 0)
                {
                    var suffix = baseSlug.Substring(lastUnderscore + 1);
                    if (int.TryParse(suffix, out _))
                    {
                        baseSlug = baseSlug.Substring(0, lastUnderscore);
                    }
                }

                var existingSlugs = new HashSet<string>(
                    existingBooks
                        .Where(b => !string.IsNullOrWhiteSpace(b.TitleSlug))
                        .Select(b => b.TitleSlug),
                    StringComparer.Ordinal);

                var copySlugBase = $"{baseSlug}_copy_{copySuffix}";
                if (!string.IsNullOrWhiteSpace(narratorIdentifier))
                {
                    copySlugBase = $"{copySlugBase}_{narratorIdentifier}";
                }

                int counter = 1;
                string nextSlug;
                do
                {
                    nextSlug = counter == 1 ? copySlugBase : $"{copySlugBase}_{counter}";
                    counter++;
                } while (existingSlugs.Contains(nextSlug));

                // Shared metadata copying keeps physical instances in the same authoritative work.
                duplicateBook = RefreshEntityCopy.CloneBook(originalBook, includeEditions: false, includeSeriesLinks: false);
                duplicateBook.TitleSlug = nextSlug;
                duplicateBook.AuthorId = author.Id;
                duplicateBook.Author = author;
                duplicateBook.MediaType = mediaType;
                duplicateBook.AnyEditionOk = anyEditionOk;
                duplicateBook.UnitKeyHash = unitKeyHash;
                duplicateBook.IsGraphicAudio = AudioProductionDetector.DetectRecording(localBook);
                duplicateBook.Added = DateTime.UtcNow;
                duplicateBook.LastUpdated = DateTime.UtcNow;
                duplicateBook.LastSearchTime = null;
                duplicateBook.AddOptions = null;
                duplicateBook.ForeignEditionId = originalEdition?.ForeignEditionId;

                // Ensure unique slug
                _bookService.InsertMany(new List<Book> { duplicateBook });

                // Create edition for the new book
                var newEdition = RefreshEntityCopy.CloneEdition(originalEdition);
                newEdition.TitleSlug = $"{duplicateBook.TitleSlug}_edition";
                newEdition.Monitored = true;
                newEdition.ManualAdd = false;
                newEdition.BookId = duplicateBook.Id;
                newEdition.Book = duplicateBook;

                _editionService.InsertMany(new List<Edition> { newEdition });
                
                // Publish the normal book-added lifecycle for the new instance.
                _eventAggregator.PublishEvent(new BookAddedEvent(duplicateBook, false));

                return new BatchBookResult { NewBook = duplicateBook, NewEdition = newEdition };
            }
            catch (Exception ex)
            {
                if (duplicateBook?.Id > 0)
                {
                    _bookService.DeleteBook(duplicateBook.Id, deleteFiles: false);
                }

                _logger.Error(ex, "Failed to create new book instance for batch");
                return null;
            }
        }

        private static HashSet<string> CloneRemoteProviderIds(IEnumerable<string> source)
        {
            var values = source?
                .Where(id => id.IsNotNullOrWhiteSpace())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return values?.Count > 0 ? values : null;
        }

        private string GetAdditionalCopyPathCollisionReason(List<ImportDecision<LocalBook>> bookDecisions, Book book, Author author)
        {
            var plannedDestinations = new List<string>();

            foreach (var decision in bookDecisions)
            {
                var localBook = decision?.Item;
                if (localBook == null || localBook.Edition == null || localBook.Quality == null)
                {
                    continue;
                }

                localBook.Author ??= author;
                localBook.Book ??= book;
                localBook.Edition.Book ??= book;

                var previewBookFile = new BookFile
                {
                    Path = localBook.Path.CleanFilePath(),
                    Quality = localBook.Quality,
                    IsGraphicAudio = AudioProductionDetector.DetectRecording(localBook),
                    AudioProductionOverride = localBook.AudioProductionOverride,
                    AudioProductionType = AudioProductionDetector.GetRecordingProductionType(localBook),
                    EditionId = localBook.Edition.Id,
                    Edition = localBook.Edition,
                    Author = author,
                    Part = localBook.Part,
                    PartCount = localBook.PartCount,
                    MediaType = BookFile.DetermineMediaType(localBook.Quality)
                };

                var destinationPath = _bookFileMover.GetImportDestinationPath(previewBookFile, localBook);
                if (destinationPath.IsNullOrWhiteSpace())
                {
                    continue;
                }

                if (plannedDestinations.Any(existing => existing.PathEquals(destinationPath)))
                {
                    return $"Additional physical copy cannot be imported because multiple files would resolve to the same managed destination: {destinationPath}. Current naming settings cannot represent a separate copy.";
                }

                plannedDestinations.Add(destinationPath);

                var existingTracked = _mediaFileService.GetFileWithPath(destinationPath);
                var destinationExists = DestinationFileExists(destinationPath);
                if (existingTracked != null && destinationExists && existingTracked.Path.PathNotEquals(localBook.Path))
                {
                    return $"Additional physical copy cannot be imported because the managed destination is already occupied: {destinationPath}. Current naming settings cannot represent a separate copy.";
                }

                if (destinationExists && (existingTracked == null || existingTracked.Path.PathNotEquals(localBook.Path)))
                {
                    return $"Additional physical copy cannot be imported because the managed destination already exists on disk: {destinationPath}. Current naming settings cannot represent a separate copy.";
                }
            }

            return null;
        }
    }
}
