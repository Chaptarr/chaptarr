using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Books;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Manual;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Download.TrackedDownloads
{
    public interface ITrackedDownloadAlreadyImported
    {
        bool IsImported(TrackedDownload trackedDownload, List<EntityHistory> historyItems,
            IReadOnlyCollection<ImportResult> results = null, IReadOnlyCollection<ManualImportFile> requestedFiles = null);
    }

    public class TrackedDownloadAlreadyImported : ITrackedDownloadAlreadyImported
    {
        private readonly Logger _logger;
        private readonly IMediaFileService _mediaFileService;

        public TrackedDownloadAlreadyImported(Logger logger, IMediaFileService mediaFileService = null)
        {
            _logger = logger;
            _mediaFileService = mediaFileService;
        }

        public bool IsImported(TrackedDownload trackedDownload, List<EntityHistory> historyItems,
            IReadOnlyCollection<ImportResult> results = null, IReadOnlyCollection<ManualImportFile> requestedFiles = null)
        {
            var timer = Stopwatch.StartNew();
            var bookIds = GetExpectedBooks(trackedDownload).Select(book => book.Id).ToList();
            var files = bookIds.Count > 0 ? _mediaFileService?.GetFilesByBooks(bookIds) ?? new List<BookFile>() : new List<BookFile>();
            var completion = Evaluate(trackedDownload, results, requestedFiles, historyItems, files);
            if (completion.Complete)
            {
                trackedDownload.ClearStatus();
            }
            else
            {
                trackedDownload.Warn(completion.Missing.ToArray());
            }

            _logger.Debug("[IMPORT-COMPLETION] DownloadId={0}, required={1}, covered={2}, history={3}, files={4}, complete={5}, elapsed={6}ms",
                trackedDownload.DownloadItem.DownloadId, completion.RequiredCount, completion.CoveredCount,
                historyItems?.Count ?? 0, files.Count, completion.Complete, timer.ElapsedMilliseconds);
            return completion.Complete;
        }

        internal sealed class Completion
        {
            public bool Complete { get; set; }
            public int RequiredCount { get; set; }
            public int CoveredCount { get; set; }
            public List<TrackedDownloadStatusMessage> Missing { get; } = new();
        }

        internal static Completion Evaluate(TrackedDownload tracked, IReadOnlyCollection<ImportResult> results = null,
            IReadOnlyCollection<ManualImportFile> requestedFiles = null, IReadOnlyCollection<EntityHistory> history = null,
            IReadOnlyCollection<BookFile> savedFiles = null)
        {
            results ??= Array.Empty<ImportResult>();
            history ??= Array.Empty<EntityHistory>();
            var downloadId = tracked?.DownloadItem?.DownloadId;
            var books = GetExpectedBooks(tracked);
            var targets = books.Select(book => book.Id).ToHashSet();
            var alternatives = tracked?.RemoteBook?.AlternativeCopies == true;
            var targetMedia = tracked?.RemoteBook?.GetMediaType();
            var coveredBooks = new HashSet<int>();
            var coveredEditions = new HashSet<(int BookId, int EditionId)>();
            var required = new Dictionary<string, int>(PathEqualityComparer.Instance);
            var covered = new HashSet<string>(PathEqualityComparer.Instance);
            var errors = new Dictionary<string, string[]>(PathEqualityComparer.Instance);
            var excluded = new HashSet<string>(PathEqualityComparer.Instance);
            var completion = new Completion();
            var manualTargets = results.Select(result => result.ImportDecision?.Item)
                .Where(item => item?.IsManualImport == true && item.Book?.Id > 0 && item.Edition?.Id > 0)
                .SelectMany(item => GetSourcePaths(item).Select(path => (Path: path, BookId: item.Book.Id, EditionId: item.Edition.Id)))
                .ToLookup(target => target.Path, target => (target.BookId, target.EditionId), PathEqualityComparer.Instance);

            bool MatchesManualSelection(string path, int bookId, int editionId) =>
                !manualTargets.Contains(path) || manualTargets[path].Contains((bookId, editionId));

            void Require(string path, int bookId = 0)
            {
                if (IsTargetMediaFile(path, targetMedia))
                {
                    if (!required.ContainsKey(path) || bookId > 0)
                    {
                        required[path] = bookId;
                    }
                }
            }

            bool IsTarget(int bookId, int editionId, DateTime? importedAt = null)
            {
                if (bookId <= 0 || (targets.Count > 0 && !targets.Contains(bookId)))
                {
                    return false;
                }

                return !alternatives || !importedAt.HasValue || (tracked.RemoteBook.TargetEditionIds.TryGetValue(bookId, out var targetEdition) &&
                    targetEdition == editionId && (!importedAt.HasValue ||
                        (tracked.RemoteBook.AlternativeCopyGrabDate.HasValue && importedAt >= tracked.RemoteBook.AlternativeCopyGrabDate)));
            }

            var inventory = tracked?.ImportItem?.FilePaths;
            if (inventory == null || inventory.Count == 0)
            {
                inventory = tracked?.DownloadItem?.FilePaths;
            }

            foreach (var path in inventory ?? new List<string>())
            {
                Require(path);
            }

            // A parent-folder preview may annotate a captured inventory, but cannot enlarge it.
            requestedFiles = (requestedFiles ?? Array.Empty<ManualImportFile>())
                .Where(file => file != null && (file.DownloadId.IsNullOrWhiteSpace() ||
                    string.Equals(file.DownloadId, downloadId, StringComparison.OrdinalIgnoreCase)) &&
                    (inventory == null || inventory.Count == 0 || required.ContainsKey(file.Path ?? string.Empty)))
                .ToList();
            foreach (var file in requestedFiles)
            {
                Require(file.Path, file.BookId);
            }

            foreach (var result in results)
            {
                var item = result.ImportDecision?.Item;
                if (!GetSourcePaths(item).Any())
                {
                    completion.Missing.Add(new TrackedDownloadStatusMessage(tracked?.DownloadItem?.Title ?? string.Empty,
                        "Import result has no source-file evidence"));
                }
                foreach (var path in GetSourcePaths(item))
                {
                    Require(path, item?.Book?.Id ?? 0);
                    if (result.Result != ImportResultType.Imported)
                    {
                        errors[path] = result.Errors.ToArray();
                    }
                    else if (IsTarget(item?.Book?.Id ?? 0, item?.Edition?.Id ?? 0))
                    {
                        covered.Add(path);
                        coveredBooks.Add(item.Book.Id);
                        coveredEditions.Add((item.Book.Id, item.Edition?.Id ?? 0));
                    }
                }
            }

            foreach (var row in history.Where(row => row.EventType == EntityHistoryEventType.BookFileImported &&
                         string.Equals(row.DownloadId, downloadId, StringComparison.OrdinalIgnoreCase) && IsTarget(row.BookId, row.EditionId, row.Date)))
            {
                var paths = GetHistorySourcePaths(row).Where(path => MatchesManualSelection(path, row.BookId, row.EditionId)).ToList();
                covered.UnionWith(paths);
                excluded.UnionWith(ReadHistoryPaths(row, "ExcludedSourcePaths"));
                if (paths.Count > 0)
                {
                    coveredBooks.Add(row.BookId);
                    coveredEditions.Add((row.BookId, row.EditionId));
                }
            }

            foreach (var file in savedFiles ?? Array.Empty<BookFile>())
            {
                if (downloadId.IsNotNullOrWhiteSpace() &&
                    string.Equals(file.MatchProvenance?.ImportDownloadId, downloadId, StringComparison.OrdinalIgnoreCase) &&
                    IsTarget(file.Edition?.BookId ?? 0, file.EditionId, file.DateAdded))
                {
                    var sources = (file.MatchProvenance.ImportSourcePaths ?? new List<string>())
                        .Where(path => MatchesManualSelection(path, file.Edition.BookId, file.EditionId)).ToList();
                    covered.UnionWith(sources);
                    excluded.UnionWith(file.MatchProvenance.ImportExcludedSourcePaths ?? new List<string>());
                    if (sources.Count > 0)
                    {
                        coveredBooks.Add(file.Edition.BookId);
                        coveredEditions.Add((file.Edition.BookId, file.EditionId));
                    }
                }
            }

            excluded.ExceptWith(errors.Keys);

            foreach (var result in results.Where(ImportApprovedBooks.IsSkippedEbookAlternative))
            {
                if (coveredBooks.Contains(result.ImportDecision.Item.Book.Id) &&
                    (!alternatives || coveredEditions.Contains((result.ImportDecision.Item.Book.Id, result.ImportDecision.Item.Edition?.Id ?? 0))))
                {
                    excluded.UnionWith(GetSourcePaths(result.ImportDecision.Item));
                }
            }

            var ebookIds = books.Where(book => book.MediaType == BookMediaType.Ebook).Select(book => book.Id).ToHashSet();
            if (!alternatives)
            {
                // Ordinary ebook import already selects one format per book. Unselected preview
                // alternatives are not additional parts; selected transfer failures still count.
                foreach (var file in requestedFiles)
                {
                    if (ebookIds.Contains(file.BookId) && coveredBooks.Contains(file.BookId) &&
                        NzbDrone.Core.Qualities.QualityMediaTypeHelper.GetMediaTypeFromPath(file.Path) == BookMediaType.Ebook &&
                        !errors.ContainsKey(file.Path) && !covered.Contains(file.Path))
                    {
                        excluded.Add(file.Path);
                    }
                }
            }

            foreach (var source in required)
            {
                // A proven other book is outside this grab; an unmatched source is still required.
                if ((targets.Count > 0 && source.Value > 0 && !targets.Contains(source.Value)) || excluded.Contains(source.Key))
                {
                    continue;
                }

                completion.RequiredCount++;
                if (covered.Contains(source.Key))
                {
                    completion.CoveredCount++;
                }
                else
                {
                    completion.Missing.Add(new TrackedDownloadStatusMessage(CompletedDownloadService.GetImportFileTitle(tracked, source.Key),
                        (errors.TryGetValue(source.Key, out var reasons) && reasons.Length > 0 ? reasons : new[] { "Source file has not been imported" }).ToList()) { SourcePath = source.Key });
                }
            }

            var mediaTypes = books.Select(book => book.MediaType).ToHashSet();
            if (targets.Count > 0 && results.Any(result => result.Result == ImportResultType.Imported &&
                result.ImportDecision?.Item?.Book is Book book && mediaTypes.Contains(book.MediaType) && !targets.Contains(book.Id)))
            {
                completion.Missing.Add(new TrackedDownloadStatusMessage(tracked.DownloadItem.Title,
                    "Imported files do not match the grabbed book rows"));
            }

            var missingTargets = alternatives ? coveredBooks.Count != 1 : targets.Any(id => !coveredBooks.Contains(id));
            if (completion.Missing.Count == 0 && (missingTargets || completion.RequiredCount == 0 || coveredBooks.Count == 0))
            {
                completion.Missing.Add(new TrackedDownloadStatusMessage(tracked?.DownloadItem?.Title ?? string.Empty,
                    "Import completion could not be proven for the requested files and books") { IsCompletionFallback = true });
            }

            completion.Complete = completion.Missing.Count == 0;
            return completion;
        }

        internal static bool IsTargetMediaFile(string path, BookMediaType? mediaType)
        {
            return path.IsNotNullOrWhiteSpace() && !DiskScanService.IsExcludedFile(path) &&
                MediaFileExtensions.AllExtensions.Contains(Path.GetExtension(path)) &&
                (!mediaType.HasValue || NzbDrone.Core.Qualities.QualityMediaTypeHelper.GetMediaTypeFromPath(path) == mediaType);
        }

        internal static List<Book> GetExpectedBooks(TrackedDownload tracked)
        {
            return tracked?.RemoteBook?.GetBooksMatchingReleaseMediaType()
                .Where(book => book != null && book.Id > 0).DistinctBy(book => book.Id).ToList() ?? new List<Book>();
        }

        internal static IEnumerable<string> GetSourcePaths(LocalBook item)
        {
            return item?.GeneratedConversionSourcePaths?.Count > 0
                ? item.GeneratedConversionSourcePaths.Where(path => path.IsNotNullOrWhiteSpace())
                : item?.Path.IsNotNullOrWhiteSpace() == true ? new[] { item.Path } : Enumerable.Empty<string>();
        }

        internal static IEnumerable<string> GetHistorySourcePaths(EntityHistory row)
        {
            var paths = ReadHistoryPaths(row, "SourcePaths").ToList();
            var dropped = row.GetDataValue("DroppedPath");
            if (dropped.IsNotNullOrWhiteSpace())
            {
                paths.Add(dropped);
            }

            return paths;
        }

        private static IEnumerable<string> ReadHistoryPaths(EntityHistory row, string key)
        {
            return row.GetDataValue(key) is string json && Json.TryDeserialize<List<string>>(json, out var paths) && paths != null
                ? paths.Where(path => path.IsNotNullOrWhiteSpace()) : Enumerable.Empty<string>();
        }

        internal static Dictionary<int, List<string>> GetImportedFileSources(IEnumerable<EntityHistory> history)
        {
            return (history ?? Enumerable.Empty<EntityHistory>()).Where(row => row.EventType == EntityHistoryEventType.BookFileImported)
                .Select(row => (Id: int.TryParse(row.GetDataValue("FileId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0, Row: row))
                .Where(item => item.Id > 0).GroupBy(item => item.Id)
                .ToDictionary(group => group.Key, group => group.SelectMany(item => GetHistorySourcePaths(item.Row)).Distinct(PathEqualityComparer.Instance).ToList());
        }

        internal static bool IsFileFromDownload(BookFile file, string downloadId, IReadOnlyDictionary<int, List<string>> importedFileSources, DateTime? grabbedAt = null)
        {
            return downloadId.IsNotNullOrWhiteSpace() && (!grabbedAt.HasValue || file.DateAdded >= grabbedAt.Value) &&
                (string.Equals(file.MatchProvenance?.ImportDownloadId, downloadId, StringComparison.OrdinalIgnoreCase) ||
                 (file.Id > 0 && importedFileSources?.ContainsKey(file.Id) == true));
        }
    }
}
