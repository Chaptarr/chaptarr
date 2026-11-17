using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Common;
using Dapper;
using DryIoc;
using Microsoft.Data.Sqlite;
using NLog;
using NLog.Config;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Common.Composition.Extensions;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public partial class ConversionJobServiceFixture
    {
        [Test]
        public void dependency_injection_keeps_the_production_outcome_retry_delay()
        {
            using var container = new Container(rules => rules.WithNzbDroneRules());
            container.RegisterInstance(InterfaceProxy.Proxy<IConversionJobRepository>((_, _) => null));
            container.RegisterInstance(ImportLifecycleStubs.NoLibraryMediaFiles());
            container.RegisterInstance<IM4bConversionService>(new UnusedConversionService());
            container.RegisterInstance<IConversionTrackingService>(new ConversionTrackingService(new StubEventAggregator()));
            container.RegisterInstance(DispatchProxy.Create<IManageCommandQueue, RecordingCommandQueueProxy>());
            container.RegisterInstance(CreateConfig());
            container.RegisterInstance(TestRootFolders());
            container.RegisterInstance(TestCleanupDisk());
            container.RegisterInstance(LogManager.GetCurrentClassLogger());

            var service = container.Resolve<ConversionJobService>();

            Assert.That(typeof(ConversionJobService).GetField("_outcomeRetryDelay", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(service),
                Is.EqualTo(TimeSpan.FromSeconds(30)));
            Assert.That(typeof(ConversionJobService).GetField("_shutdownWait", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(service),
                Is.EqualTo(TimeSpan.FromSeconds(5)));
            Assert.That(typeof(ConversionJobService).GetField("_heartbeatInterval", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(service),
                Is.EqualTo(TimeSpan.FromSeconds(30)));
        }

        private static void RunWorker(ConversionJobService service, ConversionJobRequest request)
        {
            service.Enqueue(request);
            Assert.That(DispatchWorkers(service).Wait(TimeSpan.FromSeconds(45)), Is.True);
        }

        private sealed class StubEventAggregator : IEventAggregator
        {
            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
            }
        }

        private sealed class UnusedConversionService : IM4bConversionService
        {
            public ConversionResult ConvertToM4b(string[] inputFiles, string outputFile, ConversionOptions options = null)
                => throw new AssertionException("Recovered artifact should not run the converter.");

            public bool CanConvert(string[] inputFiles) => true;
            public ConversionEstimate EstimateConversion(string[] inputFiles) => new() { CanConvert = true };
        }

        private sealed class BlockingConversionService : IM4bConversionService
        {
            public ManualResetEventSlim Started { get; } = new(false);

            public ConversionResult ConvertToM4b(string[] inputFiles, string outputFile, ConversionOptions options = null)
            {
                Started.Set();
                options.CancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
                return new ConversionResult
                {
                    Success = false,
                    FailureCategory = ConversionFailureCategory.Cancelled,
                    ErrorMessage = "Process cancelled"
                };
            }

            public bool CanConvert(string[] inputFiles) => true;
            public ConversionEstimate EstimateConversion(string[] inputFiles) => new() { CanConvert = true };
        }

        private sealed class TokenRecordingConversionService : IM4bConversionService
        {
            public ConcurrentDictionary<string, ConversionOptions> OptionsByDownloadId { get; } = new(StringComparer.OrdinalIgnoreCase);
            public ConcurrentDictionary<string, string[]> InputsByDownloadId { get; } = new(StringComparer.OrdinalIgnoreCase);
            public ConcurrentDictionary<string, int> CallsByDownloadId { get; } = new(StringComparer.OrdinalIgnoreCase);
            public ManualResetEventSlim Release { get; } = new(false);
            public bool HoldCancellation { get; set; }
            public Action<string> OnStarted { get; set; }

            public ConversionResult ConvertToM4b(string[] inputFiles, string outputFile, ConversionOptions options = null)
            {
                var workFolder = Directory.GetParent(Path.GetDirectoryName(outputFile));
                var downloadId = workFolder?.Name ?? outputFile;
                CallsByDownloadId.AddOrUpdate(downloadId, 1, (_, count) => count + 1);
                InputsByDownloadId[downloadId] = inputFiles;
                OptionsByDownloadId[downloadId] = options;
                OnStarted?.Invoke(downloadId);

                if (HoldCancellation) Release.Wait();
                else WaitHandle.WaitAny(new[] { options.CancellationToken.WaitHandle, Release.WaitHandle });
                if (options.CancellationToken.IsCancellationRequested)
                {
                    return new ConversionResult
                    {
                        Success = false,
                        FailureCategory = ConversionFailureCategory.Cancelled,
                        ErrorMessage = "Process cancelled"
                    };
                }

                Directory.CreateDirectory(Path.GetDirectoryName(outputFile));
                File.WriteAllText(outputFile, "converted");
                return new ConversionResult { Success = true };
            }

            public bool CanConvert(string[] inputFiles) => true;
            public ConversionEstimate EstimateConversion(string[] inputFiles) => new() { CanConvert = true };
        }

        private class FailingRepositoryProxy : DispatchProxy
        {
            public IConversionJobRepository Repository { get; set; }
            public string Method { get; set; }
            public int FailOnCall { get; set; }
            public bool AfterWrite { get; set; }
            public Func<object[], bool> ArgumentsMatch { get; set; }
            public ManualResetEventSlim Failed { get; } = new(false);
            public ManualResetEventSlim RecoveryRetried { get; } = new(false);
            private int _calls;
            private int _leaseReads;

            protected override object Invoke(MethodInfo method, object[] args)
            {
                var fail = method.Name == Method && (ArgumentsMatch?.Invoke(args) ?? true) &&
                    Interlocked.Increment(ref _calls) == FailOnCall;
                try
                {
                    if (fail && !AfterWrite)
                    {
                        throw new DataException("Injected conversion database outage");
                    }

                    var result = method.Invoke(Repository, args);
                    if (method.Name == nameof(IConversionJobRepository.ByStatus) &&
                        args[0] is ConversionJobStatus.Converting && Interlocked.Increment(ref _leaseReads) == 2)
                        RecoveryRetried.Set();
                    if (fail)
                    {
                        throw new DataException("Injected conversion database outage");
                    }

                    return result;
                }
                catch (TargetInvocationException ex) when (ex.InnerException != null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                    throw;
                }
                finally
                {
                    if (fail)
                    {
                        Failed.Set();
                    }
                }
            }
        }

        private class DecimalProjection
        {
            public decimal Required { get; set; }
            public decimal? Optional { get; set; }
        }

        private class RecordingCommandQueueProxy : DispatchProxy
        {
            private int _nextId;
            public ConcurrentQueue<Command> Commands { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IManageCommandQueue.Push) && args?.FirstOrDefault() is Command command)
                {
                    Commands.Enqueue(command);
                    return new CommandModel
                    {
                        Id = Interlocked.Increment(ref _nextId),
                        Name = command.Name,
                        Body = command,
                        Status = CommandStatus.Queued
                    };
                }

                throw new NotImplementedException($"Unexpected command queue call: {targetMethod?.Name}");
            }
        }

        private class ConfigServiceProxy : DispatchProxy
        {
            private string _orphanReviewProgress = string.Empty;
            public int ConcurrentConversions { get; set; } = 1;
            public int MaxCpuThreads { get; set; } = 8;

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    "GetConversionOrphanReviewProgress" => _orphanReviewProgress,
                    "SetConversionOrphanReviewProgress" => SetOrphanReviewProgress((string)args[0]),
                    "get_AudiobookConversionConcurrentConversions" => ConcurrentConversions,
                    "get_AudiobookConversionMaxCpuThreads" => MaxCpuThreads,
                    "set_AudiobookConversionConcurrentConversions" => SetConcurrentConversions((int)args[0]),
                    "set_AudiobookConversionMaxCpuThreads" => SetMaxCpuThreads((int)args[0]),
                    _ => throw new NotImplementedException($"Unexpected config call: {targetMethod?.Name}")
                };
            }

            private object SetOrphanReviewProgress(string value)
            {
                _orphanReviewProgress = value;
                return null;
            }

            private object SetConcurrentConversions(int value)
            {
                ConcurrentConversions = value;
                return null;
            }

            private object SetMaxCpuThreads(int value)
            {
                MaxCpuThreads = value;
                return null;
            }
        }

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            if (TableMapping.Mapper.TableMap.Count == 0)
            {
                TableMapping.Map();
            }
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void progress_should_read_mixed_numeric_storage_and_updates(bool postgres, bool fractionalFirst)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var repository = new ConversionJobRepository(database, new StubEventAggregator());
                decimal?[] values = fractionalFirst ? new decimal?[] { 12.5m, 0m, 100m, null } : new decimal?[] { 0m, 12.5m, 100m, null };
                var jobs = values.Select((value, index) => repository.Insert(new ConversionJob
                {
                    DownloadId = "mixed-progress-" + index,
                    Status = ConversionJobStatus.Queued,
                    RequestJson = "{}",
                    TargetQualityId = 12,
                    Progress = value,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                })).ToList();

                Assert.That(repository.NonCompleted().OrderBy(job => job.Id).Select(job => job.Progress), Is.EqualTo(values));
                using var connection = database.OpenConnection();
                var projections = connection.Query<DecimalProjection>(
                    "SELECT \"Progress\" AS \"Required\", \"Progress\" AS \"Optional\" FROM \"ConversionJobs\" WHERE \"Progress\" IS NOT NULL ORDER BY \"Id\"").ToList();
                Assert.That(projections.Select(value => value.Required), Is.EqualTo(values.Where(value => value.HasValue).Select(value => value.Value)));
                Assert.That(projections.Select(value => value.Optional), Is.EqualTo(values.Where(value => value.HasValue)));

                jobs[0].Progress = fractionalFirst ? 0m : 25.75m;
                jobs[1].Progress = fractionalFirst ? 25.75m : 0m;
                repository.UpdateMany(jobs.Take(2).ToList());
                Assert.That(repository.NonCompleted().OrderBy(job => job.Id).Select(job => job.Progress), Is.EqualTo(jobs.Select(job => job.Progress)));
            });
        }

        [TestCase(false, 1)]
        [TestCase(false, 2)]
        [TestCase(true, 1)]
        [TestCase(true, 2)]
        public void scheduler_should_resume_after_startup_or_runtime_read_failure(bool postgres, int failOnCall)
        {
            // Startup scans NonCompleted once; runtime recovery uses the narrowed status query.
            VerifySchedulerRecovery(postgres, failOnCall == 1 ? nameof(IConversionJobRepository.NonCompleted) : nameof(IConversionJobRepository.ByStatus), 1, false);
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void dispatch_failure_should_not_strand_an_earlier_reservation(bool postgres, bool afterWrite)
        {
            VerifySchedulerRecovery(postgres, nameof(IConversionJobRepository.SetFields), 2, afterWrite);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void startup_recovery_retry_must_not_requeue_live_conversions(bool postgres)
        {
            VerifySchedulerRecovery(postgres, nameof(IConversionJobRepository.NonCompleted), 1, false, holdThroughRecovery: true);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void startup_recovers_an_unowned_conversion_without_waiting_for_its_fresh_lease(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, Path.GetRandomFileName());
                Directory.CreateDirectory(root);
                var repository = new ConversionJobRepository(database, new StubEventAggregator());
                var converter = new TokenRecordingConversionService();
                var service = CreateService(repository, converter);
                try
                {
                    var request = CreateRequest(root, "fresh-lease");
                    repository.Insert(CreatePersistedJob(request, ConversionJobStatus.Converting, DateTime.UtcNow));
                    service.Handle(new ApplicationStartedEvent());

                    Assert.That(SpinWait.SpinUntil(() => converter.CallsByDownloadId.ContainsKey(request.DownloadId), TimeSpan.FromSeconds(5)),
                        Is.True, "Startup must recover a previous process's job without waiting for the lease to expire");
                    Assert.That(repository.FindByDownloadId(request.DownloadId).AttemptCount, Is.EqualTo(1));
                    converter.Release.Set();
                    Assert.That(SpinWait.SpinUntil(() => repository.FindByDownloadId(request.DownloadId).Status == ConversionJobStatus.ReadyToImport &&
                        !service.IsRunning(request.DownloadId), TimeSpan.FromSeconds(5)), Is.True);
                    Assert.That(converter.CallsByDownloadId[request.DownloadId], Is.EqualTo(1));
                }
                finally
                {
                    converter.Release.Set();
                    ShutdownAndWait(service);
                    Directory.Delete(root, true);
                }
            });
        }

        private static void VerifySchedulerRecovery(bool postgres, string method, int failOnCall, bool afterWrite, bool holdThroughRecovery = false)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "conversion-recovery-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                var repository = new ConversionJobRepository(database, new StubEventAggregator());
                var proxy = DispatchProxy.Create<IConversionJobRepository, FailingRepositoryProxy>();
                var failure = (FailingRepositoryProxy)(object)proxy;
                failure.Repository = repository;
                failure.Method = method;
                failure.FailOnCall = failOnCall;
                failure.AfterWrite = afterWrite;
                failure.ArgumentsMatch = args => method != nameof(IConversionJobRepository.SetFields) ||
                    (args.Length > 0 && args[0] is ConversionJob { Status: ConversionJobStatus.Converting, Progress: 1m });
                var converter = new TokenRecordingConversionService();
                var log = new ConcurrentLogTarget { Layout = "${level}|${message}|${exception:format=Message}" };
                using var logFactory = new LogFactory();
                var configuration = new LoggingConfiguration(logFactory);
                configuration.AddRuleForAllLevels(log);
                logFactory.Configuration = configuration;
                var service = CreateService(proxy, converter, 2, 8, logFactory.GetLogger("conversion-recovery"));
                try
                {
                    service.Enqueue(CreateRequest(root, "first"));
                    service.Enqueue(CreateRequest(root, "second"));
                    Assert.DoesNotThrow(() => service.Handle(new ApplicationStartedEvent()));
                    Assert.That(failure.Failed.Wait(TimeSpan.FromSeconds(5)), Is.True, "The failure must occur in the running scheduler");

                    if (method == nameof(IConversionJobRepository.SetFields))
                    {
                        Assert.That(SpinWait.SpinUntil(() => converter.OptionsByDownloadId.ContainsKey("first"), TimeSpan.FromSeconds(5)), Is.True,
                            "An earlier persisted reservation must start even when a later write fails");
                        var interrupted = repository.FindByDownloadId("second");
                        interrupted.HeartbeatAt = DateTime.UtcNow.AddMinutes(-5);
                        repository.SetFields(interrupted, job => job.HeartbeatAt);
                    }

                    if (holdThroughRecovery)
                    {
                        Assert.That(SpinWait.SpinUntil(() => converter.OptionsByDownloadId.Count == 2, TimeSpan.FromSeconds(5)), Is.True,
                            "The failed startup pass must dispatch both converters before startup recovery is retried");
                    }

                    service.Enqueue(CreateRequest(root, "after-outage"));
                    if (holdThroughRecovery)
                    {
                        Assert.That(failure.RecoveryRetried.Wait(TimeSpan.FromSeconds(5)), Is.True,
                            "The second lease scan follows startup recovery while both converters remain held");
                        foreach (var downloadId in new[] { "first", "second" })
                        {
                            var running = repository.FindByDownloadId(downloadId);
                            Assert.That(running.Status, Is.EqualTo(ConversionJobStatus.Converting), "Startup retry must leave a live owner's row alone");
                            Assert.That(running.Message, Does.Not.Contain("Recovering interrupted"));
                            Assert.That(running.AttemptCount, Is.EqualTo(1));

                            running.HeartbeatAt = DateTime.UtcNow.AddMinutes(-5);
                            repository.SetFields(running, job => job.HeartbeatAt);
                        }

                        service.RecoverExpiredLeases();
                        Assert.That(repository.NonCompleted().Where(job => job.DownloadId != "after-outage").Select(job => job.Status),
                            Is.All.EqualTo(ConversionJobStatus.Converting), "An expired heartbeat must not override a live owner either");
                    }

                    converter.Release.Set();
                    Assert.That(SpinWait.SpinUntil(() => repository.NonCompleted().Count(job => job.Status == ConversionJobStatus.ReadyToImport) == 3,
                        TimeSpan.FromSeconds(5)), Is.True, "All durable work must resume without restarting the service");
                    Assert.That(converter.CallsByDownloadId.Values, Is.All.EqualTo(1));
                    Assert.That(converter.CallsByDownloadId.Count, Is.EqualTo(3));
                }
                finally
                {
                    converter.Release.Set();
                    ShutdownAndWait(service);
                    Directory.Delete(root, true);
                }
                Assert.That(log.Logs.Any(line => line.StartsWith("Error|") && line.Contains("Injected conversion database outage")), Is.True);
                Assert.That(log.Logs.Any(line => line.StartsWith("Info|") && line.Contains("scheduler pass completed")), Is.True);
            });
        }

        [Test]
        public void startup_should_adopt_valid_artifact_and_queue_serialized_import_sweep()
        {
            var tempDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-job-recovery-{Guid.NewGuid():N}");
            var databasePath = Path.Combine(tempDir, "jobs.db");
            var sourcePath = Path.Combine(tempDir, "source.mp3");
            var workRoot = Path.Combine(tempDir, ".chaptarr-conversions", "recover-download");
            var workFolder = Path.Combine(workRoot, "work");
            var outputPath = Path.Combine(workFolder, "book.m4b");
            Directory.CreateDirectory(workFolder);
            File.WriteAllText(sourcePath, "source");
            File.WriteAllText(outputPath, "converted");

            try
            {
                var sourceInfo = new FileInfo(sourcePath);
                var source = new ConversionArtifactSource
                {
                    Path = sourcePath,
                    Size = sourceInfo.Length,
                    ModifiedUtcTicks = sourceInfo.LastWriteTimeUtc.Ticks
                };
                var request = new ConversionJobRequest
                {
                    DownloadId = "recover-download",
                    BookTitle = "Recovered Book",
                    WorkRoot = workRoot,
                    WorkFolder = workFolder,
                    OutputPath = outputPath,
                    Sources = new List<ConversionArtifactSource> { source },
                    TargetQualityId = 12,
                    TargetQualityName = "M4B",
                    AudioBitrate = 64,
                    AudioChannels = 0,
                    TagSignature = "tags",
                    TagOptions = new ConversionTagOptions { Mode = "preserve" }
                };
                var manifest = new ConversionArtifactManifest
                {
                    EncoderSucceeded = true, OutputSize = new FileInfo(outputPath).Length,
                    CreatedUtc = DateTime.UtcNow,
                    OutputPath = outputPath,
                    TargetQualityId = request.TargetQualityId,
                    TargetQualityName = request.TargetQualityName,
                    AudioBitrate = request.AudioBitrate,
                    AudioChannels = request.AudioChannels,
                    TagMode = request.TagOptions.Mode,
                    TagSignature = request.TagSignature,
                    Sources = request.Sources
                };
                File.WriteAllText(
                    Path.Combine(workFolder, "conversion-artifact.json"),
                    JsonSerializer.Serialize(manifest));

                var repository = CreateRepository(databasePath);
                repository.Insert(new ConversionJob
                {
                    DownloadId = request.DownloadId,
                    Status = ConversionJobStatus.Converting,
                    RequestJson = JsonSerializer.Serialize(request),
                    WorkRoot = request.WorkRoot,
                    WorkFolder = request.WorkFolder,
                    OutputPath = request.OutputPath,
                    TargetQualityId = request.TargetQualityId,
                    TargetQualityName = request.TargetQualityName,
                    Progress = 55m,
                    Message = "Converting",
                    CreatedAt = DateTime.UtcNow.AddMinutes(-10),
                    UpdatedAt = DateTime.UtcNow.AddMinutes(-5),
                    StartedAt = DateTime.UtcNow.AddMinutes(-10),
                    HeartbeatAt = DateTime.UtcNow.AddMinutes(-5)
                });

                var eventAggregator = new StubEventAggregator();
                var tracking = new ConversionTrackingService(eventAggregator);
                var commandQueue = DispatchProxy.Create<IManageCommandQueue, RecordingCommandQueueProxy>();
                var commandRecorder = (RecordingCommandQueueProxy)(object)commandQueue;
                var service = new ConversionJobService(Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestCleanupDisk(), Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestRootFolders(), Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoLibraryMediaFiles(),
                    repository,
                    new UnusedConversionService(),
                    tracking,
                    commandQueue,
                    CreateConfig(),
                    LogManager.GetCurrentClassLogger());

                service.Handle(new ApplicationStartedEvent());
                try
                {
                    Assert.That(SpinWait.SpinUntil(() => repository.FindByDownloadId(request.DownloadId)?.Status == ConversionJobStatus.ReadyToImport &&
                        commandRecorder.Commands.Any(), TimeSpan.FromSeconds(5)), Is.True);
                    var recovered = repository.FindByDownloadId(request.DownloadId);
                    Assert.That(recovered.Status, Is.EqualTo(ConversionJobStatus.ReadyToImport));
                    Assert.That(recovered.Progress, Is.EqualTo(98m));
                    Assert.That(commandRecorder.Commands, Has.Some.InstanceOf<ProcessMonitoredDownloadsCommand>());
                }
                finally
                {
                    ShutdownAndWait(service);
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
        }

        [Test]
        public void stop_should_cancel_active_durable_job()
        {
            var tempDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-job-cancel-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            var databasePath = Path.Combine(tempDir, "jobs.db");
            var request = CreateRequest(tempDir, "cancel-download");
            var repository = CreateRepository(databasePath);
            var converter = new BlockingConversionService();
            var eventAggregator = new StubEventAggregator();
            var tracking = new ConversionTrackingService(eventAggregator);
            var commandQueue = DispatchProxy.Create<IManageCommandQueue, RecordingCommandQueueProxy>();
            var service = new ConversionJobService(Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestCleanupDisk(), Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestRootFolders(), Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoLibraryMediaFiles(), repository, converter, tracking, commandQueue, CreateConfig(), LogManager.GetCurrentClassLogger());

            try
            {
                service.Handle(new ApplicationStartedEvent());
                service.Enqueue(request);
                Assert.That(converter.Started.Wait(TimeSpan.FromSeconds(2)), Is.True);

                Assert.That(service.Cancel(request.DownloadId, ConversionRemovalOrigin.UserAction), Is.True);
                Assert.That(
                    SpinWait.SpinUntil(
                        () => repository.FindByDownloadId(request.DownloadId)?.Status == ConversionJobStatus.Cancelled,
                        TimeSpan.FromSeconds(2)),
                    Is.True);
            }
            finally
            {
                ShutdownAndWait(service);
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
        }

        [Test]
        public void durable_cancel_should_not_use_disposed_owner_source_or_cancel_replacement_attempt()
        {
            var directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "conversion-stop-owner-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var repository = CreateRepository(Path.Combine(directory, "jobs.db"));
            var request = CreateRequest(directory, "cancel-owner-" + Guid.NewGuid().ToString("N"));
            var converter = new TokenRecordingConversionService();
            var actualTracking = new ConversionTrackingService(new StubEventAggregator());
            using var trackerCancelEntered = new ManualResetEventSlim();
            using var releaseTrackerCancel = new ManualResetEventSlim();
            using var ownerFinished = new ManualResetEventSlim();
            using var replacement = new CancellationTokenSource();
            CancellationTokenSource registered = null;
            var tracking = InterfaceProxy.Proxy<IConversionTrackingService>((method, args) =>
            {
                if (method.Name == nameof(IConversionTrackingService.RegisterCancellation)) registered = (CancellationTokenSource)args[1];
                if (method.Name == nameof(IConversionTrackingService.Cancel))
                {
                    trackerCancelEntered.Set();
                    if (!releaseTrackerCancel.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Tracker cancellation was not released.");
                }
                return method.Invoke(actualTracking, args);
            });
            var queue = InterfaceProxy.Proxy<IManageCommandQueue>((method, args) =>
            {
                if (method.Name != nameof(IManageCommandQueue.Push)) throw new NotSupportedException(method.Name);
                ownerFinished.Set();
                return new CommandModel { Body = (Command)args[0] };
            });
            var service = new ConversionJobService(Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestCleanupDisk(), Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestRootFolders(), Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoLibraryMediaFiles(), repository, converter, tracking, queue, CreateConfig(), LogManager.GetCurrentClassLogger());
            Task<bool> cancel = null;
            try
            {
                service.Handle(new ApplicationStartedEvent());
                service.Enqueue(request);
                Assert.That(SpinWait.SpinUntil(() => converter.OptionsByDownloadId.ContainsKey(request.DownloadId), TimeSpan.FromSeconds(5)), Is.True);
                ownerFinished.Reset();
                cancel = Task.Run(() => service.Cancel(request.DownloadId, ConversionRemovalOrigin.UserAction));
                Assert.That(trackerCancelEntered.Wait(TimeSpan.FromSeconds(5)), Is.True);
                converter.Release.Set();
                Assert.That(ownerFinished.Wait(TimeSpan.FromSeconds(5)), Is.True);
                Assert.That(registered, Is.Not.Null);
                Assert.Throws<ObjectDisposedException>(() => _ = registered.Token);
                actualTracking.Start(request.DownloadId, 12, "M4B", "Retrying conversion");
                actualTracking.RegisterCancellation(request.DownloadId, replacement);
                releaseTrackerCancel.Set();
                Assert.That(cancel.Wait(TimeSpan.FromSeconds(5)), Is.True);
                Assert.That(cancel.Result, Is.True);
                Assert.That(repository.FindByDownloadId(request.DownloadId).Status, Is.EqualTo(ConversionJobStatus.Cancelled));
                Assert.That(replacement.IsCancellationRequested, Is.False);
                Assert.That(actualTracking.Get(request.DownloadId).CanCancel, Is.True);
                Assert.That(actualTracking.Cancel(request.DownloadId), Is.True);
                Assert.That(replacement.IsCancellationRequested, Is.True);
            }
            finally
            {
                converter.Release.Set();
                releaseTrackerCancel.Set();
                if (cancel != null) Assert.That(cancel.Wait(TimeSpan.FromSeconds(5)), Is.True);
                ShutdownAndWait(service);
                actualTracking.Complete(request.DownloadId);
                SqliteConnection.ClearAllPools();
                Directory.Delete(directory, true);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void durable_success_after_accepted_stop_should_be_cancelled(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "conversion-stop-success-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var repository = new ConversionJobRepository(database, new StubEventAggregator());
                var request = CreateRequest(directory, "stop-success-" + Guid.NewGuid().ToString("N"));
                using var started = new ManualResetEventSlim();
                using var finished = new ManualResetEventSlim();
                var converter = InterfaceProxy.Proxy<IM4bConversionService>((method, args) =>
                {
                    if (method.Name != nameof(IM4bConversionService.ConvertToM4b))
                    {
                        throw new NotSupportedException(method.Name);
                    }

                    var options = (ConversionOptions)args[2];
                    started.Set();
                    if (!options.CancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)))
                    {
                        throw new TimeoutException("Stop was not requested.");
                    }

                    var output = (string)args[1];
                    Directory.CreateDirectory(Path.GetDirectoryName(output));
                    File.WriteAllText(output, "constructed successful conversion after accepted Stop");
                    return new ConversionResult { Success = true, OutputFile = output };
                });
                var queue = InterfaceProxy.Proxy<IManageCommandQueue>((method, args) =>
                {
                    if (method.Name != nameof(IManageCommandQueue.Push))
                    {
                        throw new NotSupportedException(method.Name);
                    }

                    finished.Set();
                    return new CommandModel { Body = (Command)args[0] };
                });
                var tracking = new ConversionTrackingService(new StubEventAggregator());
                var service = new ConversionJobService(Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestCleanupDisk(), Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestRootFolders(), Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoLibraryMediaFiles(), repository, converter, tracking, queue, CreateConfig(), LogManager.GetCurrentClassLogger());
                try
                {
                    service.Handle(new ApplicationStartedEvent());
                    service.Enqueue(request);
                    Assert.That(started.Wait(TimeSpan.FromSeconds(5)), Is.True);
                    Assert.That(service.Cancel(request.DownloadId, ConversionRemovalOrigin.UserAction), Is.True);
                    Assert.That(finished.Wait(TimeSpan.FromSeconds(5)), Is.True);
                    Assert.Multiple(() =>
                    {
                        Assert.That(repository.FindByDownloadId(request.DownloadId).Status, Is.EqualTo(ConversionJobStatus.Cancelled));
                        Assert.That(tracking.Get(request.DownloadId).Status, Is.EqualTo("cancelled"));
                        Assert.That(tracking.Cancel(request.DownloadId), Is.False);
                        Assert.That(Directory.Exists(request.WorkRoot), Is.False);
                    });
                }
                finally
                {
                    ShutdownAndWait(service);
                    tracking.Clear(request.DownloadId);
                    Directory.Delete(directory, true);
                }
            });
        }

        [Test]
        public void application_shutdown_should_requeue_active_job_instead_of_persisting_user_cancellation()
        {
            var tempDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-job-shutdown-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            var databasePath = Path.Combine(tempDir, "jobs.db");
            var request = CreateRequest(tempDir, "shutdown-download");
            var repository = CreateRepository(databasePath);
            var converter = new BlockingConversionService();
            var eventAggregator = new StubEventAggregator();
            var tracking = new ConversionTrackingService(eventAggregator);
            var commandQueue = DispatchProxy.Create<IManageCommandQueue, RecordingCommandQueueProxy>();
            var service = new ConversionJobService(Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestCleanupDisk(), Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestRootFolders(), Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoLibraryMediaFiles(), repository, converter, tracking, commandQueue, CreateConfig(), LogManager.GetCurrentClassLogger());

            try
            {
                service.Handle(new ApplicationStartedEvent());
                service.Enqueue(request);
                Assert.That(converter.Started.Wait(TimeSpan.FromSeconds(2)), Is.True);

                service.Handle(new ApplicationShutdownRequested());

                var interrupted = repository.FindByDownloadId(request.DownloadId);
                Assert.That(interrupted.Status, Is.EqualTo(ConversionJobStatus.Queued));
                Assert.That(interrupted.Message, Does.Contain("shutdown"));
            }
            finally
            {
                ShutdownAndWait(service);
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
        }

        [TestCase(ConversionJobStatus.Queued)]
        [TestCase(ConversionJobStatus.ReadyToImport)]
        public void stop_should_cancel_nonrunning_job_without_starting_converter(ConversionJobStatus status)
        {
            var tempDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-job-queued-cancel-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            var request = CreateRequest(tempDir, "queued-cancel-download");
            var repository = CreateRepository(Path.Combine(tempDir, "jobs.db"));
            var service = CreateService(repository, new UnusedConversionService());

            try
            {
                service.Enqueue(request);
                if (status != ConversionJobStatus.Queued)
                {
                    var job = repository.FindByDownloadId(request.DownloadId);
                    job.Status = status;
                    repository.Update(job);
                }

                Assert.That(service.Cancel(request.DownloadId, ConversionRemovalOrigin.UserAction), Is.True);

                var cancelled = repository.FindByDownloadId(request.DownloadId);
                Assert.That(cancelled.Status, Is.EqualTo(ConversionJobStatus.Cancelled));
                Assert.That(Directory.Exists(request.WorkRoot), Is.False);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
        }

        [Test]
        public void startup_should_requeue_conversion_without_valid_artifact_and_run_it()
        {
            var tempDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-job-negative-recovery-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            var request = CreateRequest(tempDir, "negative-recovery-download");
            var repository = CreateRepository(Path.Combine(tempDir, "jobs.db"));
            repository.Insert(CreatePersistedJob(request, ConversionJobStatus.Converting, DateTime.UtcNow.AddMinutes(-5)));
            var converter = new BlockingConversionService();
            var service = CreateService(repository, converter);

            try
            {
                service.Handle(new ApplicationStartedEvent());
                Assert.That(converter.Started.Wait(TimeSpan.FromSeconds(2)), Is.True);

                var recovered = repository.FindByDownloadId(request.DownloadId);
                Assert.That(recovered.Status, Is.EqualTo(ConversionJobStatus.Converting));
                Assert.That(recovered.AttemptCount, Is.EqualTo(1));
            }
            finally
            {
                ShutdownAndWait(service);
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
        }

        [Test]
        public void runtime_scavenger_should_requeue_expired_nonlocal_lease()
        {
            var tempDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-job-runtime-lease-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            var request = CreateRequest(tempDir, "runtime-lease-download");
            var repository = CreateRepository(Path.Combine(tempDir, "jobs.db"));
            repository.Insert(CreatePersistedJob(request, ConversionJobStatus.Converting, DateTime.UtcNow.AddMinutes(-5)));
            var service = CreateService(repository, new UnusedConversionService());

            try
            {
                service.RecoverExpiredLeases();

                var recovered = repository.FindByDownloadId(request.DownloadId);
                Assert.That(recovered.Status, Is.EqualTo(ConversionJobStatus.Queued));
                Assert.That(recovered.Message, Does.Contain("expired"));
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
        }

        [Test]
        public void lone_conversion_should_claim_all_tokens_and_write_shared_manifest_contract()
        {
            var tempDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-job-lone-tokens-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            var request = CreateRequest(tempDir, "lone-token-download");
            var repository = CreateRepository(Path.Combine(tempDir, "jobs.db"));
            var converter = new TokenRecordingConversionService();
            var service = CreateService(repository, converter, concurrentConversions: 2, maxCpuThreads: 8);

            try
            {
                service.Enqueue(request);
                service.Handle(new ApplicationStartedEvent());
                Assert.That(
                    SpinWait.SpinUntil(() => converter.OptionsByDownloadId.ContainsKey(request.DownloadId), TimeSpan.FromSeconds(2)),
                    Is.True);

                var options = converter.OptionsByDownloadId[request.DownloadId];
                Assert.That(options.Jobs, Is.EqualTo(1));
                Assert.That(options.FfmpegThreads, Is.EqualTo(8));

                converter.Release.Set();
                Assert.That(
                    SpinWait.SpinUntil(
                        () => repository.FindByDownloadId(request.DownloadId)?.Status == ConversionJobStatus.ReadyToImport,
                        TimeSpan.FromSeconds(2)),
                    Is.True);

                var manifestPath = Path.Combine(request.WorkFolder, "conversion-artifact.json");
                var manifest = JsonSerializer.Deserialize<ConversionArtifactManifest>(File.ReadAllText(manifestPath));
                var roundTrip = JsonSerializer.Deserialize<ConversionArtifactManifest>(JsonSerializer.Serialize(manifest));
                Assert.That(roundTrip.OutputPath, Is.EqualTo(request.OutputPath));
                Assert.That(roundTrip.Sources.Single().Path, Is.EqualTo(request.Sources.Single().Path));
                Assert.That(roundTrip.TagSignature, Is.EqualTo(request.TagSignature));
            }
            finally
            {
                ShutdownAndWait(service);
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
        }

        [Test]
        public void ready_batch_should_share_available_tokens_fairly()
        {
            var tempDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-job-fair-tokens-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            var first = CreateRequest(tempDir, "fair-token-first");
            var second = CreateRequest(tempDir, "fair-token-second");
            using var timing = new WorkerTiming();
            var repository = CreateRepository(Path.Combine(tempDir, "jobs.db"));
            timing.Record("database created");
            var converter = new TokenRecordingConversionService();
            var service = CreateService(timing.Observe(repository), converter, concurrentConversions: 2, maxCpuThreads: 8, logger: timing.Logger);
            using var started = new CountdownEvent(2);
            converter.OnStarted = id =>
            {
                timing.Record("converter entered " + id);
                started.Signal();
            };

            try
            {
                service.Enqueue(first);
                service.Enqueue(second);
                service.Handle(new ApplicationStartedEvent());
                Assert.That(
                    started.Wait(TimeSpan.FromSeconds(2)),
                    Is.True, timing.Details);

                Assert.That(converter.OptionsByDownloadId[first.DownloadId].FfmpegThreads, Is.EqualTo(4));
                Assert.That(converter.OptionsByDownloadId[second.DownloadId].FfmpegThreads, Is.EqualTo(4));
                Assert.That(
                    converter.OptionsByDownloadId.Values.Sum(options => options.Jobs * options.FfmpegThreads),
                    Is.LessThanOrEqualTo(8));

                timing.Record("converters released");
                converter.Release.Set();
                Assert.That(service.Completion.Workers.Wait(TimeSpan.FromSeconds(2)), Is.True, timing.Details);
                Assert.That(repository.FindByDownloadId(first.DownloadId)?.Status, Is.EqualTo(ConversionJobStatus.ReadyToImport));
                Assert.That(repository.FindByDownloadId(second.DownloadId)?.Status, Is.EqualTo(ConversionJobStatus.ReadyToImport));
            }
            finally
            {
                converter.Release.Set();
                ShutdownAndWait(service);
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
        }

        [Test]
        public void cancelling_active_conversion_should_release_tokens_to_waiter()
        {
            var tempDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-job-cancel-release-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            var first = CreateRequest(tempDir, "cancel-release-first");
            var second = CreateRequest(tempDir, "cancel-release-second");
            var repository = CreateRepository(Path.Combine(tempDir, "jobs.db"));
            var converter = new TokenRecordingConversionService();
            var service = CreateService(repository, converter, concurrentConversions: 2, maxCpuThreads: 8);

            try
            {
                service.Handle(new ApplicationStartedEvent());
                service.Enqueue(first);
                Assert.That(
                    SpinWait.SpinUntil(() => converter.OptionsByDownloadId.ContainsKey(first.DownloadId), TimeSpan.FromSeconds(10)),
                    Is.True);
                Assert.That(converter.OptionsByDownloadId[first.DownloadId].FfmpegThreads, Is.EqualTo(8));

                service.Enqueue(second);
                Assert.That(
                    SpinWait.SpinUntil(() => converter.OptionsByDownloadId.ContainsKey(second.DownloadId), TimeSpan.FromMilliseconds(300)),
                    Is.False,
                    "A late conversion must wait while the running job owns every CPU token.");

                Assert.That(service.Cancel(first.DownloadId, ConversionRemovalOrigin.UserAction), Is.True);
                Assert.That(
                    SpinWait.SpinUntil(() => converter.OptionsByDownloadId.ContainsKey(second.DownloadId), TimeSpan.FromSeconds(10)),
                    Is.True);
                Assert.That(converter.OptionsByDownloadId[second.DownloadId].FfmpegThreads, Is.EqualTo(8));
                Assert.That(service.Cancel(second.DownloadId, ConversionRemovalOrigin.UserAction), Is.True);
            }
            finally
            {
                ShutdownAndWait(service);
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
        }

        [Test]
        public void recovered_job_should_rebuild_staging_from_original_sources_instead_of_legacy_temporary_paths()
        {
            var tempDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-job-legacy-staging-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            var request = CreateRequest(tempDir, "legacy-staging");
            var repository = CreateRepository(Path.Combine(tempDir, "jobs.db"));
            var job = CreatePersistedJob(request, ConversionJobStatus.Converting, DateTime.UtcNow.AddMinutes(-5));
            var json = System.Text.Json.Nodes.JsonNode.Parse(job.RequestJson);
            json["ConversionInputFiles"] = new System.Text.Json.Nodes.JsonArray(Path.Combine(request.WorkFolder, "provider-chapters", "missing.mp3"));
            job.RequestJson = json.ToJsonString();
            repository.Insert(job);
            var converter = new TokenRecordingConversionService();
            var service = CreateService(repository, converter);
            try
            {
                service.Handle(new ApplicationStartedEvent());
                Assert.That(SpinWait.SpinUntil(() => converter.OptionsByDownloadId.ContainsKey(request.DownloadId), TimeSpan.FromSeconds(10)), Is.True);
                Assert.That(converter.InputsByDownloadId[request.DownloadId], Is.EqualTo(request.Sources.Select(source => source.Path)));
                converter.Release.Set();
                Assert.That(SpinWait.SpinUntil(() => repository.FindByDownloadId(request.DownloadId).Status == ConversionJobStatus.ReadyToImport, TimeSpan.FromSeconds(10)), Is.True);
            }
            finally
            {
                ShutdownAndWait(service);
                SqliteConnection.ClearAllPools();
                Directory.Delete(tempDir, true);
            }
        }

        private static ConversionJobRequest CreateRequest(string tempDir, string downloadId)
        {
            var sourcePath = Path.Combine(tempDir, $"{downloadId}.mp3");
            var workRoot = Path.Combine(tempDir, ".chaptarr-conversions", downloadId);
            var workFolder = Path.Combine(workRoot, "work");
            Directory.CreateDirectory(workFolder);
            File.WriteAllText(sourcePath, "source");
            var info = new FileInfo(sourcePath);

            return new ConversionJobRequest
            {
                DownloadId = downloadId,
                DownloadClientId = 0,
                BookTitle = "Test Book",
                WorkRoot = workRoot,
                WorkFolder = workFolder,
                OutputPath = Path.Combine(workFolder, "book.m4b"),
                Sources = new List<ConversionArtifactSource>
                {
                    new()
                    {
                        Path = sourcePath,
                        Size = info.Length,
                        ModifiedUtcTicks = info.LastWriteTimeUtc.Ticks
                    }
                },
                TargetQualityId = 12,
                TargetQualityName = "M4B",
                AudioBitrate = 64,
                TagSignature = "tags",
                TagOptions = new ConversionTagOptions { Mode = "preserve" }
            };
        }
        private static ConversionJob CreatePersistedJob(ConversionJobRequest request, ConversionJobStatus status, DateTime heartbeat)
        {
            return new ConversionJob
            {
                DownloadId = request.DownloadId,
                DownloadClientId = request.DownloadClientId,
                Status = status,
                RequestJson = JsonSerializer.Serialize(request),
                WorkRoot = request.WorkRoot,
                WorkFolder = request.WorkFolder,
                OutputPath = request.OutputPath,
                TargetQualityId = request.TargetQualityId,
                TargetQualityName = request.TargetQualityName,
                Progress = 50m,
                Message = "Converting",
                AttemptCount = 0,
                CreatedAt = DateTime.UtcNow.AddMinutes(-10),
                UpdatedAt = heartbeat,
                StartedAt = DateTime.UtcNow.AddMinutes(-10),
                HeartbeatAt = heartbeat
            };
        }

        internal static ConversionJobService CreateService(
            IConversionJobRepository repository,
            IM4bConversionService converter,
            int concurrentConversions = 1,
            int maxCpuThreads = 8,
            Logger logger = null,
            IConfigService config = null,
            IRootFolderService roots = null,
            NzbDrone.Common.Disk.IDiskProvider disk = null,
            TimeSpan? shutdownWait = null,
            TimeSpan? heartbeatInterval = null)
        {
            var eventAggregator = new StubEventAggregator();
            var tracking = new ConversionTrackingService(eventAggregator);
            var commandQueue = DispatchProxy.Create<IManageCommandQueue, RecordingCommandQueueProxy>();
            return new ConversionJobService(disk ?? TestCleanupDisk(), roots ?? TestRootFolders(), Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoLibraryMediaFiles(),
                repository,
                converter,
                tracking,
                commandQueue,
                config ?? CreateConfig(concurrentConversions, maxCpuThreads),
                logger ?? LogManager.GetCurrentClassLogger(),
                outcomeRetryDelay: TimeSpan.FromSeconds(2), shutdownWait: shutdownWait, heartbeatInterval: heartbeatInterval);
        }

        internal static NzbDrone.Common.Disk.IDiskProvider TestCleanupDisk(Action beforeProbe = null) => InterfaceProxy.Proxy<NzbDrone.Common.Disk.IDiskProvider>((method, args) =>
        {
            beforeProbe?.Invoke();
            return method.Name switch
            {
                nameof(NzbDrone.Common.Disk.IDiskProvider.FolderExists) => Directory.Exists((string)args[0]),
                nameof(NzbDrone.Common.Disk.IDiskProvider.FolderEmpty) => !Directory.EnumerateFileSystemEntries((string)args[0]).Any(),
                _ => throw new NotImplementedException(method.Name)
            };
        });

        internal static IRootFolderService TestRootFolders(params string[] paths)
        {
            var folders = (paths.Length == 0 ? new[] { Path.GetTempPath() } : paths).Select(path => new RootFolder { Path = path }).ToList();
            return new RootFolderService(InterfaceProxy.Proxy<IRootFolderRepository>((method, _) =>
                method.Name == nameof(IRootFolderRepository.All) ? folders : throw new NotImplementedException(method.Name)),
                null, null, null, LogManager.GetCurrentClassLogger());
        }

        private static IConfigService CreateConfig(int concurrentConversions = 1, int maxCpuThreads = 8)
        {
            var config = DispatchProxy.Create<IConfigService, ConfigServiceProxy>();
            var proxy = (ConfigServiceProxy)(object)config;
            proxy.ConcurrentConversions = concurrentConversions;
            proxy.MaxCpuThreads = maxCpuThreads;
            return config;
        }


        internal static ConversionJobRepository CreateRepository(string databasePath)
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString();

            // Keep the small fixture schema, but use production connection initialization:
            // rollback-journal/FULL defaults serialize readers and flush every write on Windows.
            var connectionInfo = new DatabaseConnectionInfo(DatabaseType.SQLite, connectionString);
            var factory = new DbFactory(
                InterfaceProxy.Proxy<IMigrationController>((_, _) => null),
                InterfaceProxy.Proxy<IConnectionStringFactory>((_, _) => connectionInfo), null);
            var database = factory.Create();
            using (var connection = database.OpenConnection())
            {
                connection.Execute(@"
CREATE TABLE ""DownloadClients"" (""Id"" INTEGER PRIMARY KEY);
CREATE TABLE ""History"" (""Id"" INTEGER PRIMARY KEY, ""DownloadId"" TEXT, ""EventType"" INTEGER);
CREATE TABLE ""DownloadHistory"" (""Id"" INTEGER PRIMARY KEY, ""DownloadId"" TEXT, ""DownloadClientId"" INTEGER, ""EventType"" INTEGER, ""Date"" TEXT);
CREATE TABLE ""ConversionJobs"" (
    ""Id"" INTEGER PRIMARY KEY AUTOINCREMENT,
    ""DownloadId"" TEXT NOT NULL UNIQUE,
    ""DownloadIdKey"" TEXT,
    ""DownloadClientId"" INTEGER NOT NULL DEFAULT 0,
    ""Status"" INTEGER NOT NULL,
    ""RequestJson"" TEXT NOT NULL,
    ""CleanupPending"" INTEGER NOT NULL DEFAULT 0,
    ""CleanupAttention"" INTEGER NOT NULL DEFAULT 0,
    ""WorkRoot"" TEXT,
    ""WorkFolder"" TEXT,
    ""OutputPath"" TEXT,
    ""TargetQualityId"" INTEGER NOT NULL,
    ""TargetQualityName"" TEXT,
    ""Progress"" NUMERIC,
    ""Message"" TEXT,
    ""Error"" TEXT,
    ""AttemptCount"" INTEGER NOT NULL DEFAULT 0,
    ""CreatedAt"" TEXT NOT NULL,
    ""UpdatedAt"" TEXT NOT NULL,
    ""StartedAt"" TEXT,
    ""HeartbeatAt"" TEXT,
    ""CompletedAt"" TEXT
);");
            }

            return new ConversionJobRepository(new MainDatabase(database), new StubEventAggregator());
        }
    }
}
