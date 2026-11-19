using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Messaging;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients.Nzbget;
using NzbDrone.Core.Download.Clients.Sabnzbd;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Queue;
using NzbDrone.Core.Qualities;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public partial class CompletedDownloadWaitingFixture
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

        private sealed class RemovalCalls
        {
            public int RemoveItem;
            public int DeleteItemData;
            public int LocalDelete;
            public int RemoteDelete;

            public IDiskProvider Disk => Proxy<IDiskProvider>((method, args) =>
            {
                if (method.Name == nameof(IDiskProvider.FolderExists)) return Directory.Exists((string)args[0]);
                Assert.That(method.Name, Is.EqualTo(nameof(IDiskProvider.DeleteFolder)));
                Assert.That(args[1], Is.True);
                LocalDelete++;
                return null;
            });
        }

        private sealed class RecordingSabnzbd : Sabnzbd
        {
            private readonly RemovalCalls _calls;

            public RecordingSabnzbd(RemovalCalls calls)
                : base(Proxy<ISabnzbdProxy>((method, args) =>
                {
                    if (method.Name == nameof(ISabnzbdProxy.GetQueue)) return new SabnzbdQueue { Items = new() };
                    Assert.That(method.Name, Is.EqualTo(nameof(ISabnzbdProxy.RemoveFromHistory)));
                    Assert.That(args[1], Is.True);
                    calls.RemoteDelete++;
                    return null;
                }), null, null, calls.Disk, null, null, null, LogManager.GetCurrentClassLogger(), DownloadClientTestRoots.Empty)
            {
                _calls = calls;
                Definition = new DownloadClientDefinition { Id = 1, Name = "SAB fixture", Settings = new SabnzbdSettings(), RemoveCompletedDownloads = true };
            }

            public override void RemoveItem(DownloadClientItem item, bool deleteData)
            {
                _calls.RemoveItem++;
                base.RemoveItem(item, deleteData);
            }

            protected override void DeleteItemData(DownloadClientItem item)
            {
                _calls.DeleteItemData++;
                base.DeleteItemData(item);
            }
        }

        private sealed class RecordingNzbget : Nzbget
        {
            private readonly RemovalCalls _calls;

            public RecordingNzbget(RemovalCalls calls)
                : base(Proxy<INzbgetProxy>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(INzbgetProxy.RemoveItem)));
                    calls.RemoteDelete++;
                    return null;
                }), null, null, calls.Disk, null, null, LogManager.GetCurrentClassLogger(), DownloadClientTestRoots.Empty)
            {
                _calls = calls;
                Definition = new DownloadClientDefinition { Id = 1, Name = "NZBGet fixture", Settings = new NzbgetSettings(), RemoveCompletedDownloads = true };
            }

            public override void RemoveItem(DownloadClientItem item, bool deleteData)
            {
                _calls.RemoveItem++;
                base.RemoveItem(item, deleteData);
            }

            protected override void DeleteItemData(DownloadClientItem item)
            {
                _calls.DeleteItemData++;
                base.DeleteItemData(item);
            }
        }

        [Test]
        public void usenet_hub_cleanup_requires_all_ten_source_files(
            [Values(false, true)] bool postgres, [Values(false, true)] bool sab, [Values(1, 10)] int imported)
        {
            Run(postgres, 10, imported, (subject, tracked, counters) =>
            {
                var calls = new RemovalCalls();
                IDownloadClient client = sab ? new RecordingSabnzbd(calls) : new RecordingNzbget(calls);
                var hub = new DownloadEventHub(subject, Proxy<IProvideDownloadClient>((method, args) => client),
                    Proxy<IDownloadImportModeResolver>((method, args) => false), LogManager.GetCurrentClassLogger());
                tracked.DownloadItem.CanBeRemoved = true;
                var complete = subject.VerifyImport(tracked, new List<ImportResult>());
                Assert.That(complete, Is.EqualTo(imported == 10));
                if (complete)
                {
                    hub.Handle(new DownloadCompletedEvent(tracked, tracked.RemoteBook.Author.Id));
                }
                else
                {
                    Assert.That(tracked.StatusMessages.Select(message => message.Title),
                        Is.EquivalentTo(tracked.DownloadItem.FilePaths.Skip(imported).Select(Path.GetFileName)));
                    Assert.That(tracked.StatusMessages.Select(message => message.SourcePath),
                        Is.EquivalentTo(tracked.DownloadItem.FilePaths.Skip(imported)));
                }
                Assert.That(calls.RemoveItem, Is.Zero);
                hub.Handle(new DownloadCanBeRemovedEvent(tracked));
                var expected = complete ? 1 : 0;
                Assert.Multiple(() =>
                {
                    Assert.That(calls.RemoveItem, Is.EqualTo(expected));
                    Assert.That(calls.DeleteItemData, Is.EqualTo(expected));
                    Assert.That(calls.LocalDelete, Is.EqualTo(expected));
                    Assert.That(calls.RemoteDelete, Is.EqualTo(expected));
                    Assert.That(tracked.DownloadItem.Removed, Is.EqualTo(complete));
                });
            }, diskInventory: !sab);
        }

        [Test]
        public void wrong_media_single_file_should_wait_for_manual_import(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType mediaType)
        {
            Run(postgres, 1, 0, (subject, tracked, counters) =>
            {
                var path = Path.Combine(tracked.DownloadItem.OutputPath.FullPath,
                    mediaType == BookMediaType.Audiobook ? "Dune.epub" : "Dune.m4b");
                File.WriteAllText(path, "fixture");
                tracked.DownloadItem.OutputPath = new OsPath(path);
                tracked.DownloadItem.FilePaths = new() { path };
                tracked.DownloadItem.CanBeRemoved = true;
                tracked.IsTrackable = true;

                subject.Import(tracked);

                var calls = new RemovalCalls();
                var client = new RecordingSabnzbd(calls);
                var hub = new DownloadEventHub(subject, Proxy<IProvideDownloadClient>((method, args) => client),
                    Proxy<IDownloadImportModeResolver>((method, args) => false), LogManager.GetCurrentClassLogger());
                hub.Handle(new DownloadCanBeRemovedEvent(tracked));

                Assert.Multiple(() =>
                {
                    Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
                    Assert.That(tracked.IsTrackable, Is.True);
                    Assert.That(tracked.StatusMessages, Has.Length.EqualTo(1));
                    Assert.That(tracked.StatusMessages.Single().Messages, Has.Count.EqualTo(1));
                    Assert.That(tracked.StatusMessages.First().Messages.First(), Does.Contain(Path.GetFileName(path)));
                    Assert.That(tracked.StatusMessages.SelectMany(message => message.Messages),
                        Has.Some.Contains("Use Manual Import to import them anyway"));
                    Assert.That(counters.DownloadHistory.GetLatestDownloadHistoryItem("waiting").EventType,
                        Is.EqualTo(DownloadHistoryEventType.DownloadImportIncomplete));
                    Assert.That(counters.FailedDownloads, Is.Zero);
                    Assert.That(calls.RemoveItem, Is.Zero);
                    Assert.That(calls.DeleteItemData, Is.Zero);
                    Assert.That(tracked.DownloadItem.Removed, Is.False);
                    Assert.That(File.Exists(path), Is.True);
                });
            }, mediaType: mediaType);
        }

        [Test]
        public void profile_refused_payload_should_remain_for_manual_import(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType mediaType)
        {
            Run(postgres, 2, 0, (subject, tracked, counters) =>
            {
                tracked.DownloadItem.CanBeRemoved = true;
                tracked.IsTrackable = true;
                var quality = mediaType == BookMediaType.Audiobook ? Quality.MP3 : Quality.EPUB;
                var expected = mediaType == BookMediaType.Audiobook ? Quality.M4B : Quality.AZW3;
                tracked.RemoteBook.ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(expected) };
                counters.SetGrabQuality(expected);
                var reason = $"Quality '{quality.Name}' not allowed by profile 'Preferred format'";
                counters.ImportResults = tracked.DownloadItem.FilePaths.Select(path =>
                {
                    File.WriteAllText(path, "fixture");
                    var book = tracked.RemoteBook.Books.Single();
                    var decision = new ImportDecision<LocalBook>(new LocalBook
                    {
                        Path = path, Book = book, Edition = book.Editions.Single(),
                        Author = tracked.RemoteBook.Author, Quality = new QualityModel(quality)
                    });
                    decision.Reject(new Rejection(reason, RejectionType.Permanent, canBypass: false, category: "Quality", severity: 3));
                    return new ImportResult(decision, reason);
                }).ToList();

                subject.Import(tracked);
                // ApplyActualFileQuality mutates ParsedBookInfo on the first attempt.
                Assert.That(tracked.RemoteBook.ParsedBookInfo.Quality.Quality, Is.EqualTo(quality));
                subject.Import(tracked);

                var calls = new RemovalCalls();
                var hub = new DownloadEventHub(subject, Proxy<IProvideDownloadClient>((method, args) => new RecordingSabnzbd(calls)),
                    Proxy<IDownloadImportModeResolver>((method, args) => false), LogManager.GetCurrentClassLogger());
                hub.Handle(new DownloadCanBeRemovedEvent(tracked));

                Assert.Multiple(() =>
                {
                    Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
                    Assert.That(tracked.IsTrackable, Is.True);
                    Assert.That(tracked.StatusMessages.First().Messages.First(), Does.StartWith($"Grabbed Dune as {expected.Name}, but it only contains"));
                    Assert.That(tracked.StatusMessages, Has.Length.EqualTo(3));
                    Assert.That(tracked.StatusMessages.Skip(1).Select(message => message.Title),
                        Is.EquivalentTo(tracked.DownloadItem.FilePaths.Select(Path.GetFileName)));
                    Assert.That(tracked.StatusMessages.Skip(1).SelectMany(message => message.Messages),
                        Is.EqualTo(new[] { reason, reason }));
                    Assert.That(counters.DownloadHistory.GetLatestDownloadHistoryItem("waiting")?.EventType,
                        Is.EqualTo(DownloadHistoryEventType.DownloadImportIncomplete));
                    Assert.That(counters.FailedDownloads, Is.Zero);
                    Assert.That(calls.RemoveItem, Is.Zero);
                    Assert.That(calls.DeleteItemData, Is.Zero);
                    Assert.That(tracked.DownloadItem.Removed, Is.False);
                    Assert.That(tracked.DownloadItem.FilePaths.All(File.Exists), Is.True);
                });
            }, mediaType: mediaType);
        }

        private sealed class Counters
        {
            public int HistoryReads;
            public int FileReads;
            public int InventoryRequests;
            public int FailedDownloads;
            public List<ImportResult> ImportResults;
            public bool FailFileListing;
            public bool FailDuringListing;
            public bool FailHistoryReads;
            public Exception HistoryReadError;
            public int IncompleteEvents;
            public Action<IEvent> OnEvent;
            public Func<ITrackedDownloadService> NewTracker;
            public QueueService Queue;
            public DownloadHistoryService DownloadHistory;
            public Action<string> ImportSource;
            public Action<Quality> SetGrabQuality;
            public void Reset() { HistoryReads = 0; FileReads = 0; InventoryRequests = 0; }
        }

        [TestCase(false, "late", BookMediaType.Audiobook)]
        [TestCase(true, "late", BookMediaType.Audiobook)]
        [TestCase(false, "late", BookMediaType.Ebook)]
        [TestCase(true, "late", BookMediaType.Ebook)]
        [TestCase(false, "extra", BookMediaType.Audiobook)]
        [TestCase(true, "extra", BookMediaType.Audiobook)]
        [TestCase(false, "other-format", BookMediaType.Audiobook)]
        [TestCase(true, "other-format", BookMediaType.Audiobook)]
        [TestCase(false, "complete", BookMediaType.Audiobook)]
        [TestCase(true, "complete", BookMediaType.Audiobook)]
        [TestCase(false, "complete", BookMediaType.Ebook)]
        [TestCase(true, "complete", BookMediaType.Ebook)]
        [TestCase(false, "unreadable", BookMediaType.Audiobook)]
        [TestCase(true, "unreadable", BookMediaType.Audiobook)]
        [TestCase(false, "authoritative", BookMediaType.Audiobook)]
        [TestCase(true, "authoritative", BookMediaType.Audiobook)]
        public void cleanup_should_surface_late_book_files_for_manual_import(bool postgres, string scenario, BookMediaType mediaType)
        {
            Run(postgres, 1, 1, (subject, tracked, counters) =>
            {
                Assert.That(subject.VerifyImport(tracked, new List<ImportResult>()), Is.True);
                tracked.IsTrackable = false;
                tracked.DownloadItem.CanBeRemoved = true;
                var removed = 0;
                var marked = 0;
                var client = Proxy<IDownloadClient>((method, args) =>
                {
                    if (method.Name == "get_Definition") return new DownloadClientDefinition { RemoveCompletedDownloads = true };
                    if (method.Name == nameof(IDownloadClient.MarkItemAsImported)) marked++;
                    if (method.Name == nameof(IDownloadClient.RemoveItem))
                    {
                        Assert.That(args[1], Is.True);
                        removed++;
                    }
                    return null;
                });
                var hub = new DownloadEventHub(subject, Proxy<IProvideDownloadClient>((method, args) => client),
                    Proxy<IDownloadImportModeResolver>((method, args) => false), LogManager.GetCurrentClassLogger());
                hub.Handle(new DownloadCompletedEvent(tracked, tracked.RemoteBook.Author.Id));
                Assert.That(marked, Is.EqualTo(1));
                Assert.That(removed, Is.Zero, "Cleanup must follow completion event persistence");

                var extension = scenario == "extra" ? ".nfo" : scenario == "other-format" || mediaType == BookMediaType.Ebook ? ".epub" : ".mp3";
                var latePath = Path.Combine(tracked.DownloadItem.OutputPath.FullPath, "Dune Part 002" + extension);
                if (scenario is "late" or "extra" or "other-format") File.WriteAllText(latePath, "fixture");
                counters.FailFileListing = scenario is "unreadable" or "authoritative";
                counters.Reset();
                hub.Handle(new DownloadCanBeRemovedEvent(tracked));

                if (scenario == "late")
                {
                    Assert.That(removed, Is.Zero);
                    Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
                    Assert.That(tracked.IsTrackable, Is.True, "The existing queue mapper must show the warning");
                    Assert.That(tracked.StatusMessages.SelectMany(message => message.Messages), Has.Some.Contains("Manual Import"));
                    Assert.That(counters.DownloadHistory.GetLatestDownloadHistoryItem("waiting").EventType,
                        Is.EqualTo(DownloadHistoryEventType.DownloadImportIncomplete));
                    Assert.That(File.Exists(latePath), Is.True);
                    Assert.That(tracked.StatusMessages.Select(message => message.Title), Does.Contain(Path.GetFileName(latePath)));
                    Assert.That(tracked.StatusMessages.Select(message => message.SourcePath), Does.Contain(latePath));
                    Assert.That(counters.HistoryReads, Is.EqualTo(1));
                    Assert.That(counters.FileReads, Is.EqualTo(1));
                    counters.ImportSource(latePath);
                    counters.FailFileListing = false;
                    hub.Handle(new DownloadCanBeRemovedEvent(tracked));
                    Assert.That(removed, Is.Zero, "A blocked download waits for an explicit import or Retry");
                    Assert.That(subject.VerifyImport(tracked, new List<ImportResult>()), Is.True,
                        "Persisted successful manual import evidence permits completion again");
                    hub.Handle(new DownloadCanBeRemovedEvent(tracked));
                    Assert.That(removed, Is.EqualTo(1));
                }
                else if (scenario == "unreadable")
                {
                    Assert.That(removed, Is.Zero);
                    Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.Imported));
                    Assert.That(counters.IncompleteEvents, Is.Zero);
                    counters.FailFileListing = false;
                    hub.Handle(new DownloadCanBeRemovedEvent(tracked));
                    Assert.That(removed, Is.EqualTo(1));
                    Assert.That(tracked.StatusMessages, Is.Empty);
                }
                else
                {
                    Assert.That(removed, Is.EqualTo(1));
                    Assert.That(counters.InventoryRequests, Is.Zero);
                    Assert.That(counters.HistoryReads, Is.EqualTo(scenario == "authoritative" ? 0 : 1));
                }
            }, diskInventory: scenario != "authoritative", mediaType: mediaType);
        }

        [Test]
        public void repeated_cleanup_keeps_one_late_file_warning([Values] bool postgres)
        {
            var logs = new NLog.Targets.MemoryTarget { Layout = "${level}|${message}" };
            using var factory = new LogFactory();
            var config = new NLog.Config.LoggingConfiguration();
            config.AddRule(LogLevel.Debug, LogLevel.Fatal, logs);
            factory.Configuration = config;
            Run(postgres, 1, 1, (subject, tracked, counters) =>
            {
                Assert.That(subject.VerifyImport(tracked, new List<ImportResult>()), Is.True);
                var late = Path.Combine(tracked.DownloadItem.OutputPath.FullPath, "Dune Part 002.mp3");
                File.WriteAllText(late, "fixture");
                for (var cycle = 0; cycle < 4; cycle++)
                {
                    tracked.State = TrackedDownloadState.Imported;
                    Assert.That(subject.CanRemoveDownload(tracked), Is.False);
                    Assert.That(tracked.StatusMessages.SelectMany(message => message.Messages)
                        .Count(message => message.StartsWith("Additional book files")), Is.EqualTo(1));
                }
                Assert.That(File.Exists(late), Is.True);
            }, diskInventory: true, loggerOverride: factory.GetLogger("download-cleanup"));
            factory.Flush();
            Assert.That(logs.Logs.Count(line => line.StartsWith("Warn|") && line.Contains("Additional book files")), Is.EqualTo(1));
            Assert.That(logs.Logs.Count(line => line.StartsWith("Debug|") && line.Contains("Additional book files")), Is.EqualTo(3));
        }

        private static IEnumerable<IFileInfo> FailAfterFirstFile(IEnumerable<IFileInfo> files)
        {
            foreach (var file in files.Take(1)) yield return file;
            throw new UnauthorizedAccessException("An output subfolder is not readable");
        }

        private static void Run(bool postgres, int sources, int imported, Action<CompletedDownloadService, TrackedDownload, Counters> check,
            bool diskInventory = false, BookMediaType mediaType = BookMediaType.Audiobook, Logger loggerOverride = null)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "waiting-download-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                try
                {
                    var logger = loggerOverride ?? LogManager.GetCurrentClassLogger();
                    var counters = new Counters();
                    var events = Proxy<IEventAggregator>((method, args) =>
                    {
                        if (args[0] is DownloadFailedEvent) counters.FailedDownloads++;
                        if (args[0] is BookImportIncompleteEvent incomplete)
                        {
                            counters.IncompleteEvents++;
                            counters.DownloadHistory?.Handle(incomplete);
                        }
                        if (args[0] is DownloadCompletedEvent completed) counters.DownloadHistory?.Handle(completed);
                        counters.OnEvent?.Invoke((IEvent)args[0]);
                        return null;
                    });
                    var author = new AuthorRepository(database, events, logger).Insert(new Author
                    {
                        Name = "Frank Herbert", CleanName = "frankherbert", SortName = "frankherbert", TitleSlug = "frank-herbert",
                        Path = directory, Added = DateTime.UtcNow
                    });
                    var book = new BookRepository(database, events).Insert(new Book
                    {
                        AuthorId = author.Id, Author = author, Title = "Dune", CleanTitle = "dune", TitleSlug = "dune",
                        MediaType = mediaType, Added = DateTime.UtcNow
                    });
                    var edition = new EditionRepository(database, events).Insert(new Edition
                    {
                        BookId = book.Id, Book = book, Title = "Dune", ForeignEditionId = "local:waiting", ReadingFormatId = 2, Monitored = true
                    });
                    book.Editions = new() { edition };
                    var extension = mediaType == BookMediaType.Ebook ? ".epub" : ".mp3";
                    var paths = Enumerable.Range(1, sources).Select(index => Path.Combine(directory, $"Dune Part {index:D3}" + extension)).ToList();
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
                    counters.SetGrabQuality = quality =>
                    {
                        var grabbed = histories.FindByDownloadId("waiting").Single(row => row.EventType == EntityHistoryEventType.Grabbed);
                        grabbed.Quality = new QualityModel(quality);
                        histories.Update(grabbed);
                    };

                    counters.ImportSource = path => histories.Insert(new EntityHistory
                    {
                        DownloadId = "waiting", AuthorId = author.Id, BookId = book.Id, EditionId = edition.Id,
                        EventType = EntityHistoryEventType.BookFileImported, Date = DateTime.UtcNow,
                        SourceTitle = "Dune", Quality = new QualityModel(Quality.MP3), Data = new() { ["DroppedPath"] = path }
                    });
                    var history = Proxy<IHistoryService>((method, args) =>
                    {
                        if (method.Name == nameof(IHistoryService.FindByDownloadIds))
                        {
                            return histories.FindByDownloadId("waiting").Where(row => row.EventType == (EntityHistoryEventType)args[1]).ToList();
                        }
                        Assert.That(method.Name, Is.EqualTo(nameof(IHistoryService.FindByDownloadId)));
                        counters.HistoryReads++;
                        if (counters.FailHistoryReads) throw counters.HistoryReadError ?? new InvalidOperationException("History database unavailable");
                        return histories.FindByDownloadId((string)args[0]);
                    });
                    counters.DownloadHistory = new DownloadHistoryService(new DownloadHistoryRepository(database, events), history);
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
                        nameof(IDiskProvider.GetFileInfo) => fs.FileInfo.FromFileName((string)args[0]),
                        nameof(IDiskProvider.GetDirectoryInfo) => Proxy<IDirectoryInfo>((member, parameters) =>
                        {
                            var info = fs.DirectoryInfo.FromDirectoryName((string)args[0]);
                            if (member.Name != nameof(IDirectoryInfo.EnumerateFiles)) return member.Invoke(info, parameters);
                            Assert.That(((EnumerationOptions)parameters[1]).IgnoreInaccessible, Is.False);
                            if (counters.FailFileListing) throw new IOException("File listing unavailable");
                            var entries = info.EnumerateFiles((string)parameters[0], (EnumerationOptions)parameters[1]);
                            return counters.FailDuringListing ? FailAfterFirstFile(entries) : entries;
                        }),
                        nameof(IDiskProvider.GetFileInfos) => counters.FailFileListing
                            ? throw new IOException("File listing unavailable")
                            : Directory.GetFiles((string)args[0], "*", SearchOption.AllDirectories).Select(fs.FileInfo.FromFileName).ToList(),
                        _ => throw new InvalidOperationException("Missing-source check attempted " + method.Name)
                    });
                    var runtime = Proxy<IRuntimeInfo>((method, args) => method.Name == "get_IsWindowsService" ? false :
                        throw new InvalidOperationException("Missing-source check attempted " + method.Name));
                    var importer = new DownloadedBooksImportService(disk, null, null, null, null, null, null, null, null, null,
                        null, null, history, events, runtime, null, logger, queueService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.EmptyQueue(), conversionJobService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoConversionJobs());
                    var snapshots = new DownloadClientFileSnapshotService(new DownloadClientFileSnapshotRepository(database, events), disk, logger);
                    var item = new DownloadClientItem { DownloadId = "waiting", Title = "Dune", Status = DownloadItemStatus.Completed,
                        OutputPath = new OsPath(directory), FilePaths = paths, FileListConfidence = DownloadClientFileListConfidence.Authoritative,
                        DownloadClientInfo = new DownloadClientItemClientInfo { Id = 1 } };
                    if (diskInventory)
                    {
                        foreach (var path in paths) File.WriteAllText(path, "fixture");
                        item.FileListConfidence = DownloadClientFileListConfidence.Disk;
                        snapshots.CaptureCompletedOutput(item);
                    }
                    else
                    {
                        snapshots.CaptureClientList(item);
                    }
                    var tracked = new TrackedDownload { DownloadClient = 1, State = TrackedDownloadState.ImportPending,
                        DownloadItem = new DownloadClientItem { DownloadId = item.DownloadId, Title = item.Title, Status = item.Status,
                            OutputPath = item.OutputPath, DownloadClientInfo = item.DownloadClientInfo },
                        RemoteBook = new RemoteBook { Author = author, Books = new() { book } } };
                    snapshots.ApplySnapshot(tracked.DownloadItem);
                    var formats = Proxy<ICustomFormatCalculationService>((method, args) => new List<CustomFormat>());
                    counters.NewTracker = () => new TrackedDownloadService(
                        Proxy<IParsingService>((method, args) => tracked.RemoteBook), new CacheManager(), history,
                        events, counters.DownloadHistory, formats, logger, snapshots);
                    counters.Queue = new QueueService(events, history, formats);
                    counters.Queue.Handle(new TrackedDownloadRefreshedEvent(new List<TrackedDownload>()));
                    var provider = Proxy<IProvideImportItemService>((method, args) =>
                    {
                        counters.InventoryRequests++;
                        return ((DownloadClientItem)args[0]).Clone();
                    });
                    var mode = Proxy<IDownloadImportModeResolver>((method, args) => ImportMode.Copy);
                    var importService = Proxy<IDownloadedBooksImportService>((method, args) =>
                    {
                        if (method.Name == nameof(IDownloadedBooksImportService.ProcessIncludedMedia)) return new List<ImportResult>();
                        Assert.That(method.Name, Is.EqualTo(nameof(IDownloadedBooksImportService.ProcessPath)));
                        return counters.ImportResults ?? importer.ProcessPath((string)args[0], (ImportMode)args[1],
                            (Author)args[2], (DownloadClientItem)args[3], (RemoteBook)args[4], (bool)args[5]);
                    });
                    var subject = new CompletedDownloadService(events, history, provider, importService, mode,
                        new TrackedDownloadAlreadyImported(logger, media), logger, snapshots);
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
