using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Chaptarr.Core.Test.Common;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Core.Test.MediaFiles
{
    public partial class ConversionJobServiceFixture
    {
        private static ConcurrentLogTarget CaptureOrphanLogs(LogFactory factory)
        {
            var sink = new ConcurrentLogTarget { Layout = "${level}|${message}" };
            var configuration = new LoggingConfiguration(factory);
            configuration.AddRuleForAllLevels(sink);
            factory.Configuration = configuration;
            return sink;
        }

        [Test]
        public void missing_orphan_workspace_waits_for_its_configured_library_root([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-orphan-mount-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var root = Path.Combine(directory, "library");
                    var offline = Path.Combine(directory, "offline");
                    var request = CreateRequest(Path.Combine(root, "Author", "Book"), "unmounted");
                    File.WriteAllText(request.OutputPath, "converted output");
                    var id = SeedOrphanHistory(database, request);
                    SeedClientHistory(database, request.DownloadId, DownloadHistoryEventType.DownloadImported);
                    var repository = new ConversionJobRepository(database, new StubEventAggregator());
                    var config = OrphanConfig(database);
                    var service = CreateService(repository, new UnusedConversionService(), config: config, roots: TestRootFolders(root));
                    Directory.Move(root, offline);
                    ReviewOrphans(service);
                    Assert.That(JsonDocument.Parse(config.GetConversionOrphanReviewProgress()).RootElement.GetProperty("DeferredIds").EnumerateArray().Select(value => value.GetInt32()), Is.EqualTo(new[] { id }),
                        "An existing ancestor outside the configured root is not mount evidence");
                    Directory.Move(offline, root);
                    MakeOrphanRetryDue(config, id);
                    ReviewOrphans(service);
                    Assert.Multiple(() =>
                    {
                        Assert.That(File.Exists(request.OutputPath), Is.False, "The deferred workspace is reconsidered after the mount returns");
                        Assert.That(File.Exists(request.Sources[0].Path), Is.True);
                        Assert.That(JsonDocument.Parse(config.GetConversionOrphanReviewProgress()).RootElement.GetProperty("DeferredIds").GetArrayLength(), Is.Zero);
                    });
                }
                finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            });
        }

        [Test]
        public void orphan_refusals_and_io_failures_back_off_across_restart_without_repeated_warnings(
            [Values(false, true)] bool postgres, [Values("validation", "ownership", "retained-path", "io")] string failure)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-orphan-backoff-" + Guid.NewGuid().ToString("N"));
                FileStream held = null;
                using var factory = new LogFactory();
                var logs = CaptureOrphanLogs(factory);
                try
                {
                    var request = CreateRequest(directory, "refused");
                    if (failure == "validation")
                    {
                        request.OutputPath = Path.Combine(request.WorkFolder, "nested", "deeper", "book.m4b");
                        Directory.CreateDirectory(Path.GetDirectoryName(request.OutputPath));
                    }
                    if (failure == "retained-path") request.OutputPath = Path.ChangeExtension(request.OutputPath, ".mp3");
                    File.WriteAllText(request.OutputPath, "retained output");
                    var receipt = Path.Combine(request.WorkFolder, "conversion-artifact.json");
                    if (failure is "ownership" or "io")
                        File.WriteAllText(receipt, JsonSerializer.Serialize(new ConversionArtifactManifest { DownloadId = "another-download", OutputPath = request.OutputPath }));
                    if (failure == "io") held = new FileStream(receipt, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    var id = SeedOrphanHistory(database, request);
                    SeedClientHistory(database, request.DownloadId, DownloadHistoryEventType.DownloadImported);
                    var repository = new ConversionJobRepository(database, new StubEventAggregator());
                    var config = OrphanConfig(database);
                    var service = CreateService(repository, new UnusedConversionService(), config: config, logger: factory.GetLogger("orphan-backoff"));
                    var before = DateTime.UtcNow;
                    ReviewOrphans(service);
                    var saved = config.GetConversionOrphanReviewProgress();
                    var due = JsonDocument.Parse(saved).RootElement.GetProperty("Retries").GetProperty(id.ToString()).GetProperty("NotBefore").GetDateTime();
                    Assert.That(due, Is.GreaterThanOrEqualTo(before.AddMinutes(59)));
                    ReviewOrphans(service);
                    config = OrphanConfig(database);
                    service = CreateService(repository, new UnusedConversionService(), config: config, logger: factory.GetLogger("orphan-restart"));
                    ReviewOrphans(service);
                    Assert.That(config.GetConversionOrphanReviewProgress(), Is.EqualTo(saved));
                    Assert.That(logs.Logs.Count(line => line.StartsWith("Warn|Retaining unverifiable old conversion workspace", StringComparison.Ordinal)), Is.EqualTo(1));
                    var progress = JsonNode.Parse(saved);
                    progress["Retries"][id.ToString()]["NotBefore"] = JsonValue.Create(DateTime.UtcNow.AddSeconds(-1));
                    config.SetConversionOrphanReviewProgress(progress.ToJsonString());
                    ReviewOrphans(service);
                    Assert.Multiple(() =>
                    {
                        Assert.That(logs.Logs.Count(line => line.StartsWith("Warn|Retaining unverifiable old conversion workspace", StringComparison.Ordinal)), Is.EqualTo(1), "An unchanged refusal warns once, including after restart");
                        Assert.That(JsonDocument.Parse(config.GetConversionOrphanReviewProgress()).RootElement.GetProperty("Retries").GetProperty(id.ToString()).GetProperty("NotBefore").GetDateTime(), Is.GreaterThan(before.AddMinutes(59)));
                        Assert.That(File.ReadAllText(request.OutputPath), Is.EqualTo("retained output"));
                        Assert.That(File.Exists(request.Sources[0].Path), Is.True);
                        Assert.That(repository.All(), Is.Empty);
                    });
                }
                finally
                {
                    held?.Dispose();
                    if (Directory.Exists(directory)) Directory.Delete(directory, true);
                }
            });
        }

        [Test]
        public void unchanged_deferred_orphans_rotate_without_config_writes_or_routine_info_logs([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-orphan-idle-" + Guid.NewGuid().ToString("N"));
                using var factory = new LogFactory();
                var logs = CaptureOrphanLogs(factory);
                try
                {
                    foreach (var index in Enumerable.Range(0, 60))
                    {
                        var request = CreateRequest(Path.Combine(directory, index.ToString()), "pending-" + index);
                        SeedOrphanHistory(database, request);
                        SeedClientHistory(database, request.DownloadId);
                    }
                    var config = OrphanConfig(database);
                    var writes = 0;
                    var recordingConfig = InterfaceProxy.Proxy<IConfigService>((method, args) =>
                    {
                        if (method.Name == nameof(IConfigService.SetConversionOrphanReviewProgress)) writes++;
                        return method.Invoke(config, args);
                    });
                    var service = CreateService(new ConversionJobRepository(database, new StubEventAggregator()), new UnusedConversionService(),
                        config: recordingConfig, logger: factory.GetLogger("orphan-idle"));
                    ReviewOrphans(service);
                    var saved = config.GetConversionOrphanReviewProgress();
                    Assert.That(writes, Is.EqualTo(1));
                    writes = 0;
                    logs.Logs.Clear();
                    for (var i = 0; i < 3; i++) ReviewOrphans(service);
                    Assert.Multiple(() =>
                    {
                        Assert.That(writes, Is.Zero, "Rotation alone is not a persistent change");
                        Assert.That(config.GetConversionOrphanReviewProgress(), Is.EqualTo(saved));
                        Assert.That(logs.Logs.Where(line => line.Contains("Conversion orphan review:") &&
                            double.Parse(line.Split("elapsedMs=")[1].Split(',')[0], System.Globalization.CultureInfo.InvariantCulture) < 1000)
                            .All(line => line.StartsWith("Debug|", StringComparison.Ordinal)), Is.True);
                    });
                }
                finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            });
        }

        [Test]
        public void corrupt_orphan_progress_warns_once_and_recovers_cleanup([Values(false, true)] bool postgres,
            [Values("not-json", "null", "{\"DeferredIds\":null}", "{\"Retries\":null}")] string corrupt)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-orphan-corrupt-" + Guid.NewGuid().ToString("N"));
                using var factory = new LogFactory();
                var logs = CaptureOrphanLogs(factory);
                try
                {
                    var request = CreateRequest(directory, "corrupt-progress");
                    File.WriteAllText(request.OutputPath, "converted output");
                    SeedOrphanHistory(database, request);
                    SeedClientHistory(database, request.DownloadId, DownloadHistoryEventType.DownloadImported);
                    var config = OrphanConfig(database);
                    config.SetConversionOrphanReviewProgress(corrupt);
                    var repository = new ConversionJobRepository(database, new StubEventAggregator());
                    var service = CreateService(repository, new UnusedConversionService(), config: config, logger: factory.GetLogger("orphan-corrupt"));
                    Assert.DoesNotThrow(() => ReviewOrphans(service));
                    ReviewOrphans(service);
                    ReviewOrphans(CreateService(repository, new UnusedConversionService(), config: OrphanConfig(database), logger: factory.GetLogger("orphan-restarted")));
                    Assert.Multiple(() =>
                    {
                        Assert.That(logs.Logs.Count(line => line.StartsWith("Warn|Invalid conversion orphan review progress", StringComparison.Ordinal)), Is.EqualTo(1));
                        Assert.That(File.Exists(request.OutputPath), Is.False);
                        Assert.That(File.Exists(request.Sources[0].Path), Is.True);
                        Assert.That(JsonDocument.Parse(config.GetConversionOrphanReviewProgress()).RootElement.GetProperty("DeferredIds").GetArrayLength(), Is.Zero);
                    });
                }
                finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            });
        }

        [Test]
        public void reset_with_an_unreadable_ready_request_reports_cleanup_failure_without_throwing([Values(false, true)] bool postgres,
            [Values("{", "null")] string invalid)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-reset-unreadable-" + Guid.NewGuid().ToString("N"));
                using var factory = new LogFactory();
                var logs = CaptureOrphanLogs(factory);
                try
                {
                    var request = CreateRequest(directory, "unreadable");
                    File.WriteAllText(request.OutputPath, "protected output");
                    var repository = new ConversionJobRepository(database, new StubEventAggregator());
                    var job = CreatePersistedJob(request, ConversionJobStatus.ReadyToImport, DateTime.UtcNow);
                    job.RequestJson = invalid;
                    job.Error = "original error";
                    job = repository.Insert(job);
                    var service = CreateService(repository, new UnusedConversionService(), logger: factory.GetLogger("reset-corrupt"));
                    Assert.DoesNotThrow(() => service.Reset(request.DownloadId, job));
                    var saved = repository.Get(job.Id);
                    Assert.Multiple(() =>
                    {
                        Assert.That(saved.RequestJson, Is.EqualTo(invalid));
                        Assert.That(saved.Status, Is.EqualTo(ConversionJobStatus.ReadyToImport));
                        Assert.That(saved.Error, Is.EqualTo("original error"));
                        Assert.That(saved.CleanupAttention, Is.True);
                        Assert.That(logs.Logs.Any(line => line.Contains("request is unreadable, so its workspace was retained")), Is.True);
                        Assert.That(File.ReadAllText(request.OutputPath), Is.EqualTo("protected output"));
                        Assert.That(File.Exists(request.Sources[0].Path), Is.True);
                    });
                }
                finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            });
        }
    }
}
