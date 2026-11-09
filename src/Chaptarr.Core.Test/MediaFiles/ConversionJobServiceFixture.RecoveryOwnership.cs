using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Common;
using Microsoft.Data.Sqlite;
using NLog;
using NLog.Config;
using NUnit.Framework;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.MediaFiles
{
    public partial class ConversionJobServiceFixture
    {
        private sealed class RecoveryScope : IDisposable
        {
            public string Root { get; } = Path.Combine(TestContext.CurrentContext.WorkDirectory, Path.GetRandomFileName());
            public ConversionJobService Service { get; set; }
            public void Stop()
            {
                ShutdownAndWait(Service);
                Service = null;
            }
            public void Dispose()
            {
                Stop();
                if (Directory.Exists(Root)) Directory.Delete(Root, true);
            }
        }

        private static void WithRecovery(bool postgres, Action<NzbDrone.Core.Datastore.IMainDatabase, RecoveryScope> test) =>
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var scope = new RecoveryScope();
                test(database, scope);
            });

        [Test]
        public void released_worker_settles_fresh_metadata_only_for_its_own_attempt(
            [Values(false, true)] bool postgres, [Values("request", "retry", "stale", "replacement", "removed")] string change)
        {
            WithRecovery(postgres, (database, scope) =>
            {
                var request = CreateRequest(scope.Root, "fresh-settlement");
                request.BookId = 42;
                request.HistoryIdentityAmbiguous = change is "request" or "retry" or "stale";
                var libraryPath = Path.Combine(scope.Root, "library.m4b");
                File.WriteAllText(libraryPath, "committed recording");
                var repository = new ConversionJobRepository(database, new StubEventAggregator());
                var converter = new TokenRecordingConversionService();
                using var released = new ManualResetEventSlim();
                using var proceed = new ManualResetEventSlim();
                var paused = 0;
                var committed = 0;
                var retryFailures = 0;
                ConversionJobService service = null;
                void ResolveHistoryIdentity()
                {
                    var current = repository.FindByDownloadId(request.DownloadId);
                    var updated = JsonSerializer.Deserialize<ConversionJobRequest>(current.RequestJson);
                    updated.HistoryIdentityAmbiguous = false;
                    current.RequestJson = JsonSerializer.Serialize(updated);
                    repository.SetFields(current, job => job.RequestJson);
                    Assert.That(repository.FindByDownloadId(request.DownloadId).CleanupAttention, Is.False);
                }
                void AtReleasedBoundary()
                {
                    if (converter.CallsByDownloadId.ContainsKey(request.DownloadId) && !service.IsRunning(request.DownloadId) &&
                        Interlocked.CompareExchange(ref paused, 1, 0) == 0)
                    {
                        released.Set();
                        if (!proceed.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Post-release settlement was not released");
                    }
                }
                var proxy = InterfaceProxy.Proxy<IConversionJobRepository>((method, args) =>
                {
                    if (method.Name == nameof(IConversionJobRepository.FindByDownloadId)) AtReleasedBoundary();
                    return method.Invoke(repository, args);
                });
                var media = InterfaceProxy.Proxy<IMediaFileService>((method, _) =>
                {
                    if (method.Name == nameof(IMediaFileService.GetFilePathOwnership)) return new List<BookFile>();
                    Assert.That(method.Name, Is.EqualTo(nameof(IMediaFileService.GetFilesByBook)));
                    AtReleasedBoundary();
                    if (change is "retry" or "stale" && Volatile.Read(ref committed) == 1 && Interlocked.CompareExchange(ref retryFailures, 1, 0) == 0)
                    {
                        // Change metadata between the fresh read and locked settlement, with or without a database failure.
                        ResolveHistoryIdentity();
                        if (change == "retry") throw new SqliteException("Injected post-release import evidence read failure", 1);
                    }
                    return Volatile.Read(ref committed) == 0 ? new List<BookFile>() : new List<BookFile>
                    {
                        new() { Path = libraryPath, Quality = new QualityModel(Quality.M4B), MatchProvenance = new MatchProvenance
                        {
                            ImportDownloadId = request.DownloadId, ImportSourcePaths = request.Sources.Select(source => source.Path).ToList()
                        } }
                    };
                });
                using var logs = new LogFactory();
                var sink = CaptureOrphanLogs(logs);
                service = scope.Service = new ConversionJobService(TestCleanupDisk(), TestRootFolders(), media, proxy, converter, null,
                    DispatchProxy.Create<IManageCommandQueue, RecordingCommandQueueProxy>(), CreateConfig(), logs.GetLogger("released-settlement"),
                    outcomeRetryDelay: TimeSpan.FromMilliseconds(10));
                Task completion = null;
                ConversionJob replacement = null;
                try
                {
                    service.Enqueue(request);
                    typeof(ConversionJobService).GetMethod("DispatchQueuedJobs", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(service, null);
                    Assert.That(SpinWait.SpinUntil(() => converter.OptionsByDownloadId.ContainsKey(request.DownloadId), TimeSpan.FromSeconds(5)), Is.True);
                    completion = service.Completion.Workers;
                    if (change == "request")
                    {
                        // Model the lifecycle writer resolving ambiguous history while encoding.
                        ResolveHistoryIdentity();
                    }
                    converter.Release.Set();
                    Assert.That(released.Wait(TimeSpan.FromSeconds(5)), Is.True);
                    if (change is "replacement" or "removed")
                    {
                        service.Reset(request.DownloadId, repository.FindByDownloadId(request.DownloadId));
                        if (change == "replacement")
                        {
                            var next = CreateRequest(Path.Combine(scope.Root, "next"), request.DownloadId);
                            next.BookId = request.BookId;
                            next.Sources = request.Sources;
                            replacement = service.Enqueue(next);
                        }
                    }
                    Volatile.Write(ref committed, 1);
                    proceed.Set();
                    Assert.That(completion.Wait(TimeSpan.FromSeconds(5)), Is.True);
                    var saved = repository.FindByDownloadId(request.DownloadId);
                    if (replacement == null)
                        Assert.That(saved, Is.Null, "Committed import settlement must not wait for restart or Reset");
                    else
                    {
                        Assert.That(saved?.Id, Is.EqualTo(replacement.Id), "An old worker must not settle a replacement attempt");
                        Assert.That(saved.Status, Is.EqualTo(ConversionJobStatus.Queued));
                        Assert.That(Directory.Exists(replacement.WorkFolder), Is.True);
                    }
                    Assert.That(retryFailures, Is.EqualTo(change is "retry" or "stale" ? 1 : 0));
                    if (change is "replacement" or "removed")
                        Assert.That(sink.Logs.Any(line => line.StartsWith("Info|", StringComparison.Ordinal) &&
                            line.Contains(request.DownloadId, StringComparison.Ordinal) &&
                            line.Contains(change == "removed" ? "no longer exists" : "different attempt", StringComparison.Ordinal)), Is.True);
                    if (change == "stale")
                        Assert.That(sink.Logs.Any(line => line.StartsWith("Warn|Skipping recovery", StringComparison.Ordinal)), Is.True);
                    Assert.That(converter.CallsByDownloadId[request.DownloadId], Is.EqualTo(1));
                    Assert.That(File.ReadAllText(libraryPath), Is.EqualTo("committed recording"));
                    Assert.That(File.Exists(request.Sources[0].Path), Is.True);
                }
                finally
                {
                    converter.Release.Set();
                    proceed.Set();
                    scope.Stop();
                    completion?.Wait(TimeSpan.FromSeconds(5));
                }
            });
        }

        [Test]
        public void committed_settlement_preserves_requested_removal_scope(
            [Values(false, true)] bool postgres, [Values("none", "current", "all")] string scopeChoice)
        {
            WithRecovery(postgres, (database, scope) =>
            {
                var request = CreateRequest(scope.Root, "settlement-scope");
                request.DiscardRequested = scopeChoice != "none";
                request.DiscardCurrentAttemptOnly = scopeChoice == "current";
                request.DiscardReason = request.DiscardRequested ? "removed" : null;
                var older = Path.Combine(request.WorkRoot, "older");
                Directory.CreateDirectory(older);
                var olderOutput = Path.Combine(older, "book.m4b");
                File.WriteAllText(olderOutput, "previous attempt");
                File.WriteAllText(Path.Combine(older, "conversion-artifact.json"), JsonSerializer.Serialize(new ConversionArtifactManifest
                {
                    DownloadId = request.DownloadId, OutputPath = olderOutput, Sources = request.Sources,
                    EncoderSucceeded = true, OutputSize = new FileInfo(olderOutput).Length
                }));
                var repository = new ConversionJobRepository(database, new StubEventAggregator());
                var job = repository.Insert(CreatePersistedJob(request, ConversionJobStatus.ReadyToImport, DateTime.UtcNow));
                ConversionJobRequest settled = null;
                var proxy = InterfaceProxy.Proxy<IConversionJobRepository>((method, args) =>
                {
                    if (method.Name == nameof(IConversionJobRepository.Update) && args[0] is ConversionJob row && row.Status == ConversionJobStatus.Completed)
                        settled = JsonSerializer.Deserialize<ConversionJobRequest>(row.RequestJson);
                    return method.Invoke(repository, args);
                });
                var service = scope.Service = CreateService(proxy, new UnusedConversionService());
                typeof(ConversionJobService).GetMethod("SettleCommittedImport", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(service, new object[] { job });
                Assert.Multiple(() =>
                {
                    Assert.That(settled.DiscardCurrentAttemptOnly, Is.EqualTo(scopeChoice != "all"));
                    Assert.That(settled.DiscardReason, Is.EqualTo(scopeChoice == "none" ? "imported" : "removed"));
                    Assert.That(Directory.Exists(request.WorkFolder), Is.False);
                    Assert.That(File.Exists(olderOutput), Is.EqualTo(scopeChoice != "all"));
                    Assert.That(File.Exists(request.Sources[0].Path), Is.True);
                    Assert.That(repository.FindByDownloadId(request.DownloadId), Is.Null);
                });
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void deleted_job_settlement_reports_absence_instead_of_newer_state(bool postgres)
        {
            WithRecovery(postgres, (database, scope) =>
            {
                var request = CreateRequest(scope.Root, "deleted-settlement");
                var repository = new ConversionJobRepository(database, new StubEventAggregator());
                using var logs = new LogFactory();
                var sink = CaptureOrphanLogs(logs);
                var service = scope.Service = CreateService(repository, new UnusedConversionService(), logger: logs.GetLogger("deleted-settlement"));
                var job = service.Enqueue(request);
                repository.Delete(job.Id);
                typeof(ConversionJobService).GetMethod("SettleCommittedImport", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(service, new object[] { job });
                Assert.That(sink.Logs.Any(line => line.StartsWith("Warn|Skipping recovery", StringComparison.Ordinal) &&
                    line.Contains(request.DownloadId, StringComparison.Ordinal) && line.Contains("no longer exists", StringComparison.Ordinal)), Is.True);
                Assert.That(sink.Logs.Any(line => line.Contains("preserving the newer state", StringComparison.Ordinal)), Is.False);
                Assert.That(Directory.Exists(request.WorkFolder), Is.True);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void lease_recovery_skips_request_inspection_for_a_live_worker(bool postgres)
        {
            WithRecovery(postgres, (database, scope) =>
            {
                var request = CreateRequest(scope.Root, "owned-lease");
                var repository = new ConversionJobRepository(database, new StubEventAggregator());
                var proxy = InterfaceProxy.Proxy<IConversionJobRepository>((method, args) =>
                {
                    var result = method.Invoke(repository, args);
                    if (method.Name == nameof(IConversionJobRepository.ByStatus) && args[0] is ConversionJobStatus.Converting)
                        foreach (var row in (List<ConversionJob>)result)
                        {
                            row.HeartbeatAt = DateTime.UtcNow.AddMinutes(-5);
                            // A deliberately unreadable snapshot reveals inspection before the ownership skip.
                            row.RequestJson = "{";
                        }
                    return result;
                });
                using var logs = new LogFactory();
                var sink = new ConcurrentLogTarget { Layout = "${message}" };
                var config = new LoggingConfiguration(logs);
                config.AddRuleForAllLevels(sink);
                logs.Configuration = config;
                var converter = new TokenRecordingConversionService();
                var service = scope.Service = CreateService(proxy, converter, logger: logs.GetLogger("owned-lease"));
                try
                {
                    service.Enqueue(request);
                    typeof(ConversionJobService).GetMethod("DispatchQueuedJobs", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(service, null);
                    Assert.That(SpinWait.SpinUntil(() => converter.OptionsByDownloadId.ContainsKey(request.DownloadId), TimeSpan.FromSeconds(5)), Is.True);
                    service.RecoverExpiredLeases();
                    Assert.That(sink.Logs.Any(line => line.Contains("Unable to read conversion job request")), Is.False,
                        "A live lease must be skipped before parsing its request or inspecting its files");
                    Assert.That(repository.FindByDownloadId(request.DownloadId).Status, Is.EqualTo(ConversionJobStatus.Converting));
                }
                finally
                {
                    converter.Release.Set();
                    scope.Stop();
                }
            });
        }

        [Test]
        public void shutdown_requeue_must_preserve_a_stop_after_its_read(
            [Values(false, true)] bool postgres, [Values(false, true)] bool stop)
        {
            WithRecovery(postgres, (database, scope) =>
            {
                var request = CreateRequest(scope.Root, "shutdown-stop");
                var repository = new ConversionJobRepository(database, new StubEventAggregator());
                using var shutdown = new CancellationTokenSource();
                var observed = 0;
                ConversionJobService service = null;
                var proxy = InterfaceProxy.Proxy<IConversionJobRepository>((method, args) =>
                {
                    var result = method.Invoke(repository, args);
                    if (method.Name == nameof(IConversionJobRepository.FindByDownloadId) && shutdown.IsCancellationRequested &&
                        result is ConversionJob { Status: ConversionJobStatus.Converting } &&
                        Interlocked.CompareExchange(ref observed, 1, 0) == 0 && stop)
                    {
                        Assert.That(service.Cancel(request.DownloadId, ConversionRemovalOrigin.UserAction), Is.True);
                        Assert.That(repository.FindByDownloadId(request.DownloadId).Status, Is.EqualTo(ConversionJobStatus.Cancelling));
                    }
                    return result;
                });
                var converter = new TokenRecordingConversionService { HoldCancellation = true };
                service = scope.Service = CreateService(proxy, converter);
                typeof(ConversionJobService).GetField("_shutdown", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(service, shutdown);
                try
                {
                    service.Enqueue(request);
                    typeof(ConversionJobService).GetMethod("DispatchQueuedJobs", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(service, null);
                    Assert.That(SpinWait.SpinUntil(() => converter.OptionsByDownloadId.ContainsKey(request.DownloadId), TimeSpan.FromSeconds(5)), Is.True);
                    shutdown.Cancel();
                    converter.Release.Set();
                    scope.Stop();
                    Assert.That(observed, Is.EqualTo(1), "The worker must read its persisted state during shutdown");
                    Assert.That(service.IsRunning(request.DownloadId), Is.False);
                    Assert.That(repository.FindByDownloadId(request.DownloadId).Status,
                        Is.EqualTo(stop ? ConversionJobStatus.Cancelled : ConversionJobStatus.Queued));
                    Assert.That(File.Exists(request.Sources[0].Path), Is.True);
                }
                finally
                {
                    converter.Release.Set();
                    scope.Stop();
                }
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void startup_recovery_must_preserve_refresh_removal_after_its_snapshot(bool postgres)
        {
            WithRecovery(postgres, (database, scope) =>
            {
                var request = CreateRequest(scope.Root, "removed-after-snapshot");
                request.DownloadClientId = 4;
                SeedClientHistory(database, request.DownloadId);
                var repository = new ConversionJobRepository(database, new StubEventAggregator());
                repository.Insert(CreatePersistedJob(request, ConversionJobStatus.Converting, DateTime.UtcNow));
                ConversionJobService service = null;
                var proxy = InterfaceProxy.Proxy<IConversionJobRepository>((method, args) =>
                {
                    // Leave the refresh handler's cancellation durable and inspectable when
                    // its final deletion fails. Recovery must preserve that newer decision.
                    if (method.Name == nameof(IConversionJobRepository.DeleteByDownloadId))
                        throw new SqliteException("Injected cleanup deletion failure", 1);
                    var result = method.Invoke(repository, args);
                    if (method.Name == nameof(IConversionJobRepository.NonCompleted))
                    {
                        service.Handle(new TrackedDownloadRefreshedEvent(new(), new Dictionary<int, HashSet<string>> { [4] = new() }));
                        Assert.That(repository.FindByDownloadId(request.DownloadId).Status, Is.EqualTo(ConversionJobStatus.Cancelled));
                    }
                    return result;
                });
                service = scope.Service = CreateService(proxy, new UnusedConversionService());
                typeof(ConversionJobService).GetMethod("Reconcile", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(service, null);
                var current = repository.FindByDownloadId(request.DownloadId);
                Assert.That(current.Status, Is.EqualTo(ConversionJobStatus.Cancelled));
                Assert.That(JsonSerializer.Deserialize<ConversionJobRequest>(current.RequestJson).DiscardRequested, Is.True);
                Assert.That(File.Exists(request.Sources[0].Path), Is.True);
            });
        }

        [Test]
        public void cleanup_metadata_must_not_erase_a_committed_import_decision(
            [Values(false, true)] bool postgres, [Values(false, true)] bool clear)
        {
            WithRecovery(postgres, (database, scope) =>
            {
                var request = CreateRequest(scope.Root, "cleanup-metadata");
                if (clear) request.CleanupError = "old cleanup error";
                var repository = new ConversionJobRepository(database, new StubEventAggregator());
                repository.Insert(CreatePersistedJob(request, ConversionJobStatus.ReadyToImport, DateTime.UtcNow));
                var expected = repository.FindByDownloadId(request.DownloadId);
                using var metadataRead = new ManualResetEventSlim();
                using var releaseMetadata = new ManualResetEventSlim();
                var metadataThread = 0;
                var paused = 0;
                var proxy = InterfaceProxy.Proxy<IConversionJobRepository>((method, args) =>
                {
                    var result = method.Invoke(repository, args);
                    if (method.Name == nameof(IConversionJobRepository.FindByDownloadId) &&
                        Environment.CurrentManagedThreadId == Volatile.Read(ref metadataThread) &&
                        Interlocked.CompareExchange(ref paused, 1, 0) == 0)
                    {
                        metadataRead.Set();
                        if (!releaseMetadata.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Cleanup metadata read was not released");
                    }
                    // Keep the completed row inspectable without proceeding into filesystem cleanup.
                    if (method.Name == nameof(IConversionJobRepository.Update) && args[0] is ConversionJob { Status: ConversionJobStatus.Completed })
                        throw new SqliteException("Injected lost acknowledgement after committed completion", 1);
                    return result;
                });
                using var logs = new LogFactory();
                var sink = CaptureOrphanLogs(logs);
                var service = scope.Service = CreateService(proxy, new UnusedConversionService(), logger: logs.GetLogger("settlement-refusal"));
                var settle = typeof(ConversionJobService).GetMethod("SettleCommittedImport", BindingFlags.NonPublic | BindingFlags.Instance);
                void Settle(ConversionJob job)
                {
                    try { settle.Invoke(service, new object[] { job }); }
                    catch (TargetInvocationException error) when (error.InnerException is SqliteException) { }
                }
                Task metadata = null;
                Task completion = null;
                try
                {
                    metadata = Task.Run(() =>
                    {
                        Volatile.Write(ref metadataThread, Environment.CurrentManagedThreadId);
                        var method = clear ? "ClearCleanupAttention" : "RecordCleanupFailure";
                        var arguments = clear ? new object[] { request.DownloadId } : new object[] { request.DownloadId, new IOException("new cleanup error") };
                        typeof(ConversionJobService).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(service, arguments);
                    });
                    Assert.That(metadataRead.Wait(TimeSpan.FromSeconds(5)), Is.True);
                    var sync = typeof(ConversionJobService).GetField("_sync", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(service);
                    var metadataOwnsGate = !Monitor.TryEnter(sync);
                    if (!metadataOwnsGate) Monitor.Exit(sync);
                    completion = Task.Run(() => Settle(expected));
                    // Fixed code holds the gate, so metadata finishes before settlement.
                    // On old code, force the stale write after settlement to reproduce the loss.
                    if (metadataOwnsGate) releaseMetadata.Set();
                    Assert.That(completion.Wait(TimeSpan.FromSeconds(5)), Is.True);
                    releaseMetadata.Set();
                    Assert.That(metadata.Wait(TimeSpan.FromSeconds(5)), Is.True);
                    var current = repository.FindByDownloadId(request.DownloadId);
                    if (current.Status == ConversionJobStatus.Completed)
                        Assert.That(JsonSerializer.Deserialize<ConversionJobRequest>(current.RequestJson).DiscardRequested, Is.True,
                            "Cleanup bookkeeping must not erase the completed import's cleanup intent");
                    else
                    {
                        Assert.That(current.Status, Is.EqualTo(ConversionJobStatus.ReadyToImport), "A changed request requires fresh completion evidence");
                        Assert.That(sink.Logs.Any(line => line.StartsWith("Warn|Skipping recovery", StringComparison.Ordinal) &&
                            line.Contains(request.DownloadId, StringComparison.Ordinal)), Is.True, "Refused settlement must be visible without Debug logging");
                    }
                    // Re-reading after a changed request still permits legitimate committed-import settlement.
                    Settle(current);
                    current = repository.FindByDownloadId(request.DownloadId);
                    Assert.That(current.Status, Is.EqualTo(ConversionJobStatus.Completed));
                    Assert.That(JsonSerializer.Deserialize<ConversionJobRequest>(current.RequestJson).DiscardRequested, Is.True);
                }
                finally
                {
                    releaseMetadata.Set();
                    metadata?.Wait(TimeSpan.FromSeconds(5));
                    completion?.Wait(TimeSpan.FromSeconds(5));
                    scope.Stop();
                }
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void recovery_does_not_require_an_exact_timestamp_match_for_an_unchanged_decision(bool postgres)
        {
            WithRecovery(postgres, (database, scope) =>
            {
                var request = CreateRequest(scope.Root, "timestamp-only");
                var repository = new ConversionJobRepository(database, new StubEventAggregator());
                repository.Insert(CreatePersistedJob(request, ConversionJobStatus.Converting, DateTime.UtcNow));
                var stale = repository.FindByDownloadId(request.DownloadId);
                var current = repository.FindByDownloadId(request.DownloadId);
                current.UpdatedAt = current.UpdatedAt.AddSeconds(1);
                repository.SetFields(current, job => job.UpdatedAt);
                var proxy = InterfaceProxy.Proxy<IConversionJobRepository>((method, args) =>
                    method.Name == nameof(IConversionJobRepository.NonCompleted)
                        ? new List<ConversionJob> { stale } : method.Invoke(repository, args));
                var service = scope.Service = CreateService(proxy, new UnusedConversionService());
                typeof(ConversionJobService).GetMethod("Reconcile", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(service, null);
                Assert.That(repository.FindByDownloadId(request.DownloadId).Status, Is.EqualTo(ConversionJobStatus.Queued));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void startup_recovery_must_not_rewrite_ready_output_while_its_worker_retries_acknowledgement(bool postgres)
        {
            WithRecovery(postgres, (database, scope) =>
            {
                var request = CreateRequest(scope.Root, "ready-owner");
                var repository = new ConversionJobRepository(database, new StubEventAggregator());
                using var committed = new ManualResetEventSlim();
                var allowAcknowledgement = 0;
                var recoveryWrites = 0;
                var recoveryThread = Environment.CurrentManagedThreadId;
                var proxy = InterfaceProxy.Proxy<IConversionJobRepository>((method, args) =>
                {
                    var result = method.Invoke(repository, args);
                    if (method.Name == nameof(IConversionJobRepository.SetFields) && args[0] is ConversionJob { Status: ConversionJobStatus.ReadyToImport })
                    {
                        if (Environment.CurrentManagedThreadId == recoveryThread) Interlocked.Increment(ref recoveryWrites);
                        committed.Set();
                        if (Volatile.Read(ref allowAcknowledgement) == 0) throw new SqliteException("Injected lost ready acknowledgement", 1);
                    }
                    return result;
                });
                var converter = new TokenRecordingConversionService();
                var service = scope.Service = CreateService(proxy, converter);
                try
                {
                    service.Enqueue(request);
                    converter.Release.Set();
                    typeof(ConversionJobService).GetMethod("DispatchQueuedJobs", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(service, null);
                    Assert.That(committed.Wait(TimeSpan.FromSeconds(5)), Is.True);
                    Assert.That(service.IsRunning(request.DownloadId), Is.True);
                    typeof(ConversionJobService).GetMethod("Reconcile", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(service, null);
                    Assert.That(recoveryWrites, Is.Zero, "A ready row may still belong to a worker saving its outcome");
                    Assert.That(repository.FindByDownloadId(request.DownloadId).Status, Is.EqualTo(ConversionJobStatus.ReadyToImport));
                }
                finally
                {
                    Volatile.Write(ref allowAcknowledgement, 1);
                    converter.Release.Set();
                    scope.Stop();
                }
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void retried_startup_recovery_must_not_clean_up_a_cancelling_encoder(bool postgres)
        {
            WithRecovery(postgres, (database, scope) =>
            {
                var request = CreateRequest(scope.Root, "held-stop");
                var repository = new ConversionJobRepository(database, new StubEventAggregator());
                var proxy = DispatchProxy.Create<IConversionJobRepository, FailingRepositoryProxy>();
                var failure = (FailingRepositoryProxy)(object)proxy;
                failure.Repository = repository;
                failure.Method = nameof(IConversionJobRepository.NonCompleted);
                failure.FailOnCall = 1;
                var converter = new TokenRecordingConversionService { HoldCancellation = true };
                var service = scope.Service = CreateService(proxy, converter);
                var reconcile = typeof(ConversionJobService).GetMethod("Reconcile", BindingFlags.NonPublic | BindingFlags.Instance);
                try
                {
                    service.Enqueue(request);
                    Assert.That(Assert.Throws<TargetInvocationException>(() => reconcile.Invoke(service, null)).InnerException,
                        Is.TypeOf<System.Data.DataException>());
                    typeof(ConversionJobService).GetMethod("DispatchQueuedJobs", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(service, null);
                    Assert.That(SpinWait.SpinUntil(() => converter.OptionsByDownloadId.ContainsKey(request.DownloadId), TimeSpan.FromSeconds(5)), Is.True);
                    File.WriteAllText(request.OutputPath, "encoder is still flushing");
                    Assert.That(service.Cancel(request.DownloadId, ConversionRemovalOrigin.UserAction), Is.True);
                    reconcile.Invoke(service, null);
                    Assert.That(repository.FindByDownloadId(request.DownloadId).Status, Is.EqualTo(ConversionJobStatus.Cancelling));
                    Assert.That(File.ReadAllText(request.OutputPath), Is.EqualTo("encoder is still flushing"));
                    Assert.That(service.IsRunning(request.DownloadId), Is.True);
                    converter.Release.Set();
                    Assert.That(SpinWait.SpinUntil(() => !service.IsRunning(request.DownloadId), TimeSpan.FromSeconds(5)), Is.True);
                    Assert.That(repository.FindByDownloadId(request.DownloadId).Status, Is.EqualTo(ConversionJobStatus.Cancelled));
                    Assert.That(Directory.Exists(request.WorkFolder), Is.False);
                }
                finally
                {
                    converter.Release.Set();
                    scope.Stop();
                }
            });
        }

        [Test]
        public void recovery_must_preserve_a_newer_failure_even_when_the_timestamp_is_equal(
            [Values(false, true)] bool postgres, [Values("startup", "lease", "ready")] string route)
        {
            WithRecovery(postgres, (database, scope) =>
            {
                var request = CreateRequest(scope.Root, "newer-failure");
                var repository = new ConversionJobRepository(database, new StubEventAggregator());
                repository.Insert(CreatePersistedJob(request, route == "ready" ? ConversionJobStatus.ReadyToImport : ConversionJobStatus.Converting,
                    DateTime.UtcNow.AddMinutes(-5)));
                var stale = repository.FindByDownloadId(request.DownloadId);
                var current = repository.FindByDownloadId(request.DownloadId);
                current.Status = ConversionJobStatus.Failed;
                current.Error = "newer encoder failure";
                // Deliberately equal timestamps: correctness must follow the changed state,
                // not the clock precision of this database or an assumed elapsed interval.
                repository.SetFields(current, job => job.Status, job => job.Error);
                var proxy = InterfaceProxy.Proxy<IConversionJobRepository>((method, args) =>
                    method.Name == nameof(IConversionJobRepository.NonCompleted) ||
                    (method.Name == nameof(IConversionJobRepository.ByStatus) && args[0] is ConversionJobStatus.Converting)
                        ? new List<ConversionJob> { stale } : method.Invoke(repository, args));
                var service = scope.Service = CreateService(proxy, new UnusedConversionService());
                if (route == "lease") service.RecoverExpiredLeases();
                else typeof(ConversionJobService).GetMethod("Reconcile", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(service, null);
                var saved = repository.FindByDownloadId(request.DownloadId);
                Assert.That(saved.Status, Is.EqualTo(ConversionJobStatus.Failed));
                Assert.That(saved.Error, Is.EqualTo("newer encoder failure"));
                Assert.That(saved.AttemptCount, Is.EqualTo(stale.AttemptCount));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void finalization_retry_must_not_release_a_replacement_workers_ownership(bool postgres)
        {
            WithRecovery(postgres, (database, scope) =>
            {
                var repository = new ConversionJobRepository(database, new StubEventAggregator());
                var converter = new TokenRecordingConversionService();
                using var finalizationRead = new ManualResetEventSlim();
                using var allowRetry = new ManualResetEventSlim();
                var planted = 0;
                ConversionJobService service = null;
                var emptyMedia = ImportLifecycleStubs.NoLibraryMediaFiles();
                var media = InterfaceProxy.Proxy<IMediaFileService>((method, args) =>
                {
                    if (method.Name == nameof(IMediaFileService.GetFilesByBook) && converter.CallsByDownloadId.ContainsKey("same-download") &&
                        !service.IsRunning("same-download") && Interlocked.CompareExchange(ref planted, 1, 0) == 0)
                    {
                        finalizationRead.Set();
                        if (!allowRetry.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Replacement worker was not admitted");
                        throw new SqliteException("Injected read failure after worker ownership was released", 1);
                    }
                    return method.Invoke(emptyMedia, args);
                });
                service = scope.Service = new ConversionJobService(TestCleanupDisk(), TestRootFolders(), media, repository, converter,
                    new ConversionTrackingService(new StubEventAggregator()),
                    DispatchProxy.Create<IManageCommandQueue, RecordingCommandQueueProxy>(), CreateConfig(), LogManager.GetCurrentClassLogger(),
                    outcomeRetryDelay: TimeSpan.FromMilliseconds(10));
                var dispatch = typeof(ConversionJobService).GetMethod("DispatchQueuedJobs", BindingFlags.NonPublic | BindingFlags.Instance);
                Task oldCompletion = null;
                try
                {
                    var first = CreateRequest(scope.Root, "same-download");
                    first.BookId = 42;
                    service.Enqueue(first);
                    dispatch.Invoke(service, null);
                    Assert.That(SpinWait.SpinUntil(() => converter.OptionsByDownloadId.ContainsKey(first.DownloadId), TimeSpan.FromSeconds(5)), Is.True);
                    var owners = (IDictionary)typeof(ConversionJobService).GetField("_activeConversions", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(service);
                    var oldOwner = owners[first.DownloadId];
                    oldCompletion = ((TaskCompletionSource)oldOwner.GetType().GetProperty("Completion").GetValue(oldOwner)).Task;
                    converter.Release.Set();
                    Assert.That(finalizationRead.Wait(TimeSpan.FromSeconds(5)), Is.True);
                    converter.Release.Reset();
                    service.Reset(first.DownloadId, repository.FindByDownloadId(first.DownloadId));
                    var next = CreateRequest(Path.Combine(scope.Root, "next"), first.DownloadId);
                    next.BookId = 42;
                    service.Enqueue(next);
                    dispatch.Invoke(service, null);
                    Assert.That(SpinWait.SpinUntil(() => converter.CallsByDownloadId[first.DownloadId] == 2, TimeSpan.FromSeconds(5)), Is.True);
                    allowRetry.Set();
                    Assert.That(oldCompletion.Wait(TimeSpan.FromSeconds(5)), Is.True);
                    Assert.That(service.IsRunning(next.DownloadId), Is.True, "A retry by the old worker must leave the replacement worker registered");
                    Assert.That(repository.FindByDownloadId(next.DownloadId).Status, Is.EqualTo(ConversionJobStatus.Converting));
                }
                finally
                {
                    allowRetry.Set();
                    converter.Release.Set();
                    scope.Stop();
                    oldCompletion?.Wait(TimeSpan.FromSeconds(5));
                }
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void startup_recovery_must_preserve_a_stop_after_its_snapshot_without_any_running_encoder(bool postgres)
        {
            WithRecovery(postgres, (database, scope) =>
            {
                var request = CreateRequest(scope.Root, "stopped-before-dispatch");
                var repository = new ConversionJobRepository(database, new StubEventAggregator());
                repository.Insert(CreatePersistedJob(request, ConversionJobStatus.Converting, DateTime.UtcNow));
                ConversionJobService service = null;
                var proxy = InterfaceProxy.Proxy<IConversionJobRepository>((method, args) =>
                {
                    var result = method.Invoke(repository, args);
                    if (method.Name == nameof(IConversionJobRepository.NonCompleted))
                    {
                        // The startup read has completed, but recovery has not consumed its rows.
                        // A real Stop at this boundary must remain authoritative even without dispatch.
                        Assert.That(service.IsRunning(request.DownloadId), Is.False);
                        Assert.That(service.Cancel(request.DownloadId, ConversionRemovalOrigin.UserAction), Is.True);
                        Assert.That(repository.FindByDownloadId(request.DownloadId).Status, Is.EqualTo(ConversionJobStatus.Cancelled));
                    }
                    return result;
                });
                service = scope.Service = CreateService(proxy, new UnusedConversionService());
                typeof(ConversionJobService).GetMethod("Reconcile", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(service, null);
                Assert.That(service.IsRunning(request.DownloadId), Is.False);
                Assert.That(repository.FindByDownloadId(request.DownloadId).Status, Is.EqualTo(ConversionJobStatus.Cancelled),
                    "Finishing startup before dispatch must not let an old snapshot undo a user's Stop");
                Assert.That(File.Exists(request.Sources[0].Path), Is.True);
            });
        }
    }
}
