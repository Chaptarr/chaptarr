using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Core.Test.MediaFiles
{
    public partial class ConversionJobServiceFixture
    {
        private static void MakeOrphanRetryDue(IConfigService config, int id)
        {
            var progress = JsonNode.Parse(config.GetConversionOrphanReviewProgress());
            if (progress["Retries"][id.ToString()] == null) return;
            progress["Retries"][id.ToString()]["NotBefore"] = JsonValue.Create(DateTime.UtcNow.AddSeconds(-1));
            config.SetConversionOrphanReviewProgress(progress.ToJsonString());
        }

        [Test]
        public void deleted_client_orphans_keep_attached_work_then_settle_without_a_poll(
            [Values(false, true)] bool postgres,
            [Values(DownloadHistoryEventType.DownloadFailed, DownloadHistoryEventType.DownloadIgnored)] DownloadHistoryEventType state)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-deleted-client-orphan-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var request = CreateRequest(directory, "deleted-client");
                    File.WriteAllText(request.OutputPath, "converted output");
                    SeedOrphanHistory(database, request);
                    SeedClientHistory(database, request.DownloadId, state, clientExists: false);
                    var repository = new ConversionJobRepository(database, new StubEventAggregator());
                    var service = CreateService(repository, new UnusedConversionService(), config: OrphanConfig(database));
                    service.Handle(new TrackedDownloadRefreshedEvent(new List<TrackedDownload>
                    {
                        new() { DownloadClient = 4, DownloadItem = new DownloadClientItem { DownloadId = request.DownloadId }, State = TrackedDownloadState.ImportPending }
                    }, new Dictionary<int, HashSet<string>>()));
                    ReviewOrphans(service);
                    Assert.That(File.Exists(request.OutputPath), Is.True, "A tracked download wins even when its client was deleted");
                    service.Handle(new TrackedDownloadRefreshedEvent(new(), new Dictionary<int, HashSet<string>>()));
                    ReviewOrphans(service);
                    Assert.Multiple(() =>
                    {
                        Assert.That(File.Exists(request.OutputPath), Is.False, "A deleted client cannot poll and must not strand terminal output");
                        Assert.That(File.Exists(request.Sources[0].Path), Is.True);
                        Assert.That(repository.All(), Is.Empty);
                    });
                }
                finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            });
        }

        [Test]
        public void orphan_with_multiple_clients_warns_once_and_stays_deferred_after_successful_polls(
            [Values(false, true)] bool postgres, [Values("unpolled", "reported", "tracked-deleted", "both-deleted")] string blocked)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-multiple-client-orphan-" + Guid.NewGuid().ToString("N"));
                using var factory = new LogFactory();
                var logs = CaptureOrphanLogs(factory);
                try
                {
                    var request = CreateRequest(directory, "multiple-client");
                    File.WriteAllText(request.OutputPath, "converted output");
                    var id = SeedOrphanHistory(database, request);
                    SeedClientHistory(database, request.DownloadId, DownloadHistoryEventType.DownloadImported, clientExists: blocked != "both-deleted");
                    SeedClientHistory(database, request.DownloadId, DownloadHistoryEventType.DownloadFailed, clientExists: blocked is not ("tracked-deleted" or "both-deleted"), clientId: 5);
                    var repository = new ConversionJobRepository(database, new StubEventAggregator());
                    var config = OrphanConfig(database);
                    var service = CreateService(repository, new UnusedConversionService(), config: config, logger: factory.GetLogger("multiple-clients"));
                    var reported = new Dictionary<int, HashSet<string>> { [4] = new() };
                    if (blocked == "reported") reported[5] = new(StringComparer.OrdinalIgnoreCase) { request.DownloadId };
                    var tracked = new List<TrackedDownload>();
                    if (blocked == "tracked-deleted") tracked.Add(new() { DownloadClient = 5, DownloadItem = new DownloadClientItem { DownloadId = request.DownloadId }, State = TrackedDownloadState.ImportPending });
                    service.Handle(new TrackedDownloadRefreshedEvent(tracked, reported));
                    ReviewOrphans(service);
                    Assert.That(File.Exists(request.OutputPath), Is.True, "One client's Imported event cannot override another client's attachment or missing poll");
                    var saved = config.GetConversionOrphanReviewProgress();
                    Assert.That(JsonDocument.Parse(saved).RootElement.GetProperty("Retries").TryGetProperty(id.ToString(), out var retry), Is.True, "Unresolved multi-client evidence needs a durable retry");
                    Assert.That(retry.GetProperty("NotBefore").GetDateTime(), Is.GreaterThan(DateTime.UtcNow.AddMinutes(59)));
                    Assert.That(retry.GetProperty("Error").GetString(), Does.Contain("does not identify one download client"));
                    ReviewOrphans(service);
                    Assert.That(config.GetConversionOrphanReviewProgress(), Is.EqualTo(saved));
                    Assert.That(logs.Logs.Count(line => line.StartsWith("Warn|Retaining unverifiable old conversion workspace", StringComparison.Ordinal)), Is.EqualTo(1));
                    config = OrphanConfig(database);
                    service = CreateService(repository, new UnusedConversionService(), config: config, logger: factory.GetLogger("multiple-clients-restart"));
                    ReviewOrphans(service);
                    Assert.That(config.GetConversionOrphanReviewProgress(), Is.EqualTo(saved), "The retry survives restart");
                    service.Handle(new TrackedDownloadRefreshedEvent(new(), new Dictionary<int, HashSet<string>> { [4] = new(), [5] = new() }));
                    MakeOrphanRetryDue(config, id);
                    ReviewOrphans(service);
                    Assert.Multiple(() =>
                    {
                        Assert.That(File.Exists(request.OutputPath), Is.True, "Successful polls do not disambiguate artifact ownership");
                        Assert.That(File.ReadAllText(request.OutputPath), Is.EqualTo("converted output"));
                        Assert.That(logs.Logs.Count(line => line.StartsWith("Warn|Retaining unverifiable old conversion workspace", StringComparison.Ordinal)), Is.EqualTo(1), "The same ambiguity must not warn again at each hourly retry");
                        Assert.That(JsonDocument.Parse(config.GetConversionOrphanReviewProgress()).RootElement.GetProperty("Retries").GetProperty(id.ToString()).GetProperty("NotBefore").GetDateTime(), Is.GreaterThan(DateTime.UtcNow.AddMinutes(59)));
                        Assert.That(File.Exists(request.Sources[0].Path), Is.True);
                        Assert.That(repository.All(), Is.Empty);
                    });
                }
                finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            });
        }

        [Test]
        public void orphan_root_availability_controls_retry_and_settlement(
            [Values(false, true)] bool postgres, [Values("empty", "missing", "unknown")] string unavailable)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-orphan-root-" + Guid.NewGuid().ToString("N"));
                using var factory = new LogFactory();
                var logs = CaptureOrphanLogs(factory);
                try
                {
                    var root = Path.Combine(directory, "library");
                    var offline = Path.Combine(directory, "offline");
                    var request = CreateRequest(Path.Combine(root, "Author", "Book"), "mount");
                    File.WriteAllText(request.OutputPath, "converted output");
                    var id = SeedOrphanHistory(database, request);
                    SeedClientHistory(database, request.DownloadId, DownloadHistoryEventType.DownloadImported);
                    var repository = new ConversionJobRepository(database, new StubEventAggregator());
                    var config = OrphanConfig(database);
                    var service = CreateService(repository, new UnusedConversionService(), config: config, logger: factory.GetLogger("orphan-root"),
                        roots: TestRootFolders(unavailable == "unknown" ? Path.Combine(directory, "different-root") : root));
                    Directory.Move(root, offline);
                    if (unavailable == "empty") Directory.CreateDirectory(root);
                    ReviewOrphans(service);
                    var saved = config.GetConversionOrphanReviewProgress();
                    var progress = JsonDocument.Parse(saved).RootElement;
                    if (unavailable == "unknown")
                    {
                        Assert.That(progress.GetProperty("AfterId").GetInt32(), Is.GreaterThanOrEqualTo(id));
                        Assert.That(progress.GetProperty("DeferredIds").GetArrayLength(), Is.Zero);
                        Assert.That(progress.GetProperty("Retries").EnumerateObject(), Is.Empty);
                        Assert.That(repository.All(), Is.Empty);
                        Assert.That(logs.Logs.Any(line => line.StartsWith("Warn|", StringComparison.Ordinal)), Is.False);
                        Assert.That(File.Exists(Path.Combine(offline, Path.GetRelativePath(root, request.OutputPath))), Is.True,
                            "Settling an absent unconfigured path must not delete files at another location");
                        Assert.That(File.Exists(Path.Combine(offline, Path.GetRelativePath(root, request.Sources[0].Path))), Is.True);
                        ReviewOrphans(service);
                        Assert.That(config.GetConversionOrphanReviewProgress(), Is.EqualTo(saved));
                        return;
                    }
                    Assert.That(progress.GetProperty("DeferredIds").EnumerateArray().Select(value => value.GetInt32()), Does.Contain(id), "A missing workspace on unavailable storage is not resolved");
                    Assert.That(progress.GetProperty("Retries").TryGetProperty(id.ToString(), out var retry), Is.True, "Unavailable roots need a durable retry");
                    Assert.That(retry.GetProperty("NotBefore").GetDateTime(), Is.GreaterThan(DateTime.UtcNow.AddMinutes(59)));
                    ReviewOrphans(service);
                    Assert.That(config.GetConversionOrphanReviewProgress(), Is.EqualTo(saved));
                    Assert.That(logs.Logs.Count(line => line.StartsWith("Warn|Retaining unverifiable old conversion workspace", StringComparison.Ordinal)), Is.EqualTo(1));
                    if (Directory.Exists(root)) Directory.Delete(root);
                    Directory.Move(offline, root);
                    MakeOrphanRetryDue(config, id);
                    ReviewOrphans(service);
                    Assert.Multiple(() =>
                    {
                        Assert.That(File.Exists(request.OutputPath), Is.False);
                        Assert.That(File.Exists(request.Sources[0].Path), Is.True);
                        Assert.That(JsonDocument.Parse(config.GetConversionOrphanReviewProgress()).RootElement.GetProperty("DeferredIds").GetArrayLength(), Is.Zero);
                    });
                }
                finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            });
        }

        [Test]
        public void attention_job_on_an_empty_mount_is_preserved_until_storage_recovers([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-attention-root-" + Guid.NewGuid().ToString("N"));
                using var factory = new LogFactory();
                var logs = CaptureOrphanLogs(factory);
                try
                {
                    var root = Path.Combine(directory, "library");
                    var offline = Path.Combine(directory, "offline");
                    var request = CreateRequest(Path.Combine(root, "Author", "Book"), "attention-mount");
                    File.WriteAllText(request.OutputPath, "converted output");
                    request.CleanupError = "earlier cleanup failure";
                    var repository = new ConversionJobRepository(database, new StubEventAggregator());
                    var job = CreatePersistedJob(request, ConversionJobStatus.ReadyToImport, DateTime.UtcNow);
                    job.Error = "original conversion diagnostic";
                    job = repository.Insert(job);
                    var service = CreateService(repository, new UnusedConversionService(), roots: TestRootFolders(root), logger: factory.GetLogger("attention-root"));
                    Directory.Move(root, offline);
                    Directory.CreateDirectory(root);
                    ReviewAttention(service);
                    var saved = repository.FindByDownloadId(request.DownloadId);
                    Assert.That(saved, Is.Not.Null, "An empty mount must not erase the conversion record");
                    Assert.That(saved.Status, Is.EqualTo(ConversionJobStatus.ReadyToImport));
                    Assert.That(saved.Error, Is.EqualTo(job.Error));
                    Assert.That(JsonSerializer.Deserialize<ConversionJobRequest>(saved.RequestJson).CleanupNotBefore, Is.GreaterThan(DateTime.UtcNow.AddMinutes(59)));
                    ReviewAttention(service);
                    Assert.That(repository.Get(job.Id).RequestJson, Is.EqualTo(saved.RequestJson));
                    Assert.That(logs.Logs.Count(line => line.StartsWith("Warn|Conversion cleanup retained:", StringComparison.Ordinal)), Is.EqualTo(1));
                    Directory.Delete(root);
                    Directory.Move(offline, root);
                    Assert.That(File.ReadAllText(request.OutputPath), Is.EqualTo("converted output"));
                    Directory.Delete(request.WorkRoot, true);
                    MakeCleanupDue(repository, request.DownloadId);
                    ReviewAttention(service);
                    Assert.That(repository.FindByDownloadId(request.DownloadId), Is.Null, "An actual external removal is settled after storage returns");
                    Assert.That(File.Exists(request.Sources[0].Path), Is.True);
                }
                finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            });
        }
    }
}
