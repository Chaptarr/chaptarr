using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Chaptarr.Api.V1.History;
using Chaptarr.Core.Test.Common;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Messaging.Events;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.History
{
    public partial class AlternativeCopyReleaseSuppressionFixture
    {
        private static IEnumerable<TestCaseData> HistoryFailureCases()
        {
            foreach (var postgres in new[] { false, true })
            foreach (var media in new[] { BookMediaType.Audiobook, BookMediaType.Ebook })
            foreach (var cdh in new[] { false, true })
            foreach (var ageHours in new[] { 1, 13 })
                yield return new TestCaseData(postgres, media, cdh, ageHours);
        }

        [TestCaseSource(nameof(HistoryFailureCases))]
        public void history_mark_as_failed_releases_only_the_failed_downloads_rss_protection(bool postgres, BookMediaType media, bool cdh, int ageHours)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Library(database, media, 3);
                var history = new HistoryService(h.History, h.Logger);
                var downloadRows = new DownloadHistoryRepository(h.Database, h.Events);
                var downloads = new DownloadHistoryService(downloadRows, history);
                var tracked = new TrackedDownload
                {
                    RemoteBook = h.Subject(h.Books.Take(2).ToArray()),
                    DownloadItem = new DownloadClientItem { DownloadId = "FAILED-PACK", Title = "Captured release" },
                    Protocol = DownloadProtocol.Torrent,
                    DownloadClient = 7
                };
                RecordGrab(history, downloads, tracked);
                var grabs = h.History.FindByDownloadId("FAILED-PACK");
                foreach (var row in grabs) row.Date = DateTime.UtcNow.AddHours(-ageHours);
                h.History.UpdateMany(grabs);
                var other = h.Grabs(date: DateTime.UtcNow.AddHours(-1)).Single(row => row.BookId == h.Books[2].Id);
                other.DownloadId = "OTHER-DOWNLOAD";
                h.History.Insert(other);

                Assert.That(h.Specification(cdh).IsSatisfiedBy(h.Subject(h.Books[0]), null).Accepted,
                    Is.EqualTo(cdh && ageHours > 12));

                var failures = new List<DownloadFailedEvent>();
                var events = Proxy<IEventAggregator>((method, args) =>
                {
                    if (args.Single() is not DownloadFailedEvent failed) throw new InvalidOperationException(method.Name);
                    failures.Add(failed);
                    history.Handle(failed);
                    downloads.Handle(failed);
                    return null;
                });
                var service = new FailedDownloadService(history, null, events);
                new HistoryController(global::Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<global::NzbDrone.Core.Books.ISeriesBookLinkService>((_, _) => throw new global::System.InvalidOperationException("Series batch loading was not expected in this fixture")), history, null, null, service, null).MarkAsFailed(grabs[0].Id);

                Assert.That(failures, Has.Count.EqualTo(1));
                Assert.That(failures[0].TrackedDownload, Is.Null);
                Assert.That(h.History.FindByDownloadId("FAILED-PACK").Count(row => row.EventType == EntityHistoryEventType.DownloadFailed), Is.EqualTo(2));
                Assert.That(downloadRows.All().Select(row => row.EventType), Is.EqualTo(new[] { DownloadHistoryEventType.DownloadGrabbed }));
                var subjects = h.Books.Select(book => h.Subject(book)).ToArray();
                var spec = h.Specification(cdh);
                spec.PrepareAlternativeCopySuppression(subjects);
                Assert.That(subjects.Select(subject => spec.IsSatisfiedBy(subject, null).Accepted), Is.EqualTo(new[] { true, true, false }));
            });
        }

        private static IEnumerable<TestCaseData> FailureIdentityCases()
        {
            foreach (var postgres in new[] { false, true })
            foreach (var scenario in new[] { "same-download", "different-download", "earlier-failure", "other-book", "other-media",
                "equal-time-failure-first", "equal-time-grab-first", "equal-time-regrab", "newer-grab", "older-grab", "incompatible-copy" })
                yield return new TestCaseData(postgres, scenario);
        }

        [TestCaseSource(nameof(FailureIdentityCases))]
        public void history_failure_must_belong_to_the_current_book_and_grab_attempt(bool postgres, string scenario)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Library(database, BookMediaType.Audiobook, 1);
                var date = DateTime.UtcNow.AddHours(-1);
                var grab = h.Grabs(date: date).Single();
                var failure = new EntityHistory
                {
                    BookId = grab.BookId, AuthorId = grab.AuthorId, EditionId = grab.EditionId, DownloadId = grab.DownloadId,
                    Date = date.AddMinutes(1), EventType = EntityHistoryEventType.DownloadFailed, Quality = h.Quality, SourceTitle = grab.SourceTitle
                };
                switch (scenario)
                {
                    case "different-download": failure.DownloadId = "OTHER-DOWNLOAD"; break;
                    case "earlier-failure": failure.Date = date.AddMinutes(-1); break;
                    case "other-book": failure.BookId = h.AddBook(BookMediaType.Audiobook).Id; break;
                    case "other-media": failure.BookId = h.AddBook(BookMediaType.Ebook).Id; break;
                    case "equal-time-failure-first":
                    case "equal-time-grab-first":
                    case "equal-time-regrab": failure.Date = date; break;
                    case "incompatible-copy": grab.Data[EntityHistory.ALTERNATIVE_COPY_INCOMPATIBLE] = bool.TrueString; break;
                }

                if (scenario == "older-grab")
                {
                    var older = h.Grabs(date: date.AddMinutes(-2)).Single();
                    older.DownloadId = "OLDER-DOWNLOAD";
                    h.History.Insert(older);
                }

                if (scenario == "equal-time-failure-first") h.History.Insert(failure);
                h.History.Insert(grab);
                if (scenario != "equal-time-failure-first") h.History.Insert(failure);
                if (scenario is "newer-grab" or "equal-time-regrab")
                {
                    var regrab = h.Grabs(date: scenario == "equal-time-regrab" ? date : failure.Date.AddMinutes(1)).Single();
                    h.History.Insert(regrab);
                }

                var subject = h.Subject(h.Books[0]);
                var result = h.Specification(false).IsSatisfiedBy(subject, null);
                Assert.That(result.Accepted, Is.EqualTo(scenario is "same-download" or "equal-time-grab-first" or "older-grab"));
                if (scenario == "incompatible-copy")
                    Assert.That(subject.PositivelyIncompatibleBookIds, Does.Contain(grab.BookId));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void history_failure_resolution_remains_batched_without_returning_old_events(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                const int books = 20;
                const int attempts = 150;
                var h = new Library(database, BookMediaType.Audiobook, books);
                var date = DateTime.UtcNow.AddHours(-1);
                var rows = h.Books.SelectMany((book, bookIndex) => Enumerable.Range(0, attempts).SelectMany(attempt => new[]
                {
                    new EntityHistory { BookId = book.Id, AuthorId = book.AuthorId, DownloadId = "ATTEMPT-" + attempt,
                        Date = date.AddSeconds(attempt * 2), EventType = EntityHistoryEventType.Grabbed, Quality = h.Quality, SourceTitle = "Captured release" },
                    new EntityHistory { BookId = book.Id, AuthorId = book.AuthorId,
                        DownloadId = attempt == attempts - 1 && bookIndex % 2 == 0 ? "OLDER-ATTEMPT" : "ATTEMPT-" + attempt,
                        Date = date.AddSeconds(attempt * 2 + 1), EventType = EntityHistoryEventType.DownloadFailed, Quality = h.Quality, SourceTitle = "Captured release" }
                })).ToList();
                h.History.InsertMany(rows);
                var subjects = Enumerable.Range(0, 30).Select(_ => h.Subject(h.Books.ToArray())).ToArray();
                var spec = h.Specification(false);
                h.Commands.Reset();
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var clock = Stopwatch.StartNew();
                spec.PrepareAlternativeCopySuppression(subjects);
                clock.Stop();
                TestContext.WriteLine($"{database.DatabaseType}: books={books}, reports={subjects.Length}, historyRows={rows.Count}, readers={h.Commands.Readers}, returnedHistoryRows={h.Commands.HistoryRows}, elapsedMs={clock.Elapsed.TotalMilliseconds:F2}, allocatedBytes={GC.GetAllocatedBytesForCurrentThread() - allocated}");
                Assert.That(subjects[0].PreparedBookHistory.Keys, Is.EquivalentTo(h.Books.Where((_, index) => index % 2 == 0).Select(book => book.Id)));
                Assert.That(h.Commands.Readers, Is.EqualTo(2));
                Assert.That(h.Commands.HistoryRows, Is.EqualTo(books / 2));
                Assert.That(h.Commands.Nonqueries + h.Commands.Scalars, Is.Zero);
            });
        }
    }
}
