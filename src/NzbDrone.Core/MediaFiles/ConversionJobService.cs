using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.MediaFiles
{
    public interface IConversionJobService
    {
        int WorkerConcurrency { get; }
        ConversionJob Get(string downloadId);
        ConversionJob Enqueue(ConversionJobRequest request);
        bool IsActive(string downloadId);
        bool Cancel(string downloadId);
        void Complete(string downloadId);
        void Fail(string downloadId, string error);
        void Reset(string downloadId);
    }

    public class ConversionJobService :
        IConversionJobService,
        IHandle<ApplicationStartedEvent>,
        IHandle<ApplicationShutdownRequested>
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
        private CancellationTokenSource _shutdown;
        private Task _scheduler;

        public ConversionJobService(
            IConversionJobRepository repository,
            IM4bConversionService conversionService,
            IConversionTrackingService trackingService,
            IManageCommandQueue commandQueue,
            IConfigService configService,
            Logger logger)
        {
            _repository = repository;
            _conversionService = conversionService;
            _trackingService = trackingService;
            _commandQueue = commandQueue;
            _configService = configService;
            _logger = logger;
        }

        public int WorkerConcurrency => Clamp(_configService?.AudiobookConversionConcurrentConversions ?? 1, 1, 16);

        public ConversionJob Get(string downloadId)
        {
            return _repository.FindByDownloadId(downloadId);
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

                if (job.Status == ConversionJobStatus.Converting || job.Status == ConversionJobStatus.Cancelling)
                {
                    job.Status = ConversionJobStatus.Cancelling;
                    job.Message = "Cancelling conversion";
                    job.UpdatedAt = DateTime.UtcNow;
                    _repository.Update(job);

                    _activeConversions.TryGetValue(downloadId, out active);
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
                _trackingService?.Cancel(downloadId);
                active.Cancellation.Cancel();
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

                CleanupWorkRoot(job.WorkRoot);
                QueueImportSweep();
            }

            return true;
        }

        public void Complete(string downloadId)
        {
            UpdateTerminal(downloadId, ConversionJobStatus.Completed, null, "Converted M4B imported");
            _trackingService?.Complete(downloadId);
        }

        public void Fail(string downloadId, string error)
        {
            UpdateTerminal(downloadId, ConversionJobStatus.Failed, error, error);
            _trackingService?.Fail(downloadId, error);
        }

        public void Reset(string downloadId)
        {
            if (downloadId.IsNullOrWhiteSpace())
            {
                return;
            }

            lock (_sync)
            {
                var job = _repository.FindByDownloadId(downloadId);
                if (job != null && IsInFlight(job.Status))
                {
                    throw new InvalidOperationException("An active conversion job must be cancelled before it can be reset.");
                }

                _repository.DeleteByDownloadId(downloadId);
            }

            _trackingService?.Clear(downloadId);
        }

        public void Handle(ApplicationStartedEvent message)
        {
            _shutdown = new CancellationTokenSource();
            Reconcile();
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
                switch (job.Status)
                {
                    case ConversionJobStatus.Converting:
                    case ConversionJobStatus.ReadyToImport:
                        if (request != null && HasReusableArtifact(request))
                        {
                            MarkReady(job, "Recovered converted M4B; waiting to import");
                            QueueImportSweep();
                        }
                        else
                        {
                            Requeue(job, "Recovering interrupted M4B conversion");
                        }

                        break;

                    case ConversionJobStatus.Cancelling:
                        MarkCancelled(job, "Conversion cancelled");
                        CleanupWorkRoot(job.WorkRoot);
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

            try
            {
                while (!_shutdown.IsCancellationRequested)
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

                    RecoverExpiredLeases();
                    DispatchQueuedJobs();
                }
            }
            catch (OperationCanceledException)
            {
                _logger.Debug("Detached audiobook conversion scheduler stopped");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Detached audiobook conversion scheduler failed");
            }
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
            var staleJobs = _repository.NonCompleted()
                .Where(job => job.Status == ConversionJobStatus.Converting &&
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
                else
                {
                    Requeue(job, "Recovering expired M4B conversion lease");
                }
            }
        }

        private void DispatchQueuedJobs()
        {
            List<(ConversionJob Job, ActiveConversion Active)> dispatches;

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

                var queued = _repository.NonCompleted()
                    .Where(candidate => candidate.Status == ConversionJobStatus.Queued &&
                                        !_activeConversions.ContainsKey(candidate.DownloadId))
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
                dispatches = new List<(ConversionJob, ActiveConversion)>(queued.Count);

                for (var index = 0; index < queued.Count; index++)
                {
                    var job = queued[index];
                    var tokenBudget = baseTokens + (index < extraTokens ? 1 : 0);
                    var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                    var active = new ActiveConversion(cancellation, tokenBudget);

                    job.Status = ConversionJobStatus.Converting;
                    job.Progress = 1m;
                    job.Message = $"Converting to M4B ({tokenBudget} CPU thread{(tokenBudget == 1 ? string.Empty : "s")})";
                    job.Error = null;
                    job.StartedAt = DateTime.UtcNow;
                    job.HeartbeatAt = job.StartedAt;
                    job.UpdatedAt = job.StartedAt.Value;
                    job.AttemptCount++;
                    _repository.Update(job);
                    _activeConversions.Add(job.DownloadId, active);
                    dispatches.Add((job, active));
                }
            }

            foreach (var dispatch in dispatches)
            {
                var job = dispatch.Job;
                var active = dispatch.Active;
                _trackingService?.Start(job.DownloadId, job.TargetQualityId, job.TargetQualityName, job.Message);
                _trackingService?.RegisterCancellation(job.DownloadId, active.Cancellation);
                _trackingService?.Progress(job.DownloadId, job.Progress, job.Message);
                _ = Task.Run(() => ExecuteJob(job, active));
            }
        }

        private void ExecuteJob(ConversionJob job, ActiveConversion active)
        {
            var cancellation = active.Cancellation;
            try
            {
                var request = DeserializeRequest(job);
                if (request == null)
                {
                    MarkFailed(job, "Conversion job request is missing or unreadable.");
                    return;
                }

                if (cancellation.IsCancellationRequested)
                {
                    HandleInterruptedConversion(job, request);
                    return;
                }

                if (HasReusableArtifact(request))
                {
                    MarkReady(job, "Using retained M4B");
                    return;
                }

                var threadPlan = GetThreadPlan(request.ConversionInputFiles.Count, active.TokenBudget);
                using var heartbeat = new Timer(_ => TouchHeartbeat(job), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

                try
                {
                    Directory.CreateDirectory(request.WorkFolder);
                    var result = _conversionService.ConvertToM4b(
                        request.ConversionInputFiles.ToArray(),
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
                        WriteArtifactManifest(request);
                        MarkReady(job, "Converted M4B ready to import");
                    }
                    else if (result.FailureCategory == ConversionFailureCategory.Cancelled || cancellation.IsCancellationRequested)
                    {
                        HandleInterruptedConversion(job, request);
                    }
                    else
                    {
                        var error = result.ErrorMessage.IsNullOrWhiteSpace() ? "M4B conversion failed" : result.ErrorMessage;
                        if (result.RetainOutputOnFailure && File.Exists(request.OutputPath))
                        {
                            WriteArtifactManifest(request);
                            error = $"{error} Converted file retained at: {request.OutputPath}";
                        }
                        else
                        {
                            CleanupWorkRoot(request.WorkRoot);
                        }

                        MarkFailed(job, error);
                    }
                }
                catch (OperationCanceledException)
                {
                    HandleInterruptedConversion(job, request);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Detached M4B conversion failed for download {0}", job.DownloadId);
                    MarkFailed(job, "M4B conversion failed: " + ex.Message);
                    CleanupWorkRoot(request.WorkRoot);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Detached M4B job orchestration failed for download {0}", job.DownloadId);
                try
                {
                    MarkFailed(job, "M4B conversion job failed: " + ex.Message);
                }
                catch (Exception persistException)
                {
                    _logger.Error(persistException, "Unable to persist failed conversion job {0}", job.DownloadId);
                }
            }
            finally
            {
                lock (_sync)
                {
                    _activeConversions.Remove(job.DownloadId);
                }

                cancellation.Dispose();
                _signal.Set();
                if (_shutdown?.IsCancellationRequested != true)
                {
                    QueueImportSweep();
                }
                active.Completion.TrySetResult();
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

            MarkCancelled(job, $"{request.TargetQualityName ?? "M4B"} conversion was cancelled.");
            CleanupWorkRoot(request.WorkRoot);
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

        private void MarkReady(ConversionJob job, string message)
        {
            lock (_sync)
            {
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
                job.Status = ConversionJobStatus.Failed;
                job.Progress = null;
                job.Message = error;
                job.Error = error;
                job.UpdatedAt = DateTime.UtcNow;
                job.CompletedAt = job.UpdatedAt;
                _repository.Update(job);
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

        private void UpdateTerminal(string downloadId, ConversionJobStatus status, string error, string message)
        {
            if (downloadId.IsNullOrWhiteSpace())
            {
                return;
            }

            lock (_sync)
            {
                var job = _repository.FindByDownloadId(downloadId);
                if (job == null)
                {
                    return;
                }

                job.Status = status;
                job.Progress = status == ConversionJobStatus.Completed ? 100m : null;
                job.Message = message;
                job.Error = error;
                job.UpdatedAt = DateTime.UtcNow;
                job.CompletedAt = job.UpdatedAt;
                _repository.Update(job);
            }
        }

        private ConversionJobRequest DeserializeRequest(ConversionJob job)
        {
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
            if (request == null ||
                request.DownloadId.IsNullOrWhiteSpace() ||
                request.WorkRoot.IsNullOrWhiteSpace() ||
                request.WorkFolder.IsNullOrWhiteSpace() ||
                request.OutputPath.IsNullOrWhiteSpace() ||
                request.ConversionInputFiles == null ||
                request.ConversionInputFiles.Count == 0 ||
                request.Sources == null ||
                request.Sources.Count == 0 ||
                request.Sources.Any(source => source?.Path.IsNullOrWhiteSpace() != false) ||
                !Path.GetExtension(request.OutputPath).Equals(".m4b", StringComparison.OrdinalIgnoreCase) ||
                !IsConversionWorkPath(request.WorkRoot))
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

        private static bool IsConversionWorkPath(string path)
        {
            return path
                .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
                .Any(part => part.Equals(".chaptarr-conversions", StringComparison.OrdinalIgnoreCase) ||
                             part.Equals("chaptarr-conversions", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsPathWithin(string candidate, string parent)
        {
            return candidate.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasReusableArtifact(ConversionJobRequest request)
        {
            if (request == null ||
                request.OutputPath.IsNullOrWhiteSpace() ||
                !File.Exists(request.OutputPath) ||
                request.WorkFolder.IsNullOrWhiteSpace())
            {
                return false;
            }

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

            try
            {
                var manifestPath = Path.Combine(request.WorkFolder, ArtifactManifestFileName);
                if (!File.Exists(manifestPath))
                {
                    return false;
                }

                var manifest = JsonSerializer.Deserialize<ConversionArtifactManifest>(File.ReadAllText(manifestPath));
                return manifest != null &&
                       manifest.TargetQualityId == request.TargetQualityId &&
                       manifest.AudioBitrate == request.AudioBitrate &&
                       manifest.AudioChannels == request.AudioChannels &&
                       string.Equals(manifest.TagSignature, request.TagSignature, StringComparison.Ordinal) &&
                       manifest.Sources.Count == request.Sources.Count &&
                       manifest.Sources.Zip(request.Sources, SourcesMatch).All(matches => matches);
            }
            catch
            {
                return false;
            }
        }

        private static bool SourcesMatch(ConversionArtifactSource left, ConversionArtifactSource right)
        {
            return left != null &&
                   right != null &&
                   left.Path.PathEquals(right.Path) &&
                   left.Size == right.Size &&
                   left.ModifiedUtcTicks == right.ModifiedUtcTicks;
        }

        private static void WriteArtifactManifest(ConversionJobRequest request)
        {
            var manifest = new ConversionArtifactManifest
            {
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

        private void CleanupWorkRoot(string workRoot)
        {
            if (workRoot.IsNullOrWhiteSpace() || !IsConversionWorkPath(workRoot) || !Directory.Exists(workRoot))
            {
                return;
            }

            try
            {
                Directory.Delete(workRoot, true);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to clean conversion work folder {0}", workRoot);
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
