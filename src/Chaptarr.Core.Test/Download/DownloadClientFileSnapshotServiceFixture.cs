using System;
using System.Collections.Generic;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Events;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.RemotePathMappings;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class DownloadClientFileSnapshotServiceFixture
    {
        private class RepositoryProxy : DispatchProxy
        {
            public DownloadClientFileSnapshot Snapshot { get; set; }
            public int PurgeCalls { get; private set; }
            public int DeleteForDownloadClientCalls { get; private set; }
            public int? LastDeletedDownloadClientId { get; private set; }
            public int WriteCalls { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    nameof(IDownloadClientFileSnapshotRepository.Find) => Snapshot != null &&
                                                                           Snapshot.DownloadClientId == (int)args[0] &&
                                                                           Snapshot.DownloadId == (string)args[1]
                        ? Snapshot
                        : null,
                    nameof(IBasicRepository<DownloadClientFileSnapshot>.Upsert) => Upsert((DownloadClientFileSnapshot)args[0]),
                    nameof(IBasicRepository<DownloadClientFileSnapshot>.Update) => Upsert((DownloadClientFileSnapshot)args[0]),
                    nameof(IDownloadClientFileSnapshotRepository.Delete) => Delete((int)args[0], (string)args[1]),
                    nameof(IDownloadClientFileSnapshotRepository.DeleteForDownloadClient) => DeleteForDownloadClient((int)args[0]),
                    nameof(IBasicRepository<DownloadClientFileSnapshot>.Purge) => Purge(),
                    _ => throw new NotImplementedException($"Test proxy does not implement {targetMethod?.Name}")
                };
            }

            private DownloadClientFileSnapshot Upsert(DownloadClientFileSnapshot snapshot)
            {
                WriteCalls++;
                Snapshot = snapshot;
                if (Snapshot.Id == 0)
                {
                    Snapshot.Id = 1;
                }

                return Snapshot;
            }

            private object Delete(int downloadClientId, string downloadId)
            {
                if (Snapshot?.DownloadClientId == downloadClientId && Snapshot.DownloadId == downloadId)
                {
                    Snapshot = null;
                }

                return null;
            }

            private object DeleteForDownloadClient(int downloadClientId)
            {
                DeleteForDownloadClientCalls++;
                LastDeletedDownloadClientId = downloadClientId;

                if (Snapshot?.DownloadClientId == downloadClientId)
                {
                    Snapshot = null;
                }

                return null;
            }

            private object Purge()
            {
                PurgeCalls++;
                Snapshot = null;
                return null;
            }
        }

        [Test]
        public void should_persist_client_file_list_and_hydrate_later_item()
        {
            var repository = DispatchProxy.Create<IDownloadClientFileSnapshotRepository, RepositoryProxy>();
            var service = new DownloadClientFileSnapshotService(repository, null, LogManager.GetCurrentClassLogger());

            service.CaptureClientList(DownloadItem(new List<string> { "/downloads/book/part1.mp3", "/downloads/book/part2.mp3" }, DownloadClientFileListConfidence.Authoritative));

            var laterItem = DownloadItem();
            service.ApplySnapshot(laterItem);

            Assert.That(laterItem.FilePaths, Is.EqualTo(new[] { "/downloads/book/part1.mp3", "/downloads/book/part2.mp3" }));
            Assert.That(laterItem.FileListConfidence, Is.EqualTo(DownloadClientFileListConfidence.Authoritative));
            Assert.That(((RepositoryProxy)(object)repository).Snapshot.Confidence, Is.EqualTo(DownloadClientFileListConfidence.Authoritative));
        }

        [Test]
        public void should_not_rewrite_unchanged_fresh_snapshot()
        {
            var repository = DispatchProxy.Create<IDownloadClientFileSnapshotRepository, RepositoryProxy>();
            var proxy = (RepositoryProxy)(object)repository;
            var service = new DownloadClientFileSnapshotService(repository, null, LogManager.GetCurrentClassLogger());

            service.CaptureClientList(DownloadItem(new List<string> { "/downloads/book/part1.mp3" }, DownloadClientFileListConfidence.Authoritative));
            Assert.That(proxy.WriteCalls, Is.EqualTo(1));

            service.CaptureClientList(DownloadItem(new List<string> { "/downloads/book/part1.mp3" }, DownloadClientFileListConfidence.Authoritative));
            service.CaptureClientList(DownloadItem(new List<string> { "/downloads/book/part1.mp3" }, DownloadClientFileListConfidence.Authoritative));

            Assert.That(proxy.WriteCalls, Is.EqualTo(1), "an unchanged fresh snapshot must not be rewritten every capture");
        }

        [Test]
        public void should_refresh_unchanged_snapshot_when_timestamp_is_stale()
        {
            var repository = DispatchProxy.Create<IDownloadClientFileSnapshotRepository, RepositoryProxy>();
            var proxy = (RepositoryProxy)(object)repository;
            var service = new DownloadClientFileSnapshotService(repository, null, LogManager.GetCurrentClassLogger());

            service.CaptureClientList(DownloadItem(new List<string> { "/downloads/book/part1.mp3" }, DownloadClientFileListConfidence.Authoritative));
            proxy.Snapshot.LastUpdated = DateTime.UtcNow.AddHours(-7);

            service.CaptureClientList(DownloadItem(new List<string> { "/downloads/book/part1.mp3" }, DownloadClientFileListConfidence.Authoritative));

            Assert.That(proxy.WriteCalls, Is.EqualTo(2));
            Assert.That(proxy.Snapshot.LastUpdated, Is.GreaterThan(DateTime.UtcNow.AddMinutes(-1)));
        }

        [Test]
        public void should_treat_unspecified_client_file_list_confidence_as_degraded()
        {
            var repository = DispatchProxy.Create<IDownloadClientFileSnapshotRepository, RepositoryProxy>();
            var service = new DownloadClientFileSnapshotService(repository, null, LogManager.GetCurrentClassLogger());

            service.CaptureClientList(DownloadItem(new List<string> { "/downloads/book/part1.mp3" }));

            Assert.That(((RepositoryProxy)(object)repository).Snapshot.Confidence, Is.EqualTo(DownloadClientFileListConfidence.Degraded));
        }

        [Test]
        public void should_keep_live_file_list_when_snapshot_exists()
        {
            var repository = DispatchProxy.Create<IDownloadClientFileSnapshotRepository, RepositoryProxy>();
            var service = new DownloadClientFileSnapshotService(repository, null, LogManager.GetCurrentClassLogger());

            service.CaptureClientList(DownloadItem(new List<string> { "/downloads/book/old.mp3" }));

            var liveItem = DownloadItem(new List<string> { "/downloads/book/current.mp3" });
            service.ApplySnapshot(liveItem);

            Assert.That(liveItem.FilePaths, Is.EqualTo(new[] { "/downloads/book/current.mp3" }));
        }

        [Test]
        public void should_not_replace_client_file_list_with_disk_capture()
        {
            var repository = DispatchProxy.Create<IDownloadClientFileSnapshotRepository, RepositoryProxy>();
            var repositoryProxy = (RepositoryProxy)(object)repository;
            var service = new DownloadClientFileSnapshotService(repository, null, LogManager.GetCurrentClassLogger());
            var item = DownloadItem(new List<string> { "/downloads/book/client.m4b" }, DownloadClientFileListConfidence.Authoritative, "/downloads/book");

            service.CaptureClientList(item);
            service.CaptureCompletedOutput(item);

            Assert.That(repositoryProxy.Snapshot.FilePaths, Is.EqualTo(new[] { "/downloads/book/client.m4b" }));
            Assert.That(repositoryProxy.Snapshot.Confidence, Is.EqualTo(DownloadClientFileListConfidence.Authoritative));
        }

        [Test]
        public void should_delete_snapshot_when_download_completed_event_fires()
        {
            var repository = DispatchProxy.Create<IDownloadClientFileSnapshotRepository, RepositoryProxy>();
            var repositoryProxy = (RepositoryProxy)(object)repository;
            var service = new DownloadClientFileSnapshotService(repository, null, LogManager.GetCurrentClassLogger());
            var item = DownloadItem(new List<string> { "/downloads/book/part1.mp3" });

            service.CaptureClientList(item);
            service.Handle(new DownloadCompletedEvent(new TrackedDownload { DownloadItem = item }, 1));

            Assert.That(repositoryProxy.Snapshot, Is.Null);
        }

        [Test]
        public void should_delete_snapshot_when_tracked_download_is_removed()
        {
            var repository = DispatchProxy.Create<IDownloadClientFileSnapshotRepository, RepositoryProxy>();
            var repositoryProxy = (RepositoryProxy)(object)repository;
            var service = new DownloadClientFileSnapshotService(repository, null, LogManager.GetCurrentClassLogger());
            var item = DownloadItem(new List<string> { "/downloads/book/part1.mp3" });

            service.CaptureClientList(item);
            service.Handle(new TrackedDownloadsRemovedEvent(new List<TrackedDownload>
            {
                new() { DownloadItem = item }
            }));

            Assert.That(repositoryProxy.Snapshot, Is.Null);
        }

        [Test]
        public void should_clear_snapshots_when_remote_path_mapping_changes()
        {
            var repository = DispatchProxy.Create<IDownloadClientFileSnapshotRepository, RepositoryProxy>();
            var repositoryProxy = (RepositoryProxy)(object)repository;
            var service = new DownloadClientFileSnapshotService(repository, null, LogManager.GetCurrentClassLogger());
            var item = DownloadItem(new List<string> { "/downloads/book/part1.mp3" });

            service.CaptureClientList(item);
            service.Handle(new ModelEvent<RemotePathMapping>(new RemotePathMapping
            {
                Id = 12,
                Host = "192.168.1.10",
                RemotePath = "/data/",
                LocalPath = "/downloads/"
            }, ModelAction.Updated));

            Assert.That(repositoryProxy.PurgeCalls, Is.EqualTo(1));
            Assert.That(repositoryProxy.Snapshot, Is.Null);
        }

        [Test]
        public void should_clear_only_affected_client_snapshots_when_scoped_mapping_is_deleted()
        {
            var repository = DispatchProxy.Create<IDownloadClientFileSnapshotRepository, RepositoryProxy>();
            var repositoryProxy = (RepositoryProxy)(object)repository;
            var service = new DownloadClientFileSnapshotService(repository, null, LogManager.GetCurrentClassLogger());
            var item = DownloadItem(new List<string> { "/downloads/book/part1.mp3" }, downloadClientId: 8);

            service.CaptureClientList(item);
            service.Handle(new ModelEvent<RemotePathMapping>(new RemotePathMapping
            {
                Id = 12,
                DownloadClientId = 7,
                Host = "192.168.1.10",
                RemotePath = "/data/",
                LocalPath = "/downloads/"
            }, ModelAction.Deleted));

            Assert.That(repositoryProxy.PurgeCalls, Is.Zero);
            Assert.That(repositoryProxy.DeleteForDownloadClientCalls, Is.EqualTo(1));
            Assert.That(repositoryProxy.LastDeletedDownloadClientId, Is.EqualTo(7));
            Assert.That(repositoryProxy.Snapshot, Is.Not.Null);
        }

        private static DownloadClientItem DownloadItem(List<string> filePaths = null,
                                                       DownloadClientFileListConfidence? confidence = null,
                                                       string outputPath = null,
                                                       int downloadClientId = 7)
        {
            return new DownloadClientItem
            {
                DownloadId = "ABC123",
                Title = "Author - Book",
                Category = "audiobooks",
                OutputPath = outputPath == null ? default : new OsPath(outputPath),
                FilePaths = filePaths,
                FileListConfidence = confidence,
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Id = downloadClientId,
                    Name = "qBittorrent",
                    Protocol = DownloadProtocol.Torrent
                }
            };
        }
    }
}
