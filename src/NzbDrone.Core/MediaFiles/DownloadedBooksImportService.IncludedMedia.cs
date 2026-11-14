using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Abstractions;
using System.Linq;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.MediaFiles
{
    public partial class DownloadedBooksImportService
    {
        // Called only after the originally grabbed media has completed. Included media
        // is matched independently; it cannot satisfy or redirect the original grab.
        public List<ImportResult> ProcessIncludedMedia(DownloadClientItem downloadClientItem, RemoteBook remoteBook, List<EntityHistory> history)
        {
            var grabbedMedia = remoteBook?.GetMediaType();
            var authorId = ResolveRestrictedAuthorId(remoteBook?.Author, remoteBook);
            if (!grabbedMedia.HasValue || !authorId.HasValue || downloadClientItem == null)
            {
                return new List<ImportResult>();
            }

            var media = grabbedMedia == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook;
            var paths = (downloadClientItem.FilePaths ?? new List<string>())
                .Where(path => GrabbedFormatMismatch.IsBookFile(path) && QualityMediaTypeHelper.GetMediaTypeFromPath(path) == media)
                .Distinct(NzbDrone.Common.PathEqualityComparer.Instance).ToList();
            if (paths.Count == 0)
            {
                return new List<ImportResult>();
            }

            var results = new List<ImportResult>();
            try
            {
                var job = media == BookMediaType.Audiobook ? _conversionJobService.Get(downloadClientItem.DownloadId) : null;
                if (job != null && ConversionJobService.IsWaitingForOutput(job, _conversionJobService))
                    return paths.Select(path => new ImportResult(new ImportDecision<LocalBook>(new LocalBook { Path = path, IsIncludedMedia = true }),
                        ImportResultType.Pending, "Waiting for conversion to finish.")).ToList();
                var accepted = job?.GetAcceptedRequest();
                ProcessIncludedMediaCore(downloadClientItem, remoteBook, history, authorId.Value, media, paths, results, accepted);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "[INCLUDED-MEDIA] Skipping optional files for {0}; the requested book was imported", downloadClientItem.DownloadId);
                if (ex is ImportApprovedBooks.IncludedImportException partial)
                {
                    results.AddRange(partial.Results.Select(result => result.Result is ImportResultType.Imported or ImportResultType.Pending
                        ? result : ImportResult.SkipIncludedMedia(result.ImportDecision, string.Join("; ", result.Errors))));
                }
                var reported = results.SelectMany(result => TrackedDownloadAlreadyImported.GetSourcePaths(result.ImportDecision.Item))
                    .ToHashSet(NzbDrone.Common.PathEqualityComparer.Instance);
                results.AddRange(SkipIncludedFiles(paths.Where(path => !reported.Contains(path)).ToList(), ex.Message));
            }

            var failures = results.Where(result => result.Result != ImportResultType.Imported &&
                result.Result != ImportResultType.Pending).ToList();
            if (failures.Count > 0)
            {
                var conversionPending = false;
                var reason = string.Join("; ", failures.SelectMany(result => result.Errors).Distinct());
                var protectedSources = results.Where(result => result.Result is ImportResultType.Imported or ImportResultType.Pending)
                    .SelectMany(result => TrackedDownloadAlreadyImported.GetSourcePaths(result.ImportDecision.Item))
                    .Concat((history ?? new List<EntityHistory>()).Where(row => row.EventType == EntityHistoryEventType.BookFileImported)
                        .SelectMany(TrackedDownloadAlreadyImported.GetHistorySourcePaths))
                    .ToHashSet(NzbDrone.Common.PathEqualityComparer.Instance);
                var failedSources = failures.SelectMany(result => TrackedDownloadAlreadyImported.GetSourcePaths(result.ImportDecision.Item))
                    .Where(path => !protectedSources.Contains(path)).Distinct(NzbDrone.Common.PathEqualityComparer.Instance).ToArray();
                try
                {
                    if (failedSources.Length > 0)
                    {
                        var retention = _conversionJobService.RetainCompletedOutput(downloadClientItem.DownloadId, failedSources, reason, discardOutput: true);
                        reason = retention.Message;
                        conversionPending = retention.Status == ConversionRetentionStatus.Pending;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "[INCLUDED-MEDIA] Could not clean up optional conversion work for download {0}", downloadClientItem.DownloadId);
                    reason += "; conversion cleanup failed: " + ex.Message;
                }

                results = results.Select(result => failures.Contains(result)
                    ? conversionPending
                        ? new ImportResult(result.ImportDecision, ImportResultType.Pending, reason)
                        : ImportResult.SkipIncludedMedia(result.ImportDecision, reason)
                    : result).ToList();
            }

            return results;
        }

        private void ProcessIncludedMediaCore(DownloadClientItem downloadClientItem, RemoteBook remoteBook,
            List<EntityHistory> history, int authorId, BookMediaType media, List<string> paths, List<ImportResult> results,
            ConversionJobRequest accepted = null)
        {
            var timer = Stopwatch.StartNew();
            var author = _authorService.GetAuthor(authorId);
            var acceptedPaths = accepted?.Sources?.Select(source => source.Path).ToList();
            if (accepted?.BookId > 0 && acceptedPaths?.Count > 0 && acceptedPaths.All(path => paths.Contains(path, NzbDrone.Common.PathEqualityComparer.Instance)) &&
                accepted.MatchesSources(accepted.BookId, acceptedPaths))
            {
                ResumeIncludedConversion(downloadClientItem, remoteBook, author, media, accepted, acceptedPaths, results);
                var remaining = paths.Except(acceptedPaths, NzbDrone.Common.PathEqualityComparer.Instance).ToList();
                if (remaining.Count > 0)
                    ProcessIncludedMediaCore(downloadClientItem, remoteBook, history, authorId, media, remaining, results);
                return;
            }
            var enabled = media == BookMediaType.Audiobook ? author?.AudiobookMonitored : author?.EbookMonitored;
            var root = media == BookMediaType.Audiobook ? author?.AudiobookRootFolderPath : author?.EbookRootFolderPath;
            var profile = author?.GetQualityProfileForMediaType(media);
            if (enabled != true || string.IsNullOrWhiteSpace(root) || profile == null)
            {
                var reason = enabled != true ? "the author format is paused" :
                    string.IsNullOrWhiteSpace(root) ? "no root folder is configured" : "no quality profile is configured";
                _logger.Debug("[INCLUDED-MEDIA] DownloadId={0}, media={1}: skipped because {2}", downloadClientItem.DownloadId, media, reason);
                results.AddRange(SkipIncludedFiles(paths, reason));
                return;
            }

            var unavailable = GetMissingAuthoritativeMediaPaths(downloadClientItem, media);
            if (unavailable.Count > 0)
            {
                var reason = "Included book files are not available: " + string.Join(", ", unavailable);
                _logger.Info("[INCLUDED-MEDIA] DownloadId={0}: {1}", downloadClientItem.DownloadId, reason);
                results.AddRange(SkipIncludedFiles(paths, reason));
                return;
            }

            var files = paths.Where(_diskProvider.FileExists).Select(_diskProvider.GetFileInfo).ToList();
            if (files.Count == 0)
            {
                _logger.Debug("[INCLUDED-MEDIA] DownloadId={0}, media={1}: no included files are present", downloadClientItem.DownloadId, media);
                results.AddRange(SkipIncludedFiles(paths, "no included files are present"));
                return;
            }

            var discovered = ReadFiles(files, out var tagsByPath);
            // Included files were not the grabbed target: require the normal local author proof.
            var matches = _fileMatchingService.MatchFilesToLibraryAsync(discovered.ToArray(), restrictToAuthorId: null,
                MatchingContextPresets.ForDownloaded(false)).GetAwaiter().GetResult();
            if (matches.MatchedFiles.Length == 0)
            {
                _logger.Debug("[INCLUDED-MEDIA] DownloadId={0}, media={1}: no included files matched a library book", downloadClientItem.DownloadId, media);
                results.AddRange(SkipIncludedFiles(paths, "the included files no longer match a library book"));
                return;
            }

            // An unidentified audio file may be a chapter of the optional recording.
            // Do not convert/import a subset and treat the remaining chapter as an extra.
            if (media == BookMediaType.Audiobook && matches.UnmatchedFiles.Length > 0)
            {
                _logger.Debug("[INCLUDED-MEDIA] DownloadId={0}, media={1}, files={2}, unmatched={3}; optional recording skipped",
                    downloadClientItem.DownloadId, media, files.Count, matches.UnmatchedFiles.Length);
                results.AddRange(SkipIncludedFiles(paths, "not every part of the included recording matched"));
                return;
            }

            var activeOtherGrabs = _queueService.GetQueue(includeConversionStatus: false)
                .Where(item => item.HasActiveDownload() &&
                    !string.Equals(item.DownloadId, downloadClientItem.DownloadId, StringComparison.OrdinalIgnoreCase))
                .SelectMany(item => item.GetTargetBookIds()).ToHashSet();
            var previousSources = TrackedDownloadAlreadyImported.GetImportedFileSources(history);
            var books = _bookService.GetBooksForLookup(matches.MatchedFiles.Select(match => match.BookId).Distinct())
                .ToDictionary(book => book.Id);
            var grabbedWorks = (remoteBook.Books ?? new List<Book>()).SelectMany(BookIdentity.GetStableWorkProviderIdentityTokens)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var authors = new Dictionary<int, Author> { [author.Id] = author };
            var completedSources = new HashSet<string>(NzbDrone.Common.PathEqualityComparer.Instance);
            var decisions = new List<ImportDecision<LocalBook>>();
            foreach (var group in matches.MatchedFiles.GroupBy(match => match.BookId))
            {
                books.TryGetValue(group.Key, out var book);
                var reason = book == null ? "the book is no longer in the library" :
                    book.AuthorId != author.Id || book.MediaType != media ? "the author or format does not match" :
                    !grabbedWorks.Overlaps(BookIdentity.GetStableWorkProviderIdentityTokens(book)) ? "it is not the grabbed work's other format" :
                    !book.IsMonitored() ? "the book is not monitored" :
                    activeOtherGrabs.Contains(book.Id) ? "another grab is still active for this book" :
                    book.BookFiles?.Any(file => !TrackedDownloadAlreadyImported.IsFileFromDownload(file, downloadClientItem.DownloadId, previousSources)) == true
                        ? "the book already has files" : null;
                if (reason != null)
                {
                    _logger.Debug("[INCLUDED-MEDIA] DownloadId={0}, bookId={1}, media={2}: skipped because {3}",
                        downloadClientItem.DownloadId, group.Key, media, reason);
                    results.AddRange(SkipIncludedFiles(
                        group.Select(match => match.File.Path).ToList(), reason));
                    continue;
                }

                foreach (var file in book.BookFiles ?? new List<BookFile>())
                {
                    if (_diskProvider.FileExists(file.Path))
                    {
                        completedSources.UnionWith(file.MatchProvenance?.ImportSourcePaths?.Count > 0
                            ? file.MatchProvenance.ImportSourcePaths : previousSources.GetValueOrDefault(file.Id) ?? new List<string>());
                    }
                }

                var groupDecisions = new List<ImportDecision<LocalBook>>();
                string groupRefusal = null;
                foreach (var match in group)
                {
                    var edition = book.Editions?.FirstOrDefault(edition => edition.Id == match.EditionId);
                    reason = match.AuthorId != author.Id ? "the matched author differs" :
                        edition == null ? "the matched edition is no longer available" :
                        EditionPinPolicy.FindConflictingProtectedEdition(book, book.Editions, edition.Id) != null ? "another edition is pinned" : null;
                    if (reason != null)
                    {
                        _logger.Debug("[INCLUDED-MEDIA] DownloadId={0}, bookId={1}, path={2}: skipped because {3}",
                            downloadClientItem.DownloadId, book.Id, match.File.Path, reason);
                        groupRefusal ??= reason;
                        continue;
                    }

                    var decision = CreateDecisionForMatch(match, tagsByPath, books, authors);
                    decision.Item.IsIncludedMedia = true;
                    groupDecisions.Add(decision);
                }

                if (media != BookMediaType.Audiobook || groupDecisions.Count == group.Count())
                {
                    decisions.AddRange(groupDecisions);
                }
                if (groupDecisions.Count != group.Count())
                {
                    var acceptedDecisionPaths = groupDecisions.Select(decision => decision.Item.Path)
                        .ToHashSet(NzbDrone.Common.PathEqualityComparer.Instance);
                    results.AddRange(SkipIncludedFiles(
                        group.Select(match => match.File.Path)
                            .Where(path => media == BookMediaType.Audiobook || !acceptedDecisionPaths.Contains(path)).ToList(),
                        groupRefusal ?? "not every part of the included recording was accepted"));
                }
            }

            ApplyTrackedReleaseEvidence(decisions, remoteBook, downloadClientItem, titleEvidenceOnly: true);
            var item = downloadClientItem.Clone();
            item.DownloadForced = false;
            item.MediaType = media;
            if (decisions.Count > 0)
                results.AddRange(_importApprovedBooks.Import(decisions, replaceExisting: false, downloadClientItem: item, importMode: ImportMode.Copy));
            results.RemoveAll(result => ImportApprovedBooks.IsSkippedEbookAlternative(result) ||
                (result.Result == ImportResultType.Skipped && !result.IsIncludedMediaSkip && TrackedDownloadAlreadyImported.GetSourcePaths(result.ImportDecision.Item)
                    .All(completedSources.Contains)));
            _logger.Debug("[INCLUDED-MEDIA] DownloadId={0}, media={1}, files={2}, matched={3}, eligible={4}, imported={5}, elapsed={6}ms",
                item.DownloadId, media, files.Count, matches.MatchedFiles.Length, decisions.Count,
                results.Count(result => result.Result == ImportResultType.Imported), timer.ElapsedMilliseconds);

        }

        private void ResumeIncludedConversion(DownloadClientItem downloadClientItem, RemoteBook remoteBook,
            Author author, BookMediaType media, ConversionJobRequest accepted, List<string> paths, List<ImportResult> results)
        {
            var book = _bookService.GetBooksForLookup(new[] { accepted.BookId }).SingleOrDefault();
            if (book == null || author == null || book.AuthorId != author.Id || book.MediaType != media ||
                !(remoteBook.Books ?? new List<Book>()).SelectMany(BookIdentity.GetStableWorkProviderIdentityTokens)
                    .Intersect(BookIdentity.GetStableWorkProviderIdentityTokens(book), StringComparer.OrdinalIgnoreCase).Any())
            {
                results.AddRange(SkipIncludedFiles(paths, "the accepted recording's author, work or format is no longer available"));
                return;
            }

            var files = paths.Select(_diskProvider.GetFileInfo).ToList();
            var discovered = ReadFiles(files, out var tagsByPath);
            if (book.Editions?.Any(edition => edition.Id == accepted.EditionId) != true)
            {
                results.AddRange(SkipIncludedFiles(paths, "the accepted recording's edition is no longer available"));
                return;
            }
            var matches = discovered.Select(file => new FileMatch { File = file, AuthorId = author.Id, BookId = book.Id, EditionId = accepted.EditionId }).ToList();

            var books = new Dictionary<int, Book> { [book.Id] = book };
            var authors = new Dictionary<int, Author> { [author.Id] = author };
            var decisions = matches.Select(match => CreateDecisionForMatch(match, tagsByPath, books, authors)).ToList();
            foreach (var decision in decisions) decision.Item.IsIncludedMedia = true;
            ApplyTrackedReleaseEvidence(decisions, remoteBook, downloadClientItem, titleEvidenceOnly: true);
            var item = downloadClientItem.Clone();
            item.DownloadForced = false;
            item.MediaType = media;
            results.AddRange(_importApprovedBooks.Import(decisions, replaceExisting: false, downloadClientItem: item, importMode: ImportMode.Copy));
            _logger.Debug("[INCLUDED-MEDIA] Resumed accepted conversion DownloadId={0}, BookId={1}, EditionId={2}, sources={3}",
                downloadClientItem.DownloadId, book.Id, accepted.EditionId, paths.Count);
        }

        private static IEnumerable<ImportResult> SkipIncludedFiles(IReadOnlyCollection<string> sourcePaths, string reason)
        {
            return sourcePaths.Select(path => ImportResult.SkipIncludedMedia(
                new ImportDecision<LocalBook>(new LocalBook { Path = path, IsIncludedMedia = true }), reason));
        }

        private List<DiscoveredFileWithMetadata> ReadFiles(List<IFileInfo> mediaFiles,
            out Dictionary<string, Dictionary<string, List<string>>> tagsByPath)
        {
            var discovered = new List<DiscoveredFileWithMetadata>(mediaFiles.Count);
            tagsByPath = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase);
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

            return discovered;
        }
    }
}
