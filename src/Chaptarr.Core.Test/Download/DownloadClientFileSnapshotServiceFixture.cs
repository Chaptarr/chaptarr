using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Events;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.History;
using NzbDrone.Core.Parser.Model;
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
        public void should_preserve_unchanged_snapshot_without_timestamp_refresh()
        {
            var repository = DispatchProxy.Create<IDownloadClientFileSnapshotRepository, RepositoryProxy>();
            var proxy = (RepositoryProxy)(object)repository;
            var service = new DownloadClientFileSnapshotService(repository, null, LogManager.GetCurrentClassLogger());

            service.CaptureClientList(DownloadItem(new List<string> { "/downloads/book/part1.mp3" }, DownloadClientFileListConfidence.Authoritative));
            var captured = DateTime.UtcNow.AddDays(-30);
            proxy.Snapshot.LastUpdated = captured;

            service.CaptureClientList(DownloadItem(new List<string> { "/downloads/book/part1.mp3" }, DownloadClientFileListConfidence.Authoritative));

            Assert.That(proxy.WriteCalls, Is.EqualTo(1));
            Assert.That(proxy.Snapshot.LastUpdated, Is.EqualTo(captured));
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
        public void should_restore_original_inventory_before_recapturing_partially_moved_download()
        {
            var repository = DispatchProxy.Create<IDownloadClientFileSnapshotRepository, RepositoryProxy>();
            var service = new DownloadClientFileSnapshotService(repository, null, LogManager.GetCurrentClassLogger());
            service.CaptureClientList(DownloadItem(new List<string> { "/downloads/book/part1.mp3", "/downloads/book/part2.mp3" }));

            var restarted = DownloadItem(new List<string>(), outputPath: "/downloads/book");
            service.CaptureCompletedOutput(restarted);
            Assert.That(restarted.FilePaths, Is.EqualTo(new[] { "/downloads/book/part1.mp3", "/downloads/book/part2.mp3" }));
        }

        [Test]
        public void disk_snapshot_should_see_later_media_without_forgetting_a_previously_seen_part()
        {
            var repository = DispatchProxy.Create<IDownloadClientFileSnapshotRepository, RepositoryProxy>();
            var proxy = (RepositoryProxy)(object)repository;
            var paths = new[] { @"C:\downloads\book\info.nfo".AsOsAgnostic() };
            var disk = Common.InterfaceProxy.Proxy<IDiskProvider>((method, args) => method.Name switch
            {
                nameof(IDiskProvider.FileExists) => false,
                nameof(IDiskProvider.FolderExists) => true,
                nameof(IDiskProvider.GetFileInfos) => paths.Select(path => Common.InterfaceProxy.Proxy<IFileInfo>((property, _) => property.Name == "get_FullName" ? path : throw new NotImplementedException(property.Name))).ToList(),
                _ => throw new NotImplementedException(method.Name)
            });
            var service = new DownloadClientFileSnapshotService(repository, disk, LogManager.GetCurrentClassLogger());
            var first = DownloadItem(outputPath: @"C:\downloads\book".AsOsAgnostic());
            service.CaptureCompletedOutput(first);
            Assert.That(first.FilePaths, Is.EqualTo(paths));

            paths = new[] { @"C:\downloads\book\Dune 01.mp3".AsOsAgnostic(), @"C:\downloads\book\Dune 02.mp3".AsOsAgnostic() };
            var afterRestart = DownloadItem(outputPath: @"C:\downloads\book".AsOsAgnostic());
            service.CaptureCompletedOutput(afterRestart);
            Assert.That(afterRestart.FilePaths, Is.EquivalentTo(paths.Append(@"C:\downloads\book\info.nfo".AsOsAgnostic())));
            Assert.That(afterRestart.FileListConfidence, Is.EqualTo(DownloadClientFileListConfidence.Disk));
            service.ApplySnapshot(first);
            Assert.That(first.FilePaths, Is.EquivalentTo(afterRestart.FilePaths), "Import receives the original download item after its clone refreshes the snapshot");

            paths = new[] { @"C:\downloads\book\Dune 02.mp3".AsOsAgnostic() };
            service.CaptureCompletedOutput(afterRestart);
            service.CaptureClientList(afterRestart);
            Assert.That(proxy.WriteCalls, Is.EqualTo(2));
            Assert.That(proxy.Snapshot.Confidence, Is.EqualTo(DownloadClientFileListConfidence.Disk));
            Assert.That(afterRestart.FilePaths, Does.Contain(@"C:\downloads\book\Dune 01.mp3".AsOsAgnostic()));

            var tracked = new TrackedDownload
            {
                DownloadItem = afterRestart,
                RemoteBook = new RemoteBook { Books = new List<Book> { new() { Id = 7, MediaType = BookMediaType.Audiobook } } }
            };
            var history = new List<EntityHistory>
            {
                new()
                {
                    DownloadId = afterRestart.DownloadId, BookId = 7, EditionId = 9,
                    EventType = EntityHistoryEventType.BookFileImported,
                    Data = new Dictionary<string, string> { ["DroppedPath"] = paths.Single() }
                }
            };
            var completion = TrackedDownloadAlreadyImported.Evaluate(tracked, history: history);
            Assert.That(completion.Complete, Is.False, "A vanished, never-imported part must not disappear from completion evidence");
            Assert.That(completion.RequiredCount, Is.EqualTo(2));
            Assert.That(completion.Missing.Select(message => message.Title), Does.Contain("Dune 01.mp3"));
            Assert.That(completion.Missing.Select(message => message.SourcePath), Does.Contain(@"C:\downloads\book\Dune 01.mp3".AsOsAgnostic()));

            history.Add(new EntityHistory
            {
                DownloadId = afterRestart.DownloadId, BookId = 7, EditionId = 9,
                EventType = EntityHistoryEventType.BookFileImported,
                Data = new Dictionary<string, string> { ["DroppedPath"] = @"C:\downloads\book\Dune 01.mp3".AsOsAgnostic() }
            });
            Assert.That(TrackedDownloadAlreadyImported.Evaluate(tracked, history: history).Complete, Is.True,
                "A part moved by an earlier successful import is covered by its saved source path");
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
