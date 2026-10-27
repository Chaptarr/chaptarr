using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class CompletedDownloadWaitingFixture
    {
        [TestCase(false, 3, 0)]
        [TestCase(true, 3, 0)]
        [TestCase(false, 655, 654)]
        [TestCase(true, 655, 654)]
        public void repeated_missing_source_checks_should_be_bounded_and_skip_matching(bool postgres, int sources, int imported)
        {
            Run(postgres, sources, imported, (subject, tracked, counters) =>
            {
                subject.Import(tracked);
                counters.Reset();
                var bytes = GC.GetAllocatedBytesForCurrentThread();
                var timer = Stopwatch.StartNew();
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    subject.Import(tracked);
                    Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.ImportPending));
                }
                bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;

                Assert.That(counters.HistoryReads, Is.EqualTo(2));
                Assert.That(counters.FileReads, Is.LessThanOrEqualTo(2));
                Assert.That(timer.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)));
                Assert.That(bytes, Is.LessThan(128L * 1024 * 1024));
                TestContext.Progress.WriteLine($"MISSING_SOURCE_COST database={(postgres ? "PostgreSQL" : "SQLite")} sources={sources} imported={imported} attempts=2 elapsedMs={timer.Elapsed.TotalMilliseconds:F2} allocatedBytes={bytes} historyReads={counters.HistoryReads} fileReads={counters.FileReads} inventoryRequests={counters.InventoryRequests} state={tracked.State}");
            });
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void inaccessible_output_should_remain_pending_beyond_three_polls(bool postgres, bool singleFile)
        {
            Run(postgres, singleFile ? 1 : 3, 0, (subject, tracked, counters) =>
            {
                Directory.Delete(tracked.DownloadItem.OutputPath.FullPath);
                if (singleFile)
                {
                    tracked.DownloadItem.OutputPath = new OsPath(tracked.DownloadItem.FilePaths[0]);
                }

                for (var attempt = 0; attempt < 6; attempt++)
                {
                    subject.Import(tracked);
                    Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.ImportPending));
                    Assert.That(tracked.StatusMessages.SelectMany(message => message.Messages),
                        Has.Some.Contains("Download path is not accessible"));
                }
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void restart_should_recognize_all_previously_moved_parts_from_durable_evidence(bool postgres)
        {
            Run(postgres, 3, 3, (subject, tracked, counters) =>
            {
                subject.Import(tracked);
                Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.Imported));
                Assert.That(tracked.StatusMessages, Is.Empty);
            });
        }

        private sealed class Counters
        {
            public int HistoryReads;
            public int FileReads;
            public int InventoryRequests;
            public void Reset() { HistoryReads = 0; FileReads = 0; InventoryRequests = 0; }
        }

        private static void Run(bool postgres, int sources, int imported, Action<CompletedDownloadService, TrackedDownload, Counters> check)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "waiting-download-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                try
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var events = Proxy<IEventAggregator>((method, args) => null);
                    var author = new AuthorRepository(database, events, logger).Insert(new Author
                    {
                        Name = "Frank Herbert", CleanName = "frankherbert", SortName = "frankherbert", TitleSlug = "frank-herbert",
                        Path = directory, Added = DateTime.UtcNow
                    });
                    var book = new BookRepository(database, events).Insert(new Book
                    {
                        AuthorId = author.Id, Author = author, Title = "Dune", CleanTitle = "dune", TitleSlug = "dune",
                        MediaType = BookMediaType.Audiobook, Added = DateTime.UtcNow
                    });
                    var edition = new EditionRepository(database, events).Insert(new Edition
                    {
                        BookId = book.Id, Book = book, Title = "Dune", ForeignEditionId = "local:waiting", ReadingFormatId = 2, Monitored = true
                    });
                    book.Editions = new() { edition };
                    var paths = Enumerable.Range(1, sources).Select(index => Path.Combine(directory, $"Dune Part {index:D3}.mp3")).ToList();
                    var histories = new HistoryRepository(database, events);
                    var files = new MediaFileRepository(database, events);
                    histories.Insert(new EntityHistory { DownloadId = "waiting", AuthorId = author.Id, BookId = book.Id, EditionId = edition.Id,
                        EventType = EntityHistoryEventType.Grabbed, Date = DateTime.UtcNow.AddMinutes(-10), Quality = new QualityModel(Quality.MP3), SourceTitle = "Dune" });
                    var importedHistory = new List<EntityHistory>();
                    var importedFiles = new List<BookFile>();
                    foreach (var path in paths.Take(imported))
                    {
                        importedHistory.Add(new EntityHistory { DownloadId = "waiting", AuthorId = author.Id, BookId = book.Id, EditionId = edition.Id,
                            EventType = EntityHistoryEventType.BookFileImported, Date = DateTime.UtcNow.AddMinutes(-5), Quality = new QualityModel(Quality.MP3),
                            SourceTitle = "Dune", Data = new() { ["DroppedPath"] = path } });
                        importedFiles.Add(new BookFile { EditionId = edition.Id, Path = Path.Combine(directory, "library", Path.GetFileName(path)),
                            MediaInfo = new MediaInfoModel(),
                            DateAdded = DateTime.UtcNow.AddMinutes(-5), Modified = DateTime.UtcNow.AddMinutes(-5), Quality = new QualityModel(Quality.MP3),
                            AllTags = new() { ["TITLE"] = new() { "Dune " + Path.GetFileNameWithoutExtension(path) }, ["AUTHOR"] = new() { "Frank Herbert" },
                                ["ALBUM"] = new() { "Dune" }, ["COMMENT"] = new() { new string('x', 1024) } },
                            MatchProvenance = new MatchProvenance { ImportDownloadId = "waiting", ImportSourcePaths = new() { path } } });
                    }
                    histories.InsertMany(importedHistory);
                    files.InsertMany(importedFiles);

                    var counters = new Counters();
                    var history = Proxy<IHistoryService>((method, args) =>
                    {
                        Assert.That(method.Name, Is.EqualTo(nameof(IHistoryService.FindByDownloadId)));
                        counters.HistoryReads++;
                        return histories.FindByDownloadId((string)args[0]);
                    });
                    var media = Proxy<IMediaFileService>((method, args) =>
                    {
                        Assert.That(method.Name, Is.EqualTo(nameof(IMediaFileService.GetFilesByBooks)));
                        counters.FileReads++;
                        return files.GetFilesByBooks((List<int>)args[0]);
                    });
                    var fs = new FileSystem();
                    var disk = Proxy<IDiskProvider>((method, args) => method.Name switch
                    {
                        nameof(IDiskProvider.FolderExists) => Directory.Exists((string)args[0]),
                        nameof(IDiskProvider.FileExists) => File.Exists((string)args[0]),
                        nameof(IDiskProvider.GetDirectoryInfo) => fs.DirectoryInfo.FromDirectoryName((string)args[0]),
                        _ => throw new InvalidOperationException("Missing-source check attempted " + method.Name)
                    });
                    var runtime = Proxy<IRuntimeInfo>((method, args) => method.Name == "get_IsWindowsService" ? false :
                        throw new InvalidOperationException("Missing-source check attempted " + method.Name));
                    var importer = new DownloadedBooksImportService(disk, null, null, null, null, null, null, null, null, null,
                        null, null, history, events, runtime, null, logger);
                    var snapshots = new DownloadClientFileSnapshotService(new DownloadClientFileSnapshotRepository(database, events), disk, logger);
                    var item = new DownloadClientItem { DownloadId = "waiting", Title = "Dune", Status = DownloadItemStatus.Completed,
                        OutputPath = new OsPath(directory), FilePaths = paths, FileListConfidence = DownloadClientFileListConfidence.Authoritative,
                        DownloadClientInfo = new DownloadClientItemClientInfo { Id = 1 } };
                    snapshots.CaptureClientList(item);
                    var tracked = new TrackedDownload { DownloadClient = 1, State = TrackedDownloadState.ImportPending,
                        DownloadItem = new DownloadClientItem { DownloadId = item.DownloadId, Title = item.Title, Status = item.Status,
                            OutputPath = item.OutputPath, DownloadClientInfo = item.DownloadClientInfo },
                        RemoteBook = new RemoteBook { Author = author, Books = new() { book } } };
                    snapshots.ApplySnapshot(tracked.DownloadItem);
                    var provider = Proxy<IProvideImportItemService>((method, args) =>
                    {
                        counters.InventoryRequests++;
                        return ((DownloadClientItem)args[0]).Clone();
                    });
                    var mode = Proxy<IDownloadImportModeResolver>((method, args) => ImportMode.Copy);
                    var subject = new CompletedDownloadService(events, history, provider, importer, mode,
                        new TrackedDownloadAlreadyImported(logger, media), null, logger, snapshots);
                    check(subject, tracked, counters);
                }
                finally
                {
                    if (Directory.Exists(directory))
                    {
                        Directory.Delete(directory, true);
                    }
                }
            });
        }
    }
}
