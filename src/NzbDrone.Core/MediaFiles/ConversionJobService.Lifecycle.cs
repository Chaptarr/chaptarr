using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NzbDrone.Common;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.MediaFiles
{
    public partial class ConversionJobService
    {
        private int _lifecycleAfterId;
        private TrackedDownloadRefreshedEvent _lastDownloadRefresh;
        private IReadOnlySet<int> _missingDownloadClients = new HashSet<int>();

        private bool IsProvablyRemoved(ConversionJob job) => job != null && job.DownloadClientId > 0 &&
            !IsAwaitingImport(_lastDownloadRefresh, job.DownloadClientId, job.DownloadId, DownloadHistoryEventType.DownloadGrabbed) &&
            (_missingDownloadClients.Contains(job.DownloadClientId) ||
             _lastDownloadRefresh?.IsProvablyMissing(job.DownloadClientId, job.DownloadId) == true);

        private static bool IsAwaitingImport(TrackedDownloadRefreshedEvent refresh, int clientId, string downloadId, DownloadHistoryEventType? latestEvent)
        {
            var tracked = refresh?.TrackedDownloads?.Where(item => item?.DownloadItem != null &&
                item.DownloadClient == clientId &&
                string.Equals(item.DownloadItem.DownloadId, downloadId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (tracked?.Count > 0)
                return tracked.Any(item => item.State is not (TrackedDownloadState.Imported or TrackedDownloadState.DownloadFailed or TrackedDownloadState.Ignored));
            return latestEvent != DownloadHistoryEventType.DownloadImported &&
                refresh?.ReportedDownloadIds?.TryGetValue(clientId, out var reported) == true && reported.Contains(downloadId);
        }

        public void Handle(DownloadCompletedEvent message) => CleanupTerminalDownload(message.TrackedDownload?.DownloadItem?.DownloadId, "imported", ConversionRemovalOrigin.Automatic, clientId: message.TrackedDownload?.DownloadClient);
        public void Handle(DownloadFailedEvent message) => CleanupTerminalDownload(
            message.TrackedDownload?.DownloadItem?.DownloadId ?? message.DownloadId, "failed", ConversionRemovalOrigin.DownloadFailure, clientId: message.TrackedDownload?.DownloadClient);
        public void Handle(DownloadIgnoredEvent message) => CleanupTerminalDownload(
            message.TrackedDownload?.DownloadItem?.DownloadId ?? message.DownloadId, "ignored", ConversionRemovalOrigin.UserAction, clientId: message.TrackedDownload?.DownloadClient ?? message.DownloadClientInfo?.Id);

        public void Handle(TrackedDownloadsRemovedEvent message)
        {
            foreach (var item in message.TrackedDownloads ?? new List<TrackedDownload>())
                CleanupTerminalDownload(item?.DownloadItem?.DownloadId, "removed", ConversionRemovalOrigin.UserAction, clientId: item?.DownloadClient);
        }

        public void Handle(TrackedDownloadRefreshedEvent message)
        {
            // Processing notifications carry no successful client poll. They must not
            // replace removal evidence or consume the next cleanup page.
            if (message.ReportedDownloadIds == null) return;
            _lastDownloadRefresh = message;
            RunSchedulerStep("download lifecycle", () => ReconcileDownloadLifecycles(message));
        }

        private void CleanupTerminalDownload(string downloadId, string reason, ConversionRemovalOrigin origin, ConversionLifecycleCandidate expected = null,
            CleanupInventory inventory = null, int? clientId = null, bool currentAttemptOnly = false)
        {
            if (string.IsNullOrWhiteSpace(downloadId)) return;
            try
            {
                // History uppercases IDs; restore the client's exact ID before checking
                // artifact receipts. Normal enqueue/lookups keep their exact identity.
                var exact = Get(downloadId);
                if (clientId > 0 && exact != null && exact.DownloadClientId > 0 && exact.DownloadClientId != clientId)
                {
                    _logger.Warn("Conversion cleanup client mismatch: downloadId={0}, eventClient={1}, jobClient={2}; no workspace was changed", downloadId, clientId, exact.DownloadClientId);
                    return;
                }
                if (expected == null && !(clientId.HasValue && exact?.DownloadClientId == clientId))
                {
                    try { downloadId = _repository.FindByHistoryDownloadId(downloadId, clientId > 0 ? clientId : null)?.DownloadId ?? downloadId; }
                    catch (InvalidOperationException ex)
                    {
                        _logger.Warn(ex, "Conversion cleanup identity is ambiguous: downloadId={0}, clientId={1}; no job or workspace was changed", downloadId, clientId);
                        return;
                    }
                }
                if (expected != null)
                {
                    var current = Get(downloadId);
                    if (current == null || current.Id != expected.Id || current.Status != expected.Status || current.UpdatedAt != expected.UpdatedAt) return;
                }
                PrepareArtifactRemoval(downloadId, currentAttemptOnly: currentAttemptOnly, reason: reason, origin: origin, inventory: inventory, expected: expected)();
            }
            catch (Exception ex)
            {
                RecordCleanupFailure(downloadId, ex);
            }
        }

        private void ReconcileDownloadLifecycles(TrackedDownloadRefreshedEvent refresh = null)
        {
            refresh ??= _lastDownloadRefresh;
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var clients = refresh?.ReportedDownloadIds?.Keys.ToArray() ?? Array.Empty<int>();
            var jobs = _repository.LifecycleCandidates(_lifecycleAfterId);
            _lifecycleAfterId = jobs.Count == ConversionJobRepository.CleanupBatchSize ? jobs[^1].Id : 0;
            _missingDownloadClients = jobs.Where(job => job.ClientMissing).Select(job => job.DownloadClientId).ToHashSet();
            var removals = new List<(ConversionLifecycleCandidate Job, string Reason, ConversionRemovalOrigin Origin)>();
            var current = _repository.ByDownloadIds(jobs.Select(item => item.DownloadId)).ToDictionary(job => job.Id);
            foreach (var candidate in jobs)
            {
                try
                {
                    if (!current.TryGetValue(candidate.Id, out var job) || job.UpdatedAt != candidate.UpdatedAt || job.Status != candidate.Status) continue;
                    var identityRequest = DeserializeRequest(job);
                    if (identityRequest != null && identityRequest.HistoryIdentityAmbiguous != candidate.IdentityAmbiguous)
                    {
                        lock (_sync)
                        {
                            var latest = Get(job.DownloadId);
                            if (latest?.Id != job.Id || latest.UpdatedAt != job.UpdatedAt) continue;
                            identityRequest.HistoryIdentityAmbiguous = candidate.IdentityAmbiguous;
                            job.RequestJson = JsonSerializer.Serialize(identityRequest);
                            _repository.SetFields(job, value => value.RequestJson);
                        }
                        if (candidate.IdentityAmbiguous)
                            _logger.Warn("Ambiguous conversion history identity: job={0}, downloadId={1}, clientId={2}; multiple raw IDs share this history key, so no inferred cleanup will run", job.Id, job.DownloadId, job.DownloadClientId);
                    }
                    if (candidate.IdentityAmbiguous) continue;
                    if (IsAwaitingImport(refresh, job.DownloadClientId, job.DownloadId, candidate.EventType)) continue;
                    if (!candidate.ClientMissing && refresh?.RetainedDownloadIds.ContainsKey(job.DownloadClientId) != true) continue;
                    var reason = candidate.EventType switch
                    {
                        DownloadHistoryEventType.DownloadImported => "imported",
                        DownloadHistoryEventType.DownloadFailed => "failed",
                        DownloadHistoryEventType.DownloadIgnored => "ignored",
                        _ => null
                    };
                    var origin = ConversionRemovalOrigin.Automatic;
                    if (IsProvablyRemoved(job))
                    {
                        reason ??= "removed";
                        origin = ConversionRemovalOrigin.Inferred;
                    }
                    if (reason == null)
                    {
                        continue;
                    }
                    if (!CleanupMayRetry(job)) continue;
                    candidate.UpdatedAt = job.UpdatedAt;
                    removals.Add((candidate, reason, origin));
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Unable to reconcile conversion job {0}, downloadId={1}; other jobs will continue", candidate.Id, candidate.DownloadId);
                }
            }
            var roots = removals.Select(item => item.Job.WorkRoot).TopmostFolders();
            var inventory = roots.Count == 0 ? null : ReadCleanupInventory(roots);
            foreach (var item in removals)
                CleanupTerminalDownload(item.Job.DownloadId, item.Reason, item.Origin, item.Job, inventory);
            var terminal = removals.Count;
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (terminal > 0 || elapsed >= 1000)
                _logger.Info("Conversion download reconciliation: clients={0}, candidates={1}, terminal={2}, elapsedMs={3}", clients.Length, jobs.Count, terminal, elapsed);
            else
                _logger.Debug("Conversion download reconciliation: clients={0}, candidates={1}, terminal={2}, elapsedMs={3}", clients.Length, jobs.Count, terminal, elapsed);
        }

        private int _attentionAfterId;
        private DateTime _attentionNotBefore;

        private static ConversionLifecycleCandidate ExpectedAttempt(ConversionJob job) => new()
        {
            Id = job.Id,
            DownloadId = job.DownloadId,
            Status = job.Status,
            UpdatedAt = job.UpdatedAt
        };

        private void ReviewCleanupAttention()
        {
            lock (_sync)
            {
                if (_attentionNotBefore > DateTime.UtcNow) return;
                _attentionNotBefore = DateTime.UtcNow.AddMinutes(1);
            }
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var jobs = _repository.CleanupReviewCandidates(_attentionAfterId);
            _attentionAfterId = jobs.Count == ConversionJobRepository.CleanupBatchSize ? jobs[^1].Id : 0;
            var roots = jobs.Where(CleanupMayRetry).Select(job => job.WorkRoot).TopmostFolders();
            var inventory = roots.Count == 0 ? null : ReadCleanupInventory(roots);
            List<RootFolder> libraryRoots = null;
            var availableRoots = new Dictionary<string, bool>(PathEqualityComparer.Instance);
            var attempted = 0;
            foreach (var candidate in jobs)
            {
                var job = candidate;
                try
                {
                    if (IsRunning(job.DownloadId) || !CleanupMayRetry(job)) continue;
                    var request = DeserializeRequest(job);
                    if (request == null) continue;
                    attempted++;
                    if (!request.DiscardRequested && !IsWaitingForOutput(job, this) && RecoverCommittedImport(job, request))
                    {
                        continue;
                    }
                    if (IsAbsentConversionPath(job.WorkFolder) && IsAbsentConversionPath(job.OutputPath))
                    {
                        EnsureCleanupRootAvailable(job.WorkFolder, ref libraryRoots, availableRoots);
                        // The workspace was removed externally; no encoder owns it now.
                        if (!TryRecover(job, current =>
                        {
                            job = current;
                            if (IsWaitingForOutput(job, this))
                            {
                                MarkFailed(job, "Conversion workspace was removed before conversion completed.");
                                job.UpdatedAt = _repository.Get(job.Id).UpdatedAt;
                            }
                        })) continue;
                        CleanupTerminalDownload(job.DownloadId, "workspace-removed", ConversionRemovalOrigin.Automatic,
                            expected: ExpectedAttempt(job), inventory: inventory, clientId: job.DownloadClientId, currentAttemptOnly: request.DiscardCurrentAttemptOnly);
                        continue;
                    }
                    if (request.HistoryIdentityAmbiguous || request.CleanupError == null) continue;
                    PrepareArtifactRemoval(job.DownloadId, request.DiscardCurrentAttemptOnly,
                        request.DiscardReason ?? "cleanup-retry", ConversionRemovalOrigin.Automatic, inventory, ExpectedAttempt(job))();
                }
                catch (Exception ex)
                {
                    RecordCleanupFailure(job.DownloadId, ex);
                }
            }
            LogCleanupPass("attention review", started, jobs.Count, attempted);
        }

        private void ClearCleanupAttention(string downloadId)
        {
            lock (_sync)
            {
                var job = Get(downloadId);
                var request = DeserializeRequest(job);
                if (request?.CleanupError == null) return;
                request.CleanupError = null;
                request.CleanupFailureCount = 0;
                request.CleanupNotBefore = null;
                job.RequestJson = JsonSerializer.Serialize(request);
                _repository.SetFields(job, value => value.RequestJson);
            }
        }

        private sealed class OrphanReviewProgress
        {
            public int AfterId { get; set; }
            public List<int> DeferredIds { get; set; } = new();
            public Dictionary<int, OrphanReviewRetry> Retries { get; set; } = new();
        }

        private sealed class OrphanReviewRetry
        {
            public DateTime NotBefore { get; set; }
            public string Error { get; set; }
        }

        private static readonly TimeSpan OrphanFailureRetryDelay = TimeSpan.FromHours(1);
        private DateTime _orphanNotBefore;
        private int _orphanDeferredAfterId;
        private string _invalidOrphanProgress;

        private void ReviewOrphanArtifacts()
        {
            if (_orphanNotBefore > DateTime.UtcNow) return;
            _orphanNotBefore = DateTime.UtcNow.AddMinutes(1);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var saved = _configService.GetConversionOrphanReviewProgress();
            OrphanReviewProgress progress;
            try
            {
                progress = string.IsNullOrEmpty(saved) ? new OrphanReviewProgress() : JsonSerializer.Deserialize<OrphanReviewProgress>(saved);
                if (progress == null || progress.AfterId < 0 || progress.DeferredIds == null || progress.Retries == null ||
                    progress.DeferredIds.Any(id => id <= 0) || progress.Retries.Any(pair => pair.Key <= 0 || pair.Value == null))
                    throw new JsonException("Invalid orphan review checkpoint.");
                _invalidOrphanProgress = null;
            }
            catch (JsonException ex)
            {
                if (_invalidOrphanProgress != saved)
                    _logger.Warn(ex, "Invalid conversion orphan review progress; resetting its checkpoint and rechecking history safely");
                _invalidOrphanProgress = saved;
                progress = new OrphanReviewProgress();
            }
            var due = progress.DeferredIds.Distinct().OrderBy(id => id)
                .Where(id => !progress.Retries.TryGetValue(id, out var retry) || retry.NotBefore <= DateTime.UtcNow).ToArray();
            // Rotation is process-local. Persist only changed membership or retry state,
            // not a reordered copy of an otherwise unchanged backlog every minute.
            var retryIds = due.Where(id => id > _orphanDeferredAfterId).Concat(due.Where(id => id <= _orphanDeferredAfterId))
                .Take(ConversionJobRepository.CleanupBatchSize / 2).ToArray();
            if (retryIds.Length > 0) _orphanDeferredAfterId = retryIds[^1];
            var candidates = _repository.OrphanArtifacts(progress.AfterId, retryIds);
            var newHistory = candidates.Count(candidate => candidate.History.Id > progress.AfterId);
            var deferred = progress.DeferredIds.Except(retryIds).ToHashSet();
            List<RootFolder> roots = null;
            var availableRoots = new Dictionary<string, bool>(PathEqualityComparer.Instance);
            var attempted = 0;
            // Old attempts are discovered through durable conversion history, never by
            // guessing ownership from a hidden folder's name or its age.
            foreach (var candidate in candidates)
            {
                var history = candidate.History;
                progress.AfterId = Math.Max(progress.AfterId, history.Id);
                var output = history.GetDataValue("ConvertedPath");
                string folder = null;
                try
                {
                    // History without a unique client cannot identify the artifact's
                    // download owner. Polling all clients does not resolve that identity.
                    if (candidate.EventType != DownloadHistoryEventType.DownloadImported && (candidate.ClientCount != 1 || candidate.DownloadClientId <= 0))
                        throw new ConversionCleanupRefusedException($"Conversion history does not identify one download client: downloadId={history.DownloadId}, clients={candidate.ClientCount}. The workspace was retained.");
                    if (IsAwaitingImport(_lastDownloadRefresh, candidate.DownloadClientId, history.DownloadId, candidate.EventType) ||
                        (candidate.EventType != DownloadHistoryEventType.DownloadImported && !candidate.ClientMissing &&
                         _lastDownloadRefresh?.RetainedDownloadIds.ContainsKey(candidate.DownloadClientId) != true))
                    {
                        deferred.Add(history.Id);
                        continue;
                    }
                    if (candidate.EventType is DownloadHistoryEventType.DownloadImported or DownloadHistoryEventType.DownloadFailed or DownloadHistoryEventType.DownloadIgnored && !candidate.Removed)
                        throw new ConversionCleanupRefusedException($"The terminal download event predates this conversion: downloadId={history.DownloadId}. The workspace was retained.");
                    var clientId = candidate.DownloadClientId;
                    if (!ConversionWorkPath.IsWorkPath(output)) continue;
                    folder = Path.GetDirectoryName(output);
                    if (!Directory.Exists(folder))
                    {
                        if (!IsAbsentConversionPath(folder))
                            throw new IOException($"Unable to verify conversion workspace availability: {folder}");
                        EnsureCleanupRootAvailable(folder, ref roots, availableRoots);
                        continue;
                    }
                    attempted++;
                    ConversionWorkPath.ValidateCleanup(folder);
                    var manifestPath = Path.Combine(folder, ArtifactManifestFileName);
                    var manifest = File.Exists(manifestPath) ? JsonSerializer.Deserialize<ConversionArtifactManifest>(File.ReadAllText(manifestPath)) : null;
                    var id = manifest?.DownloadId ?? history.DownloadId;
                    if (DownloadIdentity.Key(id) != history.DownloadId || (manifest?.OutputPath != null && !manifest.OutputPath.PathEquals(output)))
                        throw new ConversionCleanupRefusedException($"Conversion history and receipt ownership disagree: {folder}");
                    ConversionWorkPath.RetainedFolder(id, output);
                    var sources = manifest?.Sources ?? (JsonSerializer.Deserialize<List<string>>(history.GetDataValue("SourcePaths") ?? "[]") ?? new())
                        .Select(path => new ConversionArtifactSource { Path = path }).ToList();
                    var request = new ConversionJobRequest
                    {
                        DownloadId = id,
                        DownloadClientId = clientId,
                        BookId = history.BookId,
                        BookTitle = history.SourceTitle,
                        WorkRoot = Path.GetDirectoryName(folder),
                        WorkFolder = folder,
                        OutputPath = output,
                        Sources = sources,
                        DiscardRequested = true,
                        DiscardReason = "orphan",
                        DiscardCurrentAttemptOnly = true
                    };
                    ConversionJob job;
                    lock (_sync)
                    {
                        if (_repository.FindByHistoryDownloadId(history.DownloadId) != null) continue;
                        job = _repository.Insert(new ConversionJob
                        {
                            DownloadId = id,
                            DownloadClientId = clientId,
                            Status = ConversionJobStatus.Cancelled,
                            WorkRoot = request.WorkRoot,
                            WorkFolder = folder,
                            OutputPath = output,
                            RequestJson = JsonSerializer.Serialize(request),
                            CreatedAt = DateTime.UtcNow,
                            UpdatedAt = DateTime.UtcNow,
                            Message = "Removing completed download conversion workspace"
                        });
                    }
                    CleanupTerminalDownload(id, "orphan", ConversionRemovalOrigin.Automatic, expected: ExpectedAttempt(_repository.Get(job.Id)), clientId: clientId);
                }
                catch (Exception ex)
                {
                    deferred.Add(history.Id);
                    var changed = !progress.Retries.TryGetValue(history.Id, out var previous) || previous.Error != ex.Message;
                    progress.Retries[history.Id] = new OrphanReviewRetry
                    {
                        Error = ex.Message,
                        NotBefore = DateTime.UtcNow.Add(OrphanFailureRetryDelay)
                    };
                    if (changed)
                        _logger.Warn(ex, "Retaining unverifiable old conversion workspace until {0}: downloadId={1}, path={2}", progress.Retries[history.Id].NotBefore, history.DownloadId, folder ?? output);
                    else
                        _logger.Debug("Old conversion workspace remains deferred until {0}: downloadId={1}, path={2}", progress.Retries[history.Id].NotBefore, history.DownloadId, folder ?? output);
                }
            }
            // Save once after the page. Missing history or a newly owned job resolves
            // a deferred ID too; its normal job lifecycle now owns cleanup.
            progress.DeferredIds = deferred.OrderBy(id => id).ToList();
            foreach (var id in progress.Retries.Keys.Where(id => !deferred.Contains(id)).ToArray()) progress.Retries.Remove(id);
            var updated = JsonSerializer.Serialize(progress);
            if (updated != saved) _configService.SetConversionOrphanReviewProgress(updated);
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            _logger.Log(attempted > 0 || newHistory > 0 || elapsed >= 1000 ? NLog.LogLevel.Info : NLog.LogLevel.Debug,
                "Conversion orphan review: candidates={0}, attempted={1}, deferred={2}, afterId={3}, elapsedMs={4}, newHistory={5}",
                candidates.Count, attempted, progress.DeferredIds.Count, progress.AfterId, elapsed, newHistory);
        }

        private void EnsureCleanupRootAvailable(string path, ref List<RootFolder> roots, Dictionary<string, bool> availableRoots)
        {
            roots ??= _rootFolderService.All();
            var root = _rootFolderService.GetBestRootFolder(path, roots);
            if (root == null)
            {
                _logger.Debug("Missing conversion workspace is outside configured library roots: {0}", path);
                return;
            }
            // An unmounted share can leave an empty directory. A file in a flat root
            // is sufficient availability evidence; a directory-only check is not.
            if (!availableRoots.TryGetValue(root.Path, out var available))
                availableRoots[root.Path] = available = _diskProvider.FolderExists(root.Path) && !_diskProvider.FolderEmpty(root.Path);
            if (!available)
                throw new ConversionCleanupRefusedException($"The library root is missing or empty and may be unavailable: {root.Path}");
        }

        private void LogCleanupPass(string pass, long started, int candidates, int attempted)
        {
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            _logger.Log(attempted > 0 || elapsed >= 1000 ? NLog.LogLevel.Info : NLog.LogLevel.Debug,
                "Conversion {0}: candidates={1}, attempted={2}, elapsedMs={3}", pass, candidates, attempted, elapsed);
        }
    }
}
