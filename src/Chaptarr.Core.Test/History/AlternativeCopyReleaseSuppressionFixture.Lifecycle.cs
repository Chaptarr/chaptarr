using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using Chaptarr.Core.Test.Common;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.History
{
    public partial class AlternativeCopyReleaseSuppressionFixture
    {
        private static IEnumerable<TestCaseData> LifecycleCases()
        {
            foreach (var postgres in new[] { false, true })
            foreach (var media in new[] { BookMediaType.Audiobook, BookMediaType.Ebook })
            foreach (var cdh in new[] { false, true })
            foreach (var outcome in new[] { "complete", "partial", "partial-upgrade", "failed", "ignored", "regrab" })
            {
                yield return new TestCaseData(postgres, media, cdh, outcome);
            }
        }

        [TestCaseSource(nameof(LifecycleCases))]
        public void rss_uses_the_recorded_download_lifecycle(bool postgres, BookMediaType media, bool cdh, string outcome)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Library(database, media, 2);
                var history = new HistoryService(h.History, h.Logger);
                var downloads = new DownloadHistoryService(new DownloadHistoryRepository(h.Database, h.Events), history);
                var tracked = new TrackedDownload
                {
                    RemoteBook = h.Subject(h.Books.ToArray()),
                    DownloadItem = new DownloadClientItem { DownloadId = "DOWNLOAD-ONE", Title = "Captured release", OutputPath = new NzbDrone.Common.Disk.OsPath("/fixture/download"),
                        DownloadClientInfo = new DownloadClientItemClientInfo { Id = 7, Name = "Fixture client", Type = "Fixture", Protocol = DownloadProtocol.Torrent } },
                    Protocol = DownloadProtocol.Torrent,
                    DownloadClient = 7
                };
                RecordGrab(history, downloads, tracked);

                var book = h.Books[0];
                var file = new BookFile { Id = 1, EditionId = book.Editions.Single().Id, Edition = book.Editions.Single(), Author = h.Author, Path = "/fixture/library/part1", Quality = h.Quality };
                var imported = new TrackImportedEvent(new LocalBook { Book = book, Edition = book.Editions.Single(), Author = h.Author, Path = "/fixture/download/part1", Quality = h.Quality },
                    file, new List<BookFile>(), true, tracked.DownloadItem);

                if (outcome == "partial-upgrade")
                {
                    history.Handle(new BookFileDeletedEvent(file, DeleteMediaFileReason.Upgrade));
                }

                history.Handle(imported);
                downloads.Handle(imported);

                switch (outcome)
                {
                    case "complete":
                    case "regrab":
                        var secondBook = h.Books[1];
                        var secondFile = new BookFile { Id = 2, EditionId = secondBook.Editions.Single().Id, Edition = secondBook.Editions.Single(), Author = h.Author, Path = "/fixture/library/second", Quality = h.Quality };
                        var secondImport = new TrackImportedEvent(new LocalBook { Book = secondBook, Edition = secondBook.Editions.Single(), Author = h.Author, Path = "/fixture/download/second", Quality = h.Quality },
                            secondFile, new List<BookFile>(), true, tracked.DownloadItem);
                        history.Handle(secondImport);
                        downloads.Handle(secondImport);
                        downloads.Handle(new DownloadCompletedEvent(tracked, h.Author.Id));
                        if (outcome == "regrab") RecordGrab(history, downloads, tracked);
                        break;
                    case "partial":
                    case "partial-upgrade":
                        tracked.Warn("A required source is missing");
                        var incomplete = new BookImportIncompleteEvent(tracked);
                        history.Handle(incomplete);
                        downloads.Handle(incomplete);
                        break;
                    case "failed":
                        var failed = new DownloadFailedEvent { TrackedDownload = tracked, AuthorId = h.Author.Id, BookIds = h.Books.Select(b => b.Id).ToList(), DownloadId = tracked.DownloadItem.DownloadId, SourceTitle = tracked.DownloadItem.Title, Quality = h.Quality };
                        history.Handle(failed);
                        downloads.Handle(failed);
                        break;
                    case "ignored":
                        var ignored = new DownloadIgnoredEvent { TrackedDownload = tracked, AuthorId = h.Author.Id, BookIds = h.Books.Select(b => b.Id).ToList(), DownloadId = tracked.DownloadItem.DownloadId, SourceTitle = tracked.DownloadItem.Title, Quality = h.Quality };
                        history.Handle(ignored);
                        downloads.Handle(ignored);
                        break;
                }

                var accepted = outcome is "complete" or "failed" or "ignored";
                var spec = h.Specification(cdh);
                foreach (var target in h.Books)
                {
                    var result = spec.IsSatisfiedBy(h.Subject(target), null);
                    Assert.That(result.Accepted, Is.EqualTo(accepted), $"book={target.Id}, outcome={outcome}");
                    if (!accepted) Assert.That(result.Reason, Does.Contain("Recent grab"));
                }

                Assert.That(downloads.GetLatestDownloadHistoryItem(tracked.DownloadItem.DownloadId).EventType,
                    Is.EqualTo(outcome switch
                    {
                        "complete" => DownloadHistoryEventType.DownloadImported,
                        "failed" => DownloadHistoryEventType.DownloadFailed,
                        "ignored" => DownloadHistoryEventType.DownloadIgnored,
                        "regrab" => DownloadHistoryEventType.DownloadGrabbed,
                        _ => DownloadHistoryEventType.DownloadImportIncomplete
                    }));
                Assert.That(h.History.All().Any(row => row.EventType == EntityHistoryEventType.DownloadImported), Is.False);
            });
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void completion_for_another_or_earlier_download_cannot_finish_the_current_grab(bool postgres, bool sameId)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Library(database, BookMediaType.Audiobook, 1);
                var grab = h.Grabs(date: DateTime.UtcNow.AddHours(-1)).Single();
                grab.Data.Clear();
                h.History.Insert(grab);
                var history = new HistoryService(h.History, h.Logger);
                var repository = new DownloadHistoryRepository(h.Database, h.Events);
                var downloads = new DownloadHistoryService(repository, history);
                downloads.Handle(new DownloadCompletedEvent(new TrackedDownload
                {
                    DownloadItem = new DownloadClientItem { DownloadId = sameId ? grab.DownloadId : "ANOTHER-DOWNLOAD", Title = "Earlier download" }
                }, h.Author.Id));
                if (sameId)
                {
                    var previous = repository.All().Single();
                    previous.Date = grab.Date.AddHours(-1);
                    repository.Update(previous);
                }

                Assert.That(h.Specification().IsSatisfiedBy(h.Subject(h.Books[0]), null).Accepted, Is.False);
            });
        }

        [TestCase(false, EntityHistoryEventType.DownloadFailed)]
        [TestCase(true, EntityHistoryEventType.DownloadFailed)]
        [TestCase(false, EntityHistoryEventType.DownloadIgnored)]
        [TestCase(true, EntityHistoryEventType.DownloadIgnored)]
        public void clients_without_download_ids_retain_book_level_outcomes(bool postgres, EntityHistoryEventType outcome)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Library(database, BookMediaType.Ebook, 1);
                var grab = h.Grabs(date: DateTime.UtcNow.AddHours(-1)).Single();
                grab.DownloadId = null;
                grab.Data.Clear();
                h.History.Insert(grab);
                var writer = new HistoryService(h.History, h.Logger);
                if (outcome == EntityHistoryEventType.DownloadFailed)
                    writer.Handle(new DownloadFailedEvent { AuthorId = h.Author.Id, BookIds = new List<int> { grab.BookId }, Quality = h.Quality, SourceTitle = grab.SourceTitle });
                else
                    writer.Handle(new DownloadIgnoredEvent { AuthorId = h.Author.Id, BookIds = new List<int> { grab.BookId }, Quality = h.Quality, SourceTitle = grab.SourceTitle });

                Assert.That(h.Specification(false).IsSatisfiedBy(h.Subject(h.Books[0]), null).Accepted, Is.True);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void batch_and_single_download_state_readers_share_ordering_and_ignore_file_events(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Library(database, BookMediaType.Audiobook, 1);
                var repository = new DownloadHistoryRepository(h.Database, h.Events);
                var service = new DownloadHistoryService(repository, new HistoryService(h.History, h.Logger));
                var states = new[] { DownloadHistoryEventType.DownloadGrabbed, DownloadHistoryEventType.DownloadImported,
                    DownloadHistoryEventType.DownloadImportIncomplete, DownloadHistoryEventType.DownloadFailed, DownloadHistoryEventType.DownloadIgnored };
                var date = DateTime.UtcNow.AddHours(-1);
                var rows = Enumerable.Range(0, 901).SelectMany(index => new[]
                {
                    new DownloadHistory { DownloadId = "BATCH-" + index, SourceTitle = "Previous state", Date = date, EventType = DownloadHistoryEventType.DownloadImported },
                    new DownloadHistory { DownloadId = "BATCH-" + index, SourceTitle = "Current state", Date = date, EventType = states[index % states.Length] },
                    new DownloadHistory { DownloadId = "BATCH-" + index, SourceTitle = "File event", Date = date.AddMinutes(1), EventType = DownloadHistoryEventType.FileImported }
                }).ToList();
                repository.InsertMany(rows);
                h.Commands.Reset();
                var clock = Stopwatch.StartNew();
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var result = service.GetLatestDownloadHistoryItems(Enumerable.Range(0, 901).Select(index => "batch-" + index)
                    .Concat(new[] { "BATCH-1", null, " ", "UNKNOWN" }));
                clock.Stop();
                TestContext.WriteLine($"{database.DatabaseType}: downloads=901, events={rows.Count}, readers={h.Commands.Readers}, rows={result.Count}, elapsedMs={clock.Elapsed.TotalMilliseconds:F2}, allocatedBytes={GC.GetAllocatedBytesForCurrentThread() - allocated}");
                Assert.That(result, Has.Count.EqualTo(901));
                Assert.That(h.Commands.Readers, Is.EqualTo(postgres ? 1 : 2));
                Assert.That(h.Commands.Nonqueries + h.Commands.Scalars, Is.Zero);
                Assert.That(result.Select(row => row.Id), Is.EquivalentTo(rows.Where(row => row.SourceTitle == "Current state").Select(row => row.Id)));
                foreach (var row in result.Take(states.Length))
                    Assert.That(service.GetLatestDownloadHistoryItem(row.DownloadId.ToLowerInvariant()).Id, Is.EqualTo(row.Id));

                var ignored = repository.CurrentlyIgnored(new NzbDrone.Core.Datastore.PagingSpec<DownloadHistory>
                    { Page = 1, PageSize = 1000, SortKey = "date", SortDirection = NzbDrone.Core.Datastore.SortDirection.Descending });
                Assert.That(ignored.Records.Select(row => row.Id), Is.EquivalentTo(result.Where(row => row.EventType == DownloadHistoryEventType.DownloadIgnored).Select(row => row.Id)));
                h.Commands.Reset();
                Assert.That(service.GetLatestDownloadHistoryItems(new[] { null, " " }), Is.Empty);
                Assert.That(h.Commands.Readers, Is.Zero);
            });
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void explicit_search_keeps_copy_suppression_without_reading_rss_download_state(bool postgres, bool incompatible)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Library(database, BookMediaType.Audiobook, 1);
                var grab = h.Grabs(date: DateTime.UtcNow.AddHours(-1)).Single();
                if (incompatible) grab.Data[EntityHistory.ALTERNATIVE_COPY_INCOMPATIBLE] = bool.TrueString;
                h.History.Insert(grab);
                h.Commands.Reset();
                var result = h.Specification().IsSatisfiedBy(h.Subject(h.Books[0]),
                    new NzbDrone.Core.IndexerSearch.Definitions.BookSearchCriteria { Author = h.Author, Books = h.Books });
                Assert.That(result.Accepted, Is.EqualTo(!incompatible));
                Assert.That(h.Commands.Readers, Is.EqualTo(1));
            });
        }

        private static void RecordGrab(HistoryService history, DownloadHistoryService downloads, TrackedDownload tracked)
        {
            var grabbed = new BookGrabbedEvent(tracked.RemoteBook)
            {
                DownloadId = tracked.DownloadItem.DownloadId,
                DownloadClientId = tracked.DownloadClient,
                DownloadClient = "Fixture client"
            };
            history.Handle(grabbed);
            downloads.Handle(grabbed);
        }
    }
}
