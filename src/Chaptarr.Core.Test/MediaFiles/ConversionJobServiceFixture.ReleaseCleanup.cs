using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Chaptarr.Core.Test.Common;
using Dapper;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.MediaFiles
{
    public partial class ConversionJobServiceFixture
    {
        [Test]
        public void removal_routes_keep_cleanup_owed_on_an_empty_mount_then_recover(
            [Values(false, true)] bool postgres, [Values("queue", "lifecycle", "retry", "regrab", "bonus", "completed-event", "failed-event", "ignored-event", "removed-event", "reset", "attention")] string route)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-removal-mount-" + Guid.NewGuid().ToString("N"));
                using var factory = new LogFactory();
                var logs = CaptureOrphanLogs(factory);
                try
                {
                    var root = Path.Combine(directory, "library");
                    var offline = Path.Combine(directory, "offline");
                    var request = CreateRequest(Path.Combine(root, "Author", "Book"), "removal-mount");
                    request.DownloadClientId = 4;
                    request.DiscardRequested = route is "retry" or "regrab" or "attention";
                    if (route == "attention") request.CleanupError = "Earlier cleanup failed";
                    File.WriteAllText(request.OutputPath, "converted output");
                    var repository = new ConversionJobRepository(database, new StubEventAggregator());
                    repository.Insert(CreatePersistedJob(request, ConversionJobStatus.Completed, DateTime.UtcNow));
                    SeedClientHistory(database, request.DownloadId, DownloadHistoryEventType.DownloadFailed);
                    ConversionJobService service = null;
                    var disk = TestCleanupDisk(() => Assert.That(System.Threading.Monitor.IsEntered(
                        typeof(ConversionJobService).GetField("_sync", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(service)), Is.False,
                        "The library availability probe must not hold the conversion lock"));
                    service = CreateService(repository, new UnusedConversionService(), roots: TestRootFolders(root), logger: factory.GetLogger("removal-mount"), disk: disk);
                    Directory.Move(root, offline);
                    Directory.CreateDirectory(root);

                    var tracked = new TrackedDownload { DownloadClient = 4, DownloadItem = new DownloadClientItem { DownloadId = request.DownloadId } };
                    void Remove(bool unavailable)
                    {
                        if (route == "reset")
                        {
                            if (unavailable) service.Reset(request.DownloadId, repository.FindByDownloadId(request.DownloadId));
                            else typeof(ConversionJobService).GetMethod("RetryDiscardCleanup", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(service, null);
                            return;
                        }
                        if (route == "attention")
                        {
                            typeof(ConversionJobService).GetField("_attentionNotBefore", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(service, DateTime.MinValue);
                            typeof(ConversionJobService).GetMethod("ReviewCleanupAttention", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(service, null);
                            return;
                        }
                        if (route == "completed-event") { service.Handle(new DownloadCompletedEvent(tracked, 0)); return; }
                        if (route == "failed-event") { service.Handle(new DownloadFailedEvent { TrackedDownload = tracked }); return; }
                        if (route == "ignored-event") { service.Handle(new DownloadIgnoredEvent { TrackedDownload = tracked }); return; }
                        if (route == "removed-event") { service.Handle(new TrackedDownloadsRemovedEvent(new() { tracked })); return; }
                        if (route == "queue")
                        {
                            var action = service.PrepareArtifactRemoval(request.DownloadId, ConversionRemovalOrigin.UserAction);
                            if (unavailable) Assert.Throws<ConversionCleanupRefusedException>(() => action());
                            else action();
                        }
                        else if (route == "bonus")
                        {
                            if (unavailable) Assert.Throws<ConversionCleanupRefusedException>(() => service.RetainCompletedOutput(request.DownloadId, request.Sources.Select(source => source.Path).ToList(), "refused", discardOutput: true));
                            else service.RetainCompletedOutput(request.DownloadId, request.Sources.Select(source => source.Path).ToList(), "refused", discardOutput: true);
                        }
                        else if (route == "lifecycle")
                            service.Handle(new TrackedDownloadRefreshedEvent(new(), new Dictionary<int, HashSet<string>> { [4] = new() }));
                        else if (route == "regrab")
                            service.Handle(new BookGrabbedEvent(new RemoteBook()) { DownloadId = request.DownloadId });
                        else
                            typeof(ConversionJobService).GetMethod("RetryDiscardCleanup", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(service, null);
                    }

                    Remove(true);
                    var kept = repository.FindByDownloadId(request.DownloadId);
                    Assert.That(kept, Is.Not.Null);
                    var saved = JsonSerializer.Deserialize<ConversionJobRequest>(kept.RequestJson);
                    Assert.That(saved.DiscardRequested, Is.True);
                    Assert.That(saved.CleanupError, Does.Contain("missing or empty"));
                    Assert.That(saved.CleanupNotBefore, Is.GreaterThan(DateTime.UtcNow.AddMinutes(59)));
                    for (var retry = 0; retry < 5; retry++) Remove(true);
                    Assert.That(logs.Logs.Count(line => line.StartsWith("Warn|Conversion cleanup retained:", StringComparison.Ordinal)), Is.EqualTo(1));
                    Assert.That(repository.FindByDownloadId(request.DownloadId), Is.Not.Null, "The absent-path backoff bypass must still verify the mount");

                    Directory.Delete(root);
                    Directory.Move(offline, root);
                    MakeCleanupDue(repository, request.DownloadId);
                    Remove(false);
                    Assert.That(repository.FindByDownloadId(request.DownloadId), Is.Null);
                    Assert.That(File.Exists(request.OutputPath), Is.False);
                    Assert.That(File.Exists(request.Sources[0].Path), Is.True);
                }
                finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            });
        }

        [Test]
        public void startup_prune_does_not_forget_cancelled_work_on_an_empty_mount([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-prune-mount-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var root = Path.Combine(directory, "library");
                    var request = CreateRequest(Path.Combine(root, "Author", "Book"), "prune-mount");
                    var repository = new ConversionJobRepository(database, new StubEventAggregator());
                    var job = CreatePersistedJob(request, ConversionJobStatus.Cancelled, DateTime.UtcNow.AddDays(-9));
                    job.CompletedAt = DateTime.UtcNow.AddDays(-9);
                    repository.Insert(job);
                    Directory.Move(root, Path.Combine(directory, "offline"));
                    Directory.CreateDirectory(root);
                    var service = CreateService(repository, new UnusedConversionService(), roots: TestRootFolders(root));
                    var reconcile = typeof(ConversionJobService).GetMethod("Reconcile", BindingFlags.NonPublic | BindingFlags.Instance);
                    reconcile.Invoke(service, null);
                    Assert.That(repository.FindByDownloadId(request.DownloadId), Is.Not.Null);
                    File.WriteAllText(Path.Combine(root, "available.txt"), "mounted root, workspace genuinely absent");
                    reconcile.Invoke(service, null);
                    Assert.That(repository.FindByDownloadId(request.DownloadId), Is.Null);
                }
                finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            });
        }

        [Test]
        public void nonterminal_orphan_history_uses_actual_client_attachment(
            [Values(false, true)] bool postgres,
            [Values(DownloadHistoryEventType.DownloadGrabbed, DownloadHistoryEventType.DownloadImportIncomplete)] DownloadHistoryEventType state,
            [Values("tracked", "reported", "absent", "unpolled", "disabled", "deleted")] string client)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-orphan-client-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var request = CreateRequest(directory, "orphan-client");
                    File.WriteAllText(request.OutputPath, "converted output");
                    SeedOrphanHistory(database, request);
                    SeedClientHistory(database, request.DownloadId, state, clientExists: client != "deleted");
                    if (client == "disabled")
                    {
                        using var connection = database.OpenConnection();
                        connection.Execute("UPDATE \"DownloadClients\" SET \"Enable\" = @enabled WHERE \"Id\" = 4", new { enabled = false });
                    }
                    var repository = new ConversionJobRepository(database, new StubEventAggregator());
                    var service = CreateService(repository, new UnusedConversionService(), config: OrphanConfig(database));
                    var reported = new Dictionary<int, HashSet<string>>();
                    if (client is "absent" or "tracked" or "reported") reported[4] = client == "reported" ? new(StringComparer.OrdinalIgnoreCase) { request.DownloadId } : new(StringComparer.OrdinalIgnoreCase);
                    var tracked = new List<TrackedDownload>();
                    if (client == "tracked") tracked.Add(new() { DownloadClient = 4, DownloadItem = new() { DownloadId = request.DownloadId }, State = TrackedDownloadState.ImportPending });
                    service.Handle(new TrackedDownloadRefreshedEvent(tracked, reported));
                    ReviewOrphans(service);
                    Assert.That(File.Exists(request.OutputPath), Is.EqualTo(client is not ("absent" or "deleted")));
                    Assert.That(File.Exists(request.Sources[0].Path), Is.True);
                }
                finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            });
        }

        [Test]
        public void stale_imported_event_does_not_settle_a_newer_orphan_after_restart([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-orphan-stale-import-" + Guid.NewGuid().ToString("N"));
                using var factory = new LogFactory();
                var logs = CaptureOrphanLogs(factory);
                try
                {
                    var request = CreateRequest(directory, "stale-import");
                    File.WriteAllText(request.OutputPath, "new output");
                    var id = SeedOrphanHistory(database, request);
                    SeedClientHistory(database, request.DownloadId, DownloadHistoryEventType.DownloadImported);
                    using (var connection = database.OpenConnection()) connection.Execute("UPDATE \"DownloadHistory\" SET \"Date\" = @old", new { old = DateTime.UtcNow.AddDays(-10) });
                    var repository = new ConversionJobRepository(database, new StubEventAggregator());
                    var config = OrphanConfig(database);
                    var service = CreateService(repository, new UnusedConversionService(), config: config, logger: factory.GetLogger("stale-event"));
                    service.Handle(new TrackedDownloadRefreshedEvent(new(), new Dictionary<int, HashSet<string>> { [4] = new() }));
                    ReviewOrphans(service);
                    MakeOrphanRetryDue(config, id);
                    ReviewOrphans(service);
                    Assert.That(File.Exists(request.OutputPath), Is.True);
                    var retry = JsonDocument.Parse(config.GetConversionOrphanReviewProgress()).RootElement.GetProperty("Retries").GetProperty(id.ToString());
                    Assert.That(retry.GetProperty("Error").GetString(), Does.Contain("predates this conversion"));
                    Assert.That(retry.GetProperty("NotBefore").GetDateTime(), Is.GreaterThan(DateTime.UtcNow.AddMinutes(59)));
                    Assert.That(logs.Logs.Count(line => line.StartsWith("Warn|Retaining unverifiable old conversion workspace", StringComparison.Ordinal)), Is.EqualTo(1));
                }
                finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            });
        }

        [Test]
        public void orphan_without_one_known_client_warns_once_and_retries_hourly([Values(false, true)] bool postgres, [Values(false, true)] bool zeroClient)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-orphan-no-client-" + Guid.NewGuid().ToString("N"));
                using var factory = new LogFactory();
                var logs = CaptureOrphanLogs(factory);
                try
                {
                    var request = CreateRequest(directory, "no-client");
                    File.WriteAllText(request.OutputPath, "converted output");
                    var id = SeedOrphanHistory(database, request);
                    if (zeroClient) SeedClientHistory(database, request.DownloadId, DownloadHistoryEventType.DownloadFailed, clientExists: false, clientId: 0);
                    var repository = new ConversionJobRepository(database, new StubEventAggregator());
                    var config = OrphanConfig(database);
                    var service = CreateService(repository, new UnusedConversionService(), config: config, logger: factory.GetLogger("no-client"));
                    ReviewOrphans(service);
                    MakeOrphanRetryDue(config, id);
                    ReviewOrphans(service);
                    Assert.That(File.Exists(request.OutputPath), Is.True);
                    var retry = JsonDocument.Parse(config.GetConversionOrphanReviewProgress()).RootElement.GetProperty("Retries").GetProperty(id.ToString());
                    Assert.That(retry.GetProperty("Error").GetString(), Does.Contain("does not identify one download client"));
                    Assert.That(retry.GetProperty("NotBefore").GetDateTime(), Is.GreaterThan(DateTime.UtcNow.AddMinutes(59)));
                    Assert.That(logs.Logs.Count(line => line.StartsWith("Warn|Retaining unverifiable old conversion workspace", StringComparison.Ordinal)), Is.EqualTo(1));
                }
                finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            });
        }
    }
}
