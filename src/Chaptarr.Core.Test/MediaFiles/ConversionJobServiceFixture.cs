using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
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
            public M4bMergeSourceInfo ProbeMergeSources(string[] inputFiles) => new();
        }

        private sealed class TestAppFolderInfo : IAppFolderInfo
        {
            public string AppDataFolder { get; init; }
            public string StartUpFolder { get; init; }
            public string TempFolder => Path.GetTempPath();
        }

        private class RealToolDiskProviderProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                var path = args?.FirstOrDefault() as string;
                return targetMethod?.Name switch
                {
                    nameof(IDiskProvider.FileExists) => File.Exists(path),
                    nameof(IDiskProvider.GetFileInfo) => new System.IO.Abstractions.FileSystem().FileInfo.New(path),
                    nameof(IDiskProvider.GetFileSize) => new FileInfo(path).Length,
                    nameof(IDiskProvider.FileGetLastWrite) => File.GetLastWriteTimeUtc(path),
                    _ => throw new NotImplementedException($"Unexpected disk-provider call: {targetMethod?.Name}")
                };
            }
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
            public M4bMergeSourceInfo ProbeMergeSources(string[] inputFiles) => new();
        }

        private sealed class TokenRecordingConversionService : IM4bConversionService
        {
            public ConcurrentDictionary<string, ConversionOptions> OptionsByDownloadId { get; } = new(StringComparer.OrdinalIgnoreCase);
            public ManualResetEventSlim Release { get; } = new(false);

            public ConversionResult ConvertToM4b(string[] inputFiles, string outputFile, ConversionOptions options = null)
            {
                var directory = new DirectoryInfo(options?.TempDirectory ?? Path.GetDirectoryName(outputFile));
                while (directory?.Parent != null &&
                       !directory.Parent.Name.Equals(".chaptarr-conversions", StringComparison.OrdinalIgnoreCase))
                {
                    directory = directory.Parent;
                }

                var downloadId = directory?.Name ?? outputFile;
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
            public M4bMergeSourceInfo ProbeMergeSources(string[] inputFiles) => new();
        }

        private sealed class RetainableFailureConversionService : IM4bConversionService
        {
            public ConversionResult ConvertToM4b(string[] inputFiles, string outputFile, ConversionOptions options = null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outputFile));
                File.WriteAllText(outputFile, "readable but tool-reported failure");
                return new ConversionResult
                {
                    Success = false,
                    RetainOutputOnFailure = true,
                    ErrorMessage = "synthetic tool failure"
                };
            }

            public bool CanConvert(string[] inputFiles) => true;
            public ConversionEstimate EstimateConversion(string[] inputFiles) => new() { CanConvert = true };
            public M4bMergeSourceInfo ProbeMergeSources(string[] inputFiles) => new();
        }

        private sealed class MissingOutputSuccessConversionService : IM4bConversionService
        {
            public ConversionResult ConvertToM4b(string[] inputFiles, string outputFile, ConversionOptions options = null)
                => new() { Success = true };

            public bool CanConvert(string[] inputFiles) => true;
            public ConversionEstimate EstimateConversion(string[] inputFiles) => new() { CanConvert = true };
            public M4bMergeSourceInfo ProbeMergeSources(string[] inputFiles) => new();
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

        private class ThrowOnceNonCompletedRepositoryProxy : DispatchProxy
        {
            public IConversionJobRepository Inner { get; set; }
            public int FailOnNonCompletedCall { get; set; }

            private int _nonCompletedCalls;

            public int NonCompletedCalls => Volatile.Read(ref _nonCompletedCalls);

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IConversionJobRepository.NonCompleted))
                {
                    var call = Interlocked.Increment(ref _nonCompletedCalls);
                    if (call == FailOnNonCompletedCall)
                    {
                        throw new InvalidOperationException("Synthetic transient conversion repository failure");
                    }
                }

                return targetMethod.Invoke(Inner, args);
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
                    Version = ConversionArtifactManifest.CurrentVersion,
                    Importable = true,
                    CreatedUtc = DateTime.UtcNow,
                    OutputPath = outputPath,
                    OutputSize = new FileInfo(outputPath).Length,
                    OutputModifiedUtcTicks = new FileInfo(outputPath).LastWriteTimeUtc.Ticks,
                    OutputSha256 = ConversionArtifactFingerprint.ComputeSha256(outputPath),
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
                    Progress = 0m,
                    Message = "Converting",
                    CreatedAt = DateTime.UtcNow.AddMinutes(-10),
                    UpdatedAt = DateTime.UtcNow.AddMinutes(-5),
                    StartedAt = DateTime.UtcNow.AddMinutes(-10),
                    HeartbeatAt = DateTime.UtcNow.AddMinutes(-5)
                });

                var connectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                    Mode = SqliteOpenMode.ReadWrite
                }.ToString();
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    foreach (var progress in new[] { 0, 1, 100 })
                    {
                        connection.Execute(
                            "UPDATE ConversionJobs SET Progress = @Progress WHERE DownloadId = @DownloadId",
                            new { request.DownloadId, Progress = progress });
                        Assert.That(
                            connection.ExecuteScalar<string>("SELECT typeof(Progress) FROM ConversionJobs WHERE DownloadId = @DownloadId", new { request.DownloadId }),
                            Is.EqualTo("integer"));
                        Assert.That(repository.NonCompleted().Single().Progress, Is.EqualTo((decimal)progress));
                    }

                    connection.Execute("UPDATE ConversionJobs SET Progress = 0 WHERE DownloadId = @DownloadId", new { request.DownloadId });
                }

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
        [NonParallelizable]
        public void real_m4b_tool_should_resume_after_application_restart_interrupts_conversion_when_installed()
        {
            if (!OperatingSystem.IsLinux())
            {
                Assert.Ignore("The pinned process-interruption smoke is run in the Linux M4B tool image.");
            }

            var tempDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-job-real-restart-{Guid.NewGuid():N}");
            var sourceDir = Path.Combine(tempDir, "source");
            var toolDir = Path.Combine(tempDir, "tool");
            var databasePath = Path.Combine(tempDir, "jobs.db");
            var gatePath = Path.Combine(tempDir, "ffmpeg-started");
            var releasePath = Path.Combine(tempDir, "ffmpeg-release");
            var wrapperPath = Path.Combine(toolDir, "ffmpeg");
            var originalFfmpeg = Environment.GetEnvironmentVariable(BookshelfM4bCompatibility.FfmpegPathEnvironmentVariable);
            var originalGate = Environment.GetEnvironmentVariable("CHAPTARRNG_M4B_TEST_GATE");
            var originalRelease = Environment.GetEnvironmentVariable("CHAPTARRNG_M4B_TEST_RELEASE");
            ConversionJobService firstService = null;
            ConversionJobService restartedService = null;

            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(toolDir);

            try
            {
                var ffmpegPath = "/usr/bin/ffmpeg";
                if (!File.Exists(ffmpegPath) || !File.Exists("/usr/bin/ffprobe") || !File.Exists("/usr/local/bin/m4b-tool"))
                {
                    Assert.Ignore("This smoke requires the pinned FFmpeg, FFprobe, PHP, m4b-tool, and MP4v2 tool image.");
                }

                var firstTrack = Path.Combine(sourceDir, "01 Opening.mp3");
                var secondTrack = Path.Combine(sourceDir, "02 The Middle.mp3");
                var coverPath = Path.Combine(tempDir, "cover.jpg");
                CreateSyntheticMp3(firstTrack, frequency: 330, title: "Opening", track: 1);
                CreateSyntheticMp3(secondTrack, frequency: 440, title: "The Middle", track: 2);
                CreateSyntheticCover(coverPath);

                File.WriteAllText(wrapperPath,
                    "#!/bin/sh\n" +
                    "for arg in \"$@\"; do\n" +
                    "  if [ \"$arg\" = \"-version\" ] || [ \"$arg\" = \"-buildconf\" ] || [ \"$arg\" = \"0:v:0\" ]; then\n" +
                    "    exec /usr/bin/ffmpeg \"$@\"\n" +
                    "  fi\n" +
                    "done\n" +
                    "printf '%s\\n' \"$$\" > \"$CHAPTARRNG_M4B_TEST_GATE\"\n" +
                    "while [ ! -f \"$CHAPTARRNG_M4B_TEST_RELEASE\" ]; do sleep 0.05; done\n" +
                    "exec /usr/bin/ffmpeg \"$@\"\n");
                File.SetUnixFileMode(wrapperPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                Environment.SetEnvironmentVariable(BookshelfM4bCompatibility.FfmpegPathEnvironmentVariable, wrapperPath);
                Environment.SetEnvironmentVariable("CHAPTARRNG_M4B_TEST_GATE", gatePath);
                Environment.SetEnvironmentVariable("CHAPTARRNG_M4B_TEST_RELEASE", releasePath);

                var externalTools = new ExternalToolsService(
                    CreateConfig(),
                    DispatchProxy.Create<IDiskProvider, RealToolDiskProviderProxy>(),
                    new TestAppFolderInfo { AppDataFolder = tempDir, StartUpFolder = TestContext.CurrentContext.TestDirectory },
                    LogManager.GetLogger("ConversionJobRealRestartFixture"));
                if (!externalTools.IsFFmpegAvailable() || !externalTools.IsFFprobeAvailable() || !externalTools.IsM4bToolAvailable())
                {
                    Assert.Ignore("The required real conversion tools are unavailable.");
                }

                var request = CreateRealToolRequest(tempDir, "restart-download", coverPath, firstTrack, secondTrack);
                var repository = CreateRepository(databasePath);
                firstService = CreateService(repository, new M4bConversionService(
                    externalTools,
                    DispatchProxy.Create<IDiskProvider, RealToolDiskProviderProxy>(),
                    CreateConfig(),
                    LogManager.GetLogger("ConversionJobRealRestartFixture")));
                firstService.Enqueue(request);
                firstService.Handle(new ApplicationStartedEvent());

                WaitFor(() => File.Exists(gatePath), TimeSpan.FromSeconds(60), "m4b-tool did not reach the FFmpeg interruption gate.");
                var interruptedJob = repository.FindByDownloadId(request.DownloadId);
                Assert.That(interruptedJob.Status, Is.EqualTo(ConversionJobStatus.Converting));
                Assert.That(File.Exists(request.OutputPath), Is.False, "Attempt output must not be promoted before a successful conversion.");

                firstService.Handle(new ApplicationShutdownRequested());
                var shutdownRecovered = SpinWait.SpinUntil(
                    () => repository.FindByDownloadId(request.DownloadId)?.Status == ConversionJobStatus.Queued,
                    TimeSpan.FromSeconds(10));
                if (!shutdownRecovered)
                {
                    var jobAfterShutdown = repository.FindByDownloadId(request.DownloadId);
                    Assert.Fail($"Application shutdown did not requeue the interrupted conversion (status={jobAfterShutdown?.Status}, error={jobAfterShutdown?.Error}, message={jobAfterShutdown?.Message}).");
                }

                var interruptedPid = int.Parse(File.ReadAllText(gatePath).Trim());
                WaitFor(() => HasExited(interruptedPid), TimeSpan.FromSeconds(10), "The interrupted FFmpeg process was left running after shutdown.");
                var requeuedJob = repository.FindByDownloadId(request.DownloadId);
                Assert.That(requeuedJob.AttemptCount, Is.EqualTo(1));
                Assert.That(File.Exists(request.OutputPath), Is.False);
                Assert.That(File.Exists(Path.Combine(request.WorkFolder, "conversion-artifact.json")), Is.False);

                File.WriteAllText(releasePath, "continue");
                var restartedRepository = CreateRepository(databasePath);
                var restartedTools = new ExternalToolsService(
                    CreateConfig(),
                    DispatchProxy.Create<IDiskProvider, RealToolDiskProviderProxy>(),
                    new TestAppFolderInfo { AppDataFolder = tempDir, StartUpFolder = TestContext.CurrentContext.TestDirectory },
                    LogManager.GetLogger("ConversionJobRealRestartFixture"));
                restartedService = CreateService(restartedRepository, new M4bConversionService(
                    restartedTools,
                    DispatchProxy.Create<IDiskProvider, RealToolDiskProviderProxy>(),
                    CreateConfig(),
                    LogManager.GetLogger("ConversionJobRealRestartFixture")));
                restartedService.Handle(new ApplicationStartedEvent());

                WaitFor(
                    () => restartedRepository.FindByDownloadId(request.DownloadId)?.Status == ConversionJobStatus.ReadyToImport,
                    TimeSpan.FromMinutes(2),
                    "The real M4B conversion did not finish after the worker restarted.");

                var resumedJob = restartedRepository.FindByDownloadId(request.DownloadId);
                Assert.That(resumedJob.AttemptCount, Is.EqualTo(2));
                Assert.That(File.Exists(request.OutputPath), Is.True);
                Assert.That(new FileInfo(request.OutputPath).Length, Is.GreaterThan(32 * 1024));
                Assert.That(File.Exists(firstTrack), Is.True);
                Assert.That(File.Exists(secondTrack), Is.True);

                var manifestPath = Path.Combine(request.WorkFolder, "conversion-artifact.json");
                var manifest = JsonSerializer.Deserialize<ConversionArtifactManifest>(File.ReadAllText(manifestPath));
                Assert.That(manifest.Importable, Is.True);
                Assert.That(manifest.OutputSha256, Is.EqualTo(ConversionArtifactFingerprint.ComputeSha256(request.OutputPath)));
            }
            finally
            {
                try
                {
                    if (Directory.Exists(toolDir))
                    {
                        File.WriteAllText(releasePath, "cleanup");
                    }
                }
                catch
                {
                }
                try { firstService?.Handle(new ApplicationShutdownRequested()); } catch { }
                try { restartedService?.Handle(new ApplicationShutdownRequested()); } catch { }
                Environment.SetEnvironmentVariable(BookshelfM4bCompatibility.FfmpegPathEnvironmentVariable, originalFfmpeg);
                Environment.SetEnvironmentVariable("CHAPTARRNG_M4B_TEST_GATE", originalGate);
                Environment.SetEnvironmentVariable("CHAPTARRNG_M4B_TEST_RELEASE", originalRelease);
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
        }

        [Test]
        [NonParallelizable]
        public void real_m4b_tool_should_recover_after_conversion_host_process_is_terminated_when_installed()
        {
            if (!OperatingSystem.IsLinux())
            {
                Assert.Ignore("The pinned process-termination smoke is run in the Linux M4B tool image.");
            }

            var tempDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-job-process-crash-{Guid.NewGuid():N}");
            var sourceDir = Path.Combine(tempDir, "source");
            var toolDir = Path.Combine(tempDir, "tool");
            var databasePath = Path.Combine(tempDir, "jobs.db");
            var wrapperPath = Path.Combine(toolDir, "ffmpeg");
            var gatePath = Path.Combine(tempDir, "ffmpeg-started");
            var releasePath = Path.Combine(tempDir, "ffmpeg-release");
            var workerReadyPath = Path.Combine(tempDir, "conversion-host-ready");
            var originalFfmpeg = Environment.GetEnvironmentVariable(BookshelfM4bCompatibility.FfmpegPathEnvironmentVariable);
            var originalGate = Environment.GetEnvironmentVariable("CHAPTARRNG_M4B_TEST_GATE");
            var originalRelease = Environment.GetEnvironmentVariable("CHAPTARRNG_M4B_TEST_RELEASE");
            Process workerProcess = null;
            ConversionJobService restartedService = null;
            var interruptedPid = 0;

            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(toolDir);

            try
            {
                if (!File.Exists("/usr/bin/ffmpeg") || !File.Exists("/usr/bin/ffprobe") || !File.Exists("/usr/local/bin/m4b-tool"))
                {
                    Assert.Ignore("This smoke requires the pinned FFmpeg, FFprobe, PHP, m4b-tool, and MP4v2 tool image.");
                }

                var firstTrack = Path.Combine(sourceDir, "01 Opening.mp3");
                var secondTrack = Path.Combine(sourceDir, "02 The Middle.mp3");
                var coverPath = Path.Combine(tempDir, "cover.jpg");
                CreateSyntheticMp3(firstTrack, frequency: 330, title: "Opening", track: 1);
                CreateSyntheticMp3(secondTrack, frequency: 440, title: "The Middle", track: 2);
                CreateSyntheticCover(coverPath);
                CreateFfmpegGateWrapper(wrapperPath, gatePath, releasePath);

                var testAssembly = Path.Combine(TestContext.CurrentContext.TestDirectory, "Chaptarr.Core.Test.dll");
                Assert.That(File.Exists(testAssembly), Is.True, $"Nested process test assembly was not found at {testAssembly}.");

                var workerStartInfo = new ProcessStartInfo("dotnet")
                {
                    WorkingDirectory = TestContext.CurrentContext.TestDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                workerStartInfo.ArgumentList.Add("vstest");
                workerStartInfo.ArgumentList.Add(testAssembly);
                workerStartInfo.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName~crash_worker_should_hold_real_conversion_until_parent_terminates_process");
                workerStartInfo.Environment["CHAPTARRNG_M4B_CRASH_TEST_ROOT"] = tempDir;
                workerStartInfo.Environment["CHAPTARRNG_M4B_CRASH_TEST_READY"] = workerReadyPath;
                workerStartInfo.Environment["CHAPTARRNG_M4B_CRASH_TEST_GATE"] = gatePath;
                workerStartInfo.Environment["CHAPTARRNG_M4B_CRASH_TEST_RELEASE"] = releasePath;
                workerStartInfo.Environment["CHAPTARRNG_M4B_CRASH_TEST_FFMPEG"] = wrapperPath;

                var childOutput = new ConcurrentQueue<string>();
                workerProcess = Process.Start(workerStartInfo);
                Assert.That(workerProcess, Is.Not.Null, "The isolated conversion host process did not start.");
                workerProcess.OutputDataReceived += (_, eventArgs) =>
                {
                    if (eventArgs.Data != null)
                    {
                        childOutput.Enqueue(eventArgs.Data);
                    }
                };
                workerProcess.ErrorDataReceived += (_, eventArgs) =>
                {
                    if (eventArgs.Data != null)
                    {
                        childOutput.Enqueue(eventArgs.Data);
                    }
                };
                workerProcess.BeginOutputReadLine();
                workerProcess.BeginErrorReadLine();

                WaitFor(
                    () => File.Exists(workerReadyPath) || workerProcess.HasExited,
                    TimeSpan.FromMinutes(2),
                    "The isolated conversion host did not reach the real FFmpeg gate.");

                if (!File.Exists(workerReadyPath))
                {
                    Assert.Fail($"The isolated conversion host exited before reporting readiness (exit={workerProcess.ExitCode}). {string.Join(Environment.NewLine, childOutput)}");
                }

                Assert.That(File.ReadAllText(workerReadyPath), Is.EqualTo("Converting|1"));
                Assert.That(File.Exists(gatePath), Is.True, "The worker must be blocked inside real FFmpeg before it is terminated.");
                interruptedPid = int.Parse(File.ReadAllText(gatePath).Trim());

                workerProcess.Kill(entireProcessTree: true);
                Assert.That(workerProcess.WaitForExit(15000), Is.True, "The isolated conversion host process tree did not terminate.");
                WaitFor(() => HasExited(interruptedPid), TimeSpan.FromSeconds(10), "FFmpeg survived abrupt termination of its conversion host process.");

                Assert.That(File.Exists(releasePath), Is.False, "The conversion must be interrupted by process termination, not a graceful release signal.");
                Assert.That(File.Exists(Path.Combine(tempDir, ".chaptarr-conversions", "crash-download", "work", "book.m4b")), Is.False);
                Assert.That(File.Exists(Path.Combine(tempDir, ".chaptarr-conversions", "crash-download", "work", "conversion-artifact.json")), Is.False);

                var restartedRepository = CreateRepository(databasePath);
                var interruptedJob = restartedRepository.FindByDownloadId("crash-download");
                Assert.That(interruptedJob, Is.Not.Null);
                Assert.That(interruptedJob.Status, Is.EqualTo(ConversionJobStatus.Converting), "A hard process stop must leave the durable in-flight state for startup reconciliation.");
                Assert.That(interruptedJob.AttemptCount, Is.EqualTo(1));

                Environment.SetEnvironmentVariable(BookshelfM4bCompatibility.FfmpegPathEnvironmentVariable, wrapperPath);
                Environment.SetEnvironmentVariable("CHAPTARRNG_M4B_TEST_GATE", gatePath);
                Environment.SetEnvironmentVariable("CHAPTARRNG_M4B_TEST_RELEASE", releasePath);
                File.Delete(gatePath);
                File.WriteAllText(releasePath, "continue");

                var restartedTools = new ExternalToolsService(
                    CreateConfig(),
                    DispatchProxy.Create<IDiskProvider, RealToolDiskProviderProxy>(),
                    new TestAppFolderInfo { AppDataFolder = tempDir, StartUpFolder = TestContext.CurrentContext.TestDirectory },
                    LogManager.GetLogger("ConversionJobProcessCrashFixture"));
                restartedService = CreateService(restartedRepository, new M4bConversionService(
                    restartedTools,
                    DispatchProxy.Create<IDiskProvider, RealToolDiskProviderProxy>(),
                    CreateConfig(),
                    LogManager.GetLogger("ConversionJobProcessCrashFixture")));
                restartedService.Handle(new ApplicationStartedEvent());

                WaitFor(
                    () => restartedRepository.FindByDownloadId("crash-download")?.Status == ConversionJobStatus.ReadyToImport,
                    TimeSpan.FromMinutes(2),
                    "The durable conversion did not recover after its host process was terminated.");

                var resumedJob = restartedRepository.FindByDownloadId("crash-download");
                var outputPath = Path.Combine(tempDir, ".chaptarr-conversions", "crash-download", "work", "book.m4b");
                var manifestPath = Path.Combine(tempDir, ".chaptarr-conversions", "crash-download", "work", "conversion-artifact.json");
                var manifest = JsonSerializer.Deserialize<ConversionArtifactManifest>(File.ReadAllText(manifestPath));
                Assert.That(resumedJob.AttemptCount, Is.EqualTo(2));
                Assert.That(File.Exists(outputPath), Is.True);
                Assert.That(new FileInfo(outputPath).Length, Is.GreaterThan(32 * 1024));
                Assert.That(manifest.Importable, Is.True);
                Assert.That(manifest.OutputSha256, Is.EqualTo(ConversionArtifactFingerprint.ComputeSha256(outputPath)));
            }
            finally
            {
                if (workerProcess != null)
                {
                    try
                    {
                        if (!workerProcess.HasExited)
                        {
                            workerProcess.Kill(entireProcessTree: true);
                            workerProcess.WaitForExit(15000);
                        }
                    }
                    catch
                    {
                    }
                    workerProcess.Dispose();
                }

                try { restartedService?.Handle(new ApplicationShutdownRequested()); } catch { }
                if (interruptedPid > 0)
                {
                    WaitFor(() => HasExited(interruptedPid), TimeSpan.FromSeconds(10), "The interrupted FFmpeg process remained after test cleanup.");
                }

                Environment.SetEnvironmentVariable(BookshelfM4bCompatibility.FfmpegPathEnvironmentVariable, originalFfmpeg);
                Environment.SetEnvironmentVariable("CHAPTARRNG_M4B_TEST_GATE", originalGate);
                Environment.SetEnvironmentVariable("CHAPTARRNG_M4B_TEST_RELEASE", originalRelease);
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
        }

        [Test]
        [NonParallelizable]
        public void crash_worker_should_hold_real_conversion_until_parent_terminates_process()
        {
            var tempDir = Environment.GetEnvironmentVariable("CHAPTARRNG_M4B_CRASH_TEST_ROOT");
            var readyPath = Environment.GetEnvironmentVariable("CHAPTARRNG_M4B_CRASH_TEST_READY");
            var gatePath = Environment.GetEnvironmentVariable("CHAPTARRNG_M4B_CRASH_TEST_GATE");
            var releasePath = Environment.GetEnvironmentVariable("CHAPTARRNG_M4B_CRASH_TEST_RELEASE");
            var ffmpegPath = Environment.GetEnvironmentVariable("CHAPTARRNG_M4B_CRASH_TEST_FFMPEG");
            if (string.IsNullOrWhiteSpace(tempDir) || string.IsNullOrWhiteSpace(readyPath) || string.IsNullOrWhiteSpace(gatePath) ||
                string.IsNullOrWhiteSpace(releasePath) || string.IsNullOrWhiteSpace(ffmpegPath))
            {
                Assert.Ignore("This worker is started only by the abrupt conversion-host recovery test.");
            }

            if (!File.Exists("/usr/bin/ffmpeg") || !File.Exists("/usr/bin/ffprobe") || !File.Exists("/usr/local/bin/m4b-tool"))
            {
                Assert.Ignore("This worker requires the pinned FFmpeg, FFprobe, PHP, m4b-tool, and MP4v2 tool image.");
            }

            Environment.SetEnvironmentVariable(BookshelfM4bCompatibility.FfmpegPathEnvironmentVariable, ffmpegPath);
            Environment.SetEnvironmentVariable("CHAPTARRNG_M4B_TEST_GATE", gatePath);
            Environment.SetEnvironmentVariable("CHAPTARRNG_M4B_TEST_RELEASE", releasePath);

            var databasePath = Path.Combine(tempDir, "jobs.db");
            var coverPath = Path.Combine(tempDir, "cover.jpg");
            var firstTrack = Path.Combine(tempDir, "source", "01 Opening.mp3");
            var secondTrack = Path.Combine(tempDir, "source", "02 The Middle.mp3");
            var externalTools = new ExternalToolsService(
                CreateConfig(),
                DispatchProxy.Create<IDiskProvider, RealToolDiskProviderProxy>(),
                new TestAppFolderInfo { AppDataFolder = tempDir, StartUpFolder = TestContext.CurrentContext.TestDirectory },
                LogManager.GetLogger("ConversionJobCrashWorkerFixture"));
            var request = CreateRealToolRequest(tempDir, "crash-download", coverPath, firstTrack, secondTrack);
            var repository = CreateRepository(databasePath);
            var service = CreateService(repository, new M4bConversionService(
                externalTools,
                DispatchProxy.Create<IDiskProvider, RealToolDiskProviderProxy>(),
                CreateConfig(),
                LogManager.GetLogger("ConversionJobCrashWorkerFixture")));

            service.Enqueue(request);
            service.Handle(new ApplicationStartedEvent());
            WaitFor(() => File.Exists(gatePath), TimeSpan.FromSeconds(60), "m4b-tool did not reach the child-process FFmpeg gate.");

            var job = repository.FindByDownloadId(request.DownloadId);
            Assert.That(job.Status, Is.EqualTo(ConversionJobStatus.Converting));
            Assert.That(job.AttemptCount, Is.EqualTo(1));
            Assert.That(File.Exists(request.OutputPath), Is.False);
            File.WriteAllText(readyPath, $"{job.Status}|{job.AttemptCount}");

            Thread.Sleep(Timeout.Infinite);
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

        [TestCase(1, false)]
        [TestCase(2, true)]
        public void startup_should_requeue_artifact_without_current_version_or_matching_fingerprint(int manifestVersion, bool corruptOutput)
        {
            var tempDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-job-content-check-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            var request = CreateRequest(tempDir, "content-check-download");
            File.WriteAllText(request.OutputPath, "converted");
            var originalTimestamp = File.GetLastWriteTimeUtc(request.OutputPath);
            var manifest = new ConversionArtifactManifest
            {
                Version = manifestVersion,
                Importable = true,
                CreatedUtc = DateTime.UtcNow,
                OutputPath = request.OutputPath,
                OutputSize = new FileInfo(request.OutputPath).Length,
                OutputModifiedUtcTicks = originalTimestamp.Ticks,
                OutputSha256 = ConversionArtifactFingerprint.ComputeSha256(request.OutputPath),
                TargetQualityId = request.TargetQualityId,
                TargetQualityName = request.TargetQualityName,
                AudioBitrate = request.AudioBitrate,
                AudioSampleRate = request.AudioSampleRate,
                AudioChannels = request.AudioChannels,
                NoConversion = request.NoConversion,
                TagMode = request.TagOptions.Mode,
                TagSignature = request.TagSignature,
                Sources = request.Sources
            };
            File.WriteAllText(Path.Combine(request.WorkFolder, "conversion-artifact.json"), JsonSerializer.Serialize(manifest));

            if (corruptOutput)
            {
                File.WriteAllText(request.OutputPath, "corrupted");
                File.SetLastWriteTimeUtc(request.OutputPath, originalTimestamp);
            }

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
        public void retry_after_partial_output_should_use_an_isolated_attempt_and_replace_only_after_success()
        {
            var tempDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-job-partial-recovery-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            var request = CreateRequest(tempDir, "partial-recovery-download");
            File.WriteAllText(request.OutputPath, "partial output from interrupted process");
            var partialOutput = new FileInfo(request.OutputPath);
            File.WriteAllText(
                Path.Combine(request.WorkFolder, "conversion-artifact.json"),
                JsonSerializer.Serialize(new ConversionArtifactManifest
                {
                    Version = ConversionArtifactManifest.CurrentVersion,
                    Importable = true,
                    CreatedUtc = DateTime.UtcNow,
                    OutputPath = request.OutputPath,
                    OutputSize = partialOutput.Length + 1,
                    OutputModifiedUtcTicks = partialOutput.LastWriteTimeUtc.Ticks,
                    OutputSha256 = ConversionArtifactFingerprint.ComputeSha256(request.OutputPath),
                    TargetQualityId = request.TargetQualityId,
                    TargetQualityName = request.TargetQualityName,
                    AudioBitrate = request.AudioBitrate,
                    AudioSampleRate = request.AudioSampleRate,
                    AudioChannels = request.AudioChannels,
                    NoConversion = request.NoConversion,
                    TagMode = request.TagOptions.Mode,
                    TagSignature = request.TagSignature,
                    Sources = request.Sources
                }));
            var repository = CreateRepository(Path.Combine(tempDir, "jobs.db"));
            repository.Insert(CreatePersistedJob(request, ConversionJobStatus.Converting, DateTime.UtcNow.AddMinutes(-5)));
            var converter = new TokenRecordingConversionService();
            converter.Release.Set();
            var service = CreateService(repository, converter);

            try
            {
                service.Handle(new ApplicationStartedEvent());
                Assert.That(
                    SpinWait.SpinUntil(
                        () => repository.FindByDownloadId(request.DownloadId)?.Status == ConversionJobStatus.ReadyToImport,
                        TimeSpan.FromSeconds(2)),
                    Is.True);

                Assert.That(File.ReadAllText(request.OutputPath), Is.EqualTo("converted"));
                var manifest = JsonSerializer.Deserialize<ConversionArtifactManifest>(
                    File.ReadAllText(Path.Combine(request.WorkFolder, "conversion-artifact.json")));
                Assert.That(manifest.Version, Is.EqualTo(ConversionArtifactManifest.CurrentVersion));
                Assert.That(manifest.Importable, Is.True);
                Assert.That(manifest.OutputPath, Is.EqualTo(request.OutputPath));
                Assert.That(manifest.OutputSize, Is.EqualTo(new FileInfo(request.OutputPath).Length));
                Assert.That(manifest.OutputModifiedUtcTicks, Is.EqualTo(new FileInfo(request.OutputPath).LastWriteTimeUtc.Ticks));
                Assert.That(manifest.OutputSha256, Is.EqualTo(ConversionArtifactFingerprint.ComputeSha256(request.OutputPath)));
                Assert.That(converter.OptionsByDownloadId.ContainsKey(request.DownloadId), Is.True);
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
        public void tool_reported_failure_should_persist_a_nonimportable_retained_artifact()
        {
            var tempDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-job-retained-failure-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            var request = CreateRequest(tempDir, "retained-failure-download");
            var repository = CreateRepository(Path.Combine(tempDir, "jobs.db"));
            var converter = new RetainableFailureConversionService();
            var service = CreateService(repository, converter);

            try
            {
                service.Handle(new ApplicationStartedEvent());
                service.Enqueue(request);
                Assert.That(
                    SpinWait.SpinUntil(
                        () => repository.FindByDownloadId(request.DownloadId)?.Status == ConversionJobStatus.Failed,
                        TimeSpan.FromSeconds(2)),
                    Is.True);

                var manifest = JsonSerializer.Deserialize<ConversionArtifactManifest>(
                    File.ReadAllText(Path.Combine(request.WorkFolder, "conversion-artifact.json")));
                Assert.That(manifest.Version, Is.EqualTo(ConversionArtifactManifest.CurrentVersion));
                Assert.That(manifest.Importable, Is.False);
                Assert.That(new DirectoryInfo(Path.GetDirectoryName(manifest.OutputPath)).Parent.Name, Is.EqualTo("attempts"));
                Assert.That(File.Exists(manifest.OutputPath), Is.True);
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
        public void converter_success_without_output_should_fail_instead_of_marking_artifact_ready()
        {
            var tempDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-job-missing-output-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            var request = CreateRequest(tempDir, "missing-output-download");
            var repository = CreateRepository(Path.Combine(tempDir, "jobs.db"));
            var service = CreateService(repository, new MissingOutputSuccessConversionService());

            try
            {
                service.Enqueue(request);
                service.Handle(new ApplicationStartedEvent());
                Assert.That(
                    SpinWait.SpinUntil(
                        () => repository.FindByDownloadId(request.DownloadId)?.Status == ConversionJobStatus.Failed,
                        TimeSpan.FromSeconds(2)),
                    Is.True);
                Assert.That(File.Exists(request.OutputPath), Is.False);
                Assert.That(File.Exists(Path.Combine(request.WorkFolder, "conversion-artifact.json")), Is.False);
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
        public void scheduler_should_back_off_then_retry_after_transient_iteration_failure()
        {
            var tempDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-job-scheduler-retry-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            var request = CreateRequest(tempDir, "scheduler-retry-download");
            var repository = CreateRepository(Path.Combine(tempDir, "jobs.db"));
            var throwingRepository = DispatchProxy.Create<IConversionJobRepository, ThrowOnceNonCompletedRepositoryProxy>();
            var repositoryProxy = (ThrowOnceNonCompletedRepositoryProxy)(object)throwingRepository;
            repositoryProxy.Inner = repository;
            repositoryProxy.FailOnNonCompletedCall = 2; // Reconcile is first; fail the first scheduler iteration.

            var converter = new TokenRecordingConversionService();
            var service = CreateService(throwingRepository, converter);

            try
            {
                service.Enqueue(request);
                service.Handle(new ApplicationStartedEvent());
                Assert.That(
                    SpinWait.SpinUntil(() => repositoryProxy.NonCompletedCalls >= 2, TimeSpan.FromSeconds(2)),
                    Is.True,
                    "The scheduler did not reach the injected transient iteration failure.");

                var retryTimer = Stopwatch.StartNew();
                Assert.That(
                    SpinWait.SpinUntil(() => converter.OptionsByDownloadId.ContainsKey(request.DownloadId), TimeSpan.FromSeconds(4)),
                    Is.False,
                    "The scheduler retried before its error backoff elapsed.");
                Assert.That(
                    SpinWait.SpinUntil(() => converter.OptionsByDownloadId.ContainsKey(request.DownloadId), TimeSpan.FromSeconds(3)),
                    Is.True,
                    "A transient scheduler failure stranded queued conversion work.");
                retryTimer.Stop();
                Assert.That(retryTimer.Elapsed, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(4)));

                converter.Release.Set();
                Assert.That(
                    SpinWait.SpinUntil(
                        () => repository.FindByDownloadId(request.DownloadId)?.Status == ConversionJobStatus.ReadyToImport,
                        TimeSpan.FromSeconds(2)),
                    Is.True);
            }
            finally
            {
                service.Handle(new ApplicationShutdownRequested());
                converter.Release.Set();
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
        }

        [Test]
        public void scheduler_shutdown_should_cancel_error_backoff_promptly()
        {
            var tempDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-job-scheduler-shutdown-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            var repository = CreateRepository(Path.Combine(tempDir, "jobs.db"));
            var throwingRepository = DispatchProxy.Create<IConversionJobRepository, ThrowOnceNonCompletedRepositoryProxy>();
            var repositoryProxy = (ThrowOnceNonCompletedRepositoryProxy)(object)throwingRepository;
            repositoryProxy.Inner = repository;
            repositoryProxy.FailOnNonCompletedCall = 2;

            var service = CreateService(throwingRepository, new UnusedConversionService());
            Task scheduler = null;

            try
            {
                service.Handle(new ApplicationStartedEvent());
                Assert.That(
                    SpinWait.SpinUntil(() => repositoryProxy.NonCompletedCalls >= 2, TimeSpan.FromSeconds(2)),
                    Is.True,
                    "The scheduler did not reach the injected transient iteration failure.");

                Thread.Sleep(TimeSpan.FromMilliseconds(100));
                scheduler = (Task)typeof(ConversionJobService)
                    .GetField("_scheduler", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(service);
                Assert.That(scheduler, Is.Not.Null);
                Assert.That(scheduler.IsCompleted, Is.False, "The scheduler should remain alive while backing off from an iteration error.");

                var shutdownTimer = Stopwatch.StartNew();
                service.Handle(new ApplicationShutdownRequested());
                shutdownTimer.Stop();

                Assert.That(SpinWait.SpinUntil(() => scheduler.IsCompleted, TimeSpan.FromSeconds(1)), Is.True);
                Assert.That(shutdownTimer.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)), "Shutdown should wake the scheduler's error backoff wait.");
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
        public void lone_conversion_should_claim_all_tokens_and_write_shared_manifest_contract()
        {
            var tempDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-job-lone-tokens-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            var request = CreateRequest(tempDir, "lone-token-download");
            request.AudioSampleRate = 44100;
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
                Assert.That(options.AudioSampleRate, Is.EqualTo(44100));

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
                Assert.That(roundTrip.AudioSampleRate, Is.EqualTo(44100));
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

        private static ConversionJobRequest CreateRealToolRequest(string tempDir, string downloadId, string coverPath, params string[] inputFiles)
        {
            var workRoot = Path.Combine(tempDir, ".chaptarr-conversions", downloadId);
            var workFolder = Path.Combine(workRoot, "work");
            Directory.CreateDirectory(workFolder);

            return new ConversionJobRequest
            {
                DownloadId = downloadId,
                BookTitle = "Restarted Book",
                WorkRoot = workRoot,
                WorkFolder = workFolder,
                OutputPath = Path.Combine(workFolder, "book.m4b"),
                ConversionInputFiles = inputFiles.ToList(),
                Sources = inputFiles.Select(path =>
                {
                    var info = new FileInfo(path);
                    return new ConversionArtifactSource
                    {
                        Path = path,
                        Size = info.Length,
                        ModifiedUtcTicks = info.LastWriteTimeUtc.Ticks
                    };
                }).ToList(),
                TargetQualityId = 12,
                TargetQualityName = "M4B",
                AudioBitrate = 64,
                AudioSampleRate = 44100,
                AudioChannels = 2,
                TagSignature = "restart-test",
                TagOptions = new ConversionTagOptions
                {
                    Mode = "preserve",
                    Name = "Restarted Book",
                    Cover = coverPath,
                    CoverIsSource = true
                }
            };
        }

        private static void CreateFfmpegGateWrapper(string wrapperPath, string gatePath, string releasePath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(wrapperPath));
            File.WriteAllText(wrapperPath,
                "#!/bin/sh\n" +
                "for arg in \"$@\"; do\n" +
                "  if [ \"$arg\" = \"-version\" ] || [ \"$arg\" = \"-buildconf\" ] || [ \"$arg\" = \"0:v:0\" ]; then\n" +
                "    exec /usr/bin/ffmpeg \"$@\"\n" +
                "  fi\n" +
                "done\n" +
                "printf '%s\\n' \"$$\" > \"$CHAPTARRNG_M4B_TEST_GATE\"\n" +
                "while [ ! -f \"$CHAPTARRNG_M4B_TEST_RELEASE\" ]; do sleep 0.05; done\n" +
                "exec /usr/bin/ffmpeg \"$@\"\n");
            File.SetUnixFileMode(wrapperPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        private static void CreateSyntheticCover(string path)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/ffmpeg",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            foreach (var argument in new[]
            {
                "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=blue:s=64x64",
                "-frames:v", "1", "-y", path
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            var standardOutput = process.StandardOutput.ReadToEnd();
            var standardError = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.That(process.ExitCode, Is.Zero, $"FFmpeg failed to create a synthetic cover. {standardOutput} {standardError}");
            Assert.That(new FileInfo(path).Length, Is.GreaterThan(0));
        }

        private static void CreateSyntheticMp3(string path, int frequency, string title, int track)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/ffmpeg",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            foreach (var argument in new[]
            {
                "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
                $"sine=frequency={frequency}:duration=12:sample_rate=44100",
                "-codec:a", "libmp3lame", "-b:a", "96k",
                "-metadata", $"title={title}", "-metadata", "album=Restarted Book",
                "-metadata", $"track={track}", "-y", path
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            var standardOutput = process.StandardOutput.ReadToEnd();
            var standardError = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.That(process.ExitCode, Is.Zero, $"FFmpeg failed to create a synthetic restart track. {standardOutput} {standardError}");
            Assert.That(new FileInfo(path).Length, Is.GreaterThan(0));
        }

        private static void WaitFor(Func<bool> condition, TimeSpan timeout, string failureMessage)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                if (condition())
                {
                    return;
                }

                Thread.Sleep(50);
            }

            Assert.Fail(failureMessage);
        }

        private static bool HasExited(int processId)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                return process.HasExited;
            }
            catch (ArgumentException)
            {
                return true;
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
CREATE TABLE IF NOT EXISTS ""ConversionJobs"" (
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
