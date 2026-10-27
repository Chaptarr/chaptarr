using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.History;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.MediaFiles
{
    public interface IConversionJobService
    {
        int WorkerConcurrency { get; }
        ConversionJob Get(string downloadId);
        ConversionJobRequest GetCompletedConversion(string downloadId);
        List<ConversionJobRequest> GetCompletedConversions(IEnumerable<string> downloadIds);
        List<ConversionJob> Get(IEnumerable<string> downloadIds);
        ConversionJob Enqueue(ConversionJobRequest request);
        bool IsActive(string downloadId);
        bool IsRunning(string downloadId);
        bool Cancel(string downloadId);
        void Complete(string downloadId);
        void Fail(string downloadId, string error);
        void Reset(string downloadId, ConversionJob expectedJob = null);
        void RemoveArtifacts(string downloadId);
        Action PrepareArtifactRemoval(string downloadId);
    }

    public class ConversionJobService :
        IConversionJobService,
        IHandle<ApplicationStartedEvent>,
        IHandle<ApplicationShutdownRequested>,
        IHandle<BookGrabbedEvent>
    {
        private const string ArtifactManifestFileName = "conversion-artifact.json";
        private static readonly TimeSpan LeaseTimeout = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan SchedulerPollInterval = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan QueueCoalesceDelay = TimeSpan.FromMilliseconds(100);
        private static readonly TimeSpan CompletedRetention = TimeSpan.FromDays(7);
        private readonly object _sync = new();
        private readonly AutoResetEvent _signal = new(false);
        private readonly Dictionary<string, ActiveConversion> _activeConversions = new(StringComparer.OrdinalIgnoreCase);
        private readonly IConversionJobRepository _repository;
        private readonly IM4bConversionService _conversionService;
        private readonly IConversionTrackingService _trackingService;
        private readonly IManageCommandQueue _commandQueue;
        private readonly Logger _logger;
        private readonly IConfigService _configService;
        private readonly IHistoryService _historyService;
        private readonly IMediaFileService _mediaFileService;
        private CancellationTokenSource _shutdown;
        private Task _scheduler;

        public ConversionJobService(
            IConversionJobRepository repository,
            IM4bConversionService conversionService,
            IConversionTrackingService trackingService,
            IManageCommandQueue commandQueue,
            IConfigService configService,
            Logger logger,
            IHistoryService historyService = null,
            IMediaFileService mediaFileService = null)
        {
            _repository = repository;
            _conversionService = conversionService;
            _trackingService = trackingService;
            _commandQueue = commandQueue;
            _configService = configService;
            _logger = logger;
            _historyService = historyService;
            _mediaFileService = mediaFileService;
        }

        public int WorkerConcurrency => Clamp(_configService?.AudiobookConversionConcurrentConversions ?? 1, 1, 16);

        public ConversionJob Get(string downloadId)
        {
            return _repository.FindByDownloadId(downloadId);
        }

        public ConversionJobRequest GetCompletedConversion(string downloadId)
        {
            return GetCompletedConversion(Get(downloadId));
        }

        public List<ConversionJobRequest> GetCompletedConversions(IEnumerable<string> downloadIds)
        {
            return Get(downloadIds).Select(GetCompletedConversion).Where(request => request != null).ToList();
        }

        private ConversionJobRequest GetCompletedConversion(ConversionJob job)
        {
            if (job == null || (IsInFlight(job.Status) && job.Status != ConversionJobStatus.ReadyToImport) || IsRunning(job.DownloadId)) return null;

            var request = DeserializeRequest(job);
            return request?.DiscardRequested == false && request.DownloadId == job.DownloadId && HasSuccessfulArtifact(request)
                ? request : null;
        }

        public List<ConversionJob> Get(IEnumerable<string> downloadIds)
        {
            return _repository.ByDownloadIds(downloadIds);
        }

        public void Handle(BookGrabbedEvent message)
        {
            lock (_sync)
            {
                var previous = Get(message.DownloadId);
                // A real new grab starts a lifecycle; an owned encoder is never retired underneath its worker.
                if (previous != null && !IsInFlight(previous.Status) && !_activeConversions.ContainsKey(message.DownloadId))
                {
                    if (DeserializeRequest(previous)?.DiscardRequested == true)
                    {
                        try { RemoveArtifacts(message.DownloadId); }
                        catch (Exception ex)
                        {
                            // The grab has already succeeded. Preserve its cleanup owner and let the
                            // existing scheduler retry; conversion admission waits for the real blocker.
                            _logger.Warn(ex, "New grab accepted for {0}; previous conversion cleanup remains pending", message.DownloadId);
                        }
                        return;
                    }

                    _repository.DeleteByDownloadId(message.DownloadId);
                    _trackingService?.Clear(message.DownloadId);
                }
            }
        }

        public ConversionJob Enqueue(ConversionJobRequest request)
        {
            if (!IsValidRequest(request))
            {
                throw new ArgumentException("A conversion job requires stable sources and an isolated .chaptarr-conversions work path.", nameof(request));
            }

            var requestJson = JsonSerializer.Serialize(request);
            ConversionJob job;

            lock (_sync)
            {
                job = _repository.FindByDownloadId(request.DownloadId);
                if (DeserializeRequest(job)?.DiscardRequested == true)
                {
                    throw new InvalidOperationException("Conversion is waiting for cleanup of the previous discarded attempt. It will resume automatically after cleanup succeeds; check the cleanup error in the logs.");
                }

                if (job != null && IsInFlight(job.Status))
                {
                    return job;
                }

                var now = DateTime.UtcNow;
                if (job == null)
                {
                    job = new ConversionJob
                    {
                        DownloadId = request.DownloadId,
                        CreatedAt = now
                    };
                }

                job.Status = ConversionJobStatus.Queued;
                job.RequestJson = requestJson;
                job.WorkRoot = request.WorkRoot;
                job.WorkFolder = request.WorkFolder;
                job.OutputPath = request.OutputPath;
                job.TargetQualityId = request.TargetQualityId;
                job.TargetQualityName = request.TargetQualityName;
                job.Progress = 0m;
                job.Message = "Waiting to convert";
                job.Error = null;
                job.UpdatedAt = now;
                job.StartedAt = null;
                job.HeartbeatAt = null;
                job.CompletedAt = null;

                if (job.Id == 0)
                {
                    job = _repository.Insert(job);
                }
                else
                {
                    job = _repository.Update(job);
                }
            }

            _trackingService?.Start(job.DownloadId, job.TargetQualityId, job.TargetQualityName, job.Message);
            _trackingService?.Progress(job.DownloadId, 0m, job.Message);
            _signal.Set();
            return job;
        }

        public bool IsActive(string downloadId)
        {
            var job = Get(downloadId);
            return job != null && IsInFlight(job.Status);
        }

        public bool IsRunning(string downloadId)
        {
            lock (_sync)
            {
                return _activeConversions.ContainsKey(downloadId);
            }
        }

        public bool Cancel(string downloadId)
        {
            if (downloadId.IsNullOrWhiteSpace())
            {
                return false;
            }

            ConversionJob job;
            ActiveConversion active = null;

            lock (_sync)
            {
                job = _repository.FindByDownloadId(downloadId);
                if (job == null || !IsInFlight(job.Status))
                {
                    return false;
                }

                _activeConversions.TryGetValue(downloadId, out active);
                if (active != null || job.Status == ConversionJobStatus.Converting || job.Status == ConversionJobStatus.Cancelling)
                {
                    job.Status = ConversionJobStatus.Cancelling;
                    job.Message = "Cancelling conversion";
                    job.UpdatedAt = DateTime.UtcNow;
                    _repository.Update(job);

                    // The owner removes active work under this gate before disposal.
                    active?.Cancellation.Cancel();
                }
                else
                {
                    job.Status = ConversionJobStatus.Cancelled;
                    job.Message = "Conversion cancelled";
                    job.Progress = null;
                    job.UpdatedAt = DateTime.UtcNow;
                    job.CompletedAt = job.UpdatedAt;
                    _repository.Update(job);
                }
            }

            if (active != null)
            {
                _trackingService?.Cancel(downloadId, active.Cancellation);
                _signal.Set();
            }
            else
            {
                if (job.Status == ConversionJobStatus.Cancelling)
                {
                    MarkCancelled(job, $"{job.TargetQualityName ?? "M4B"} conversion was cancelled.");
                }
                else
                {
                    _trackingService?.Cancelled(downloadId, job.Message);
                }

                CleanupWorkRoot(job.WorkFolder);
                QueueImportSweep();
            }

            return true;
        }

        public void Complete(string downloadId)
        {
            lock (_sync)
            {
                var job = UpdateTerminal(downloadId, ConversionJobStatus.Completed, null, "Converted M4B imported");
                var request = DeserializeRequest(job);
                if (request?.Sources.Count > 0)
                {
                    foreach (var manifest in ReadOwnedAttempts(downloadId, request.WorkRoot)
                        .Where(attempt => ConversionWorkPath.SourcesMatch(attempt.Sources, request.Sources)))
                    {
                        CleanupWorkRoot(Path.GetDirectoryName(manifest.OutputPath));
                    }
                }
            }
            _trackingService?.Complete(downloadId);
        }

        public void Fail(string downloadId, string error)
        {
            UpdateTerminal(downloadId, ConversionJobStatus.Failed, error, error);
            _trackingService?.Fail(downloadId, error);
        }

        public void Reset(string downloadId, ConversionJob expectedJob = null)
        {
            if (downloadId.IsNullOrWhiteSpace())
            {
                return;
            }

            lock (_sync)
            {
                var job = _repository.FindByDownloadId(downloadId);
                if (expectedJob != null && (job == null || job.Id != expectedJob.Id ||
                    !job.WorkFolder.PathEquals(expectedJob.WorkFolder) || !job.OutputPath.PathEquals(expectedJob.OutputPath)))
                {
                    _logger.Debug("Leaving a newer conversion attempt unchanged after original-file import for download {0}", downloadId);
                    return;
                }

                var running = IsRunning(downloadId);
                if (DeserializeRequest(job)?.DiscardRequested == true)
                {
                    throw new InvalidOperationException("This download was discarded and conversion cleanup is still pending. Check the cleanup error in the logs.");
                }

                if (!running && job != null && RecoverCommittedImport(job, DeserializeRequest(job)))
                {
                    return;
                }

                // A failed import-state write can leave a finished conversion ready.
                // Keep its provenance and let the normal importer consume the validated output.
                if (expectedJob == null && !running && job?.Status == ConversionJobStatus.ReadyToImport)
                {
                    if (!HasReusableArtifact(DeserializeRequest(job)))
                    {
                        throw new InvalidOperationException("Unable to verify the completed conversion output. Check that its source files and retained M4B are available before retrying import.");
                    }

                    return;
                }

                if (running || (job != null && IsInFlight(job.Status) && !(expectedJob != null && job.Status == ConversionJobStatus.ReadyToImport)))
                {
                    throw new InvalidOperationException("An active conversion job must be cancelled before it can be reset.");
                }

                // An original-file import retires its captured failed/stopped attempt. Retry
                // callers omit expectedJob and retain earlier output until its replacement succeeds.
                if (expectedJob != null) CleanupWorkRoot(job.WorkFolder, throwOnError: true);
                _repository.DeleteByDownloadId(downloadId);
                _trackingService?.Clear(downloadId);
            }
        }

        public void RemoveArtifacts(string downloadId)
        {
            PrepareArtifactRemoval(downloadId)();
        }

        public Action PrepareArtifactRemoval(string downloadId)
        {
            if (downloadId.IsNullOrWhiteSpace()) return () => { };

            // Capture and validate the exact owned attempts before a client/category/ignore mutation.
            var job = Get(downloadId);
            var history = _historyService?.FindByDownloadId(downloadId) ?? new List<EntityHistory>();
            var outputPaths = history
                .Where(row => row.EventType == EntityHistoryEventType.BookFileConverted || row.EventType == EntityHistoryEventType.BookFileConversionFailed)
                .Select(row => row.GetDataValue("ConvertedPath"))
                .Append(job?.OutputPath)
                .Where(path => path.IsNotNullOrWhiteSpace())
                .Distinct(PathEqualityComparer.Instance).ToList();
            var folders = new List<string>();
            foreach (var path in outputPaths.ToList())
            {
                var folder = ConversionWorkPath.RetainedFolder(downloadId, path);
                if (HasConflictingArtifactOwner(folder, downloadId))
                {
                    if (job?.OutputPath.PathEquals(path) == true) throw new IOException($"Conversion attempt records another download owner: {folder}");
                    // History normalizes download IDs. Exact receipt ownership must win over a
                    // whitespace/case collision in that historical lookup.
                    _logger.Warn("Retaining conversion attempt with different manifest ownership than download history for {0}: {1}", downloadId, folder);
                    outputPaths.Remove(path);
                    continue;
                }
                folders.Add(folder);
            }
            folders = folders.Distinct(PathEqualityComparer.Instance).ToList();
            if (job != null && job.WorkRoot.IsNotNullOrWhiteSpace() &&
                !Path.GetDirectoryName(ConversionWorkPath.RetainedFolder(downloadId, job.OutputPath)).PathEquals(job.WorkRoot))
            {
                throw new IOException($"Conversion workspace does not match its recorded output for download {downloadId}");
            }

            // Sanitized root names are not owner identities: a/b and a_b can share one root.
            // Existing job/history references prove their exact attempts. Discover an additional
            // attempt only through the exact DownloadId in its existing artifact receipt.
            var knownFolders = folders.ToHashSet(PathEqualityComparer.Instance);
            foreach (var root in folders.Select(Path.GetDirectoryName).Distinct(PathEqualityComparer.Instance).ToList())
            {
                foreach (var manifest in ReadOwnedAttempts(downloadId, root))
                {
                    var folder = Path.GetDirectoryName(manifest.OutputPath);
                    if (!knownFolders.Add(folder)) continue;
                    folders.Add(folder);
                    outputPaths.Add(manifest.OutputPath);
                }
            }

            var cleanupPaths = folders.Distinct(PathEqualityComparer.Instance).ToList();
            foreach (var path in cleanupPaths) ConversionWorkPath.ValidateCleanup(path);

            return () =>
            {
                lock (_sync)
                {
                    var current = Get(downloadId);
                    if (current != null && (job == null || current.Id != job.Id ||
                        !current.WorkFolder.PathEquals(job.WorkFolder) || !current.OutputPath.PathEquals(job.OutputPath)))
                    {
                        throw new InvalidOperationException("Conversion attempt changed while removing the download; its workspace was retained.");
                    }

                    if (current == null && folders.Count > 0)
                    {
                        // Completed rows may already have expired. Keep cleanup failure recoverable in
                        // the existing job table even when conversion history is the only prior owner.
                        var firstFolder = folders[0];
                        var cleanup = new ConversionJobRequest
                        {
                            DownloadId = downloadId, DiscardRequested = true,
                            WorkRoot = Path.GetDirectoryName(firstFolder), WorkFolder = firstFolder,
                            OutputPath = outputPaths.First(path => ConversionWorkPath.RetainedFolder(downloadId, path).PathEquals(firstFolder))
                        };
                        current = _repository.Insert(new ConversionJob
                        {
                            DownloadId = downloadId, Status = ConversionJobStatus.Cancelled,
                            RequestJson = JsonSerializer.Serialize(cleanup), WorkRoot = cleanup.WorkRoot,
                            WorkFolder = cleanup.WorkFolder, OutputPath = cleanup.OutputPath,
                            Message = "Download discarded; removing conversion files", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
                        });
                    }

                    if (current != null)
                    {
                        var request = DeserializeRequest(current);
                        if (request != null)
                        {
                            request.DiscardRequested = true;
                            current.RequestJson = JsonSerializer.Serialize(request);
                            _repository.Update(current);
                        }
                    }

                    if (!Cancel(downloadId))
                    {
                        if (current != null) MarkCancelled(current, "Download discarded; removing conversion files");
                        else _trackingService?.Cancel(downloadId);
                    }
                    var activeFolder = _activeConversions.ContainsKey(downloadId) ? job?.WorkFolder : null;
                    foreach (var path in cleanupPaths)
                    {
                        // The encoder owns its exact attempt until it has stopped.
                        if (activeFolder != null && path.PathEquals(activeFolder)) continue;
                        CleanupWorkRoot(path, throwOnError: true);
                    }

                    if (activeFolder == null)
                    {
                        _repository.DeleteByDownloadId(downloadId);
                        _trackingService?.Clear(downloadId);
                        QueueImportSweep();
                    }
                }
            };
        }

        private List<ConversionArtifactManifest> ReadOwnedAttempts(string downloadId, string root)
        {
            var attempts = new List<ConversionArtifactManifest>();
            if (root.IsNullOrWhiteSpace() || !Directory.Exists(root)) return attempts;
            foreach (var folder in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var manifestPath = Path.Combine(folder, ArtifactManifestFileName);
                    if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0 ||
                        !File.Exists(manifestPath) || (File.GetAttributes(manifestPath) & FileAttributes.ReparsePoint) != 0)
                    {
                        _logger.Warn("Retaining conversion attempt without ownership proof for download {0}: {1}", downloadId, folder);
                        continue;
                    }

                    var manifest = JsonSerializer.Deserialize<ConversionArtifactManifest>(File.ReadAllText(manifestPath));
                    if (!string.Equals(manifest?.DownloadId, downloadId, StringComparison.Ordinal) ||
                        manifest.OutputPath.IsNullOrWhiteSpace() ||
                        !ConversionWorkPath.RetainedFolder(downloadId, manifest.OutputPath).PathEquals(folder))
                    {
                        _logger.Warn("Retaining conversion attempt without matching download ownership for {0}: {1}", downloadId, folder);
                        continue;
                    }

                    attempts.Add(manifest);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is ArgumentException)
                {
                    _logger.Warn(ex, "Retaining conversion attempt with unverifiable ownership for download {0}: {1}", downloadId, folder);
                }
            }
            return attempts;
        }

        private static bool HasConflictingArtifactOwner(string folder, string downloadId)
        {
            var path = Path.Combine(folder, ArtifactManifestFileName);
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
            try
            {
                var manifest = JsonSerializer.Deserialize<ConversionArtifactManifest>(File.ReadAllText(path));
                return manifest?.DownloadId.IsNotNullOrWhiteSpace() == true &&
                    !string.Equals(manifest.DownloadId, downloadId, StringComparison.Ordinal);
            }
            catch (JsonException)
            {
                return false; // Captured request/history still proves its exact legacy attempt.
            }
        }

        private void RetryDiscardCleanup()
        {
            foreach (var job in _repository.PendingCleanup())
            {
                if (DeserializeRequest(job)?.DiscardRequested == true)
                {
                    try { RemoveArtifacts(job.DownloadId); }
                    catch (Exception ex) { _logger.Warn(ex, "Discard succeeded but conversion cleanup is still pending for download {0}", job.DownloadId); }
                }
            }
        }

        public void Handle(ApplicationStartedEvent message)
        {
            _shutdown = new CancellationTokenSource();
            _scheduler = Task.Factory.StartNew(
                SchedulerLoop,
                _shutdown.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            _signal.Set();
        }

        public void Handle(ApplicationShutdownRequested message)
        {
            _logger.Info("Shutting down detached audiobook conversion worker");
            _shutdown?.Cancel();
            Task[] activeTasks;
            lock (_sync)
            {
                foreach (var active in _activeConversions.Values)
                {
                    active.Cancellation.Cancel();
                }

                activeTasks = _activeConversions.Values.Select(active => active.Completion.Task).ToArray();
            }

            _signal.Set();
            try
            {
                _scheduler?.Wait(TimeSpan.FromSeconds(5));
                Task.WaitAll(activeTasks, TimeSpan.FromSeconds(5));
            }
            catch (AggregateException ex) when (ex.InnerExceptions.All(inner => inner is OperationCanceledException))
            {
                // Expected during shutdown.
            }
        }

        private void Reconcile()
        {
            _repository.DeleteCompletedBefore(DateTime.UtcNow - CompletedRetention);

            foreach (var job in _repository.NonCompleted())
            {
                var request = DeserializeRequest(job);
                if (request?.DiscardRequested == true)
                {
                    MarkCancelled(job, "Download discarded; removing conversion files");
                    continue;
                }

                if (RecoverCommittedImport(job, request)) continue;
                switch (job.Status)
                {
                    case ConversionJobStatus.Converting:
                    case ConversionJobStatus.ReadyToImport:
                        if (request != null && HasReusableArtifact(request))
                        {
                            MarkReady(job, "Recovered converted M4B; waiting to import");
                            QueueImportSweep();
                        }
                        else if (!FailUnverifiedRetainedArtifact(job, request))
                        {
                            Requeue(job, "Recovering interrupted M4B conversion");
                        }

                        break;

                    case ConversionJobStatus.Cancelling:
                        MarkCancelled(job, "Conversion cancelled");
                        CleanupWorkRoot(job.WorkFolder);
                        QueueImportSweep();
                        break;

                    case ConversionJobStatus.Queued:
                        _trackingService?.Start(job.DownloadId, job.TargetQualityId, job.TargetQualityName, job.Message);
                        _trackingService?.Progress(job.DownloadId, job.Progress, job.Message);
                        break;

                    case ConversionJobStatus.Failed:
                        _trackingService?.Fail(job.DownloadId, job.Error ?? job.Message);
                        break;

                    case ConversionJobStatus.Cancelled:
                        _trackingService?.Cancelled(job.DownloadId, job.Message);
                        break;
                }
            }
        }

        private void SchedulerLoop()
        {
            var waitHandles = new[] { _signal, _shutdown.Token.WaitHandle };
            var reconciled = false;
            DateTime? failureStarted = null;

            while (!_shutdown.IsCancellationRequested)
            {
                try
                {
                    var signalled = WaitHandle.WaitAny(waitHandles, SchedulerPollInterval);
                    if (signalled == 1 || _shutdown.IsCancellationRequested)
                    {
                        break;
                    }

                    if (signalled == 0 && GetActiveCount() == 0)
                    {
                        _shutdown.Token.WaitHandle.WaitOne(QueueCoalesceDelay);
                    }

                    _shutdown.Token.ThrowIfCancellationRequested();
                    if (!reconciled)
                    {
                        Reconcile();
                        reconciled = true;
                    }

                    RecoverExpiredLeases();
                    RetryDiscardCleanup();
                    DispatchQueuedJobs();

                    if (failureStarted.HasValue)
                    {
                        _logger.Info("Detached audiobook conversion scheduler pass completed after {0:0.0}s of failures; {1} jobs running",
                            (DateTime.UtcNow - failureStarted.Value).TotalSeconds, GetActiveCount());
                        failureStarted = null;
                    }
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    failureStarted ??= DateTime.UtcNow;
                    _logger.Error(ex, "Detached audiobook conversion scheduler pass failed; durable jobs will be retried");
                }
            }

            _logger.Debug("Detached audiobook conversion scheduler stopped");
        }

        private int GetActiveCount()
        {
            lock (_sync)
            {
                return _activeConversions.Count;
            }
        }

        internal void RecoverExpiredLeases()
        {
            var cutoff = DateTime.UtcNow - LeaseTimeout;
            var staleJobs = _repository.ByStatus(ConversionJobStatus.Converting)
                .Where(job =>
                              (!job.HeartbeatAt.HasValue || job.HeartbeatAt.Value < cutoff))
                .ToList();

            foreach (var job in staleJobs)
            {
                lock (_sync)
                {
                    if (_activeConversions.ContainsKey(job.DownloadId))
                    {
                        continue;
                    }
                }

                var request = DeserializeRequest(job);
                if (request != null && HasReusableArtifact(request))
                {
                    MarkReady(job, "Recovered converted M4B; waiting to import");
                    QueueImportSweep();
                }
                else if (!FailUnverifiedRetainedArtifact(job, request))
                {
                    Requeue(job, "Recovering expired M4B conversion lease");
                }
            }
        }

        private void DispatchQueuedJobs()
        {
            lock (_sync)
            {
                var processLimit = WorkerConcurrency;
                var tokenLimit = Clamp(_configService?.AudiobookConversionMaxCpuThreads ?? 4, processLimit, 64);
                var availableProcesses = processLimit - _activeConversions.Count;
                var availableTokens = tokenLimit - _activeConversions.Values.Sum(active => active.TokenBudget);
                if (availableProcesses <= 0 || availableTokens <= 0)
                {
                    return;
                }

                var queued = _repository.ByStatus(ConversionJobStatus.Queued)
                    .Where(candidate => !_activeConversions.ContainsKey(candidate.DownloadId))
                    .OrderBy(candidate => candidate.CreatedAt)
                    .ThenBy(candidate => candidate.Id)
                    .Take(Math.Min(availableProcesses, availableTokens))
                    .ToList();
                if (queued.Count == 0)
                {
                    return;
                }

                var baseTokens = availableTokens / queued.Count;
                var extraTokens = availableTokens % queued.Count;
                for (var index = 0; index < queued.Count; index++)
                {
                    var job = queued[index];
                    var tokenBudget = baseTokens + (index < extraTokens ? 1 : 0);
                    var active = StartJob(job, tokenBudget);
                    _ = Task.Run(() => ExecuteJob(job, active));
                }
            }
        }

        // Caller holds _sync through admission and ownership registration.
        private ActiveConversion StartJob(ConversionJob job, int tokenBudget)
        {
            job.Status = ConversionJobStatus.Converting;
            job.Progress = 1m;
            job.Message = $"Converting to M4B ({tokenBudget} CPU thread{(tokenBudget == 1 ? string.Empty : "s")})";
            job.Error = null;
            job.StartedAt = DateTime.UtcNow;
            job.HeartbeatAt = job.StartedAt;
            job.UpdatedAt = job.StartedAt.Value;
            job.AttemptCount++;
            _repository.Update(job);
            var active = new ActiveConversion(CancellationTokenSource.CreateLinkedTokenSource(_shutdown?.Token ?? CancellationToken.None), tokenBudget);
            _activeConversions.Add(job.DownloadId, active);
            return active;
        }

        private void ExecuteJob(ConversionJob job, ActiveConversion active)
        {
            var cancellation = active.Cancellation;
            try
            {
                _trackingService?.Start(job.DownloadId, job.TargetQualityId, job.TargetQualityName, job.Message);
                _trackingService?.RegisterCancellation(job.DownloadId, cancellation);
                _trackingService?.Progress(job.DownloadId, job.Progress, job.Message);
                var request = DeserializeRequest(job);
                if (request == null)
                {
                    PersistWorkerOutcome(job, () => MarkFailed(job, "Conversion job request is missing or unreadable."));
                    return;
                }

                if (cancellation.IsCancellationRequested)
                {
                    PersistWorkerOutcome(job, () => HandleInterruptedConversion(job, request));
                    return;
                }

                if (RecoverCommittedImport(job, request)) return;

                if (HasReusableArtifact(request))
                {
                    PersistWorkerOutcome(job, () =>
                    {
                        if (!MarkReady(job, "Using retained M4B", cancellation.Token))
                        {
                            HandleInterruptedConversion(job, request);
                        }
                    });
                    return;
                }

                var threadPlan = GetThreadPlan(request.Sources.Count, active.TokenBudget);
                using var heartbeat = new Timer(_ => TouchHeartbeat(job), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

                var encoderSucceeded = false;
                try
                {
                    Directory.CreateDirectory(request.WorkFolder);
                    WriteArtifactManifest(request, encoderSucceeded: false);
                    var result = _conversionService.ConvertToM4b(
                        request.Sources.Select(source => source.Path).ToArray(),
                        request.OutputPath,
                        new ConversionOptions
                        {
                            TempDirectory = request.WorkFolder,
                            AudioBitrate = request.AudioBitrate,
                            AudioChannels = request.AudioChannels,
                            ExpectedSourceDuration = TimeSpan.FromTicks(request.ExpectedSourceDurationTicks),
                            Jobs = threadPlan.ParallelFiles,
                            FfmpegThreads = threadPlan.FfmpegThreads,
                            TagOptions = request.TagOptions,
                            CancellationToken = cancellation.Token,
                            ProgressHandler = update => UpdateProgress(job, update)
                        });

                    if (result.Success)
                    {
                        encoderSucceeded = true;
                        WriteArtifactManifest(request, encoderSucceeded: true);
                        PersistWorkerOutcome(job, () =>
                        {
                            if (!MarkReady(job, "Converted M4B ready to import", cancellation.Token))
                            {
                                HandleInterruptedConversion(job, request);
                            }
                        });
                    }
                    else if (result.FailureCategory == ConversionFailureCategory.Cancelled || cancellation.IsCancellationRequested)
                    {
                        PersistWorkerOutcome(job, () => HandleInterruptedConversion(job, request));
                    }
                    else
                    {
                        var error = result.ErrorMessage.IsNullOrWhiteSpace() ? "M4B conversion failed" : result.ErrorMessage;
                        if (result.RetainOutputOnFailure && File.Exists(request.OutputPath))
                        {
                            error = $"{error} Converted file retained at: {request.OutputPath}";
                        }
                        else
                        {
                            CleanupWorkRoot(request.WorkFolder);
                        }

                        PersistWorkerOutcome(job, () => MarkFailed(job, error));
                    }
                }
                catch (OperationCanceledException)
                {
                    PersistWorkerOutcome(job, () => HandleInterruptedConversion(job, request));
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Detached M4B conversion failed for download {0}", job.DownloadId);
                    PersistWorkerOutcome(job, () => MarkFailed(job, "M4B conversion failed: " + ex.Message));
                    if (!encoderSucceeded) CleanupWorkRoot(request.WorkFolder);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Detached M4B job orchestration failed for download {0}", job.DownloadId);
                try
                {
                    PersistWorkerOutcome(job, () => MarkFailed(job, "M4B conversion job failed: " + ex.Message));
                }
                catch (Exception persistException)
                {
                    _logger.Error(persistException, "Unable to persist failed conversion job {0}", job.DownloadId);
                }
            }
            finally
            {
                try
                {
                    PersistWorkerOutcome(job, () =>
                    {
                        lock (_sync)
                        {
                            if (cancellation.IsCancellationRequested &&
                                _repository.FindByDownloadId(job.DownloadId)?.Status == ConversionJobStatus.Cancelling)
                            {
                                HandleInterruptedConversion(job, DeserializeRequest(job));
                            }
                            _activeConversions.Remove(job.DownloadId);
                        }
                    });
                }
                finally
                {
                    lock (_sync)
                    {
                        if (_activeConversions.TryGetValue(job.DownloadId, out var owner) && ReferenceEquals(owner, active))
                            _activeConversions.Remove(job.DownloadId);
                    }
                    _trackingService?.UnregisterCancellation(job.DownloadId, cancellation);
                    cancellation.Dispose();
                    _signal.Set();
                    if (_shutdown?.IsCancellationRequested != true)
                    {
                        QueueImportSweep();
                    }
                    active.Completion.TrySetResult();
                }
            }
        }

        private void PersistWorkerOutcome(ConversionJob job, Action persist)
        {
            DateTime? failureStarted = null;
            while (true)
            {
                try
                {
                    persist();
                    if (failureStarted.HasValue)
                    {
                        _logger.Info("Saved conversion outcome {0} for download {1} after {2:0.0}s of database failures",
                            job.Status, job.DownloadId, (DateTime.UtcNow - failureStarted.Value).TotalSeconds);
                    }

                    return;
                }
                catch (DbException ex)
                {
                    failureStarted ??= DateTime.UtcNow;
                    _logger.Error(ex, "Unable to save conversion outcome {0} for download {1}; retaining worker ownership and retrying. Outcome: {2}",
                        job.Status, job.DownloadId, job.Error ?? job.Message);
                    if ((_shutdown?.Token ?? CancellationToken.None).WaitHandle.WaitOne(SchedulerPollInterval))
                    {
                        return;
                    }
                }
            }
        }

        private void HandleInterruptedConversion(ConversionJob job, ConversionJobRequest request)
        {
            var persisted = _repository.FindByDownloadId(job.DownloadId);
            if (_shutdown?.IsCancellationRequested == true &&
                persisted?.Status != ConversionJobStatus.Cancelling)
            {
                Requeue(job, "Conversion interrupted by application shutdown");
                return;
            }

            // Preserve discard intent saved by the remover while this worker was stopping.
            if (persisted != null) job.RequestJson = persisted.RequestJson;
            MarkCancelled(job, $"{request?.TargetQualityName ?? "M4B"} conversion was cancelled.");
            CleanupWorkRoot(request?.WorkFolder ?? job.WorkFolder);
        }

        private void UpdateProgress(ConversionJob job, ConversionProgressUpdate update)
        {
            if (update == null)
            {
                return;
            }

            lock (_sync)
            {
                job.Progress = update.Progress;
                job.Message = update.Message ?? job.Message;
                job.HeartbeatAt = DateTime.UtcNow;
                job.UpdatedAt = job.HeartbeatAt.Value;
                _repository.SetFields(job, j => j.Progress, j => j.Message, j => j.HeartbeatAt, j => j.UpdatedAt);
            }

            _trackingService?.Progress(job.DownloadId, update.Progress, update.Message);
        }

        private void TouchHeartbeat(ConversionJob job)
        {
            try
            {
                lock (_sync)
                {
                    job.HeartbeatAt = DateTime.UtcNow;
                    job.UpdatedAt = job.HeartbeatAt.Value;
                    _repository.SetFields(job, j => j.HeartbeatAt, j => j.UpdatedAt);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to update conversion heartbeat for {0}", job.DownloadId);
            }
        }

        private bool MarkReady(ConversionJob job, string message, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                // Stop and the ready transition share this gate, so accepted cancellation
                // cannot be overwritten by a converter finishing successfully.
                if (cancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                job.Status = ConversionJobStatus.ReadyToImport;
                job.Progress = 98m;
                job.Message = message;
                job.Error = null;
                job.HeartbeatAt = DateTime.UtcNow;
                job.UpdatedAt = job.HeartbeatAt.Value;
                _repository.Update(job);
            }

            _trackingService?.Start(job.DownloadId, job.TargetQualityId, job.TargetQualityName, message);
            _trackingService?.Progress(job.DownloadId, 98m, message);
            return true;
        }

        private void Requeue(ConversionJob job, string message)
        {
            lock (_sync)
            {
                job.Status = ConversionJobStatus.Queued;
                job.Progress = 0m;
                job.Message = message;
                job.Error = null;
                job.UpdatedAt = DateTime.UtcNow;
                job.StartedAt = null;
                job.HeartbeatAt = null;
                job.CompletedAt = null;
                _repository.Update(job);
            }

            _trackingService?.Start(job.DownloadId, job.TargetQualityId, job.TargetQualityName, message);
            _trackingService?.Progress(job.DownloadId, 0m, message);
            _signal.Set();
        }

        private void MarkFailed(ConversionJob job, string error)
        {
            lock (_sync)
            {
                if (_activeConversions.TryGetValue(job.DownloadId, out var active) && active.Cancellation.IsCancellationRequested)
                {
                    HandleInterruptedConversion(job, DeserializeRequest(job));
                    return;
                }

                job.Status = ConversionJobStatus.Failed;
                job.Progress = null;
                job.Message = error;
                job.Error = error;
                job.UpdatedAt = DateTime.UtcNow;
                job.CompletedAt = job.UpdatedAt;
                _repository.SetFields(job, j => j.Status, j => j.Progress, j => j.Message, j => j.Error, j => j.UpdatedAt, j => j.CompletedAt);
            }

            _trackingService?.Fail(job.DownloadId, error);
        }

        private void MarkCancelled(ConversionJob job, string message)
        {
            lock (_sync)
            {
                job.Status = ConversionJobStatus.Cancelled;
                job.Progress = null;
                job.Message = message;
                job.Error = null;
                job.UpdatedAt = DateTime.UtcNow;
                job.CompletedAt = job.UpdatedAt;
                _repository.Update(job);
            }

            _trackingService?.Cancelled(job.DownloadId, message);
        }

        private ConversionJob UpdateTerminal(string downloadId, ConversionJobStatus status, string error, string message)
        {
            if (downloadId.IsNullOrWhiteSpace())
            {
                return null;
            }

            lock (_sync)
            {
                var job = _repository.FindByDownloadId(downloadId);
                if (job == null)
                {
                    return null;
                }

                job.Status = status;
                job.Progress = status == ConversionJobStatus.Completed ? 100m : null;
                job.Message = message;
                job.Error = error;
                job.UpdatedAt = DateTime.UtcNow;
                job.CompletedAt = job.UpdatedAt;
                _repository.Update(job);
                return job;
            }
        }

        private bool FailUnverifiedRetainedArtifact(ConversionJob job, ConversionJobRequest request)
        {
            if (request?.OutputPath.IsNotNullOrWhiteSpace() != true || !File.Exists(request.OutputPath) ||
                !File.Exists(Path.Combine(request.WorkFolder, ArtifactManifestFileName))) return false;

            try
            {
                var receipt = JsonSerializer.Deserialize<ConversionArtifactManifest>(File.ReadAllText(Path.Combine(request.WorkFolder, ArtifactManifestFileName)));
                // A new ownership-only receipt is not a legacy completion claim. Its interrupted
                // encoder can resume through normal recovery; it can never be reused as success.
                if (string.Equals(receipt?.DownloadId, request.DownloadId, StringComparison.Ordinal) && receipt?.EncoderSucceeded == false) return false;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                _logger.Warn(ex, "Unable to verify retained conversion receipt for download {0}", job.DownloadId);
            }

            // Old manifests were also written for failed-but-readable output. Without the explicit
            // success contract, neither reuse nor automatic overwrite is justified.
            MarkFailed(job, "Retained conversion completion cannot be verified. Manual Import imports the original files; Retry starts a new conversion. The retained output has been kept.");
            QueueImportSweep();
            return true;
        }

        private bool RecoverCommittedImport(ConversionJob job, ConversionJobRequest request)
        {
            if (request == null || _mediaFileService == null) return false;

            // Import source coverage is committed with BookFiles, before history or the job completion write.
            // Reuse that authority instead of treating a moved workspace output as an interrupted encoder.
            if (request.BookId > 0)
            {
                var sources = request.Sources.Select(source => source.Path).ToHashSet(PathEqualityComparer.Instance);
                var imported = _mediaFileService.GetFilesByBook(request.BookId).Any(file =>
                    file.Quality?.Quality == Qualities.Quality.M4B && File.Exists(file.Path) &&
                    string.Equals(file.MatchProvenance?.ImportDownloadId, job.DownloadId, StringComparison.OrdinalIgnoreCase) &&
                    file.MatchProvenance?.ImportSourcePaths?.Count > 0 &&
                    sources.SetEquals(file.MatchProvenance.ImportSourcePaths));
                if (imported)
                {
                    Complete(job.DownloadId);
                    _logger.Info("Recovered committed M4B import for download {0}, book {1}; conversion will not be repeated", job.DownloadId, request.BookId);
                    return true;
                }
            }

            // Legacy requests lack BookId, and an approved additional-copy import may create a new
            // book row. Its committed conversion history identifies the actual managed destination.
            foreach (var history in _historyService?.FindByDownloadId(job.DownloadId) ?? new List<EntityHistory>())
            {
                if (history.EventType != EntityHistoryEventType.BookFileConverted ||
                    !string.Equals(history.GetDataValue("ConvertedPath"), job.OutputPath, StringComparison.OrdinalIgnoreCase)) continue;
                var path = history.GetDataValue("ImportedPath");
                if (path.IsNullOrWhiteSpace() || !File.Exists(path)) continue;
                var file = _mediaFileService.GetFileWithPath(path);
                if (file == null || file.EditionId != history.EditionId || file.Quality?.Quality != Qualities.Quality.M4B) continue;
                Complete(job.DownloadId);
                _logger.Info("Recovered committed M4B import for download {0} from converted-file history; conversion will not be repeated", job.DownloadId);
                return true;
            }

            return false;
        }

        private ConversionJobRequest DeserializeRequest(ConversionJob job)
        {
            if (job == null) return null;
            try
            {
                var request = JsonSerializer.Deserialize<ConversionJobRequest>(job?.RequestJson ?? string.Empty);
                return IsValidRequest(request) ? request : null;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Unable to read conversion job request for {0}", job?.DownloadId);
                return null;
            }
        }

        private static bool IsValidRequest(ConversionJobRequest request)
        {
            if (request?.DiscardRequested == true)
            {
                try
                {
                    return request.DownloadId.IsNotNullOrWhiteSpace() &&
                        ConversionWorkPath.RetainedFolder(request.DownloadId, request.OutputPath).PathEquals(request.WorkFolder) &&
                        Path.GetDirectoryName(request.WorkFolder).PathEquals(request.WorkRoot);
                }
                catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is UnauthorizedAccessException)
                {
                    return false;
                }
            }

            if (request == null ||
                request.DownloadId.IsNullOrWhiteSpace() ||
                request.WorkRoot.IsNullOrWhiteSpace() ||
                request.WorkFolder.IsNullOrWhiteSpace() ||
                request.OutputPath.IsNullOrWhiteSpace() ||
                request.Sources == null ||
                request.Sources.Count == 0 ||
                request.Sources.Any(source => source?.Path.IsNullOrWhiteSpace() != false) ||
                !Path.GetExtension(request.OutputPath).Equals(".m4b", StringComparison.OrdinalIgnoreCase) ||
                !ConversionWorkPath.IsWorkPath(request.WorkRoot))
            {
                return false;
            }

            try
            {
                var root = Path.GetFullPath(request.WorkRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var folder = Path.GetFullPath(request.WorkFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var output = Path.GetFullPath(request.OutputPath);
                return IsPathWithin(folder, root) && IsPathWithin(output, folder);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsPathWithin(string candidate, string parent)
        {
            return candidate.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasReusableArtifact(ConversionJobRequest request)
        {
            if (!HasSuccessfulArtifact(request)) return false;

            foreach (var source in request.Sources ?? new List<ConversionArtifactSource>())
            {
                if (source?.Path.IsNullOrWhiteSpace() != false || !File.Exists(source.Path))
                {
                    return false;
                }

                var info = new FileInfo(source.Path);
                if (info.Length != source.Size || info.LastWriteTimeUtc.Ticks != source.ModifiedUtcTicks)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool HasSuccessfulArtifact(ConversionJobRequest request)
        {
            if (request == null ||
                request.OutputPath.IsNullOrWhiteSpace() ||
                !File.Exists(request.OutputPath) ||
                request.WorkFolder.IsNullOrWhiteSpace())
            {
                return false;
            }

            try
            {
                var manifestPath = Path.Combine(request.WorkFolder, ArtifactManifestFileName);
                if (!File.Exists(manifestPath))
                {
                    return false;
                }

                var manifest = JsonSerializer.Deserialize<ConversionArtifactManifest>(File.ReadAllText(manifestPath));
                return ConversionWorkPath.IsSuccessfulArtifact(manifest, request.OutputPath) &&
                       (manifest.DownloadId.IsNullOrWhiteSpace() || string.Equals(manifest.DownloadId, request.DownloadId, StringComparison.Ordinal)) &&
                       manifest.TargetQualityId == request.TargetQualityId &&
                       manifest.AudioBitrate == request.AudioBitrate &&
                       manifest.AudioChannels == request.AudioChannels &&
                       string.Equals(manifest.TagSignature, request.TagSignature, StringComparison.Ordinal) &&
                       ConversionWorkPath.SourcesMatch(manifest.Sources, request.Sources);
            }
            catch
            {
                return false;
            }
        }

        private static void WriteArtifactManifest(ConversionJobRequest request, bool encoderSucceeded)
        {
            var manifest = new ConversionArtifactManifest
            {
                DownloadId = request.DownloadId,
                EncoderSucceeded = encoderSucceeded,
                OutputSize = encoderSucceeded ? new FileInfo(request.OutputPath).Length : 0,
                CreatedUtc = DateTime.UtcNow,
                OutputPath = request.OutputPath,
                TargetQualityId = request.TargetQualityId,
                TargetQualityName = request.TargetQualityName,
                AudioBitrate = request.AudioBitrate,
                AudioChannels = request.AudioChannels,
                TagMode = request.TagOptions?.Mode,
                TagSignature = request.TagSignature,
                Sources = request.Sources
            };

            Directory.CreateDirectory(request.WorkFolder);
            var manifestPath = Path.Combine(request.WorkFolder, ArtifactManifestFileName);
            var temporaryPath = manifestPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, manifestPath, true);
        }

        private void QueueImportSweep()
        {
            try
            {
                _commandQueue.Push(new ProcessMonitoredDownloadsCommand(), CommandPriority.High);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Unable to queue completed-download import after conversion state changed");
            }
        }

        private void CleanupWorkRoot(string workRoot, bool throwOnError = false)
        {
            ConversionWorkPath.Cleanup(workRoot, _logger, throwOnError);
        }

        private static (int ParallelFiles, int FfmpegThreads) GetThreadPlan(int inputFileCount, int tokenBudget)
        {
            var availableTokens = Math.Max(1, tokenBudget);
            var sourceFiles = Math.Max(1, inputFileCount);
            var parallelFiles = Math.Min(sourceFiles, availableTokens);
            var ffmpegThreads = Math.Max(1, availableTokens / parallelFiles);
            return (parallelFiles, ffmpegThreads);
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min)
            {
                return min;
            }

            return value > max ? max : value;
        }

        private static bool IsInFlight(ConversionJobStatus status)
        {
            return status == ConversionJobStatus.Queued ||
                   status == ConversionJobStatus.Converting ||
                   status == ConversionJobStatus.ReadyToImport ||
                   status == ConversionJobStatus.Cancelling;
        }

        private sealed class ActiveConversion
        {
            public ActiveConversion(CancellationTokenSource cancellation, int tokenBudget)
            {
                Cancellation = cancellation;
                TokenBudget = tokenBudget;
            }

            public CancellationTokenSource Cancellation { get; }
            public int TokenBudget { get; }
            public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
