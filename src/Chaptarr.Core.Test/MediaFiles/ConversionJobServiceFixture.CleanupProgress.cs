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
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Core.Test.MediaFiles
{
    public partial class ConversionJobServiceFixture
    {
        [Test]
        public void processing_notifications_do_not_consume_cleanup_pages_or_replace_poll_evidence([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-cleanup-pages-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var repository = new ConversionJobRepository(database, new StubEventAggregator());
                    var requests = Enumerable.Range(0, 150).Select(i =>
                    {
                        var request = CreateRequest(Path.Combine(directory, i.ToString()), "paged-" + i);
                        request.DownloadClientId = 4;
                        File.WriteAllText(request.OutputPath, "converted");
                        return request;
                    }).ToList();
                    SeedClientHistory(database, requests[0].DownloadId);
                    repository.InsertMany(requests.Select(request => CreatePersistedJob(request, ConversionJobStatus.Completed, DateTime.UtcNow)).ToList());
                    var reported = new Dictionary<int, HashSet<string>> { [4] = requests.Take(100).Select(request => request.DownloadId).ToHashSet(StringComparer.OrdinalIgnoreCase) };
                    var poll = new TrackedDownloadRefreshedEvent(new List<TrackedDownload>(), reported);
                    var processing = new TrackedDownloadRefreshedEvent(new List<TrackedDownload>());
                    var service = CreateService(repository, new UnusedConversionService());
                    service.Handle(poll);
                    service.Handle(processing);
                    service.Handle(poll);
                    service.Handle(processing);
                    Assert.Multiple(() =>
                    {
                        Assert.That(repository.All().Select(job => job.DownloadId), Is.EquivalentTo(reported[4]));
                        Assert.That(requests.Skip(100).Any(request => File.Exists(request.OutputPath)), Is.False, "Page two must be cleaned using actual poll evidence");
                        Assert.That(requests.Take(100).All(request => File.Exists(request.OutputPath)), Is.True);
                        Assert.That(requests.All(request => File.Exists(request.Sources[0].Path)), Is.True);
                        Assert.That(typeof(ConversionJobService).GetField("_lastDownloadRefresh", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(service), Is.SameAs(poll));
                    });
                }
                finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            });
        }

        private static int SeedOrphanHistory(IMainDatabase database, ConversionJobRequest request)
        {
            using var connection = database.OpenConnection();
            return connection.QuerySingle<int>(@"INSERT INTO ""History"" (""AuthorId"", ""BookId"", ""EditionId"", ""Quality"", ""Date"", ""EventType"", ""Data"", ""SourceTitle"", ""DownloadId"")
                VALUES (1, 1, 1, '{}', @date, @type, @data, 'Converted book', @id) RETURNING ""Id""", new
            {
                date = DateTime.UtcNow.AddDays(-8), type = (int)EntityHistoryEventType.BookFileConverted,
                data = JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["ConvertedPath"] = request.OutputPath,
                    ["SourcePaths"] = JsonSerializer.Serialize(request.Sources.Select(source => source.Path))
                }),
                id = DownloadIdentity.Key(request.DownloadId)
            });
        }

        private static ConfigService OrphanConfig(IMainDatabase database) => new(
            new ConfigRepository(database, new StubEventAggregator()), new StubEventAggregator(), null, LogManager.GetCurrentClassLogger());

        private static void ReviewOrphans(ConversionJobService service)
        {
            typeof(ConversionJobService).GetField("_orphanNotBefore", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(service, DateTime.MinValue);
            typeof(ConversionJobService).GetMethod("ReviewOrphanArtifacts", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(service, null);
        }

        [Test]
        public void deferred_orphans_do_not_starve_new_history_and_each_pass_is_bounded([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-deferred-pages-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var repository = new ConversionJobRepository(database, new StubEventAggregator());
                    var pageCounts = new List<int>();
                    var recordingRepository = InterfaceProxy.Proxy<IConversionJobRepository>((method, args) =>
                    {
                        var result = method.Invoke(repository, args);
                        if (method.Name == nameof(IConversionJobRepository.OrphanArtifacts))
                            pageCounts.Add(((List<ConversionOrphanCandidate>)result).Count);
                        return result;
                    });
                    var requests = Enumerable.Range(0, 121).Select(i =>
                    {
                        var request = CreateRequest(Path.Combine(directory, i.ToString()), "deferred-" + i);
                        request.DownloadClientId = 4;
                        File.WriteAllText(request.OutputPath, "converted");
                        return request;
                    }).ToList();
                    foreach (var request in requests.Take(120))
                    {
                        SeedOrphanHistory(database, request);
                        SeedClientHistory(database, request.DownloadId);
                    }
                    var config = OrphanConfig(database);
                    var service = CreateService(recordingRepository, new UnusedConversionService(), config: config);
                    ReviewOrphans(service);
                    ReviewOrphans(service);
                    Assert.That(JsonDocument.Parse(config.GetConversionOrphanReviewProgress()).RootElement.GetProperty("DeferredIds").GetArrayLength(), Is.EqualTo(120));
                    SeedOrphanHistory(database, requests[120]);
                    SeedClientHistory(database, requests[120].DownloadId, DownloadHistoryEventType.DownloadImported);
                    SeedClientHistory(database, requests[75].DownloadId, DownloadHistoryEventType.DownloadImported);
                    var stillReported = requests.Where((_, i) => i != 75 && i != 120).Select(request => request.DownloadId)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    service.Handle(new TrackedDownloadRefreshedEvent(new(), new Dictionary<int, HashSet<string>> { [4] = stillReported }));
                    ReviewOrphans(service);
                    Assert.Multiple(() =>
                    {
                        Assert.That(pageCounts, Is.EqualTo(new[] { 100, 70, 51 }));
                        Assert.That(File.Exists(requests[120].OutputPath), Is.False, "New history must not wait behind all deferred downloads");
                        Assert.That(File.Exists(requests[75].OutputPath), Is.False, "Deferred candidates must rotate fairly");
                        Assert.That(requests.Where((_, i) => i != 75 && i != 120).All(request => File.Exists(request.OutputPath)), Is.True);
                        Assert.That(requests.All(request => File.Exists(request.Sources[0].Path)), Is.True);
                    });
                }
                finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            });
        }

        [Test]
        public void resolved_orphan_history_is_not_revisited_after_restart_but_deferred_and_new_history_are([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-orphan-progress-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var repository = new ConversionJobRepository(database, new StubEventAggregator());
                    var requests = Enumerable.Range(0, 4).Select(i =>
                    {
                        var request = CreateRequest(Path.Combine(directory, i.ToString()), "orphan-" + i);
                        request.DownloadClientId = 4;
                        File.WriteAllText(request.OutputPath, "converted");
                        return request;
                    }).ToList();
                    Directory.Delete(requests[0].WorkFolder, true);
                    for (var i = 0; i < 3; i++)
                    {
                        SeedOrphanHistory(database, requests[i]);
                        SeedClientHistory(database, requests[i].DownloadId, i == 2 ? DownloadHistoryEventType.DownloadGrabbed : DownloadHistoryEventType.DownloadImported);
                    }
                    var config = OrphanConfig(database);
                    var service = CreateService(repository, new UnusedConversionService(), config: config);
                    service.Handle(new TrackedDownloadRefreshedEvent(new(), new Dictionary<int, HashSet<string>>
                    {
                        [4] = new(StringComparer.OrdinalIgnoreCase) { requests[2].DownloadId }
                    }));
                    ReviewOrphans(service);
                    Assert.That(File.Exists(requests[1].OutputPath), Is.False);
                    Assert.That(File.Exists(requests[2].OutputPath), Is.True);
                    var deferredId = JsonDocument.Parse(config.GetConversionOrphanReviewProgress()).RootElement.GetProperty("DeferredIds").EnumerateArray().Single().GetInt32();
                    // Reusing a settled path must not make old history a cleanup authority again.
                    foreach (var request in requests.Take(2))
                    {
                        Directory.CreateDirectory(request.WorkFolder);
                        File.WriteAllText(request.OutputPath, "new bytes at a previously settled path");
                    }
                    SeedClientHistory(database, requests[2].DownloadId, DownloadHistoryEventType.DownloadImported);
                    var newId = SeedOrphanHistory(database, requests[3]);
                    SeedClientHistory(database, requests[3].DownloadId, DownloadHistoryEventType.DownloadImported);
                    var persisted = OrphanConfig(database);
                    Assert.That(persisted.GetConversionOrphanReviewProgress(), Does.Contain(deferredId.ToString()));
                    var restarted = CreateService(repository, new UnusedConversionService(), config: persisted);
                    ReviewOrphans(restarted);
                    var saved = persisted.GetConversionOrphanReviewProgress();
                    ReviewOrphans(restarted);
                    ReviewOrphans(CreateService(repository, new UnusedConversionService(), config: OrphanConfig(database)));
                    Assert.Multiple(() =>
                    {
                        Assert.That(requests.Take(2).All(request => File.Exists(request.OutputPath)), Is.True, "Resolved paths must not be probed again");
                        Assert.That(requests.Skip(2).Any(request => File.Exists(request.OutputPath)), Is.False);
                        Assert.That(requests.All(request => File.Exists(request.Sources[0].Path)), Is.True);
                        Assert.That(JsonDocument.Parse(saved).RootElement.GetProperty("AfterId").GetInt32(), Is.EqualTo(newId));
                        Assert.That(JsonDocument.Parse(saved).RootElement.GetProperty("DeferredIds").GetArrayLength(), Is.Zero);
                        Assert.That(persisted.GetConversionOrphanReviewProgress(), Is.EqualTo(saved));
                        Assert.That(repository.All(), Is.Empty);
                    });
                }
                finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            });
        }
    }
}
