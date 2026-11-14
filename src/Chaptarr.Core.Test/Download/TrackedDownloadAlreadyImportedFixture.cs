using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Common.Disk;
using static Chaptarr.Core.Test.Common.InterfaceProxy;
using NzbDrone.Common.Serializer;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class TrackedDownloadAlreadyImportedFixture
    {
        [TestCaseSource(typeof(MediaFileExtensions), nameof(MediaFileExtensions.AllExtensions))]
        public void completion_must_require_failed_files_for_every_catalogued_extension(string extension)
        {
            var media = QualityMediaTypeHelper.GetMediaTypeFromPath("book" + extension);
            var book = new Book { Id = 1, MediaType = media.Value };
            var original = "/downloads/original" + extension;
            var selected = "/downloads/selected" + extension;
            var tracked = new TrackedDownload
            {
                DownloadItem = new DownloadClientItem { DownloadId = "catalog", FilePaths = new() { original, selected } },
                RemoteBook = new RemoteBook { Books = new() { book } }
            };
            var history = new[] { new EntityHistory
            {
                DownloadId = "catalog", BookId = book.Id, EditionId = 10, EventType = EntityHistoryEventType.BookFileImported,
                Data = new() { ["DroppedPath"] = original }
            } };
            if (media == BookMediaType.Ebook) history[0].Data["ExcludedSourcePaths"] = new[] { selected }.ToJson();
            var failed = new ImportResult(new ImportDecision<LocalBook>(new LocalBook
            {
                IsManualImport = true, Path = selected, Book = book, Edition = new Edition { Id = 10, BookId = book.Id }
            }), "Transfer failed");
            var result = TrackedDownloadAlreadyImported.Evaluate(tracked, new[] { failed }, history: history);
            Assert.That(result.Complete, Is.False);
            Assert.That(result.Missing.Single().Title, Is.EqualTo(System.IO.Path.GetFileName(selected)));
            Assert.That(result.Missing.Single().SourcePath, Is.EqualTo(selected));
        }

        [Test]
        public void missing_inventory_status_uses_distinct_relative_paths_without_host_directories()
        {
            var tracked = new TrackedDownload
            {
                DownloadItem = new DownloadClientItem
                {
                    DownloadId = "relative-inventory",
                    OutputPath = new OsPath(@"C:\downloads\book".AsOsAgnostic()),
                    FilePaths = new() { @"C:\downloads\book\disc-1\01.mp3".AsOsAgnostic(), @"C:\downloads\book\disc-2\01.mp3".AsOsAgnostic(), @"C:\elsewhere\03.mp3".AsOsAgnostic() }
                },
                RemoteBook = new RemoteBook { Books = new() { new Book { Id = 1, MediaType = BookMediaType.Audiobook } } }
            };
            Assert.That(new TrackedDownloadAlreadyImported(LogManager.GetCurrentClassLogger()).IsImported(tracked, new()), Is.False);
            Assert.That(tracked.StatusMessages.Select(message => message.Title),
                Is.EquivalentTo(new[] { @"disc-1\01.mp3".AsOsAgnostic(), @"disc-2\01.mp3".AsOsAgnostic(), "03.mp3" }));
            var json = System.Text.Json.JsonSerializer.Serialize(tracked.StatusMessages);
            Assert.That(json, Does.Not.Contain("SourcePath"));
            Assert.That(json, Does.Not.Contain("downloads"));
            Assert.That(json, Does.Not.Contain("elsewhere"));
        }

        [TestCase(false, false, false)]
        [TestCase(false, false, true)]
        [TestCase(false, true, false)]
        [TestCase(true, false, false)]
        [TestCase(true, false, true)]
        [TestCase(true, true, false)]
        public void alternative_ebook_waiver_requires_current_grab_same_edition_coverage(bool savedFileEvidence, bool oldGrab, bool differentEdition)
        {
            var grabbed = DateTime.UtcNow.AddMinutes(-5);
            var book = new Book { Id = 1, MediaType = BookMediaType.Ebook };
            var edition = new Edition { Id = 10, BookId = book.Id, Book = book };
            const string epub = "/downloads/book.epub";
            const string mobi = "/downloads/book.mobi";
            var tracked = new TrackedDownload
            {
                DownloadItem = new DownloadClientItem { DownloadId = "alternative-retry", FilePaths = new() { epub, mobi } },
                RemoteBook = new RemoteBook { Books = new() { book }, AlternativeCopies = true, AlternativeCopyGrabDate = grabbed,
                    TargetEditionIds = new() { [book.Id] = edition.Id } }
            };
            var skipped = new ImportDecision<LocalBook>(new LocalBook { Path = mobi, Book = book, Edition = edition });
            skipped.Reject(new NzbDrone.Core.DecisionEngine.Rejection("Skipped duplicate ebook format")
                { Category = ImportApprovedBooks.EbookAlternativeRejectionCategory });
            var importedEditionId = differentEdition ? 11 : edition.Id;
            var importedAt = grabbed.AddMinutes(oldGrab ? -1 : 1);
            var history = new[] { new EntityHistory { DownloadId = "alternative-retry", BookId = book.Id, EditionId = importedEditionId,
                Date = importedAt, EventType = EntityHistoryEventType.BookFileImported,
                Data = new() { ["DroppedPath"] = epub, ["ExcludedSourcePaths"] = new[] { mobi }.ToJson() } } };
            var saved = new[] { new BookFile { EditionId = importedEditionId, Edition = new Edition { Id = importedEditionId, BookId = book.Id },
                DateAdded = importedAt, MatchProvenance = new MatchProvenance { ImportDownloadId = "alternative-retry",
                    ImportSourcePaths = new() { epub }, ImportExcludedSourcePaths = new() { mobi } } } };
            var result = TrackedDownloadAlreadyImported.Evaluate(tracked, new[] { new ImportResult(skipped, "Skipped duplicate ebook format") },
                history: savedFileEvidence ? null : history, savedFiles: savedFileEvidence ? saved : null);
            Assert.That(result.Complete, Is.EqualTo(!oldGrab && !differentEdition));
        }

        [TestCase(ImportResultType.Skipped)]
        [TestCase(ImportResultType.Rejected)]
        [TestCase(ImportResultType.Pending)]
        public void current_unsuccessful_import_cannot_reuse_an_old_alternative_exclusion(ImportResultType outcome)
        {
            const string importedPath = "/downloads/book.epub";
            const string alternativePath = "/downloads/book.mobi";
            var book = new Book { Id = 1, MediaType = BookMediaType.Ebook };
            var edition = new Edition { Id = 10, BookId = book.Id, Book = book };
            var tracked = new TrackedDownload
            {
                DownloadItem = new DownloadClientItem { DownloadId = "alternative", FilePaths = new() { importedPath, alternativePath } },
                RemoteBook = new RemoteBook { Books = new() { book } }
            };
            var history = new[] { new EntityHistory
            {
                DownloadId = "alternative", BookId = book.Id, EditionId = edition.Id, EventType = EntityHistoryEventType.BookFileImported,
                Data = new() { ["DroppedPath"] = importedPath, ["ExcludedSourcePaths"] = new[] { alternativePath }.ToJson() }
            } };
            var local = new LocalBook { IsManualImport = true, Path = alternativePath, Book = book, Edition = edition };
            var failed = new ImportResult(new ImportDecision<LocalBook>(local), outcome, "Import not finished");
            Assert.That(TrackedDownloadAlreadyImported.Evaluate(tracked, history: history).Complete, Is.True);
            var result = TrackedDownloadAlreadyImported.Evaluate(tracked, new[] { failed }, history: history);
            Assert.That(result.Complete, Is.False);
            Assert.That(result.Missing.Single().Title, Is.EqualTo(System.IO.Path.GetFileName(alternativePath)));

            var skipped = new ImportDecision<LocalBook>(local);
            skipped.Reject(new NzbDrone.Core.DecisionEngine.Rejection("Skipped duplicate ebook format")
            {
                Category = ImportApprovedBooks.EbookAlternativeRejectionCategory
            });
            Assert.That(TrackedDownloadAlreadyImported.Evaluate(tracked,
                new[] { new ImportResult(skipped, "Skipped duplicate ebook format") }, history: history).Complete, Is.True);
            Assert.That(TrackedDownloadAlreadyImported.Evaluate(tracked,
                new[] { new ImportResult(new ImportDecision<LocalBook>(local)) }, history: history).Complete, Is.True);
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void restart_completion_must_cover_original_inventory_from_per_file_history(bool postgres, bool conversion)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var events = Proxy<IEventAggregator>((method, args) => null);
                var logger = LogManager.GetCurrentClassLogger();
                var snapshots = new DownloadClientFileSnapshotRepository(database, events);
                var snapshotService = new DownloadClientFileSnapshotService(snapshots, null, logger);
                var histories = new HistoryRepository(database, events);
                var original = new DownloadClientItem { DownloadId = "restart", Title = "Parts", OutputPath = new OsPath(@"C:\downloads\book".AsOsAgnostic()),
                    DownloadClientInfo = new DownloadClientItemClientInfo { Id = 1 },
                    FilePaths = new List<string> { @"C:\downloads\book\01.mp3".AsOsAgnostic(), @"C:\downloads\book\02.mp3".AsOsAgnostic(), @"C:\downloads\book\cover.jpg".AsOsAgnostic(), @"C:\downloads\book\info.nfo".AsOsAgnostic(),
                        @"C:\downloads\book\._01.mp3".AsOsAgnostic(), @"C:\downloads\book\._02.mp3".AsOsAgnostic() } };
                snapshotService.CaptureClientList(original);
                var tracked = new TrackedDownload
                {
                    DownloadItem = new DownloadClientItem { DownloadId = original.DownloadId, Title = original.Title, OutputPath = original.OutputPath,
                        DownloadClientInfo = original.DownloadClientInfo },
                    RemoteBook = new RemoteBook { Books = new List<Book> { new() { Id = 10, MediaType = BookMediaType.Audiobook } },
                        ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.MP3) } }
                };
                // A restarted process sees only the saved inventory and persisted history.
                new DownloadClientFileSnapshotService(snapshots, null, logger).CaptureCompletedOutput(tracked.DownloadItem);
                Assert.That(tracked.DownloadItem.FilePaths, Is.EquivalentTo(original.FilePaths));
                var imported = histories.Insert(new EntityHistory { DownloadId = original.DownloadId, BookId = 10, EditionId = 100,
                    EventType = EntityHistoryEventType.BookFileImported, Date = DateTime.UtcNow, Quality = new QualityModel(Quality.M4B),
                    SourceTitle = "Parts", Data = new Dictionary<string, string> { ["DroppedPath"] = @"C:\downloads\book\01.mp3".AsOsAgnostic() } });
                var subject = new TrackedDownloadAlreadyImported(logger);
                Assert.That(subject.IsImported(tracked, histories.FindByDownloadId(original.DownloadId)), Is.False);
                Assert.That(tracked.StatusMessages.Any(message => message.Title == "02.mp3"), Is.True);
                if (conversion)
                {
                    imported.Data["DroppedPath"] = @"C:\conversion\generated.m4b".AsOsAgnostic();
                    imported.Data["SourcePaths"] = new[] { @"C:\downloads\book\01.mp3".AsOsAgnostic(), @"C:\downloads\book\02.mp3".AsOsAgnostic() }.ToJson();
                    histories.Update(imported);
                }
                else
                {
                    histories.Insert(new EntityHistory { DownloadId = original.DownloadId, BookId = 10, EditionId = 100,
                        EventType = EntityHistoryEventType.BookFileImported, Date = DateTime.UtcNow, Quality = new QualityModel(Quality.MP3),
                        SourceTitle = "Parts", Data = new Dictionary<string, string> { ["DroppedPath"] = @"C:\downloads\book\02.mp3".AsOsAgnostic() } });
                }

                Assert.That(subject.IsImported(tracked, histories.FindByDownloadId(original.DownloadId)), Is.True);
                Assert.That(tracked.StatusMessages, Is.Empty);
                snapshotService.Handle(new TrackedDownloadsRemovedEvent(new List<TrackedDownload> { tracked }));
                Assert.That(snapshots.Find(1, original.DownloadId), Is.Null);
                TestContext.Progress.WriteLine($"SOURCE_RECOVERY database={database.DatabaseType} conversion={conversion} sources=2");
            });
        }

        [TestCase("/downloads/bonus.mp3")]
        [TestCase("/downloads/book.m4b")]
        [TestCase("/downloads/sample.mp3")]
        public void unresolved_audio_must_not_be_discarded_by_filename_or_extension(string unresolved)
        {
            var tracked = new TrackedDownload
            {
                DownloadItem = new DownloadClientItem { DownloadId = "parts", Title = "Book", FilePaths = new List<string> { "/downloads/part.mp3", unresolved } },
                RemoteBook = new RemoteBook { Books = new List<Book> { new() { Id = 1, MediaType = BookMediaType.Audiobook } } }
            };
            var history = new List<EntityHistory> { new() { DownloadId = "parts", BookId = 1, EventType = EntityHistoryEventType.BookFileImported,
                Data = new Dictionary<string, string> { ["DroppedPath"] = "/downloads/part.mp3" } } };
            var missing = new ImportResult(new ImportDecision<LocalBook>(new LocalBook { Path = unresolved }), "Selected file no longer exists");
            var result = TrackedDownloadAlreadyImported.Evaluate(tracked, new[] { missing }, history: history);
            Assert.That(result.Complete, Is.False);
            Assert.That(result.Missing.Single().Title, Is.EqualTo(System.IO.Path.GetFileName(unresolved)));
            Assert.That(result.Missing.Single().SourcePath, Is.EqualTo(unresolved));
            Assert.That(result.Missing.Single().Messages, Has.Member("Selected file no longer exists"));
        }

        [Test]
        public void completion_work_should_be_bounded_by_file_count_and_one_batched_file_read()
        {
            const int count = 4000;
            var paths = Enumerable.Range(1, count).Select(index => $"/downloads/{index}.mp3").ToList();
            var tracked = new TrackedDownload { DownloadItem = new DownloadClientItem { DownloadId = "large", Title = "Large recording", FilePaths = paths },
                RemoteBook = new RemoteBook { Books = new List<Book> { new() { Id = 1, MediaType = BookMediaType.Audiobook } } } };
            var history = paths.Select(path => new EntityHistory { DownloadId = "large", BookId = 1, EventType = EntityHistoryEventType.BookFileImported,
                Data = new Dictionary<string, string> { ["DroppedPath"] = path } }).ToList();
            var reads = 0;
            var media = Proxy<IMediaFileService>((method, args) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(IMediaFileService.GetFilesByBooks)));
                Assert.That((List<int>)args[0], Is.EqualTo(new[] { 1 }));
                reads++;
                return new List<BookFile>();
            });
            var timer = Stopwatch.StartNew();
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            Assert.That(new TrackedDownloadAlreadyImported(LogManager.GetCurrentClassLogger(), media).IsImported(tracked, history), Is.True);
            var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Assert.That(reads, Is.EqualTo(1));
            Assert.That(timer.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)));
            Assert.That(bytes, Is.LessThan(32L * 1024 * 1024));
            TestContext.Progress.WriteLine($"COMPLETION_BOUND sources={count} fileQueries={reads} elapsedMs={timer.Elapsed.TotalMilliseconds:F2} allocatedBytes={bytes}");
        }

        [TestCase(BookMediaType.Audiobook)] [TestCase(BookMediaType.Ebook)]
        public void alternative_history_completion_should_require_current_grab_and_complete_source_paths(BookMediaType media)
        {
            var grabbed = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
            var extension = media == BookMediaType.Audiobook ? ".mp3" : ".epub";
            var first = "/downloads/one" + extension;
            var second = "/downloads/two" + (media == BookMediaType.Audiobook ? ".mp3" : ".mobi");
            var companion = "/downloads/one" + (media == BookMediaType.Audiobook ? ".epub" : ".m4b");
            var tracked = new TrackedDownload { DownloadItem = new DownloadClientItem { DownloadId = "alt", Title = "Captured alternatives",
                FilePaths = new List<string> { first, second, companion, "/downloads/cover.jpg" } },
                RemoteBook = new RemoteBook { AlternativeCopies = true, AlternativeCopyGrabDate = grabbed, TargetEditionIds = new() { [10] = 100, [11] = 110 },
                    Books = new List<Book> { new() { Id = 10, AuthorId = 7, MediaType = media }, new() { Id = 11, AuthorId = 7, MediaType = media } } } };
            var imported = new EntityHistory { DownloadId = "alt", BookId = 10, EditionId = 100, AuthorId = 7, EventType = EntityHistoryEventType.BookFileImported, Date = grabbed.AddMinutes(1),
                Data = new Dictionary<string,string> { ["droppedPath"] = first } };
            var history = new List<EntityHistory> { imported };
            var subject = new TrackedDownloadAlreadyImported(LogManager.GetCurrentClassLogger());
            Assert.That(subject.IsImported(tracked, history), Is.False);
            imported.Data["sourcePaths"] = new[] { first, second }.ToJson();
            Assert.That(subject.IsImported(tracked, history), Is.True);
            imported.Data["sourcePaths"] = "{broken";
            Assert.That(subject.IsImported(tracked, history), Is.False);
            imported.Data["sourcePaths"] = new[] { first, second }.ToJson();
            imported.EditionId = 101;
            Assert.That(subject.IsImported(tracked, history), Is.False);
            imported.EditionId = 100;
            imported.Date = grabbed.AddMinutes(-1);
            Assert.That(subject.IsImported(tracked, history), Is.False);
            imported.Date = grabbed.AddMinutes(1); imported.BookId = 99;
            Assert.That(subject.IsImported(tracked, history), Is.False);
            imported.BookId = 10; tracked.RemoteBook.AlternativeCopies = false;
            Assert.That(subject.IsImported(tracked, history), Is.False);
        }

        [Test]
        public void should_not_treat_empty_remote_book_list_as_already_imported()
        {
            var subject = new TrackedDownloadAlreadyImported(LogManager.GetCurrentClassLogger());

            var trackedDownload = new TrackedDownload
            {
                DownloadItem = new DownloadClientItem
                {
                    DownloadId = "download-1",
                    Title = "Missing.Context",
                    DownloadClientInfo = new DownloadClientItemClientInfo
                    {
                        Name = "qBittorrent",
                        Protocol = DownloadProtocol.Torrent
                    }
                },
                RemoteBook = new RemoteBook
                {
                    Books = new List<Book>()
                }
            };

            var historyItems = new List<EntityHistory>
            {
                new()
                {
                    EventType = EntityHistoryEventType.BookFileImported,
                    BookId = 123,
                    AuthorId = 7,
                    Date = DateTime.UtcNow
                }
            };

            Assert.That(subject.IsImported(trackedDownload, historyItems), Is.False);
        }

        [Test]
        public void should_not_treat_missing_remote_book_context_as_already_imported()
        {
            var subject = new TrackedDownloadAlreadyImported(LogManager.GetCurrentClassLogger());

            var trackedDownload = new TrackedDownload
            {
                DownloadItem = new DownloadClientItem
                {
                    DownloadId = "download-2",
                    Title = "Missing.Context",
                    DownloadClientInfo = new DownloadClientItemClientInfo
                    {
                        Name = "qBittorrent",
                        Protocol = DownloadProtocol.Torrent
                    }
                },
                RemoteBook = null
            };

            var historyItems = new List<EntityHistory>
            {
                new()
                {
                    EventType = EntityHistoryEventType.BookFileImported,
                    BookId = 123,
                    AuthorId = 7,
                    Date = DateTime.UtcNow
                }
            };

            Assert.That(subject.IsImported(trackedDownload, historyItems), Is.False);
        }

        [Test]
        public void should_only_require_import_history_for_books_matching_release_media_type()
        {
            var subject = new TrackedDownloadAlreadyImported(LogManager.GetCurrentClassLogger());

            var trackedDownload = new TrackedDownload
            {
                DownloadItem = new DownloadClientItem
                {
                    DownloadId = "download-3",
                    FilePaths = new List<string> { "/downloads/book.m4b" },
                    Title = "Mixed.Context.Audiobook",
                    DownloadClientInfo = new DownloadClientItemClientInfo
                    {
                        Name = "qBittorrent",
                        Protocol = DownloadProtocol.Torrent
                    }
                },
                RemoteBook = new RemoteBook
                {
                    ParsedBookInfo = new ParsedBookInfo
                    {
                        Quality = new QualityModel(Quality.MP3)
                    },
                    Books = new List<Book>
                    {
                        new() { Id = 123, AuthorId = 7, Title = "Expected Audiobook", MediaType = BookMediaType.Audiobook },
                        new() { Id = 456, AuthorId = 7, Title = "Expected Ebook", MediaType = BookMediaType.Ebook }
                    }
                }
            };

            var historyItems = new List<EntityHistory>
            {
                new()
                {
                    EventType = EntityHistoryEventType.BookFileImported,
                    DownloadId = "download-3",
                    Data = new Dictionary<string, string> { ["DroppedPath"] = "/downloads/book.m4b" },
                    BookId = 123,
                    AuthorId = 7,
                    Date = DateTime.UtcNow
                }
            };

            Assert.That(subject.IsImported(trackedDownload, historyItems), Is.True);
        }
    }
}
