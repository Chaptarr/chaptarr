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
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RootFolders;

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
        bool Cancel(string downloadId, ConversionRemovalOrigin origin);
        void Complete(string downloadId);
        ConversionRetentionResult RetainCompletedOutput(string downloadId, IReadOnlyCollection<string> sourcePaths, string reason, bool discardOutput = false);
        void Fail(string downloadId, string error);
        string Reset(string downloadId, ConversionJob expectedJob = null);
        Action PrepareArtifactRemoval(string downloadId, ConversionRemovalOrigin origin);
    }

    public enum ConversionRemovalOrigin { Automatic, Inferred, UserAction, DownloadFailure }

    public partial class ConversionJobService :
        IConversionJobService,
        IHandle<ApplicationStartedEvent>,
        IHandle<ApplicationShutdownRequested>,
        IHandle<BookGrabbedEvent>,
        IHandle<DownloadCompletedEvent>,
        IHandle<DownloadFailedEvent>,
        IHandle<DownloadIgnoredEvent>,
        IHandle<TrackedDownloadsRemovedEvent>,
        IHandle<TrackedDownloadRefreshedEvent>
    {
        private const string ArtifactManifestFileName = "conversion-artifact.json";
        private static readonly TimeSpan LeaseTimeout = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan SchedulerPollInterval = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan QueueCoalesceDelay = TimeSpan.FromMilliseconds(100);
        private static readonly TimeSpan CompletedRetention = TimeSpan.FromDays(7);
        private readonly object _sync = new();
        private readonly AutoResetEvent _signal = new(false);
        private readonly Dictionary<string, ActiveConversion> _activeConversions = new(StringComparer.OrdinalIgnoreCase);
        // Encode ownership can end before import bookkeeping. Shutdown tracks the whole worker.
        private readonly Dictionary<Task, string> _workers = new();
        private readonly IConversionJobRepository _repository;
        private readonly IM4bConversionService _conversionService;
        private readonly IConversionTrackingService _trackingService;
        private readonly IManageCommandQueue _commandQueue;
        private readonly Logger _logger;
        private readonly IConfigService _configService;
        private readonly IHistoryService _historyService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IRootFolderService _rootFolderService;
        private readonly IDiskProvider _diskProvider;
        private readonly TimeSpan _outcomeRetryDelay;
        private readonly TimeSpan _shutdownWait;
        private readonly TimeSpan _heartbeatInterval;
        private readonly CancellationTokenSource _shutdown = new();
        private Task _scheduler;

        public ConversionJobService(
            IDiskProvider diskProvider,
            IRootFolderService rootFolderService,
            IMediaFileService mediaFileService,
            IConversionJobRepository repository,
            IM4bConversionService conversionService,
            IConversionTrackingService trackingService,
            IManageCommandQueue commandQueue,
            IConfigService configService,
            Logger logger,
            IHistoryService historyService = null,
            TimeSpan? outcomeRetryDelay = null,
            TimeSpan? shutdownWait = null,
            TimeSpan? heartbeatInterval = null)
        {
            _diskProvider = diskProvider ?? throw new ArgumentNullException(nameof(diskProvider));
            _rootFolderService = rootFolderService ?? throw new ArgumentNullException(nameof(rootFolderService));
            _repository = repository;
            _conversionService = conversionService;
            _trackingService = trackingService;
            _commandQueue = commandQueue;
            _configService = configService;
            _logger = logger;
            _historyService = historyService;
            _mediaFileService = mediaFileService ?? throw new ArgumentNullException(nameof(mediaFileService));
            _outcomeRetryDelay = outcomeRetryDelay ?? SchedulerPollInterval;
            _shutdownWait = shutdownWait ?? TimeSpan.FromSeconds(5);
            _heartbeatInterval = heartbeatInterval ?? TimeSpan.FromSeconds(30);
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

        private ConversionJobRequest GetCompletedConversion(ConversionJob job) => GetCompletedConversion(job, DeserializeRequest(job));

        private ConversionJobRequest GetCompletedConversion(ConversionJob job, ConversionJobRequest request)
        {
            if (job == null || IsWaitingForOutput(job, this)) return null;
            return request?.DiscardRequested == false && request.DownloadId == job.DownloadId && HasSuccessfulArtifact(request)
                ? request : null;
        }

        public List<ConversionJob> Get(IEnumerable<string> downloadIds)
        {
            return _repository.ByDownloadIds(downloadIds);
        }

        public void Handle(BookGrabbedEvent message)
        {
            ConversionJob previous;
            lock (_sync)
            {
                previous = Get(message.DownloadId);
                // A real new grab starts a lifecycle; an owned encoder is never retired underneath its worker.
                if (previous == null || IsInFlight(previous.Status) || _activeConversions.ContainsKey(message.DownloadId)) return;
            }
            if (DeserializeRequest(previous)?.DiscardRequested == true)
            {
                try { PrepareArtifactRemoval(message.DownloadId, DeserializeRequest(previous)?.DiscardCurrentAttemptOnly == true, "regrab", ConversionRemovalOrigin.Automatic, expected: ExpectedAttempt(previous))(); }
                catch (Exception ex) { RecordCleanupFailure(message.DownloadId, ex); }
                return;
            }
            CleanupTerminalDownload(message.DownloadId, "regrab", ConversionRemovalOrigin.Automatic, expected: ExpectedAttempt(previous));
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

                job.DownloadClientId = request.DownloadClientId;
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

        private static bool IsWaitingForOutput(ConversionJobStatus status) =>
            status is ConversionJobStatus.Queued or ConversionJobStatus.Converting or ConversionJobStatus.Cancelling;

        public static bool IsWaitingForOutput(ConversionJob job, IConversionJobService service) =>
            job != null && (IsWaitingForOutput(job.Status) || service.IsRunning(job.DownloadId));

        public bool IsRunning(string downloadId)
        {
            lock (_sync)
            {
                return _activeConversions.ContainsKey(downloadId);
            }
        }

        private bool MayStopForRemoval(ConversionJob job, ConversionRemovalOrigin origin) =>
            origin switch
            {
                ConversionRemovalOrigin.Inferred => IsProvablyRemoved(job),
                ConversionRemovalOrigin.Automatic => !IsWaitingForOutput(job, this),
                _ => true
            };

        public bool Cancel(string downloadId, ConversionRemovalOrigin origin)
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

                if (!MayStopForRemoval(job, origin)) return false;

                _activeConversions.TryGetValue(downloadId, out active);
                if (active != null || job.Status == ConversionJobStatus.Converting || job.Status == ConversionJobStatus.Cancelling)
                {
                    job.Status = ConversionJobStatus.Cancelling;
                    job.Message = "Cancelling conversion";
                    job.UpdatedAt = DateTime.UtcNow;
                    SaveState(job);

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
                    SaveState(job);
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

                if (DeserializeRequest(job)?.DiscardRequested != true)
                    CleanupWorkRoot(job.WorkFolder, downloadId: job.DownloadId, reason: "cancelled");
                QueueImportSweep();
            }

            return true;
        }

        public void Complete(string downloadId)
        {
            lock (_sync)
            {
                if (!TryUpdateTerminal(downloadId, ConversionJobStatus.Completed, null, "Converted M4B imported", out var job)) return;
                var request = DeserializeRequest(job);
                if (request?.Sources.Count > 0)
                {
                    foreach (var manifest in ReadOwnedAttempts(downloadId, request.WorkRoot)
                        .Where(attempt => ConversionWorkPath.SourcesMatch(attempt.Sources, request.Sources)))
                    {
                        CleanupWorkRoot(Path.GetDirectoryName(manifest.OutputPath), downloadId: downloadId, reason: "imported");
                    }
                }
                if (job != null && IsAbsentConversionPath(job.WorkFolder) && IsAbsentConversionPath(job.OutputPath)) ClearCleanupAttention(downloadId);
            }
            _trackingService?.Complete(downloadId);
        }

        public ConversionRetentionResult RetainCompletedOutput(string downloadId, IReadOnlyCollection<string> sourcePaths, string reason, bool discardOutput = false)
        {
            ConversionJob discardJob;
            lock (_sync)
            {
                var job = Get(downloadId);
                if (job == null)
                {
                    if (discardOutput) _logger.Debug("No optional conversion workspace to discard for download {0}: no job", downloadId);
                    return new(ConversionRetentionStatus.None, reason);
                }
                var request = DeserializeRequest(job);
                var paths = (sourcePaths ?? Array.Empty<string>()).ToHashSet(PathEqualityComparer.Instance);
                if (request?.DownloadId != job.DownloadId || request.Sources == null || request.Sources.Count == 0 ||
                    request.Sources.Any(source => source == null || !paths.Contains(source.Path)))
                {
                    if (discardOutput) _logger.Debug("No optional conversion workspace to discard for download {0}: source ownership differs", downloadId);
                    return new(ConversionRetentionStatus.None, reason);
                }

                if (IsWaitingForOutput(job, this))
                    return new(ConversionRetentionStatus.Pending, "Waiting for conversion to finish before resolving the import conflict.");

                if (!discardOutput)
                {
                    if (request.DiscardRequested)
                        return new(ConversionRetentionStatus.Pending, "Conversion discard cleanup is pending; original files are kept until it finishes.");
                    if (job.Status == ConversionJobStatus.Cancelled && job.StartedAt == null && job.AttemptCount == 0)
                    {
                        _trackingService?.Complete(downloadId);
                        return new(ConversionRetentionStatus.None, reason);
                    }
                    if (job.Status == ConversionJobStatus.Failed || job.Status == ConversionJobStatus.Cancelled)
                    {
                        var error = !string.IsNullOrWhiteSpace(job.Error) ? job.Error : job.Message;
                        return new(ConversionRetentionStatus.Blocked, string.IsNullOrWhiteSpace(error) ? "The conversion did not complete successfully." : error);
                    }
                    if (job.Status != ConversionJobStatus.ReadyToImport && job.Status != ConversionJobStatus.Completed)
                        return new(ConversionRetentionStatus.None, reason);

                    if (GetCompletedConversion(job, request) == null)
                    {
                        if (job.Status == ConversionJobStatus.Completed) return new(ConversionRetentionStatus.None, reason);
                        MarkFailed(job, HasUnverifiedRetainedArtifact(request) ? UnverifiedRetainedOutputMessage :
                            "Completed conversion output cannot be verified. Original files are kept; retry conversion or use Manual Import.");
                        QueueImportSweep();
                        return new(ConversionRetentionStatus.Blocked, job.Error ?? job.Message);
                    }

                    var result = new ConversionRetentionResult(ConversionRetentionStatus.Retained, reason, request.OutputPath);
                    // Finishing optional work must not delete its verified output or report an import.
                    if (job.Status != ConversionJobStatus.Completed || job.Error != null || job.Message != result.Message)
                        TryUpdateTerminal(downloadId, ConversionJobStatus.Completed, null, result.Message, out _);
                    _trackingService?.Complete(downloadId);
                    _logger.Info("Download {0}: {1}", downloadId, result.Message);
                    return result;
                }
                discardJob = job;
            }
            var workspace = discardJob.WorkFolder;
            var hadWorkspace = Directory.Exists(workspace);
            try
            {
                PrepareArtifactRemoval(downloadId, true, "refused", ConversionRemovalOrigin.Automatic, expected: ExpectedAttempt(discardJob))();
            }
            catch (Exception error)
            {
                RecordCleanupFailure(downloadId, error);
                throw;
            }
            if (Get(downloadId) != null)
                return new(ConversionRetentionStatus.Pending, "Waiting for conversion cleanup before resolving the import conflict.");
            var message = hadWorkspace && !Directory.Exists(workspace) ? $"{reason} Conversion workspace discarded: {workspace}" : reason;
            _logger.Info("Download {0}: {1}", downloadId, message);
            return new(ConversionRetentionStatus.None, message);
        }

        public void Fail(string downloadId, string error)
        {
            if (TryUpdateTerminal(downloadId, ConversionJobStatus.Failed, error, error, out _))
                _trackingService?.Fail(downloadId, error);
        }

        public string Reset(string downloadId, ConversionJob expectedJob = null)
        {
            if (downloadId.IsNullOrWhiteSpace()) return null;

            ConversionJob job;
            lock (_sync)
            {
                job = Get(downloadId);
                if (expectedJob != null && (job == null || job.Id != expectedJob.Id ||
                    !job.WorkFolder.PathEquals(expectedJob.WorkFolder) || !job.OutputPath.PathEquals(expectedJob.OutputPath)))
                {
                    _logger.Debug("Leaving a newer conversion attempt unchanged after original-file import for download {0}", downloadId);
                    return null;
                }

                if (DeserializeRequest(job)?.DiscardRequested == true)
                {
                    if (expectedJob != null) return null;
                    throw new InvalidOperationException("This download was discarded and conversion cleanup is still pending. Check the cleanup error in the logs.");
                }
                if (IsRunning(downloadId) || (job != null && IsInFlight(job.Status) && job.Status != ConversionJobStatus.ReadyToImport))
                    throw new InvalidOperationException("An active conversion job must be cancelled before it can be reset.");
            }

            if (job != null && RecoverCommittedImport(job, DeserializeRequest(job))) return null;
            // Retry keeps verified output until its replacement succeeds. A captured
            // attempt can instead be retired after import or a source-signature change.
            if (expectedJob == null && job?.Status == ConversionJobStatus.ReadyToImport)
            {
                if (!HasReusableArtifact(DeserializeRequest(job)))
                    throw new InvalidOperationException("Unable to verify the completed conversion output. Check that its source files and retained M4B are available before retrying import.");
                return null;
            }

            lock (_sync)
            {
                var current = Get(downloadId);
                if (current?.Id != job?.Id || (job != null && (current.Status != job.Status || current.UpdatedAt != job.UpdatedAt ||
                    !current.WorkFolder.PathEquals(job.WorkFolder) || !current.OutputPath.PathEquals(job.OutputPath)))) return null;
                if (IsRunning(downloadId)) throw new InvalidOperationException("An active conversion job must be cancelled before it can be reset.");
                if (expectedJob == null)
                {
                    _repository.DeleteByDownloadId(downloadId);
                    _trackingService?.Clear(downloadId);
                    return null;
                }

                // Reserve this inactive attempt before disk work. The same persisted
                // discard operation retries cleanup after an I/O failure or restart.
                var request = DeserializeRequest(current);
                if (request == null)
                {
                    RecordCleanupFailure(downloadId, new InvalidOperationException("The conversion request is unreadable; its workspace cannot be reset safely."));
                    return null;
                }
                request.DiscardRequested = true;
                request.DiscardCurrentAttemptOnly = true;
                request.DiscardReason = "reset";
                current.RequestJson = JsonSerializer.Serialize(request);
                current.Status = ConversionJobStatus.Cancelled;
                current.Message = "Waiting for conversion cleanup before retry";
                current.CompletedAt = current.UpdatedAt = DateTime.UtcNow;
                _repository.Update(current);
                job = _repository.Get(current.Id);
            }

            try
            {
                var hadWorkspace = Directory.Exists(job.WorkFolder);
                PrepareArtifactRemoval(downloadId, currentAttemptOnly: true, reason: "reset",
                    origin: ConversionRemovalOrigin.Automatic, expected: ExpectedAttempt(job))();
                if (hadWorkspace && !Directory.Exists(job.WorkFolder))
                {
                    _logger.Info("Reset conversion for download {0}; discarded conversion workspace {1}", downloadId, job.WorkFolder);
                    return job.WorkFolder;
                }
            }
            catch (Exception ex)
            {
                RecordCleanupFailure(downloadId, ex);
            }
            return null;
        }

        public Action PrepareArtifactRemoval(string downloadId, ConversionRemovalOrigin origin)
        {
            var cleanup = PrepareArtifactRemoval(downloadId, false, "removed", origin);
            return () =>
            {
                try { cleanup(); }
                catch (Exception ex)
                {
                    RecordCleanupFailure(downloadId, ex);
                    throw;
                }
            };
        }

        private Action PrepareArtifactRemoval(string downloadId, bool currentAttemptOnly, string reason, ConversionRemovalOrigin origin, CleanupInventory inventory = null,
            ConversionLifecycleCandidate expected = null)
        {
            if (downloadId.IsNullOrWhiteSpace()) return () => { };

            // Capture and validate the exact owned attempts before a client/category/ignore mutation.
            var job = Get(downloadId);
            if (expected != null && (job == null || job.Id != expected.Id || job.Status != expected.Status || job.UpdatedAt != expected.UpdatedAt)) return () => { };
            if (!MayStopForRemoval(job, origin)) return () => { };
            if (origin != ConversionRemovalOrigin.UserAction && !CleanupMayRetry(job) &&
                !(IsAbsentConversionPath(job?.WorkFolder) && IsAbsentConversionPath(job?.OutputPath))) return () => { };
            downloadId = job?.DownloadId ?? downloadId;
            if (currentAttemptOnly && job == null) return () => { };
            var history = currentAttemptOnly ? new List<EntityHistory>()
                : _historyService?.FindByDownloadId(downloadId) ?? new List<EntityHistory>();
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
                    if (job?.OutputPath.PathEquals(path) == true) throw new ConversionCleanupRefusedException($"Conversion attempt records another download owner: {folder}");
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
                throw new ConversionCleanupRefusedException($"Conversion workspace does not match its recorded output for download {downloadId}");
            }

            // Sanitized root names are not owner identities: a/b and a_b can share one root.
            // Existing job/history references prove their exact attempts. Discover an additional
            // attempt only through the exact DownloadId in its existing artifact receipt.
            var knownFolders = folders.ToHashSet(PathEqualityComparer.Instance);
            foreach (var root in currentAttemptOnly ? Enumerable.Empty<string>() : folders.Select(Path.GetDirectoryName).Distinct(PathEqualityComparer.Instance).ToList())
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
            var sources = DeserializeRequest(job)?.Sources ?? new List<ConversionArtifactSource>();
            if (sources.Any(source => source?.Path != null && cleanupPaths.Any(path => path.PathEquals(source.Path) || path.IsParentPath(source.Path))))
                throw new ConversionCleanupRefusedException("A conversion workspace contains a source file; it was retained.");

            VerifyCleanupOwnership(cleanupPaths, inventory);
            return () =>
            {
                ConversionJob current;
                lock (_sync)
                {
                    current = Get(downloadId);
                    if (expected != null && (current == null || current.Id != expected.Id || current.Status != expected.Status || current.UpdatedAt != expected.UpdatedAt)) return;
                    if (!MayStopForRemoval(current, origin)) return;
                    if (current != null && (job == null || current.Id != job.Id ||
                        !current.WorkFolder.PathEquals(job.WorkFolder) || !current.OutputPath.PathEquals(job.OutputPath)))
                    {
                        throw new InvalidOperationException("Conversion attempt changed while removing the download; its workspace was retained.");
                    }

                    if (current == null && cleanupPaths.All(path => !Directory.Exists(path))) return;

                    if (current == null && folders.Count > 0)
                    {
                        // Completed rows may already have expired. Keep cleanup failure recoverable in
                        // the existing job table even when conversion history is the only prior owner.
                        var firstFolder = folders[0];
                        var cleanup = new ConversionJobRequest
                        {
                            DownloadId = downloadId,
                            DiscardRequested = true,
                            DiscardReason = reason,
                            WorkRoot = Path.GetDirectoryName(firstFolder),
                            WorkFolder = firstFolder,
                            OutputPath = outputPaths.First(path => ConversionWorkPath.RetainedFolder(downloadId, path).PathEquals(firstFolder))
                        };
                        current = _repository.Insert(new ConversionJob
                        {
                            DownloadId = downloadId,
                            Status = ConversionJobStatus.Cancelled,
                            RequestJson = JsonSerializer.Serialize(cleanup),
                            WorkRoot = cleanup.WorkRoot,
                            WorkFolder = cleanup.WorkFolder,
                            OutputPath = cleanup.OutputPath,
                            Message = "Download discarded; removing conversion files",
                            CreatedAt = DateTime.UtcNow,
                            UpdatedAt = DateTime.UtcNow
                        });
                    }

                    if (current != null)
                    {
                        var request = DeserializeRequest(current);
                        if (request != null)
                        {
                            request.DiscardRequested = true;
                            request.DiscardReason = reason;
                            request.DiscardCurrentAttemptOnly = currentAttemptOnly;
                            current.RequestJson = JsonSerializer.Serialize(request);
                            _repository.Update(current);
                        }
                    }

                }

                // DiscardRequested is durable before releasing admission: Enqueue cannot
                // replace this attempt while its validated files are being removed.
                if (!Cancel(downloadId, origin))
                {
                    lock (_sync)
                    {
                        if (current != null) MarkCancelled(current, "Download discarded; removing conversion files");
                        else _trackingService?.Cancel(downloadId);
                    }
                }
                string activeFolder;
                lock (_sync) activeFolder = _activeConversions.ContainsKey(downloadId) ? current?.WorkFolder : null;
                List<RootFolder> roots = null;
                var availableRoots = new Dictionary<string, bool>(PathEqualityComparer.Instance);
                foreach (var path in cleanupPaths)
                {
                    // The encoder owns its exact attempt until it has stopped.
                    if (activeFolder != null && path.PathEquals(activeFolder)) continue;
                    if (!_diskProvider.FolderExists(path))
                    {
                        if (!IsAbsentConversionPath(path))
                            throw new IOException($"Unable to verify conversion workspace availability: {path}");
                        EnsureCleanupRootAvailable(path, ref roots, availableRoots);
                    }
                    CleanupWorkRoot(path, throwOnError: true, downloadId: downloadId, reason: reason, ownershipVerified: true);
                }

                lock (_sync)
                {
                    var settled = Get(downloadId);
                    if (activeFolder == null && settled?.Id == current?.Id && !_activeConversions.ContainsKey(downloadId))
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
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var jobs = _repository.PendingCleanup();
            var roots = jobs.Where(CleanupMayRetry).Select(job => job.WorkRoot).TopmostFolders();
            var inventory = roots.Count == 0 ? null : ReadCleanupInventory(roots);
            var attempted = 0;
            var succeeded = 0;
            var failed = 0;
            foreach (var job in jobs)
            {
                if (!CleanupMayRetry(job)) continue;
                var request = DeserializeRequest(job);
                if (request?.DiscardRequested == true)
                {
                    attempted++;
                    try
                    {
                        PrepareArtifactRemoval(job.DownloadId, request.DiscardCurrentAttemptOnly, request.DiscardReason ?? "removed", ConversionRemovalOrigin.Automatic, inventory,
                            ExpectedAttempt(job))();
                        succeeded++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        RecordCleanupFailure(job.DownloadId, ex);
                    }
                }
            }
            if (attempted > 0)
                _logger.Info("Conversion cleanup retry: attempted={0}, succeeded={1}, failed={2}, elapsedMs={3}",
                    attempted, succeeded, failed, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        public void Handle(ApplicationStartedEvent message)
        {
            if (_shutdown.IsCancellationRequested) return;
            _scheduler = Task.Factory.StartNew(
                SchedulerLoop,
                _shutdown.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            _signal.Set();
        }

        // Snapshot both lifetimes: encode ownership may end before a worker finishes.
        internal (Task Scheduler, Task Workers) Completion
        {
            get
            {
                lock (_sync) return (_scheduler ?? Task.CompletedTask, Task.WhenAll(_workers.Keys));
            }
        }

        public void Handle(ApplicationShutdownRequested message)
        {
            _logger.Info("Shutting down detached audiobook conversion worker");
            _shutdown.Cancel();
            KeyValuePair<Task, string>[] workers;
            lock (_sync)
            {
                workers = _workers.ToArray();
            }

            _signal.Set();
            var completion = Completion;
            try
            {
                if (!completion.Scheduler.Wait(_shutdownWait))
                    _logger.Warn("Conversion scheduler is still running after the {0}s shutdown wait", _shutdownWait.TotalSeconds);
            }
            catch (AggregateException ex) when (ex.InnerExceptions.All(inner => inner is OperationCanceledException))
            {
                // A cancelled scheduler must not skip the separate worker wait.
            }
            if (!completion.Workers.Wait(_shutdownWait))
            {
                var pending = workers.Where(worker => !worker.Key.IsCompleted).ToArray();
                if (pending.Length > 0)
                    _logger.Warn("Shutdown timed out after {0}s waiting for {1} conversion worker(s) still running: {2}",
                        _shutdownWait.TotalSeconds, pending.Length, string.Join(", ", pending.Select(worker => worker.Value)));
            }
        }

        private void Reconcile()
        {
            var cutoff = DateTime.UtcNow - CompletedRetention;
            RunSchedulerStep("startup cleanup attention", ReviewCleanupAttention);
            RunSchedulerStep("startup orphan cleanup", ReviewOrphanArtifacts);
            RunSchedulerStep("startup download reconciliation", () => ReconcileDownloadLifecycles());
            var expiredCancelled = new List<int>();
            List<RootFolder> cleanupRoots = null;
            var availableRoots = new Dictionary<string, bool>(PathEqualityComparer.Instance);

            foreach (var job in _repository.NonCompleted())
            {
                try
                {
                    if (!CleanupMayRetry(job)) continue;
                    var request = DeserializeRequest(job);
                    if (request?.DiscardRequested == true)
                    {
                        if (job.Status != ConversionJobStatus.Cancelled)
                            TryRecover(job, current => MarkCancelled(current, "Download discarded; removing conversion files"));
                        continue;
                    }

                    if (job.Status == ConversionJobStatus.Cancelled && job.CompletedAt < cutoff && request != null &&
                        request.WorkFolder == job.WorkFolder && request.OutputPath == job.OutputPath &&
                        IsAbsentConversionPath(job.WorkFolder) && IsAbsentConversionPath(job.OutputPath) && IsAbsentConversionPath(request.WorkRoot))
                    {
                        EnsureCleanupRootAvailable(job.WorkFolder, ref cleanupRoots, availableRoots);
                        expiredCancelled.Add(job.Id);
                        continue;
                    }
                    if (RecoverCommittedImport(job, request)) continue;
                    switch (job.Status)
                    {
                        case ConversionJobStatus.Converting:
                        case ConversionJobStatus.ReadyToImport:
                            RecoverInterruptedConversion(job, request, "Recovering interrupted M4B conversion");
                            break;

                        case ConversionJobStatus.Cancelling:
                            if (!TryRecover(job, current => MarkCancelled(current, "Conversion cancelled"))) continue;
                            CleanupWorkRoot(job.WorkFolder, downloadId: job.DownloadId, reason: request?.DiscardReason ?? "cancelled");
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
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Unable to recover conversion job {0}, downloadId={1}; other jobs will continue", job.Id, job.DownloadId);
                }
            }
            _repository.DeleteExpiredCancelled(expiredCancelled, cutoff);
        }

        private static bool IsAbsentConversionPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try { File.GetAttributes(path); return false; }
            catch (FileNotFoundException) { return true; }
            catch (DirectoryNotFoundException) { return true; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
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
                        reconciled = RunSchedulerStep("startup recovery", Reconcile);
                    }

                    var passSucceeded = reconciled;
                    passSucceeded &= RunSchedulerStep("cleanup attention", ReviewCleanupAttention);
                    passSucceeded &= RunSchedulerStep("orphan cleanup", ReviewOrphanArtifacts);
                    passSucceeded &= RunSchedulerStep("lease recovery", RecoverExpiredLeases);
                    passSucceeded &= RunSchedulerStep("discard cleanup", RetryDiscardCleanup);
                    passSucceeded &= RunSchedulerStep("dispatch", DispatchQueuedJobs);

                    if (!passSucceeded)
                    {
                        failureStarted ??= DateTime.UtcNow;
                    }
                    else if (failureStarted.HasValue)
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

        private bool RunSchedulerStep(string step, Action action)
        {
            try
            {
                action();
                return true;
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Conversion scheduler step {0} failed; independent steps will continue and this step will retry", step);
                return false;
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
                if (IsRunning(job.DownloadId)) continue;
                try
                {
                    RecoverInterruptedConversion(job, DeserializeRequest(job), "Recovering expired M4B conversion lease");
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Unable to recover expired conversion job {0}, downloadId={1}; other jobs will continue", job.Id, job.DownloadId);
                }
            }
        }

        private void RecoverInterruptedConversion(ConversionJob job, ConversionJobRequest request, string message)
        {
            var reusable = request != null && HasReusableArtifact(request);
            var unverified = !reusable && HasUnverifiedRetainedArtifact(request);
            if (!TryRecover(job, current =>
            {
                if (reusable) MarkReady(current, "Recovered converted M4B; waiting to import");
                else if (unverified) MarkFailed(current, UnverifiedRetainedOutputMessage);
                else Requeue(current, message);
            })) return;
            if (reusable || unverified) QueueImportSweep();
        }

        // Recovery requires no owner; an interrupted worker supplies its own identity.
        // Compare state and the complete request snapshot, not clock precision; hold the gate through the write.
        private bool TryRecover(ConversionJob expected, Action<ConversionJob> write, ActiveConversion owner = null, Action whenOwned = null)
        {
            if (expected == null) return false;
            lock (_sync)
            {
                _activeConversions.TryGetValue(expected.DownloadId, out var active);
                if (!ReferenceEquals(active, owner))
                {
                    whenOwned?.Invoke();
                    _logger.Debug("Skipping recovery for conversion job {0}, downloadId={1}: worker ownership differs", expected.Id, expected.DownloadId);
                    return false;
                }
                var current = Get(expected.DownloadId);
                if (current == null)
                {
                    _logger.Warn("Skipping recovery for conversion job {0}, downloadId={1}: the job no longer exists", expected.Id, expected.DownloadId);
                    return false;
                }
                if (!IsSameAttempt(current, expected) || current.Status != expected.Status || current.RequestJson != expected.RequestJson)
                {
                    _logger.Warn("Skipping recovery for conversion job {0}, downloadId={1}: the job changed after recovery read it; preserving the newer state", expected.Id, expected.DownloadId);
                    return false;
                }
                write(current);
                return true;
            }
        }

        private static bool IsSameAttempt(ConversionJob current, ConversionJob expected) =>
            current?.Id == expected.Id && current.AttemptCount == expected.AttemptCount &&
            current.WorkFolder.PathEquals(expected.WorkFolder) && current.OutputPath.PathEquals(expected.OutputPath);

        private void DispatchQueuedJobs()
        {
            lock (_sync)
            {
                if (_shutdown.IsCancellationRequested) return;
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
                    _ = Task.Run(() =>
                    {
                        try { ExecuteJob(job, active); }
                        catch (Exception ex) { _logger.Error(ex, "Conversion worker finalization failed for download {0}", job.DownloadId); }
                        finally
                        {
                            lock (_sync)
                            {
                                active.Completion.TrySetResult();
                                _workers.Remove(active.Completion.Task);
                            }
                        }
                    });
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
            SaveState(job);
            var active = new ActiveConversion(CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token), tokenBudget);
            _activeConversions.Add(job.DownloadId, active);
            _workers.Add(active.Completion.Task, job.DownloadId);
            return active;
        }

        private void ExecuteJob(ConversionJob job, ActiveConversion active)
        {
            var cancellation = active.Cancellation;
            var committedImport = false;
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
                    PersistWorkerOutcome(job, () => HandleInterruptedConversion(job, request, active));
                    return;
                }

                if (HasCommittedImport(job, request))
                {
                    // Proof prevents another encode; settlement waits until this worker releases ownership.
                    committedImport = true;
                    return;
                }

                if (!request.MatchesSources(request.BookId, request.Sources.Select(source => source.Path).ToArray()))
                    throw new IOException("Conversion sources changed or are missing since this job was queued. Nothing was encoded; import must evaluate the current files again.");

                if (HasReusableArtifact(request))
                {
                    PersistWorkerOutcome(job, () =>
                    {
                        if (!MarkReady(job, "Using retained M4B", cancellation.Token))
                        {
                            HandleInterruptedConversion(job, request, active);
                        }
                    });
                    return;
                }

                var threadPlan = GetThreadPlan(request.Sources.Count, active.TokenBudget);
                var heartbeat = new Timer(_ => TouchHeartbeat(job), null, _heartbeatInterval, _heartbeatInterval);

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
                            ProgressHandler = update => UpdateProgress(job, active, update)
                        });

                    if (result.Success)
                    {
                        encoderSucceeded = true;
                        WriteArtifactManifest(request, encoderSucceeded: true);
                        PersistWorkerOutcome(job, () =>
                        {
                            if (!MarkReady(job, "Converted M4B ready to import", cancellation.Token))
                            {
                                HandleInterruptedConversion(job, request, active);
                            }
                        });
                    }
                    else if (result.FailureCategory == ConversionFailureCategory.Cancelled || cancellation.IsCancellationRequested)
                    {
                        PersistWorkerOutcome(job, () => HandleInterruptedConversion(job, request, active));
                    }
                    else
                    {
                        var error = result.ErrorMessage.IsNullOrWhiteSpace() ? "M4B conversion failed" : result.ErrorMessage;
                        var retain = result.RetainOutputOnFailure && File.Exists(request.OutputPath);
                        if (retain)
                        {
                            error = ConversionRetentionResult.AppendRetainedConversionPath(error, request.OutputPath);
                        }

                        PersistWorkerOutcome(job, () =>
                        {
                            MarkFailed(job, error);
                            if (!retain) CleanupWorkRoot(request.WorkFolder, downloadId: job.DownloadId, reason: "failed");
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    PersistWorkerOutcome(job, () => HandleInterruptedConversion(job, request, active));
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Detached M4B conversion failed for download {0}", job.DownloadId);
                    PersistWorkerOutcome(job, () =>
                    {
                        MarkFailed(job, "M4B conversion failed: " + ex.Message);
                        if (!encoderSucceeded) CleanupWorkRoot(request.WorkFolder, downloadId: job.DownloadId, reason: "failed");
                    });
                }
                finally
                {
                    // Disposal must drain queued/running callbacks before releasing ownership.
                    heartbeat.DisposeAsync().AsTask().GetAwaiter().GetResult();
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
                                HandleInterruptedConversion(job, DeserializeRequest(job), active);
                            }
                            // Import may start as soon as this job is no longer active.
                            // Detach its stop callback before publishing that boundary.
                            ReleaseConversionOwnership(job.DownloadId, active);
                        }
                    });
                    // Import bookkeeping can fail after ownership was released. Retrying it
                    // must never repeat the old worker's cancellation/release operation.
                    if (committedImport || job.Status == ConversionJobStatus.ReadyToImport)
                        PersistWorkerOutcome(job, () =>
                        {
                            var current = Get(job.DownloadId);
                            // Refresh metadata without adopting a replacement attempt after release.
                            if (!IsSameAttempt(current, job))
                            {
                                _logger.Info("Skipping import settlement for conversion job {0}, downloadId={1}: {2}", job.Id, job.DownloadId,
                                    current == null ? "the job no longer exists" : "the download belongs to a different attempt");
                                return true;
                            }
                            return !HasCommittedImport(current, DeserializeRequest(current)) || SettleCommittedImport(current);
                        }, "retrying import bookkeeping after worker ownership was released");
                }
                finally
                {
                    ReleaseConversionOwnership(job.DownloadId, active);
                    cancellation.Dispose();
                    _signal.Set();
                    if (!_shutdown.IsCancellationRequested)
                    {
                        QueueImportSweep();
                    }
                }
            }
        }

        // Call under _sync so checking ownership and changing state are one operation.
        private bool OwnsConversion(string downloadId, ActiveConversion owner) =>
            _activeConversions.TryGetValue(downloadId, out var current) && ReferenceEquals(current, owner);

        private void ReleaseConversionOwnership(string downloadId, ActiveConversion active)
        {
            lock (_sync)
            {
                if (!OwnsConversion(downloadId, active)) return;
                _trackingService?.UnregisterCancellation(downloadId, active.Cancellation);
                _activeConversions.Remove(downloadId);
            }
        }

        private void PersistWorkerOutcome(ConversionJob job, Action persist, string retryMessage = "retaining worker ownership and retrying") =>
            PersistWorkerOutcome(job, () => { persist(); return true; }, retryMessage);

        private void PersistWorkerOutcome(ConversionJob job, Func<bool> persist, string retryMessage = "retaining worker ownership and retrying")
        {
            DateTime? failureStarted = null;
            while (true)
            {
                try
                {
                    if (persist())
                    {
                        if (failureStarted.HasValue)
                        {
                            _logger.Info("Saved conversion outcome {0} for download {1} after {2:0.0}s of database failures",
                                job.Status, job.DownloadId, (DateTime.UtcNow - failureStarted.Value).TotalSeconds);
                        }
                        return;
                    }
                }
                catch (DbException ex)
                {
                    failureStarted ??= DateTime.UtcNow;
                    _logger.Error(ex, "Unable to save conversion outcome {0} for download {1}; {2}. Outcome: {3}",
                        job.Status, job.DownloadId, retryMessage, job.Error ?? job.Message);
                }
                // A stale snapshot also needs a fresh read. Share the bounded, cancellable
                // retry delay rather than spinning while lifecycle writers keep changing it.
                if (_shutdown.Token.WaitHandle.WaitOne(_outcomeRetryDelay)) return;
            }
        }

        private void HandleInterruptedConversion(ConversionJob job, ConversionJobRequest request, ActiveConversion owner)
        {
            var persisted = _repository.FindByDownloadId(job.DownloadId);
            if (_shutdown.IsCancellationRequested &&
                persisted?.Status != ConversionJobStatus.Cancelling)
            {
                TryRecover(persisted, current => Requeue(current, "Conversion interrupted by application shutdown"), owner);
                return;
            }

            // Preserve discard intent saved by the remover while this worker was stopping.
            if (persisted != null) job.RequestJson = persisted.RequestJson;
            MarkCancelled(job, $"{request?.TargetQualityName ?? "M4B"} conversion was cancelled.");
            CleanupWorkRoot(request?.WorkFolder ?? job.WorkFolder, downloadId: job.DownloadId, reason: DeserializeRequest(job)?.DiscardReason ?? "cancelled");
        }

        private void UpdateProgress(ConversionJob job, ActiveConversion owner, ConversionProgressUpdate update)
        {
            if (update == null)
            {
                return;
            }

            lock (_sync)
            {
                if (!OwnsConversion(job.DownloadId, owner) || job.Status != ConversionJobStatus.Converting || owner.Cancellation.IsCancellationRequested) return;
                job.Progress = update.Progress;
                job.Message = update.Message ?? job.Message;
                job.HeartbeatAt = DateTime.UtcNow;
                job.UpdatedAt = job.HeartbeatAt.Value;
                _repository.SetFields(job, j => j.Progress, j => j.Message, j => j.HeartbeatAt, j => j.UpdatedAt);
                _trackingService?.Progress(job.DownloadId, update.Progress, update.Message);
            }
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

        private void SaveState(ConversionJob job)
        {
            // Ownership can change during a long conversion. State transitions must not
            // overwrite its durable request (or discard intent) with the worker's old copy.
            _repository.SetFields(job, j => j.Status, j => j.Progress, j => j.Message, j => j.Error,
                j => j.UpdatedAt, j => j.StartedAt, j => j.HeartbeatAt, j => j.CompletedAt, j => j.AttemptCount);
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
                SaveState(job);
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
                SaveState(job);
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
                    HandleInterruptedConversion(job, DeserializeRequest(job), active);
                    return;
                }

                job.Status = ConversionJobStatus.Failed;
                job.Progress = null;
                job.Message = error;
                job.Error = error;
                job.UpdatedAt = DateTime.UtcNow;
                job.CompletedAt = job.UpdatedAt;
                SaveState(job);
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
                SaveState(job);
            }

            _trackingService?.Cancelled(job.DownloadId, message);
        }

        private bool TryUpdateTerminal(string downloadId, ConversionJobStatus status, string error, string message, out ConversionJob job)
        {
            job = null;
            if (downloadId.IsNullOrWhiteSpace()) return false;
            lock (_sync)
            {
                job = _repository.FindByDownloadId(downloadId);
                if (IsRunning(downloadId) || !MayStopForRemoval(job, ConversionRemovalOrigin.Automatic)) return false;
                // A verified legacy artifact may have no detached job after Reset. Its import
                // still updates the live progress tracker, without creating a new durable job.
                if (job == null) return true;
                job.Status = status;
                job.Progress = status == ConversionJobStatus.Completed ? 100m : null;
                job.Message = message;
                job.Error = error;
                job.UpdatedAt = DateTime.UtcNow;
                job.CompletedAt = job.UpdatedAt;
                SaveState(job);
                return true;
            }
        }

        private const string UnverifiedRetainedOutputMessage = "Retained conversion completion cannot be verified. Manual Import imports the original files; Retry starts a new conversion. The retained output has been kept.";

        private bool HasUnverifiedRetainedArtifact(ConversionJobRequest request)
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
                _logger.Warn(ex, "Unable to verify retained conversion receipt for download {0}", request.DownloadId);
            }

            // Old manifests were also written for failed-but-readable output. Without the explicit
            // success contract, neither reuse nor automatic overwrite is justified.
            return true;
        }

        private bool RecoverCommittedImport(ConversionJob job, ConversionJobRequest request)
        {
            if (!HasCommittedImport(job, request)) return false;
            SettleCommittedImport(job);
            return true;
        }

        private bool HasCommittedImport(ConversionJob job, ConversionJobRequest request)
        {
            if (request == null) return false;

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
                if (imported) return true;
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
                return true;
            }

            return false;
        }

        private bool SettleCommittedImport(ConversionJob job)
        {
            ConversionLifecycleCandidate expected = null;
            ConversionJobRequest removal = null;
            if (!TryRecover(job, current =>
            {
                current.Status = ConversionJobStatus.Completed;
                current.Progress = 100m;
                current.Message = "Converted M4B import recovered";
                current.Error = null;
                current.CompletedAt = current.UpdatedAt = DateTime.UtcNow;
                var request = DeserializeRequest(current);
                if (!request.DiscardRequested)
                {
                    request.DiscardRequested = true;
                    request.DiscardCurrentAttemptOnly = true;
                    request.DiscardReason = "imported";
                }
                removal = request;
                current.RequestJson = JsonSerializer.Serialize(request);
                _repository.Update(current);
                // PostgreSQL stores timestamps at microsecond precision. Capture the
                // persisted version, just as lifecycle-page readers do.
                expected = ExpectedAttempt(_repository.Get(current.Id));
            }, whenOwned: () => _logger.Info("Import is committed for conversion job {0}, download {1}; workspace cleanup waits for the active worker to finish", job.Id, job.DownloadId))) return false;
            // The committed BookFile/source proof above, not a policy refusal, prevents
            // another encode. A cleanup refusal cannot put this job back in the queue.
            _trackingService?.Complete(job.DownloadId);
            _logger.Info("Recovered committed M4B import for conversion job {0}, download {1}; the job is completed and will not encode again", job.Id, job.DownloadId);
            try
            {
                PrepareArtifactRemoval(job.DownloadId, removal.DiscardCurrentAttemptOnly, removal.DiscardReason ?? "imported",
                    origin: ConversionRemovalOrigin.Automatic, expected: expected)();
            }
            catch (Exception ex)
            {
                RecordCleanupFailure(job.DownloadId, ex);
            }
            return true;
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

            return request.MatchesSources(request.BookId, request.Sources.Select(source => source.Path).ToArray());
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

        private bool CleanupMayRetry(ConversionJob job) => !(DeserializeRequest(job)?.CleanupNotBefore > DateTime.UtcNow);

        private void RecordCleanupFailure(string downloadId, Exception error)
        {
            try
            {
                ConversionJobRequest request;
                bool changed;
                lock (_sync)
                {
                    var job = Get(downloadId);
                    if (job == null) { _logger.Warn(error, "Conversion cleanup failed for download {0}; no cleanup record exists", downloadId); return; }
                    request = DeserializeRequest(job);
                    if (request == null)
                    {
                        _logger.Warn(error, "Conversion cleanup failed for job {0}, downloadId={1}; its request is unreadable, so its workspace was retained", job.Id, downloadId);
                        return;
                    }
                    changed = request.CleanupError != error.Message;
                    request.CleanupFailureCount = Math.Min(request.CleanupFailureCount + 1, 1000);
                    var minutes = error is ConversionCleanupRefusedException ? 60 : Math.Min(60, Math.Pow(2, Math.Min(request.CleanupFailureCount - 1, 6)));
                    request.CleanupNotBefore = DateTime.UtcNow.AddMinutes(minutes);
                    request.CleanupError = error.Message;
                    job.RequestJson = JsonSerializer.Serialize(request);
                    job.UpdatedAt = DateTime.UtcNow;
                    // Preserve concurrent discard/completion intent as well as the encoder error.
                    _repository.SetFields(job, value => value.RequestJson, value => value.UpdatedAt);
                }
                if (changed || (request.CleanupFailureCount == 5 && error is not ConversionCleanupRefusedException))
                    _logger.Warn(error, "Conversion cleanup retained: downloadId={0}, attempt={1}, nextAttempt={2}", downloadId, request.CleanupFailureCount, request.CleanupNotBefore);
                else
                    _logger.Debug("Conversion cleanup remains pending: downloadId={0}, attempt={1}, nextAttempt={2}", downloadId, request.CleanupFailureCount, request.CleanupNotBefore);
            }
            catch (Exception persistenceError)
            {
                _logger.Warn(error, "Conversion cleanup failed for download {0}", downloadId);
                _logger.Error(persistenceError, "Unable to record conversion cleanup failure for download {0}; other jobs will continue", downloadId);
            }
        }

        private sealed record CleanupInventory(IReadOnlyCollection<string> Folders, List<BookFile> Files);

        private CleanupInventory ReadCleanupInventory(IReadOnlyCollection<string> paths)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var files = _mediaFileService.GetFilePathOwnership(Array.Empty<int>(), paths);
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            _logger.Log(elapsed >= 1000 ? LogLevel.Info : LogLevel.Debug,
                "Conversion cleanup ownership: folders={0}, trackedFiles={1}, elapsedMs={2}", paths.Count, files.Count, elapsed);
            return new CleanupInventory(paths, files);
        }

        private void VerifyCleanupOwnership(IReadOnlyCollection<string> paths, CleanupInventory inventory = null)
        {
            if (paths.Count == 0) return;
            var uncovered = paths.Where(path => inventory == null || !inventory.Folders.Any(folder => folder.PathEquals(path) || folder.IsParentPath(path))).ToArray();
            var files = (inventory?.Files ?? new List<BookFile>()).Concat(uncovered.Length == 0 ? Enumerable.Empty<BookFile>() : ReadCleanupInventory(uncovered).Files);
            if (files.Any(file => (file.ReplicaPaths ?? new List<string>()).Append(file.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path)).Any(path => paths.Any(folder => folder.PathEquals(path) || folder.IsParentPath(path)))))
                throw new ConversionCleanupRefusedException("A conversion workspace contains a tracked library file; it was retained.");
        }

        private void CleanupWorkRoot(string workRoot, bool throwOnError = false, string downloadId = null, string reason = "conversion", bool ownershipVerified = false)
        {
            if (string.IsNullOrWhiteSpace(workRoot)) return;
            try
            {
                if (!throwOnError && !CleanupMayRetry(Get(downloadId))) return;
                if (!ownershipVerified) VerifyCleanupOwnership(new[] { workRoot });
                ConversionWorkPath.Cleanup(workRoot, _logger, true, downloadId, reason);
            }
            catch (Exception ex)
            {
                if (throwOnError) throw;
                RecordCleanupFailure(downloadId, ex);
            }
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
            return IsWaitingForOutput(status) || status == ConversionJobStatus.ReadyToImport;
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
