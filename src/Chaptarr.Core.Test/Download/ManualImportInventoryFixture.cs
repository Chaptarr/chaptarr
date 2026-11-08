using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Manual;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;
using ImportResult = NzbDrone.Core.MediaFiles.BookImport.ImportResult;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class ManualImportInventoryFixture
    {
        private static IEnumerable<TestCaseData> InventoryCases()
        {
            foreach (var postgres in new[] { false, true })
            foreach (var media in new[] { BookMediaType.Audiobook, BookMediaType.Ebook })
            foreach (var clientList in new[] { false, true })
            {
                yield return new TestCaseData(postgres, media, clientList);
            }
        }

        [TestCaseSource(nameof(InventoryCases))]
        public void folder_preview_cannot_add_a_neighbor_to_the_download(bool postgres, BookMediaType media, bool clientList)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var context = new ImportContext(database, media, clientList, "one");
                context.Import("one");

                Assert.That(context.Tracked.State, Is.EqualTo(TrackedDownloadState.Imported));
                Assert.That(context.Events.OfType<DownloadCompletedEvent>().Count(), Is.EqualTo(1));
                Assert.That(File.Exists(context.Neighbor), Is.True);
                Assert.That(context.Snapshots.Find(1, "manual-download").FilePaths,
                    Does.Contain(context.Source("one")).And.Not.Contain(context.Neighbor));
                Assert.That(context.OutputScans, Is.EqualTo(clientList ? 0 : 1));
            });
        }

        [TestCaseSource(nameof(InventoryCases))]
        public void partial_move_and_restart_keep_the_original_required_files(bool postgres, BookMediaType media, bool clientList)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var context = new ImportContext(database, media, clientList, "one", "two");
                context.Import("one");

                Assert.That(context.Tracked.State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
                Assert.That(context.Tracked.StatusMessages.Select(message => message.Title), Does.Contain(Path.GetFileName(context.Source("two"))));
                Assert.That(context.Tracked.StatusMessages.Select(message => message.SourcePath), Does.Contain(context.Source("two")));
                Assert.That(context.Events.OfType<DownloadCompletedEvent>(), Is.Empty);
                Assert.That(File.Exists(context.Source("one")), Is.False);
                Assert.That(context.Snapshots.Find(1, "manual-download").FilePaths, Does.Contain(context.Source("one")));

                // Restore only persisted inventory/history, after the first source has moved away.
                context.Tracked.DownloadItem.FilePaths = null;
                context.Tracked.DownloadItem.FileListConfidence = null;
                context.Import("two");

                Assert.That(context.Tracked.State, Is.EqualTo(TrackedDownloadState.Imported));
                Assert.That(context.Events.OfType<DownloadCompletedEvent>().Count(), Is.EqualTo(1));
                Assert.That(context.History.FindByDownloadId("manual-download").Count, Is.EqualTo(2));
                Assert.That(File.Exists(context.Neighbor), Is.True);
                Assert.That(context.OutputScans, Is.EqualTo(clientList ? 0 : 2),
                    "Disk inventories must discover files arriving between imports; client inventories need no disk scan");
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void vanished_client_file_not_in_the_preview_still_blocks_completion(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var context = new ImportContext(database, BookMediaType.Audiobook, true, "one", "two");
                File.Delete(context.Source("two"));

                context.Import("one");

                Assert.That(context.Tracked.State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
                Assert.That(context.Tracked.StatusMessages.Select(message => message.Title), Does.Contain(Path.GetFileName(context.Source("two"))));
                Assert.That(context.Tracked.StatusMessages.Select(message => message.SourcePath), Does.Contain(context.Source("two")));
                Assert.That(context.Events.OfType<DownloadCompletedEvent>(), Is.Empty);
            });
        }

        [Test]
        public void another_downloads_preview_cannot_exclude_a_required_ebook()
        {
            var book = new Book { Id = 10, MediaType = BookMediaType.Ebook };
            var tracked = new TrackedDownload
            {
                DownloadItem = new DownloadClientItem
                {
                    DownloadId = "own", FilePaths = new() { "/downloads/book.epub", "/downloads/unknown.mobi" }
                },
                RemoteBook = new RemoteBook { Books = new() { book } }
            };
            var result = new ImportResult(new ImportDecision<LocalBook>(new LocalBook { Book = book, Path = "/downloads/book.epub" }));
            var preview = new ManualImportFile { Path = "/downloads/unknown.mobi", BookId = book.Id, DownloadId = "other" };

            Assert.That(TrackedDownloadAlreadyImported.Evaluate(tracked, new[] { result }, new[] { preview }).Complete, Is.False);

            preview.DownloadId = "own";
            Assert.That(TrackedDownloadAlreadyImported.Evaluate(tracked, new[] { result }, new[] { preview }).Complete, Is.True);
        }

        [Test]
        public void selected_failure_outside_the_captured_inventory_still_prevents_completion()
        {
            var book = new Book { Id = 10, MediaType = BookMediaType.Audiobook };
            var tracked = new TrackedDownload
            {
                DownloadItem = new DownloadClientItem { DownloadId = "own", FilePaths = new() { "/downloads/one.mp3" } },
                RemoteBook = new RemoteBook { Books = new() { book } }
            };
            var imported = new ImportResult(new ImportDecision<LocalBook>(new LocalBook { Book = book, Path = "/downloads/one.mp3" }));
            var failed = new ImportResult(new ImportDecision<LocalBook>(new LocalBook { Book = book, Path = "/elsewhere/two.mp3" }), "Transfer failed");

            var completion = TrackedDownloadAlreadyImported.Evaluate(tracked, new[] { imported, failed });

            Assert.That(completion.Complete, Is.False);
            Assert.That(completion.Missing.Single().Title, Is.EqualTo("two.mp3"));
            Assert.That(completion.Missing.Single().SourcePath, Is.EqualTo("/elsewhere/two.mp3"));
            Assert.That(completion.Missing.Single().Messages, Does.Contain("Transfer failed"));
        }

        [Test]
        public void unavailable_inventory_does_not_waive_an_unimported_preview_part()
        {
            var book = new Book { Id = 10, MediaType = BookMediaType.Audiobook };
            var tracked = new TrackedDownload
            {
                DownloadItem = new DownloadClientItem { DownloadId = "own" },
                RemoteBook = new RemoteBook { Books = new() { book } }
            };
            var imported = new ImportResult(new ImportDecision<LocalBook>(new LocalBook { Book = book, Path = "/downloads/one.mp3" }));
            var preview = new ManualImportFile { Path = "/downloads/two.mp3", BookId = book.Id };

            var completion = TrackedDownloadAlreadyImported.Evaluate(tracked, new[] { imported }, new[] { preview });

            Assert.That(completion.Complete, Is.False);
            Assert.That(completion.Missing.Single().Title, Is.EqualTo(Path.GetFileName(preview.Path)));
            Assert.That(completion.Missing.Single().SourcePath, Is.EqualTo(preview.Path));
        }

        private sealed class ImportContext : IDisposable
        {
            private readonly string _root = Path.Combine(Path.GetTempPath(), "chaptarr-manual-inventory-" + Guid.NewGuid().ToString("N"));
            private readonly ManualImportService _service;
            private readonly Book _book;
            private readonly Edition _edition;
            private readonly QualityModel _quality;
            private readonly string _extension;
            public TrackedDownload Tracked { get; }
            public List<object> Events { get; } = new();
            public DownloadClientFileSnapshotRepository Snapshots { get; }
            public HistoryService History { get; }
            public int OutputScans { get; private set; }
            public string Neighbor => Path.Combine(_root, "neighbor" + _extension);

            public ImportContext(IMainDatabase database, BookMediaType media, bool clientList, params string[] names)
            {
                _extension = media == BookMediaType.Audiobook ? ".mp3" : ".epub";
                _quality = new QualityModel(media == BookMediaType.Audiobook ? Quality.MP3 : Quality.EPUB);
                var logger = LogManager.GetCurrentClassLogger();
                var events = Proxy<IEventAggregator>((method, args) => { Events.Add(args[0]); return null; });
                var author = new AuthorRepository(database, events, logger).Insert(new Author
                {
                    Name = "Inventory Author", CleanName = "inventoryauthor", SortName = "inventoryauthor",
                    TitleSlug = "inventory-author", Path = Path.Combine(_root, "library"), Added = DateTime.UtcNow
                });
                _book = new BookRepository(database, events).Insert(new Book
                {
                    Author = author, AuthorId = author.Id, Title = "Inventory Book", CleanTitle = "inventorybook",
                    TitleSlug = "inventory-book", MediaType = media, AnyEditionOk = true, Added = DateTime.UtcNow
                });
                _edition = new EditionRepository(database, events).Insert(new Edition
                {
                    Book = _book, BookId = _book.Id, Title = _book.Title, ForeignEditionId = "local:inventory",
                    Monitored = true, ReadingFormatId = media == BookMediaType.Audiobook ? 2 : 3
                });
                _book.Editions = new() { _edition };
                try
                {
                    Directory.CreateDirectory(Path.Combine(_root, "download"));
                    Directory.CreateDirectory(author.Path);
                    foreach (var name in names) { File.WriteAllText(Source(name), "book fixture"); }
                    File.WriteAllText(Neighbor, "unrelated book fixture");
                    File.WriteAllText(Path.Combine(_root, "download", "cover.jpg"), "cover fixture");
                    File.WriteAllText(Path.Combine(_root, "download", "book.nfo"), "sidecar fixture");
                    var fs = new FileSystem();
                    var disk = Proxy<IDiskProvider>((method, args) => method.Name switch
                    {
                        nameof(IDiskProvider.FileExists) => File.Exists((string)args[0]),
                        nameof(IDiskProvider.FolderExists) => Directory.Exists((string)args[0]),
                        nameof(IDiskProvider.GetFileInfo) => fs.FileInfo.FromFileName((string)args[0]),
                        nameof(IDiskProvider.GetFileInfos) => ReadOutputFiles(fs, (string)args[0]),
                        _ => throw new NotImplementedException(method.Name)
                    });
                    Snapshots = new DownloadClientFileSnapshotRepository(database, events);
                    var snapshots = new DownloadClientFileSnapshotService(Snapshots, disk, logger);
                    Tracked = new TrackedDownload
                    {
                        State = TrackedDownloadState.ImportBlocked,
                        DownloadItem = new DownloadClientItem
                        {
                            DownloadId = "manual-download", Title = _book.Title,
                            DownloadClientInfo = new DownloadClientItemClientInfo { Id = 1, Protocol = DownloadProtocol.Torrent },
                            OutputPath = new OsPath(Path.Combine(_root, "download")),
                            FilePaths = clientList ? names.Select(Source).ToList() : null,
                            FileListConfidence = clientList ? DownloadClientFileListConfidence.Authoritative : null
                        },
                        RemoteBook = new RemoteBook
                        {
                            Author = author, Books = new() { _book }, ParsedBookInfo = new ParsedBookInfo { Quality = _quality }
                        }
                    };
                    if (clientList) { snapshots.CaptureClientList(Tracked.DownloadItem); }
                    History = new HistoryService(new HistoryRepository(database, events), logger);
                    var dependencies = new Dictionary<Type, object>
                    {
                        [typeof(Logger)] = logger,
                        [typeof(IDiskProvider)] = disk,
                        [typeof(IDownloadClientFileSnapshotService)] = snapshots,
                        [typeof(ITrackedDownloadService)] = Proxy<ITrackedDownloadService>((method, _) =>
                            method.Name == nameof(ITrackedDownloadService.GetTrackedDownloads) ? new List<TrackedDownload> { Tracked } : Tracked),
                        [typeof(IHistoryService)] = History,
                        [typeof(ITrackedDownloadAlreadyImported)] = new TrackedDownloadAlreadyImported(logger),
                        [typeof(IAuthorService)] = Proxy<IAuthorService>((_, _) => author),
                        [typeof(IBookService)] = Proxy<IBookService>((_, _) => _book),
                        [typeof(IEditionService)] = Proxy<IEditionService>((_, _) => _edition),
                        [typeof(IRootFolderService)] = Proxy<IRootFolderService>((_, _) => new RootFolder { Path = author.Path }),
                        [typeof(IMetadataTagService)] = Proxy<IMetadataTagService>((_, _) => (new Dictionary<string, List<string>>(), (int?)60)),
                        [typeof(IDownloadImportModeResolver)] = Proxy<IDownloadImportModeResolver>((_, _) => ImportMode.Move),
                        [typeof(IEventAggregator)] = events,
                        [typeof(IImportApprovedBooks)] = Proxy<IImportApprovedBooks>((_, args) =>
                        {
                            var decisions = (List<ImportDecision<LocalBook>>)args[0];
                            foreach (var decision in decisions)
                            {
                                Assert.That(decision.Approved, Is.True);
                                var destination = Path.Combine(author.Path, Path.GetFileName(decision.Item.Path));
                                File.Move(decision.Item.Path, destination);
                                History.Handle(new TrackImportedEvent(decision.Item,
                                    new BookFile { Path = destination, Edition = _edition, EditionId = _edition.Id, Author = author, Quality = _quality },
                                    new List<BookFile>(), true, (DownloadClientItem)args[2]));
                            }
                            return decisions.Select(decision => new ImportResult(decision)).ToList();
                        })
                    };
                    var constructor = typeof(ManualImportService).GetConstructors().Single();
                    _service = (ManualImportService)constructor.Invoke(constructor.GetParameters()
                        .Select(parameter => dependencies.GetValueOrDefault(parameter.ParameterType)).ToArray());
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            public string Source(string name) => Path.Combine(_root, "download", name + _extension);

            private List<IFileInfo> ReadOutputFiles(FileSystem fs, string path)
            {
                Assert.That(path, Is.EqualTo(Path.Combine(_root, "download")));
                OutputScans++;
                return Directory.GetFiles(path, "*", SearchOption.AllDirectories).Select(file => fs.FileInfo.FromFileName(file)).ToList();
            }

            public void Import(string name)
            {
                _service.Execute(new ManualImportCommand
                {
                    ImportMode = ImportMode.Move,
                    Files = new() { new() { Path = Source(name), AuthorId = _book.AuthorId, BookId = _book.Id, EditionId = _edition.Id, Quality = _quality } },
                    // A parent-folder preview includes unselected, unmatched neighboring downloads.
                    PreviewFiles = Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
                        .Where(path => !path.Contains(Path.DirectorySeparatorChar + "library" + Path.DirectorySeparatorChar))
                        .Select(path => new ManualImportFile { Path = path }).ToList()
                });
            }

            public void Dispose()
            {
                if (Directory.Exists(_root)) { Directory.Delete(_root, true); }
            }
        }
    }
}
