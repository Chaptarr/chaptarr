using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using Dapper;
using Microsoft.Data.Sqlite;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class ConversionJobServiceFixture
    {
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
            public ManualResetEventSlim Release { get; } = new(false);

            public ConversionResult ConvertToM4b(string[] inputFiles, string outputFile, ConversionOptions options = null)
            {
                var workFolder = Directory.GetParent(Path.GetDirectoryName(outputFile));
                var downloadId = workFolder?.Name ?? outputFile;
                OptionsByDownloadId[downloadId] = options;

                WaitHandle.WaitAny(new[] { options.CancellationToken.WaitHandle, Release.WaitHandle });
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

        private class RecordingCommandQueueProxy : DispatchProxy
        {
            public List<Command> Commands { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IManageCommandQueue.Push) && args?.FirstOrDefault() is Command command)
                {
                    Commands.Add(command);
                    return new CommandModel
                    {
                        Id = Commands.Count,
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
            public int ConcurrentConversions { get; set; } = 1;
            public int MaxCpuThreads { get; set; } = 8;

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    "get_AudiobookConversionConcurrentConversions" => ConcurrentConversions,
                    "get_AudiobookConversionMaxCpuThreads" => MaxCpuThreads,
                    "set_AudiobookConversionConcurrentConversions" => SetConcurrentConversions((int)args[0]),
                    "set_AudiobookConversionMaxCpuThreads" => SetMaxCpuThreads((int)args[0]),
                    _ => throw new NotImplementedException($"Unexpected config call: {targetMethod?.Name}")
                };
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
                    ConversionInputFiles = new List<string> { sourcePath },
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
                var service = new ConversionJobService(
                    repository,
                    new UnusedConversionService(),
                    tracking,
                    commandQueue,
                    CreateConfig(),
                    LogManager.GetCurrentClassLogger());

                service.Handle(new ApplicationStartedEvent());
                try
                {
                    var recovered = repository.FindByDownloadId(request.DownloadId);
                    Assert.That(recovered.Status, Is.EqualTo(ConversionJobStatus.ReadyToImport));
                    Assert.That(recovered.Progress, Is.EqualTo(98m));
                    Assert.That(commandRecorder.Commands, Has.Some.InstanceOf<ProcessMonitoredDownloadsCommand>());
                }
                finally
                {
                    service.Handle(new ApplicationShutdownRequested());
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
            var service = new ConversionJobService(repository, converter, tracking, commandQueue, CreateConfig(), LogManager.GetCurrentClassLogger());

            try
            {
                service.Handle(new ApplicationStartedEvent());
                service.Enqueue(request);
                Assert.That(converter.Started.Wait(TimeSpan.FromSeconds(2)), Is.True);

                Assert.That(service.Cancel(request.DownloadId), Is.True);
                Assert.That(
                    SpinWait.SpinUntil(
                        () => repository.FindByDownloadId(request.DownloadId)?.Status == ConversionJobStatus.Cancelled,
                        TimeSpan.FromSeconds(2)),
                    Is.True);
            }
            finally
            {
                service.Handle(new ApplicationShutdownRequested());
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
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
            var service = new ConversionJobService(repository, converter, tracking, commandQueue, CreateConfig(), LogManager.GetCurrentClassLogger());

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
                service.Handle(new ApplicationShutdownRequested());
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

                Assert.That(service.Cancel(request.DownloadId), Is.True);

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
                service.Handle(new ApplicationShutdownRequested());
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
                service.Handle(new ApplicationShutdownRequested());
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
            var repository = CreateRepository(Path.Combine(tempDir, "jobs.db"));
            var converter = new TokenRecordingConversionService();
            var service = CreateService(repository, converter, concurrentConversions: 2, maxCpuThreads: 8);

            try
            {
                service.Enqueue(first);
                service.Enqueue(second);
                service.Handle(new ApplicationStartedEvent());
                Assert.That(
                    SpinWait.SpinUntil(() => converter.OptionsByDownloadId.Count == 2, TimeSpan.FromSeconds(2)),
                    Is.True);

                Assert.That(converter.OptionsByDownloadId[first.DownloadId].FfmpegThreads, Is.EqualTo(4));
                Assert.That(converter.OptionsByDownloadId[second.DownloadId].FfmpegThreads, Is.EqualTo(4));
                Assert.That(
                    converter.OptionsByDownloadId.Values.Sum(options => options.Jobs * options.FfmpegThreads),
                    Is.LessThanOrEqualTo(8));

                converter.Release.Set();
                Assert.That(
                    SpinWait.SpinUntil(
                        () => repository.FindByDownloadId(first.DownloadId)?.Status == ConversionJobStatus.ReadyToImport &&
                              repository.FindByDownloadId(second.DownloadId)?.Status == ConversionJobStatus.ReadyToImport,
                        TimeSpan.FromSeconds(2)),
                    Is.True);
            }
            finally
            {
                service.Handle(new ApplicationShutdownRequested());
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
                    SpinWait.SpinUntil(() => converter.OptionsByDownloadId.ContainsKey(first.DownloadId), TimeSpan.FromSeconds(2)),
                    Is.True);
                Assert.That(converter.OptionsByDownloadId[first.DownloadId].FfmpegThreads, Is.EqualTo(8));

                service.Enqueue(second);
                Assert.That(
                    SpinWait.SpinUntil(() => converter.OptionsByDownloadId.ContainsKey(second.DownloadId), TimeSpan.FromMilliseconds(300)),
                    Is.False,
                    "A late conversion must wait while the running job owns every CPU token.");

                Assert.That(service.Cancel(first.DownloadId), Is.True);
                Assert.That(
                    SpinWait.SpinUntil(() => converter.OptionsByDownloadId.ContainsKey(second.DownloadId), TimeSpan.FromSeconds(2)),
                    Is.True);
                Assert.That(converter.OptionsByDownloadId[second.DownloadId].FfmpegThreads, Is.EqualTo(8));
                Assert.That(service.Cancel(second.DownloadId), Is.True);
            }
            finally
            {
                service.Handle(new ApplicationShutdownRequested());
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
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
                BookTitle = "Test Book",
                WorkRoot = workRoot,
                WorkFolder = workFolder,
                OutputPath = Path.Combine(workFolder, "book.m4b"),
                ConversionInputFiles = new List<string> { sourcePath },
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

        private static ConversionJobService CreateService(
            IConversionJobRepository repository,
            IM4bConversionService converter,
            int concurrentConversions = 1,
            int maxCpuThreads = 8)
        {
            var eventAggregator = new StubEventAggregator();
            var tracking = new ConversionTrackingService(eventAggregator);
            var commandQueue = DispatchProxy.Create<IManageCommandQueue, RecordingCommandQueueProxy>();
            return new ConversionJobService(
                repository,
                converter,
                tracking,
                commandQueue,
                CreateConfig(concurrentConversions, maxCpuThreads),
                LogManager.GetCurrentClassLogger());
        }

        private static IConfigService CreateConfig(int concurrentConversions = 1, int maxCpuThreads = 8)
        {
            var config = DispatchProxy.Create<IConfigService, ConfigServiceProxy>();
            var proxy = (ConfigServiceProxy)(object)config;
            proxy.ConcurrentConversions = concurrentConversions;
            proxy.MaxCpuThreads = maxCpuThreads;
            return config;
        }


        private static ConversionJobRepository CreateRepository(string databasePath)
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString();

            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                connection.Execute(@"
CREATE TABLE ""ConversionJobs"" (
    ""Id"" INTEGER PRIMARY KEY AUTOINCREMENT,
    ""DownloadId"" TEXT NOT NULL UNIQUE,
    ""Status"" INTEGER NOT NULL,
    ""RequestJson"" TEXT NOT NULL,
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

            var database = new Database("main", () =>
            {
                var connection = new SqliteConnection(connectionString);
                connection.Open();
                return connection;
            });

            return new ConversionJobRepository(new MainDatabase(database), new StubEventAggregator());
        }
    }
}
