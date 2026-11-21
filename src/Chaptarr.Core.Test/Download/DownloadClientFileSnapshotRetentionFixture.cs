using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients.QBittorrent;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Housekeeping.Housekeepers;
using NzbDrone.Core.Messaging.Events;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class DownloadClientFileSnapshotRetentionFixture
    {
        [TestCase(false)]
        [TestCase(true)]
        public void inventory_survives_age_outage_and_restart_until_confirmed_removal(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var events = Proxy<IEventAggregator>((method, args) => null);
                var clients = new DownloadClientRepository(database, events);
                var client = clients.Insert(new DownloadClientDefinition
                {
                    Name = "Fixture client", Implementation = "QBittorrent", ConfigContract = "QBittorrentSettings",
                    Settings = new QBittorrentSettings(), Enable = true
                });
                var repository = new DownloadClientFileSnapshotRepository(database, events);
                var snapshot = repository.Insert(new DownloadClientFileSnapshot
                {
                    DownloadClientId = client.Id, DownloadId = "tracked", Title = "Multipart book",
                    OutputPath = "/downloads/book", FilePaths = new List<string> { "/downloads/book/part1.mp3", "/downloads/book/part2.mp3" },
                    CreatedAt = DateTime.UtcNow.AddDays(-40), LastUpdated = DateTime.UtcNow.AddDays(-30),
                    Source = "disk", Confidence = DownloadClientFileListConfidence.Disk
                });
                var service = new DownloadClientFileSnapshotService(repository, null, LogManager.GetCurrentClassLogger());
                new CleanupOrphanedDownloadClientFileSnapshots(repository).Clean();
                var item = new DownloadClientItem { DownloadId = "tracked", DownloadClientInfo = new DownloadClientItemClientInfo { Id = client.Id } };
                service.ApplySnapshot(item);
                Assert.That(item.FilePaths, Is.EqualTo(snapshot.FilePaths), "A restart must recover the original list, including a vanished part");

                service.Handle(new TrackedDownloadRefreshedEvent(new(), new Dictionary<int, HashSet<string>>()));
                Assert.That(repository.All(), Has.Count.EqualTo(1), "A failed poll has no authoritative removal evidence");
                service.Handle(new TrackedDownloadRefreshedEvent(new(), new Dictionary<int, HashSet<string>>
                {
                    [client.Id] = new() { "TRACKED" }
                }));
                Assert.That(repository.All(), Has.Count.EqualTo(1), "Client ID casing must not discard the source inventory");

                var importing = new TrackedDownload { DownloadClient = client.Id, DownloadItem = item, State = TrackedDownloadState.Importing, IsTrackable = true };
                service.Handle(new TrackedDownloadRefreshedEvent(new() { importing }, new Dictionary<int, HashSet<string>> { [client.Id] = new() }));
                Assert.That(repository.All(), Has.Count.EqualTo(1), "The existing import pipeline still owns this download");
                service.Handle(new TrackedDownloadRefreshedEvent(new(), new Dictionary<int, HashSet<string>> { [client.Id] = new() }));
                Assert.That(repository.All(), Is.Empty);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void cleanup_is_scoped_to_confirmed_clients_and_deletes_orphaned_clients(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var events = Proxy<IEventAggregator>((method, args) => null);
                var clients = new DownloadClientRepository(database, events);
                var client = clients.Insert(new DownloadClientDefinition
                {
                    Name = "Disabled fixture client", Implementation = "QBittorrent", ConfigContract = "QBittorrentSettings",
                    Settings = new QBittorrentSettings(), Enable = false
                });
                var repository = new DownloadClientFileSnapshotRepository(database, events);
                var rows = Enumerable.Range(0, 1200).Select(index => new DownloadClientFileSnapshot
                {
                    DownloadClientId = client.Id, DownloadId = "download-" + index, Title = "Book",
                    FilePaths = new() { "/downloads/part.mp3" }, Source = "client",
                    CreatedAt = DateTime.UtcNow.AddDays(-90), LastUpdated = DateTime.UtcNow.AddDays(-90)
                }).ToList();
                repository.InsertMany(rows);
                new CleanupOrphanedDownloadClientFileSnapshots(repository).Clean();
                Assert.That(repository.All(), Has.Count.EqualTo(1200), "Disabling a client is not evidence that its downloads were removed");
                var timer = Stopwatch.StartNew();
                repository.RemoveMissingDownloads(client.Id + 1, Array.Empty<string>());
                Assert.That(repository.All(), Has.Count.EqualTo(1200));
                repository.RemoveMissingDownloads(client.Id, new[] { rows[0].DownloadId });
                timer.Stop();
                TestContext.WriteLine($"{database.DatabaseType}: reconcile 1200 snapshots, elapsedMs={timer.ElapsedMilliseconds}");
                Assert.That(repository.All().Select(row => row.DownloadId), Is.EqualTo(new[] { rows[0].DownloadId }));
                clients.Delete(client.Id);
                new CleanupOrphanedDownloadClientFileSnapshots(repository).Clean();
                Assert.That(repository.All(), Is.Empty);
            });
        }
    }
}
