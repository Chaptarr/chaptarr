using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.DecisionEngine.Specifications.RssSync;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Queue;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.History
{
    public partial class AlternativeCopyReleaseSuppressionFixture
    {
        private static IEnumerable<TestCaseData> FileDeletionCases()
        {
            foreach (var postgres in new[] { false, true })
            foreach (var media in new[] { BookMediaType.Audiobook, BookMediaType.Ebook })
            foreach (var cdh in new[] { false, true })
            foreach (var hasDownloadId in new[] { false, true })
            foreach (var reason in new[] { DeleteMediaFileReason.Manual, DeleteMediaFileReason.MissingFromDisk })
                yield return new TestCaseData(postgres, media, cdh, hasDownloadId, reason);
        }

        [TestCaseSource(nameof(FileDeletionCases))]
        public void confirmed_last_file_deletion_releases_old_grab_without_a_client_completion(bool postgres, BookMediaType media, bool cdh, bool hasDownloadId, DeleteMediaFileReason reason)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Library(database, media, 2);
                var book = h.Books[0];
                h.Author.AudiobookMonitored = true;
                h.Author.EbookMonitored = true;
                new AuthorRepository(h.Database, h.Events, h.Logger).SetFields(h.Author, a => a.AudiobookMonitored, a => a.EbookMonitored);
                book.SetMonitored(true);
                h.BookRows.Update(book);
                var grabs = h.Grabs(date: DateTime.UtcNow.AddHours(cdh ? -1 : -13));
                foreach (var grab in grabs)
                {
                    grab.Data.Clear();
                    if (!hasDownloadId) grab.DownloadId = null;
                }
                h.History.InsertMany(grabs);
                var otherSide = h.AddBook(media == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook);
                var otherFile = AddDeletionFile(h, otherSide, "other-format");
                var history = new HistoryService(h.History, h.Logger);
                var files = Enumerable.Range(1, 2).Select(part => AddDeletionFile(h, book, "part-" + part)).ToList();
                foreach (var file in files)
                    history.Handle(new TrackImportedEvent(new LocalBook { Book = book, Edition = file.Edition, Author = h.Author,
                        Quality = h.Quality, Path = "/downloads/" + file.Id }, file, new List<BookFile>(), true, null));
                var events = Proxy<IEventAggregator>((_, args) =>
                {
                    if (args.Single() is BookFileDeletedEvent deleted) history.Handle(deleted);
                    return null;
                });
                var service = new MediaFileService(h.Files, events, null, null, h.Logger);

                Assert.That(h.Specification(cdh).IsSatisfiedBy(h.Subject(book), null).Accepted, Is.False, "An imported part is not completion");
                service.Delete(files[0], reason);
                Assert.That(h.Specification(cdh).IsSatisfiedBy(h.Subject(book), null).Accepted, Is.False, "Another part still exists");
                service.Delete(files[1], reason);
                Assert.That(h.Specification(cdh).IsSatisfiedBy(h.Subject(book), null).Accepted, Is.True);
                Assert.That(h.Specification(cdh).IsSatisfiedBy(h.Subject(h.Books[1]), null).Accepted, Is.False, "Another book's grab remains protected");
                Assert.That(h.Files.Get(otherFile.Id), Is.Not.Null, "The other media side is independent");
                Assert.That(h.History.All().Count(row => row.EventType == EntityHistoryEventType.BookFileDeleted), Is.EqualTo(2));
                Assert.That(h.BookRows.Get(book.Id).IsMonitored(), Is.True);
                var monitored = new MonitoredBookSpecification(h.Logger);
                var monitoringDecision = monitored.IsSatisfiedBy(h.Subject(book), null);
                Assert.That(monitoringDecision.Accepted, Is.True, monitoringDecision.Reason);
                book.SetMonitored(false);
                Assert.That(monitored.IsSatisfiedBy(h.Subject(book), null).Accepted, Is.False, "An unmonitored book remains ineligible");
                book.SetMonitored(true);

                var config = Proxy<IConfigService>((method, _) => method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null);
                var queue = new QueueSpecification(Proxy<IQueueService>((_, _) => new List<NzbDrone.Core.Queue.Queue>
                {
                    new() { Book = book, RemoteBook = h.Subject(book), Quality = h.Quality,
                        Title = "Dune - download still awaiting import", TrackedDownloadState = TrackedDownloadState.ImportBlocked }
                }), new UpgradableSpecification(config, h.Logger),
                    Proxy<ICustomFormatCalculationService>((_, _) => new List<CustomFormat>()), config, h.Logger);
                Assert.That(queue.IsSatisfiedBy(h.Subject(book), null).Accepted, Is.False, "Deleting library files does not bypass a queued download");
            });
        }

        [TestCase(false, "absent")]
        [TestCase(true, "absent")]
        [TestCase(false, "present")]
        [TestCase(true, "present")]
        [TestCase(false, "refused")]
        [TestCase(true, "refused")]
        public void only_confirmed_canonical_absence_emits_missing_history_and_releases_a_grab(bool postgres, string state)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Library(database, BookMediaType.Audiobook, 1);
                var book = h.Books.Single();
                var grab = h.Grabs(date: DateTime.UtcNow.AddHours(-1)).Single();
                grab.Data.Clear();
                h.History.Insert(grab);
                var file = AddDeletionFile(h, book, "missing-check");
                var history = new HistoryService(h.History, h.Logger);
                var events = Proxy<IEventAggregator>((_, args) =>
                {
                    if (args.Single() is BookFileDeletedEvent deleted) history.Handle(deleted);
                    return null;
                });
                var service = new MediaFileService(h.Files, events, null, null, h.Logger);
                var media = Proxy<IMediaFileService>((method, args) => method.Invoke(service, args));
                var disk = Proxy<IDiskProvider>((method, _) =>
                {
                    if (method.Name != nameof(IDiskProvider.FileExistsCanonical)) throw new InvalidOperationException(method.Name);
                    if (state == "refused") throw new UnauthorizedAccessException("Fixture canonical check refused");
                    return state == "present";
                });
                var cleanup = new MediaFileTableCleanupService(media, disk, h.Logger);
                void Scan() => cleanup.Clean("/library", new List<string> { "/library/neighbour.m4b" }, "audiobook");
                if (state == "refused") Assert.Throws<UnauthorizedAccessException>(Scan);
                else Scan();
                Assert.That(h.History.All().Count(row => row.EventType == EntityHistoryEventType.BookFileDeleted), Is.EqualTo(state == "absent" ? 1 : 0));
                Assert.That(h.Files.All().Any(row => row.Id == file.Id), Is.EqualTo(state != "absent"));
                Assert.That(h.Specification().IsSatisfiedBy(h.Subject(book), null).Accepted, Is.EqualTo(state == "absent"));
            });
        }

        private static IEnumerable<TestCaseData> DeletionBoundaryCases()
        {
            foreach (var postgres in new[] { false, true })
            foreach (var scenario in new[] { "manual", "upgrade", "missing", "unknown-reason", "legacy-reason-key", "remaining-unmonitored-edition",
                "other-book", "other-media", "earlier-deletion", "newer-grab", "same-time-grab-first", "same-time-deletion-first",
                "manual-then-upgrade", "upgrade-then-manual", "incompatible-release" })
            foreach (var reason in new[] { DeleteMediaFileReason.Manual, DeleteMediaFileReason.MissingFromDisk })
                yield return new TestCaseData(postgres, scenario, reason);
        }

        [TestCaseSource(nameof(DeletionBoundaryCases))]
        public void file_deletion_only_supersedes_an_older_grab_for_the_same_empty_book(bool postgres, string scenario, DeleteMediaFileReason reason)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Library(database, BookMediaType.Audiobook, 1);
                var book = h.Books[0];
                var grab = h.Grabs(date: DateTime.UtcNow.AddHours(-13)).Single();
                if (scenario == "incompatible-release") grab.Data[EntityHistory.ALTERNATIVE_COPY_INCOMPATIBLE] = bool.TrueString;
                var deletion = new EntityHistory { BookId = book.Id, AuthorId = h.Author.Id, EditionId = book.Editions.Single().Id,
                    Date = grab.Date.AddMinutes(1), EventType = EntityHistoryEventType.BookFileDeleted, Quality = h.Quality, SourceTitle = "/library/Dune.m4b",
                    Data = new Dictionary<string, string> { { scenario == "legacy-reason-key" ? "REASON" : "Reason", reason.ToString() } } };
                switch (scenario)
                {
                    case "upgrade": deletion.Data["Reason"] = DeleteMediaFileReason.Upgrade.ToString(); break;
                    case "missing": deletion.Data["Reason"] = DeleteMediaFileReason.MissingFromDisk.ToString(); break;
                    case "unknown-reason": deletion.Data.Clear(); break;
                    case "other-book": deletion.BookId = h.AddBook(BookMediaType.Audiobook).Id; break;
                    case "other-media": deletion.BookId = h.AddBook(BookMediaType.Ebook).Id; break;
                    case "earlier-deletion": deletion.Date = grab.Date.AddMinutes(-1); break;
                    case "same-time-grab-first":
                    case "same-time-deletion-first": deletion.Date = grab.Date; break;
                    case "remaining-unmonitored-edition":
                        var previous = book.Editions.Single();
                        h.ChangePin(book);
                        AddDeletionFile(h, book, "older-recording", previous);
                        break;
                }
                if (scenario == "same-time-deletion-first") h.History.Insert(deletion);
                h.History.Insert(grab);
                if (scenario != "same-time-deletion-first") h.History.Insert(deletion);
                if (scenario == "newer-grab")
                    h.History.Insert(h.Grabs(date: deletion.Date.AddMinutes(1)).First());
                if (scenario is "manual-then-upgrade" or "upgrade-then-manual")
                {
                    deletion.Data["Reason"] = scenario == "manual-then-upgrade" ? reason.ToString() : "Upgrade";
                    h.History.Update(deletion);
                    var later = new EntityHistory { BookId = book.Id, AuthorId = h.Author.Id, Date = deletion.Date.AddMinutes(1),
                        EventType = EntityHistoryEventType.BookFileDeleted, Quality = h.Quality, SourceTitle = "/library/Dune-part2.m4b",
                        Data = new Dictionary<string, string> { { "Reason", scenario == "manual-then-upgrade" ? "Upgrade" : reason.ToString() } } };
                    h.History.Insert(later);
                }
                var expected = scenario is "manual" or "missing" or "legacy-reason-key" or "same-time-grab-first" or "upgrade-then-manual";
                Assert.That(h.Specification(false).IsSatisfiedBy(h.Subject(book), null).Accepted, Is.EqualTo(expected), scenario);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void last_file_deletion_stays_in_the_existing_batched_history_read(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                const int books = 20;
                var h = new Library(database, BookMediaType.Audiobook, books);
                var rows = h.Books.SelectMany(book => Enumerable.Range(0, 150).Select(attempt => new EntityHistory
                {
                    BookId = book.Id, AuthorId = book.AuthorId, DownloadId = "OLD-" + attempt, Date = DateTime.UtcNow.AddDays(-2).AddMinutes(attempt),
                    EventType = EntityHistoryEventType.Grabbed, Quality = h.Quality, SourceTitle = "Dune - fixture release"
                })).ToList();
                h.History.InsertMany(rows);
                var history = new HistoryService(h.History, h.Logger);
                foreach (var book in h.Books.Where((_, i) => i % 2 == 0))
                    history.Handle(new BookFileDeletedEvent(new BookFile { Edition = book.Editions.Single(), EditionId = book.Editions.Single().Id,
                        Author = h.Author, Quality = h.Quality, Path = "/library/removed.m4b" }, DeleteMediaFileReason.Manual));
                var subjects = Enumerable.Range(0, 30).Select(_ => h.Subject(h.Books.ToArray())).ToArray();
                var spec = h.Specification(false);
                h.Commands.Reset();
                var allocation = GC.GetAllocatedBytesForCurrentThread();
                var clock = Stopwatch.StartNew();
                spec.PrepareAlternativeCopySuppression(subjects);
                clock.Stop();
                TestContext.WriteLine($"{database.DatabaseType}: books={books}, releases={subjects.Length}, events={rows.Count + books / 2}, readers={h.Commands.Readers}, returned={h.Commands.HistoryRows}, elapsedMs={clock.Elapsed.TotalMilliseconds:F2}, allocatedBytes={GC.GetAllocatedBytesForCurrentThread() - allocation}");
                Assert.That(subjects[0].PreparedBookHistory.Keys, Is.EquivalentTo(h.Books.Where((_, i) => i % 2 != 0).Select(book => book.Id)));
                Assert.That(h.Commands.Readers, Is.EqualTo(2));
                Assert.That(h.Commands.HistoryRows, Is.EqualTo(books / 2));
                Assert.That(h.Commands.Nonqueries + h.Commands.Scalars, Is.Zero);
            });
        }

        private static BookFile AddDeletionFile(Library library, Book book, string name, Edition edition = null)
        {
            edition ??= book.Editions.Single();
            return library.Files.Insert(new BookFile { EditionId = edition.Id, Edition = edition, Author = library.Author,
                Path = "/library/" + name + (book.MediaType == BookMediaType.Audiobook ? ".m4b" : ".epub"), Quality = library.Quality,
                MediaInfo = new MediaInfoModel(), Size = 100, Modified = DateTime.UtcNow, DateAdded = DateTime.UtcNow });
        }
    }
}
