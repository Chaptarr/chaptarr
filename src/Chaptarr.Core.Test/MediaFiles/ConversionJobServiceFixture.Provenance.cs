using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.MediaFiles
{
    public partial class ConversionJobServiceFixture
    {
        private sealed class RetainedResultConverter : IM4bConversionService
        {
            public bool Success { get; set; }
            public int Calls;
            public ConversionResult ConvertToM4b(string[] inputs, string output, ConversionOptions options = null)
            {
                Interlocked.Increment(ref Calls);
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                File.WriteAllText(output, "readable constructed recording");
                return new ConversionResult
                {
                    Success = Success, RetainOutputOnFailure = !Success,
                    ErrorMessage = Success ? null : "encoder reported a chapter failure"
                };
            }
            public bool CanConvert(string[] inputs) => true;
            public ConversionEstimate EstimateConversion(string[] inputs) => new() { CanConvert = true };
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void stop_after_ready_write_must_finish_cancellation_before_releasing_worker(bool postgres, bool discard)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-stop-ready-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var repository = new ConversionJobRepository(database, new StubEventAggregator());
                var converter = new RetainedResultConverter { Success = true };
                var request = CreateRequest(directory, Guid.NewGuid().ToString("N"));
                ConversionJobService service = null;
                var stopped = false;
                var tracking = InterfaceProxy.Proxy<IConversionTrackingService>((method, args) =>
                {
                    if (method.Name == nameof(IConversionTrackingService.Progress) && args[1] is decimal progress && progress == 98m && !stopped)
                    {
                        stopped = true;
                        if (discard) service.RemoveArtifacts(request.DownloadId);
                        else Assert.That(service.Cancel(request.DownloadId), Is.True);
                    }
                    return method.ReturnType == typeof(bool) ? false : null;
                });
                service = new ConversionJobService(repository, converter, tracking,
                    DispatchProxy.Create<IManageCommandQueue, RecordingCommandQueueProxy>(), CreateConfig(), LogManager.GetCurrentClassLogger());
                try
                {
                    RunWorker(service, request);
                    var saved = repository.FindByDownloadId(request.DownloadId);
                    Assert.That(stopped, Is.True);
                    Assert.That(saved.Status, Is.EqualTo(ConversionJobStatus.Cancelled));
                    Assert.That(JsonSerializer.Deserialize<ConversionJobRequest>(saved.RequestJson).DiscardRequested, Is.EqualTo(discard));
                    Assert.That(service.IsRunning(request.DownloadId), Is.False);
                    Assert.That(Directory.Exists(request.WorkFolder), Is.False);
                    Assert.That(File.Exists(request.Sources.Single().Path), Is.True);
                    if (discard)
                    {
                        typeof(ConversionJobService).GetMethod("RetryDiscardCleanup", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(service, null);
                        Assert.That(repository.FindByDownloadId(request.DownloadId), Is.Null);
                    }
                }
                finally
                {
                    service.Handle(new ApplicationShutdownRequested());
                    if (Directory.Exists(directory)) Directory.Delete(directory, true);
                }
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void completed_import_cleans_only_receipts_for_its_own_source_group(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-old-attempts-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var repository = new ConversionJobRepository(database, new StubEventAggregator());
                var service = CreateService(repository, new UnusedConversionService());
                var request = CreateRequest(directory, Guid.NewGuid().ToString("N"));
                try
                {
                    repository.Insert(CreatePersistedJob(request, ConversionJobStatus.ReadyToImport, DateTime.UtcNow));
                    var old = Path.Combine(request.WorkRoot, "old");
                    var other = Path.Combine(request.WorkRoot, "other-book");
                    var foreign = Path.Combine(request.WorkRoot, "other-download");
                    var unknown = Path.Combine(request.WorkRoot, "no-receipt");
                    Directory.CreateDirectory(unknown);
                    foreach (var folder in new[] { old, other, foreign })
                    {
                        Directory.CreateDirectory(folder);
                        var output = Path.Combine(folder, "book.m4b");
                        File.WriteAllText(output, "retained recording");
                        File.WriteAllText(Path.Combine(folder, "conversion-artifact.json"), JsonSerializer.Serialize(new ConversionArtifactManifest
                        {
                            DownloadId = folder == foreign ? "different-download" : request.DownloadId,
                            OutputPath = output,
                            Sources = folder == other ? new() { new() { Path = Path.Combine(directory, "other-book.mp3") } } : request.Sources
                        }));
                    }
                    service.Complete(request.DownloadId);
                    Assert.That(Directory.Exists(old), Is.False);
                    Assert.That(Directory.Exists(other), Is.True);
                    Assert.That(Directory.Exists(foreign), Is.True);
                    Assert.That(Directory.Exists(unknown), Is.True);
                    Assert.That(File.Exists(request.Sources.Single().Path), Is.True);
                    Assert.That(repository.FindByDownloadId(request.DownloadId).Status, Is.EqualTo(ConversionJobStatus.Completed));
                    service.Complete(request.DownloadId);
                }
                finally
                {
                    if (Directory.Exists(directory)) Directory.Delete(directory, true);
                }
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void failed_readable_encoder_output_must_not_become_success_after_restart(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-proof-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var repository = new ConversionJobRepository(database, new StubEventAggregator());
                var converter = new RetainedResultConverter();
                var service = CreateService(repository, converter);
                var request = CreateRequest(directory, Guid.NewGuid().ToString("N"));
                try
                {
                    service.Enqueue(request);
                    service.Handle(new ApplicationStartedEvent());
                    Assert.That(SpinWait.SpinUntil(() => repository.FindByDownloadId(request.DownloadId)?.Status == ConversionJobStatus.Failed && !service.IsRunning(request.DownloadId), TimeSpan.FromSeconds(5)), Is.True);
                    Assert.That(File.Exists(request.OutputPath), Is.True);
                    var ownership = JsonSerializer.Deserialize<ConversionArtifactManifest>(File.ReadAllText(Path.Combine(request.WorkFolder, "conversion-artifact.json")));
                    Assert.That(ownership.DownloadId, Is.EqualTo(request.DownloadId));
                    Assert.That(ownership.EncoderSucceeded, Is.False, "A readable failed output has ownership evidence, never successful provenance");
                    service.Handle(new ApplicationShutdownRequested());
                    // Simulate losing the failure-state acknowledgement: restart sees the old lease.
                    var interrupted = repository.FindByDownloadId(request.DownloadId);
                    interrupted.Status = ConversionJobStatus.Converting;
                    repository.Update(interrupted);
                    var restarted = CreateService(repository, new UnusedConversionService());
                    typeof(ConversionJobService).GetMethod("Reconcile", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(restarted, null);
                    Assert.That(repository.FindByDownloadId(request.DownloadId).Status, Is.EqualTo(ConversionJobStatus.Queued));
                    Assert.That(converter.Calls, Is.EqualTo(1));
                    Assert.That(File.Exists(request.Sources.Single().Path), Is.True);
                }
                finally
                {
                    service.Handle(new ApplicationShutdownRequested());
                    if (Directory.Exists(directory)) Directory.Delete(directory, true);
                }
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void successful_output_survives_non_database_ready_state_failure(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-state-proof-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var repository = new ConversionJobRepository(database, new StubEventAggregator());
                var failed = 0;
                var proxy = InterfaceProxy.Proxy<IConversionJobRepository>((method, args) =>
                {
                    if (method.Name == nameof(IConversionJobRepository.Update) && args[0] is ConversionJob job && job.Status == ConversionJobStatus.ReadyToImport && Interlocked.Exchange(ref failed, 1) == 0)
                        throw new IOException("constructed ready-state failure");
                    return method.Invoke(repository, args);
                });
                var converter = new RetainedResultConverter { Success = true };
                var service = CreateService(proxy, converter);
                var request = CreateRequest(directory, Guid.NewGuid().ToString("N"));
                try
                {
                    service.Enqueue(request);
                    service.Handle(new ApplicationStartedEvent());
                    Assert.That(SpinWait.SpinUntil(() => repository.FindByDownloadId(request.DownloadId)?.Status == ConversionJobStatus.Failed && !service.IsRunning(request.DownloadId), TimeSpan.FromSeconds(5)), Is.True);
                    Assert.That(File.Exists(request.OutputPath), Is.True);
                    var manifest = JsonSerializer.Deserialize<ConversionArtifactManifest>(File.ReadAllText(Path.Combine(request.WorkFolder, "conversion-artifact.json")));
                    Assert.That(manifest.EncoderSucceeded, Is.True);
                    Assert.That(manifest.OutputSize, Is.EqualTo(new FileInfo(request.OutputPath).Length));
                    Assert.That(converter.Calls, Is.EqualTo(1));
                }
                finally
                {
                    service.Handle(new ApplicationShutdownRequested());
                    if (Directory.Exists(directory)) Directory.Delete(directory, true);
                }
            });
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void committed_source_provenance_recovers_import_before_missing_workspace_can_reencode(bool postgres, bool wrongSources)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-committed-proof-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var repository = new ConversionJobRepository(database, new StubEventAggregator());
                var request = CreateRequest(directory, Guid.NewGuid().ToString("N"));
                request.BookId = 42;
                var libraryPath = Path.Combine(directory, "library.m4b");
                File.WriteAllText(libraryPath, "committed recording");
                var files = InterfaceProxy.Proxy<IMediaFileService>((method, args) => method.Name == nameof(IMediaFileService.GetFilesByBook)
                    ? new List<BookFile> { new() { Id = 123, Path = libraryPath, Quality = new QualityModel(Quality.M4B), MatchProvenance = new MatchProvenance
                    {
                        ImportDownloadId = request.DownloadId,
                        ImportSourcePaths = new List<string> { wrongSources ? Path.Combine(directory, "different-book.mp3") : request.Sources.Single().Path }
                    } } }
                    : throw new AssertionException("Unexpected media query: " + method.Name));
                var service = new ConversionJobService(repository, new UnusedConversionService(), new ConversionTrackingService(new StubEventAggregator()),
                    InterfaceProxy.Proxy<IManageCommandQueue>((method, args) => null), CreateConfig(), LogManager.GetCurrentClassLogger(), mediaFileService: files);
                try
                {
                    var job = service.Enqueue(request);
                    job.Status = ConversionJobStatus.ReadyToImport;
                    repository.Update(job);
                    typeof(ConversionJobService).GetMethod("Reconcile", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(service, null);
                    Assert.That(repository.FindByDownloadId(request.DownloadId).Status, Is.EqualTo(wrongSources ? ConversionJobStatus.Queued : ConversionJobStatus.Completed));
                    Assert.That(File.ReadAllText(libraryPath), Is.EqualTo("committed recording"));
                }
                finally
                {
                    if (Directory.Exists(directory)) Directory.Delete(directory, true);
                }
            });
        }

        [TestCase(false, ConversionJobStatus.Converting, false)]
        [TestCase(true, ConversionJobStatus.Converting, false)]
        [TestCase(false, ConversionJobStatus.ReadyToImport, false)]
        [TestCase(true, ConversionJobStatus.ReadyToImport, false)]
        [TestCase(false, ConversionJobStatus.Converting, true)]
        [TestCase(true, ConversionJobStatus.Converting, true)]
        public void legacy_receipt_and_output_survive_recovery_without_invented_success_or_automatic_overwrite(bool postgres, ConversionJobStatus status, bool expiredLease)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-legacy-receipt-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var repository = new ConversionJobRepository(database, new StubEventAggregator());
                var request = CreateRequest(directory, Guid.NewGuid().ToString("N"));
                const string retained = "legacy retained recording, encoder outcome cannot be inferred";
                File.WriteAllText(request.OutputPath, retained);
                // This is the actual old production manifest shape, which both successful and
                // retained-failed encoder branches wrote. Its bytes deliberately lack success proof.
                var receipt = JsonSerializer.Serialize(new
                {
                    CreatedUtc = DateTime.UtcNow.AddDays(-2), request.OutputPath,
                    request.TargetQualityId, request.TargetQualityName, request.AudioBitrate,
                    request.AudioChannels, TagMode = request.TagOptions.Mode, request.TagSignature, request.Sources
                });
                var manifestPath = Path.Combine(request.WorkFolder, "conversion-artifact.json");
                File.WriteAllText(manifestPath, receipt);
                var service = CreateService(repository, new UnusedConversionService());
                try
                {
                    var job = service.Enqueue(request);
                    job.Status = status;
                    job.HeartbeatAt = DateTime.UtcNow.AddMinutes(-10);
                    repository.Update(job);
                    if (expiredLease) service.RecoverExpiredLeases();
                    else typeof(ConversionJobService).GetMethod("Reconcile", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(service, null);
                    var recovered = repository.FindByDownloadId(request.DownloadId);
                    Assert.That(recovered.Status, Is.EqualTo(ConversionJobStatus.Failed));
                    Assert.That(recovered.Error, Does.Contain("Manual Import").And.Contain("Retry"));
                    Assert.That(File.ReadAllText(request.OutputPath), Is.EqualTo(retained));
                    Assert.That(File.ReadAllText(manifestPath), Is.EqualTo(receipt));
                    service.Reset(request.DownloadId);
                    Assert.That(repository.FindByDownloadId(request.DownloadId), Is.Null, "Only deliberate Retry reopens conversion admission");
                    Assert.That(File.ReadAllText(request.OutputPath), Is.EqualTo(retained), "Retry admission must preserve the previous attempt");
                    TestContext.Progress.WriteLine($"LEGACY_CONVERSION database={database.DatabaseType} original={status} lease={expiredLease} recovered=Failed outputRetained=true receiptUnchanged=true");
                }
                finally
                {
                    if (Directory.Exists(directory)) Directory.Delete(directory, true);
                }
            });
        }
    }
}
