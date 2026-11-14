using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients.Transmission;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;
using ImportResult = NzbDrone.Core.MediaFiles.BookImport.ImportResult;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class RetryImportInventoryFixture
    {
        [TestCase(false)]
        [TestCase(true)]
        public void retry_uses_refreshed_client_files_for_import_and_completion(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var context = new RetryContext(database);
                context.Files = new[] { "part1.mp3", "renamed.mp3", "added.mp3" };
                context.PutFiles(context.Files);

                context.Retry();

                Assert.That(context.ImportedPaths, Is.EquivalentTo(context.Paths(context.Files)));
                Assert.That(context.Tracked.State, Is.EqualTo(TrackedDownloadState.Imported));
                Assert.That(context.Events.OfType<DownloadCompletedEvent>().Count(), Is.EqualTo(1));
                context.AssertSnapshot(context.Files, DownloadClientFileListConfidence.Authoritative);
                Assert.That(context.QueueCalls, Is.EqualTo(1));
                Assert.That(context.DetailCalls, Is.EqualTo(1));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void refreshed_selected_file_missing_on_disk_still_blocks_completion(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var context = new RetryContext(database);
                context.Files = new[] { "part1.mp3", "renamed.mp3", "missing.mp3" };
                context.PutFiles("part1.mp3", "renamed.mp3");

                context.Retry();

                Assert.That(context.AttemptedPaths, Is.EquivalentTo(context.Paths(context.Files)));
                Assert.That(context.Tracked.State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
                Assert.That(context.Events.OfType<DownloadCompletedEvent>(), Is.Empty);
                context.AssertSnapshot(context.Files, DownloadClientFileListConfidence.Authoritative);
            });
        }

        [TestCase(false, "queue")]
        [TestCase(true, "queue")]
        [TestCase(false, "details")]
        [TestCase(true, "details")]
        [TestCase(false, "empty")]
        [TestCase(true, "empty")]
        public void outage_preserves_missing_part_inventory_and_next_retry_recovers(bool postgres, string failure)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var context = new RetryContext(database);
                context.PollFails = failure == "queue";
                context.DetailsFail = failure != "empty";
                context.Files = failure == "empty" ? Array.Empty<string>() : new[] { "part1.mp3", "renamed.mp3" };
                context.PutFiles("part1.mp3");

                context.Retry();

                Assert.That(context.AttemptedPaths, Is.EquivalentTo(context.Paths("part1.mp3", "part2.mp3")));
                Assert.That(context.Tracked.State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
                Assert.That(context.Events.OfType<DownloadCompletedEvent>(), Is.Empty);
                context.AssertSnapshot(new[] { "part1.mp3", "part2.mp3" }, DownloadClientFileListConfidence.Authoritative);

                context.PollFails = false;
                context.DetailsFail = false;
                context.Files = new[] { "part1.mp3", "renamed.mp3" };
                context.PutFiles(context.Files);
                context.Retry();

                Assert.That(context.AttemptedPaths, Is.EquivalentTo(context.Paths(context.Files)));
                Assert.That(context.Tracked.State, Is.EqualTo(TrackedDownloadState.Imported));
                context.AssertSnapshot(context.Files, DownloadClientFileListConfidence.Authoritative);
            });
        }

        [TestCase(false, DownloadClientFileListConfidence.Authoritative)]
        [TestCase(true, DownloadClientFileListConfidence.Authoritative)]
        [TestCase(false, DownloadClientFileListConfidence.Disk)]
        [TestCase(true, DownloadClientFileListConfidence.Disk)]
        public void retry_during_outage_recovers_persisted_inventory_after_restart(bool postgres, DownloadClientFileListConfidence confidence)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var context = new RetryContext(database, confidence);
                context.Tracked.DownloadItem.FilePaths = null;
                context.Tracked.DownloadItem.FileListConfidence = null;
                context.Tracked.ImportItem = null;
                context.PollFails = true;
                context.DetailsFail = true;
                context.PutFiles("part1.mp3");

                context.Retry();

                Assert.That(context.AttemptedPaths, Is.EquivalentTo(context.Paths("part1.mp3", "part2.mp3")));
                Assert.That(context.Tracked.State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
                Assert.That(context.Events.OfType<DownloadCompletedEvent>(), Is.Empty);
                context.AssertSnapshot(new[] { "part1.mp3", "part2.mp3" }, confidence);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void outage_does_not_erase_coverage_of_an_already_imported_part(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var context = new RetryContext(database);
                context.PollFails = true;
                context.DetailsFail = true;
                context.PutFiles("part2.mp3");
                context.History.Add(new EntityHistory
                {
                    BookId = context.Book.Id, DownloadId = "ABC123", EventType = EntityHistoryEventType.BookFileImported,
                    Data = new Dictionary<string, string> { ["DroppedPath"] = context.Paths("part1.mp3")[0] }
                });

                context.Retry();

                Assert.That(context.ImportedPaths, Is.EquivalentTo(context.Paths("part2.mp3")));
                Assert.That(context.Tracked.State, Is.EqualTo(TrackedDownloadState.Imported));
                Assert.That(context.Events.OfType<DownloadCompletedEvent>().Count(), Is.EqualTo(1));
                context.AssertSnapshot(new[] { "part1.mp3", "part2.mp3" }, DownloadClientFileListConfidence.Authoritative);
            });
        }

        private sealed class RetryContext : IDisposable
        {
            private readonly string _root = Path.Combine(Path.GetTempPath(), "chaptarr-retry-files-" + Guid.NewGuid().ToString("N"));
            private readonly DownloadClientFileSnapshotRepository _repository;
            private readonly RetryFailedImportService _service;
            public readonly List<IEvent> Events = new();
            public readonly List<EntityHistory> History = new();
            public readonly Book Book = new() { Id = 11, AuthorId = 7, MediaType = BookMediaType.Audiobook, Title = "Book" };
            public TrackedDownload Tracked { get; }
            public List<string> AttemptedPaths { get; private set; }
            public List<string> ImportedPaths { get; private set; }
            public string[] Files { get; set; } = { "part1.mp3", "part2.mp3" };
            public bool PollFails { get; set; }
            public bool DetailsFail { get; set; }
            public int QueueCalls { get; private set; }
            public int DetailCalls { get; private set; }

            public RetryContext(IMainDatabase database, DownloadClientFileListConfidence confidence = DownloadClientFileListConfidence.Authoritative)
            {
                var logger = LogManager.GetCurrentClassLogger();
                var events = Proxy<IEventAggregator>((_, args) => { Events.Add((IEvent)args[0]); return null; });
                var definition = new DownloadClientRepository(database, events).Insert(new DownloadClientDefinition
                {
                    Name = "Fixture client", Implementation = "Transmission", ConfigContract = "TransmissionSettings",
                    Settings = new TransmissionSettings(), Enable = true, Protocol = DownloadProtocol.Torrent
                });
                _repository = new DownloadClientFileSnapshotRepository(database, events);
                var snapshots = new DownloadClientFileSnapshotService(_repository,
                    Proxy<IDiskProvider>((method, args) =>
                    {
                        Assert.That(confidence, Is.EqualTo(DownloadClientFileListConfidence.Disk),
                            "A saved client inventory must not rescan disk");
                        return method.Name switch
                        {
                            nameof(IDiskProvider.FileExists) => File.Exists((string)args[0]),
                            nameof(IDiskProvider.FolderExists) => Directory.Exists((string)args[0]),
                            nameof(IDiskProvider.GetFileInfos) => new System.IO.Abstractions.FileSystem().DirectoryInfo
                                .FromDirectoryName((string)args[0]).GetFiles("*", SearchOption.AllDirectories)
                                .OrderByDescending(file => file.Name, StringComparer.Ordinal).ToList(),
                            _ => throw new NotImplementedException(method.Name)
                        };
                    }), logger);
                var item = new DownloadClientItem
                {
                    DownloadId = "ABC123", Title = "Book", OutputPath = new OsPath(Path.Combine(_root, "Book")),
                    Status = DownloadItemStatus.Completed,
                    FilePaths = Paths("part1.mp3", "part2.mp3"), FileListConfidence = confidence,
                    DownloadClientInfo = new DownloadClientItemClientInfo { Id = definition.Id, Name = definition.Name, Protocol = DownloadProtocol.Torrent }
                };
                if (confidence == DownloadClientFileListConfidence.Disk)
                {
                    PutFiles("part1.mp3", "part2.mp3");
                    item.FilePaths = null;
                    snapshots.CaptureCompletedOutput(item);
                    Assert.That(item.FilePaths, Is.EquivalentTo(Paths("part1.mp3", "part2.mp3")),
                        "The restart fixture must persist the original disk inventory before files disappear");
                    foreach (var path in item.FilePaths) File.Delete(path);
                }
                else
                {
                    snapshots.CaptureClientList(item);
                }
                var history = Proxy<IHistoryService>((method, _) => method.Name == nameof(IHistoryService.FindByDownloadId)
                    ? History.ToList() : throw new NotImplementedException(method.Name));
                var mappings = Proxy<IRemotePathMappingService>((method, args) => method.Name == nameof(IRemotePathMappingService.RemapRemoteToLocal)
                    ? args.Last() : throw new NotImplementedException(method.Name));
                var client = new Transmission(Proxy<ITransmissionProxy>((method, _) =>
                {
                    switch (method.Name)
                    {
                        case nameof(ITransmissionProxy.GetTorrents):
                            QueueCalls++;
                            if (PollFails) { throw new IOException("Client temporarily unavailable"); }
                            return new List<TransmissionTorrent> { Torrent() };
                        case nameof(ITransmissionProxy.GetTorrentDetails):
                            DetailCalls++;
                            if (DetailsFail) { throw new IOException("File details temporarily unavailable"); }
                            return Torrent();
                        default:
                            throw new NotImplementedException(method.Name);
                    }
                }), null, null, null, null, mappings, null, logger) { Definition = definition };
                var clients = Proxy<IProvideDownloadClient>((method, _) => method.Name == nameof(IProvideDownloadClient.Get)
                    ? client : throw new NotImplementedException(method.Name));
                var cache = new CacheManager();
                Tracked = new TrackedDownload
                {
                    DownloadClient = definition.Id, DownloadItem = item, ImportItem = item.Clone(),
                    State = TrackedDownloadState.ImportBlocked, IsTrackable = true, Added = DateTime.UtcNow,
                    RemoteBook = new RemoteBook { Books = new List<Book> { Book }, Author = new Author { Id = 7 } }
                };
                cache.GetCache<TrackedDownload>(typeof(TrackedDownloadService)).Set(item.DownloadId, Tracked);
                var tracking = new TrackedDownloadService(null, cache, history, events,
                    Proxy<IDownloadHistoryService>((_, _) => null), null, logger, snapshots);
                var completed = new CompletedDownloadService(events, history, new ProvideImportItemService(clients),
                    Proxy<IDownloadedBooksImportService>((method, args) =>
                    {
                        if (method.Name == nameof(IDownloadedBooksImportService.ProcessIncludedMedia)) return new List<ImportResult>();
                        Assert.That(method.Name, Is.EqualTo(nameof(IDownloadedBooksImportService.ProcessPath)));
                        AttemptedPaths = ((DownloadClientItem)args[3]).FilePaths.ToList();
                        ImportedPaths = AttemptedPaths.Where(File.Exists).ToList();
                        return AttemptedPaths.Select(path => new ImportResult(
                            new ImportDecision<LocalBook>(new LocalBook { Path = path, Book = Book }),
                            File.Exists(path) ? Array.Empty<string>() : new[] { "Required file missing" })).ToList();
                    }),
                    Proxy<IDownloadImportModeResolver>((_, _) => ImportMode.Copy),
                    new TrackedDownloadAlreadyImported(logger), logger, snapshots);
                _service = new RetryFailedImportService(tracking, clients, mappings, completed, snapshots, null,
                    Proxy<ICommandResultReporter>((_, _) => null), events, logger);
            }

            public void Retry() => _service.Execute(new RetryFailedImportCommand { DownloadId = "ABC123" });

            public List<string> Paths(params string[] names) => names.Select(name => Path.Combine(_root, "Book", name)).ToList();

            public void PutFiles(params string[] names)
            {
                Directory.CreateDirectory(Path.Combine(_root, "Book"));
                foreach (var path in Paths(names)) { File.WriteAllText(path, "fixture"); }
            }

            public void AssertSnapshot(string[] names, DownloadClientFileListConfidence confidence)
            {
                var snapshot = _repository.Find(Tracked.DownloadClient, "ABC123");
                Assert.That(snapshot.FilePaths, Is.EquivalentTo(Paths(names)));
                Assert.That(snapshot.Confidence, Is.EqualTo(confidence));
            }

            private TransmissionTorrent Torrent() => new()
            {
                HashString = "ABC123", Name = "Book", DownloadDir = _root, TotalSize = 100,
                Status = TransmissionTorrentStatus.Seeding, LeftUntilDone = 0,
                Files = Files.Select(name => new TransmissionTorrentFile { Name = Path.Combine("Book", name) }).ToList()
            };

            public void Dispose()
            {
                if (Directory.Exists(_root)) { Directory.Delete(_root, true); }
            }
        }
    }
}
