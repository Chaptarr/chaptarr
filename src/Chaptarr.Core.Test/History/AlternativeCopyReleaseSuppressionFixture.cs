using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Chaptarr.Api.V1.Indexers;
using Chaptarr.Core.Test.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.DecisionEngine.Specifications.RssSync;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Housekeeping.Housekeepers;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.History
{
    [TestFixture]
    [NonParallelizable]
    public partial class AlternativeCopyReleaseSuppressionFixture
    {
        private static IEnumerable<TestCaseData> ReadCases()
        {
            foreach (var postgres in new[] { false, true })
            foreach (var side in new[] { BookMediaType.Audiobook, BookMediaType.Ebook })
            foreach (var scenario in new[] { "exact", "different_guid", "different_indexer", "different_protocol", "missing_guid", "missing_indexer", "missing_protocol", "no_flag", "false_flag", "ordinary", "changed_edition", "changed_owner", "unpin", "winner", "opposite_media", "hash_conflict", "hash_same_case", "hash_absent" })
                yield return new TestCaseData(postgres, side, scenario);
        }

        [TestCaseSource(nameof(ReadCases))]
        public void persisted_positive_fact_is_only_for_the_same_release_and_current_wanted_copy(bool postgres, BookMediaType side, string scenario)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Library(database, side);
                var rows = h.Grabs();
                var loser = rows.Single(row => row.BookId == h.Books[1].Id);
                loser.Data[EntityHistory.ALTERNATIVE_COPY_INCOMPATIBLE] = bool.TrueString;
                var subject = h.Subject(h.Books[1]);
                switch (scenario)
                {
                    case "different_guid": subject.Release.Guid = "different-release"; break;
                    case "different_indexer": subject.Release.IndexerId++; break;
                    case "different_protocol": subject.Release.DownloadProtocol = DownloadProtocol.Usenet; break;
                    case "missing_guid": loser.Data.Remove("Guid"); break;
                    case "missing_indexer": loser.Data.Remove(EntityHistory.INDEXER_ID); break;
                    case "missing_protocol": loser.Data.Remove("Protocol"); break;
                    case "no_flag": loser.Data.Remove(EntityHistory.ALTERNATIVE_COPY_INCOMPATIBLE); break;
                    case "false_flag": loser.Data[EntityHistory.ALTERNATIVE_COPY_INCOMPATIBLE] = bool.FalseString; break;
                    case "ordinary": loser.Data[EntityHistory.ALTERNATIVE_COPIES] = bool.FalseString; break;
                    case "changed_edition": h.ChangePin(h.Books[1]); break;
                    case "changed_owner": h.ChangeOwner(h.Books[1]); break;
                    case "unpin": h.Books[1].AnyEditionOk = true; h.BookRows.Update(h.Books[1]); break;
                    case "winner": subject = h.Subject(h.Books[0]); break;
                    case "opposite_media": subject = h.Subject(h.AddBook(side == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook)); break;
                    case "hash_conflict": ((TorrentInfo)subject.Release).InfoHash = "DIFFERENT-HASH"; break;
                    case "hash_same_case": ((TorrentInfo)subject.Release).InfoHash = "captured-hash"; break;
                    case "hash_absent": loser.Data.Remove("TorrentInfoHash"); break;
                }
                h.History.InsertMany(rows);
                var stored = h.History.Get(loser.Id);
                Assert.That(stored.Data.ContainsKey("alternativeCopies"), Is.True);
                var decision = h.Specification().IsSatisfiedBy(subject, null);
                var rejected = scenario is "exact" or "hash_same_case" or "hash_absent";
                Assert.That(decision.Accepted, Is.EqualTo(!rejected));
                if (rejected) Assert.That(decision.CanBypass, Is.True);
            });
        }

        [TestCase(false, BookMediaType.Audiobook)]
        [TestCase(true, BookMediaType.Audiobook)]
        [TestCase(false, BookMediaType.Ebook)]
        [TestCase(true, BookMediaType.Ebook)]
        public void automatic_searches_reject_a_proved_wrong_copy_but_explicit_manual_post_can_grab_it(bool postgres, BookMediaType side)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Library(database, side);
                var rows = h.Grabs(); rows[1].Data[EntityHistory.ALTERNATIVE_COPY_INCOMPATIBLE] = bool.TrueString;
                h.History.InsertMany(rows);
                var spec = h.Specification();
                foreach (var mode in new[] { ReleaseSourceType.Search, ReleaseSourceType.UserInvokedSearch, ReleaseSourceType.InteractiveSearch })
                {
                    var subject = h.Subject(h.Books[1]); subject.ReleaseSource = mode;
                    var criteria = new BookSearchCriteria { Author = h.Author, Books = subject.Books,
                        UserInvokedSearch = mode == ReleaseSourceType.UserInvokedSearch, InteractiveSearch = mode == ReleaseSourceType.InteractiveSearch };
                    var result = spec.IsSatisfiedBy(subject, criteria);
                    Assert.That(result.Accepted, Is.False, mode.ToString());
                    Assert.That(result.CanBypass, Is.True);
                    subject.DownloadAllowed = false;
                    var decision = new DownloadDecision(subject, new Rejection(result.Reason, RejectionType.Permanent, result.CanBypass, result.Category, result.Severity));
                    var cache = new CacheManager();
                    cache.GetCache<DownloadDecision>(typeof(ReleaseController), "downloadDecisions").Set("7_release-one", decision);
                    var dispatched = new List<RemoteBook>();
                    var controller = new ReleaseController(null, null, null, null,
                        Proxy<IDownloadService>((method, args) => { dispatched.Add((RemoteBook)args[0]); return Task.CompletedTask; }),
                        null, null, null, null, cache, h.Logger, null)
                    { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
                    controller.HttpContext.Request.Method = HttpMethods.Post;
                    controller.DownloadRelease(new ReleaseResource { IndexerId = 7, Guid = "release-one", BookId = h.Books[1].Id }).GetAwaiter().GetResult();
                    Assert.That(dispatched.Single().Books.Single().Id, Is.EqualTo(h.Books[1].Id));
                    Assert.That(dispatched.Single().DownloadAllowed, Is.False);
                }
            });
        }

        private static IEnumerable<TestCaseData> WriterCases()
        {
            foreach (var postgres in new[] { false, true })
            foreach (var side in new[] { BookMediaType.Audiobook, BookMediaType.Ebook })
            foreach (var scenario in new[] { "proved", "no_fact", "missing_anchor", "wrong_anchor", "wrong_edition", "wrong_owner", "ordinary", "missing_winner", "reused_download", "converted", "replay" })
                yield return new TestCaseData(postgres, side, scenario);
        }

        private static IEnumerable<TestCaseData> RecentHistoryCases()
        {
            foreach (var postgres in new[] { false, true })
            foreach (var side in new[] { BookMediaType.Audiobook, BookMediaType.Ebook })
            foreach (var scenario in new[] { "proved", "no_proof", "earlier_ordinary", "newer_ordinary", "changed_edition", "changed_owner", "unpin" })
                yield return new TestCaseData(postgres, side, scenario);
        }

        [TestCaseSource(nameof(RecentHistoryCases))]
        public void recent_proved_loser_allows_a_different_rss_release_without_bypassing_ordinary_grabs(bool postgres, BookMediaType side, string scenario)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Library(database, side);
                var rows = h.Grabs(date: DateTime.UtcNow.AddMinutes(-5)); h.History.InsertMany(rows);
                var captured = h.History.FindByDownloadId("DOWNLOAD-ONE");
                var loser = rows.Single(row => row.BookId == h.Books[1].Id);
                if (scenario is "earlier_ordinary" or "newer_ordinary")
                {
                    var ordinary = h.Grabs("ordinary-release", captured.First().Date.AddMinutes(scenario == "earlier_ordinary" ? -1 : 1))
                        .Single(row => row.BookId == h.Books[1].Id);
                    ordinary.DownloadId = "OTHER-DOWNLOAD"; ordinary.Data[EntityHistory.ALTERNATIVE_COPIES] = bool.FalseString;
                    h.History.Insert(ordinary);
                }
                var winner = h.Books[0]; var edition = winner.Editions.Single();
                var local = new LocalBook { Book = winner, Author = h.Author, Edition = edition, Quality = h.Quality, Path = "/downloads/proved-unit",
                    AlternativeCopyGrabDate = captured.First().Date,
                    AlternativeCopyIncompatibleEditionIds = scenario == "no_proof" ? null : new Dictionary<int, int> { { h.Books[1].Id, h.Books[1].Editions.Single().Id } } };
                var file = h.Files.Insert(new BookFile { EditionId = edition.Id, Edition = edition, Author = h.Author, Path = "/library/proved-unit",
                    Quality = h.Quality, MediaInfo = new MediaInfoModel(), Size = 100, Modified = DateTime.UtcNow, DateAdded = DateTime.UtcNow });
                new HistoryService(h.History, h.Logger).Handle(new TrackImportedEvent(local, file, new List<BookFile>(), true, new DownloadClientItem { DownloadId = "DOWNLOAD-ONE" }));
                Assert.That(h.History.Get(loser.Id).GetDataValue(EntityHistory.ALTERNATIVE_COPY_INCOMPATIBLE) == bool.TrueString, Is.EqualTo(scenario != "no_proof"));

                var removals = 0;
                var client = Proxy<IDownloadClient>((method, _) =>
                {
                    if (method.Name == "get_Definition") return new DownloadClientDefinition { RemoveCompletedDownloads = true };
                    if (method.Name == nameof(IDownloadClient.RemoveItem)) removals++;
                    return null;
                });
                var tracked = new TrackedDownload { DownloadClient = 1, RemoteBook = h.Subject(h.Books.ToArray()),
                    DownloadItem = new DownloadClientItem { DownloadId = "DOWNLOAD-ONE", CanBeRemoved = true,
                        DownloadClientInfo = new DownloadClientItemClientInfo { Name = "Fixture client" } } };
                new DownloadEventHub(Proxy<ICompletedDownloadService>((_, _) => true), Proxy<IProvideDownloadClient>((_, _) => client),
                    Proxy<IDownloadImportModeResolver>((_, _) => false), h.Logger).Handle(new DownloadCanBeRemovedEvent(tracked));
                Assert.That(removals, Is.EqualTo(1)); Assert.That(tracked.DownloadItem.Removed, Is.True);
                Assert.That(h.History.Get(loser.Id).EventType, Is.EqualTo(EntityHistoryEventType.Grabbed));
                if (scenario == "changed_edition") h.ChangePin(h.Books[1]);
                if (scenario == "changed_owner") h.ChangeOwner(h.Books[1]);
                if (scenario == "unpin") { h.Books[1].AnyEditionOk = true; h.BookRows.Update(h.Books[1]); }

                var different = h.Subject(h.Books[1], "different-release"); var same = h.Subject(h.Books[1]);
                var spec = h.Specification(); h.Commands.Reset(); spec.PrepareAlternativeCopySuppression(new[] { different, same });
                var differentDecision = spec.IsSatisfiedBy(different, null); var sameDecision = spec.IsSatisfiedBy(same, null);
                var currentProof = scenario is "proved" or "earlier_ordinary" or "newer_ordinary";
                Assert.Multiple(() =>
                {
                    Assert.That(differentDecision.Accepted, Is.EqualTo(scenario is "proved" or "changed_edition" or "unpin"), differentDecision.Reason);
                    Assert.That(sameDecision.Accepted, Is.EqualTo(scenario is "changed_edition" or "unpin"), sameDecision.Reason);
                    Assert.That(sameDecision.CanBypass, Is.EqualTo(currentProof));
                    Assert.That(h.Commands.Readers, Is.EqualTo(scenario is "no_proof" or "earlier_ordinary" or "newer_ordinary" or "changed_owner" ? 2 : 1));
                    Assert.That(h.Commands.Nonqueries + h.Commands.Scalars, Is.Zero);
                });
            });
        }

        [TestCaseSource(nameof(WriterCases))]
        public void imported_fact_updates_only_the_original_captured_group_after_successful_import(bool postgres, BookMediaType side, string scenario)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Library(database, side);
                var rows = h.Grabs().OrderByDescending(row => row.BookId).ToList();
                var loser = rows.Single(row => row.BookId == h.Books[1].Id);
                if (scenario == "ordinary") foreach (var row in rows) row.Data[EntityHistory.ALTERNATIVE_COPIES] = bool.FalseString;
                if (scenario == "wrong_owner") loser.AuthorId++;
                if (scenario == "missing_winner") rows.RemoveAll(row => row.BookId == h.Books[0].Id);
                h.History.InsertMany(rows);
                var captured = h.History.FindByDownloadId("DOWNLOAD-ONE");
                var date = captured.First().Date;
                var legacyScore = captured.Where(row => row.EventType == EntityHistoryEventType.Grabbed &&
                    (row.BookId == h.Books[0].Id || row.AuthorId == h.Author.Id)).OrderByDescending(row => row.Date)
                    .Select(row => row.GetDataValue("CustomFormatScore")).FirstOrDefault(score => !string.IsNullOrWhiteSpace(score));
                EntityHistory newerLoser = null;
                if (scenario == "reused_download")
                {
                    var newer = h.Grabs("different-release", date.AddHours(1));
                    foreach (var row in newer) row.Data["CustomFormatScore"] = "999";
                    h.History.InsertMany(newer);
                    newerLoser = newer.Single(row => row.BookId == h.Books[1].Id);
                }
                var local = new LocalBook { Book = h.Books[0], Author = h.Author, Edition = h.Books[0].Editions.Single(),
                    Path = "/downloads/proved-unit", Quality = h.Quality,
                    AlternativeCopyGrabDate = scenario == "missing_anchor" ? null : scenario == "wrong_anchor" ? date.AddDays(1) : date,
                    AlternativeCopyIncompatibleEditionIds = scenario == "no_fact" ? null : new Dictionary<int, int>
                    { { h.Books[1].Id, h.Books[1].Editions.Single().Id + (scenario == "wrong_edition" ? 10000 : 0) } } };
                if (scenario == "converted") local.GeneratedConversionSourcePaths = new List<string> { "/downloads/unit-one.mp3", "/downloads/unit-two.mp3" };
                var file = h.Files.Insert(new BookFile { EditionId = local.Edition.Id, Edition = local.Edition, Author = h.Author,
                    Path = "/library/imported-unit", Size = 100, Modified = DateTime.UtcNow, DateAdded = DateTime.UtcNow,
                    Quality = h.Quality, MediaInfo = new MediaInfoModel(), MediaType = side == BookMediaType.Audiobook ? "audiobook" : "ebook" });
                var message = new TrackImportedEvent(local, file, new List<BookFile>(), true, new DownloadClientItem { DownloadId = "download-one" });
                var service = new HistoryService(h.History, h.Logger);
                h.Commands.Reset(); service.Handle(message);
                if (scenario == "replay")
                {
                    h.Commands.Reset(); service.Handle(message);
                    Assert.That(h.Commands.HistoryUpdates, Is.Zero);
                }
                var history = h.History.FindByDownloadId("DOWNLOAD-ONE");
                var imports = history.Where(row => row.EventType == EntityHistoryEventType.BookFileImported).ToList();
                Assert.That(imports, Has.Count.EqualTo(scenario == "replay" ? 2 : 1));
                var imported = imports.Last();
                Assert.That(imported.BookId, Is.EqualTo(h.Books[0].Id));
                Assert.That(imported.Data.GetValueOrDefault("customFormatScore"), Is.EqualTo(scenario == "missing_anchor" ? legacyScore : scenario is "wrong_anchor" or "missing_winner" ? null : "30"));
                var proved = scenario is "proved" or "reused_download" or "converted" or "replay";
                Assert.That(bool.TryParse(h.History.Get(loser.Id).Data.GetValueOrDefault("alternativeCopyIncompatible"), out var blocked) && blocked, Is.EqualTo(proved));
                Assert.That(history.Any(row => row.EventType == EntityHistoryEventType.BookFileImported && row.BookId == h.Books[1].Id), Is.False);
                if (newerLoser != null) Assert.That(h.History.Get(newerLoser.Id).Data.ContainsKey("alternativeCopyIncompatible"), Is.False);
                if (scenario == "converted") Assert.That(JsonSerializer.Deserialize<List<string>>(imported.Data.GetValueOrDefault("sourcePaths")), Is.EqualTo(local.GeneratedConversionSourcePaths));
                if (proved)
                {
                    Assert.That(h.Specification().IsSatisfiedBy(h.Subject(h.Books[1]), null).Accepted, Is.False);
                    Assert.That(h.Specification().IsSatisfiedBy(h.Subject(h.Books[1], "different-release"), new BookSearchCriteria()).Accepted, Is.True);
                }
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void existing_history_cleanup_removes_suppression_only_when_the_losing_row_is_gone(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Library(database, BookMediaType.Audiobook);
                var rows = h.Grabs(); rows[1].Data[EntityHistory.ALTERNATIVE_COPY_INCOMPATIBLE] = bool.TrueString;
                h.History.InsertMany(rows);
                Assert.That(h.Specification().IsSatisfiedBy(h.Subject(h.Books[1]), null).Accepted, Is.False);
                new CleanupOrphanedHistoryItems(h.Database).Clean();
                Assert.That(h.History.Get(rows[1].Id), Is.Not.Null);
                // BookService publishes synchronous edition teardown before removing the parent row.
                new EditionRepository(h.Database, h.Events).DeleteMany(h.Books[1].Editions.Select(edition => edition.Id));
                h.BookRows.Delete(h.Books[1].Id);
                new CleanupOrphanedHistoryItems(h.Database).Clean();
                Assert.That(h.History.FindByDownloadId("DOWNLOAD-ONE").Select(row => row.BookId), Is.EqualTo(new[] { h.Books[0].Id }));
                Assert.That(h.Specification().IsSatisfiedBy(h.Subject(h.Books[1]), null).Accepted, Is.True);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void prepared_latest_history_preserves_the_existing_maxby_date_result_including_equal_dates(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Library(database, BookMediaType.Audiobook);
                var rows = h.Grabs(date: DateTime.UtcNow);
                h.History.InsertMany(rows);
                var equal = h.Grabs(date: rows[1].Date).Single(row => row.BookId == h.Books[1].Id);
                equal.EventType = EntityHistoryEventType.BookFileImported; h.History.Insert(equal);
                var old = h.History.GetByBook(h.Books[1].Id, null).MaxBy(row => row.Date);
                var subject = h.Subject(h.Books[1]); var spec = h.Specification();
                spec.PrepareAlternativeCopySuppression(new[] { subject });
                if (subject.PreparedBookHistory != null) Assert.That(subject.PreparedBookHistory[h.Books[1].Id].Id, Is.EqualTo(old.Id));
                Assert.That(spec.IsSatisfiedBy(subject, null).Accepted, Is.EqualTo(old.EventType != EntityHistoryEventType.Grabbed));
            });
        }

        [TestCase(false, 1)]
        [TestCase(false, 20)]
        [TestCase(true, 1)]
        [TestCase(true, 20)]
        public void one_history_batch_replaces_repeated_report_reads_without_retaining_all_historical_rows(bool postgres, int copies)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                const int eventsPerCopy = 300; const int reports = 30;
                var h = new Library(database, BookMediaType.Audiobook, copies);
                var history = h.Books.SelectMany(book => Enumerable.Range(0, eventsPerCopy).Select(index => new EntityHistory
                { BookId = book.Id, AuthorId = h.Author.Id, EditionId = book.Editions.Single().Id, EventType = EntityHistoryEventType.Grabbed,
                    Date = DateTime.UtcNow.AddDays(-10).AddMinutes(index), SourceTitle = "Historical release " + index,
                    Quality = h.Quality, DownloadId = "HISTORY-" + index,
                    Data = new Dictionary<string, string> { { "CustomFormatScore", index.ToString() }, { "Guid", "historical-" + index } } })).ToList();
                h.History.InsertMany(history);
                var subjects = Enumerable.Range(0, reports).Select(index => h.Subject(h.Books.ToArray())).ToArray();
                var spec = h.Specification(); h.Commands.Reset();
                var before = GC.GetAllocatedBytesForCurrentThread(); var timer = Stopwatch.StartNew();
                spec.PrepareAlternativeCopySuppression(subjects);
                var decisions = subjects.Select(subject => spec.IsSatisfiedBy(subject, null)).ToList();
                timer.Stop(); var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                TestContext.WriteLine($"{database.DatabaseType}: copies={copies}, reports={reports}, eventsPerCopy={eventsPerCopy}, readers={h.Commands.Readers}, nonqueries={h.Commands.Nonqueries}, scalars={h.Commands.Scalars}, returnedHistoryRows={h.Commands.HistoryRows}, allocatedBytes={allocated}, elapsedMs={timer.ElapsedMilliseconds}, retainedLatestRows={subjects[0].PreparedBookHistory?.Count ?? 0}");
                Assert.Multiple(() =>
                {
                    Assert.That(decisions.All(decision => decision.Accepted), Is.True);
                    Assert.That(h.Commands.Readers, Is.EqualTo(2));
                    Assert.That(h.Commands.Nonqueries + h.Commands.Scalars, Is.Zero);
                    Assert.That(h.Commands.HistoryRows, Is.EqualTo(copies));
                    Assert.That(subjects[0].PreparedBookHistory?.Count, Is.EqualTo(copies));
                });
            });
        }

        [TestCase(false, false)] [TestCase(true, false)]
        [TestCase(false, true)] [TestCase(true, true)]
        public void acquisition_reader_should_keep_old_suppression_and_invalid_markers_without_loading_ordinary_history(bool postgres, bool legacyKeyCasing)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Library(database, BookMediaType.Audiobook);
                var rows = h.Grabs(date: DateTime.UtcNow.AddDays(-2));
                rows[1].Data[EntityHistory.ALTERNATIVE_COPY_INCOMPATIBLE] = bool.TrueString;
                if (legacyKeyCasing)
                {
                    rows[1].Data.Remove(EntityHistory.ALTERNATIVE_COPY_INCOMPATIBLE);
                    rows[1].Data.Remove(EntityHistory.ALTERNATIVE_COPIES);
                    rows[1].Data["ALTERNATIVECOPYINCOMPATIBLE"] = " True ";
                    rows[1].Data["ALTERNATIVECOPIES"] = "TRUE";
                }

                h.History.InsertMany(rows);
                var ordinary = h.Grabs("ordinary", DateTime.UtcNow.AddDays(-1)).Single(row => row.BookId == h.Books[1].Id);
                ordinary.Data[EntityHistory.ALTERNATIVE_COPIES] = bool.FalseString;
                h.History.Insert(ordinary);
                var malformed = h.Grabs("malformed", DateTime.UtcNow).Single(row => row.BookId == h.Books[1].Id);
                malformed.Data[EntityHistory.ALTERNATIVE_COPY_INCOMPATIBLE] = bool.TrueString;
                malformed.Data.Remove(EntityHistory.INDEXER_ID);
                h.History.Insert(malformed);
                h.Commands.Reset();
                var subject = h.Subject(h.Books[1]);
                h.Specification().PrepareAlternativeCopySuppression(new[] { subject });
                Assert.That(subject.PositivelyIncompatibleBookIds, Does.Contain(h.Books[1].Id));
                Assert.That(subject.PreparedBookHistory[h.Books[1].Id].Id, Is.EqualTo(malformed.Id));
                Assert.That(h.Commands.Readers, Is.EqualTo(2));
                Assert.That(h.Commands.HistoryRows, Is.EqualTo(3));
            });
        }

        [TestCase(false, BookMediaType.Audiobook)]
        [TestCase(true, BookMediaType.Audiobook)]
        [TestCase(false, BookMediaType.Ebook)]
        [TestCase(true, BookMediaType.Ebook)]
        public void persisted_capture_roundtrip_restores_the_date_editions_and_per_row_scores(bool postgres, BookMediaType side)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Library(database, side);
                var rows = h.Grabs(date: DateTime.UtcNow.AddTicks(1)); h.History.InsertMany(rows);
                var persisted = h.History.FindByDownloadId("DOWNLOAD-ONE");
                var restored = h.Subject(h.Books.ToArray()); restored.RestoreGrabbedTargets(persisted);
                Assert.Multiple(() =>
                {
                    Assert.That(restored.AlternativeCopies, Is.True);
                    Assert.That(restored.AlternativeCopyGrabDate, Is.EqualTo(persisted.First().Date));
                    Assert.That(restored.TargetEditionIds, Is.EquivalentTo(h.Books.ToDictionary(book => book.Id, book => book.Editions.Single().Id)));
                    Assert.That(restored.TargetCustomFormatScores[h.Books[0].Id], Is.EqualTo(30));
                    Assert.That(restored.TargetCustomFormatScores[h.Books[1].Id], Is.EqualTo(90));
                });
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void batch_reader_chunks_only_at_the_existing_sqlite_parameter_boundary(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Library(database, BookMediaType.Audiobook, 901);
                var rows = h.Grabs(); h.History.InsertMany(rows); h.Commands.Reset();
                var result = h.History.GetAcquisitionHistory(h.Books.Select(book => book.Id).Concat(new[] { 0, -1, h.Books[0].Id }));
                Assert.That(result, Has.Count.EqualTo(901));
                Assert.That(h.Commands.Readers, Is.EqualTo(postgres ? 1 : 2));
                Assert.That(result.Select(row => row.BookId), Is.EquivalentTo(h.Books.Select(book => book.Id)));
            });
        }

        [TestCase(false, 1)]
        [TestCase(false, 20)]
        [TestCase(true, 1)]
        [TestCase(true, 20)]
        public void positive_import_reuses_one_download_history_read_for_all_losing_copies(bool postgres, int copies)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Library(database, BookMediaType.Audiobook, copies);
                var rows = h.Grabs(); h.History.InsertMany(rows);
                var date = h.History.FindByDownloadId("DOWNLOAD-ONE").First().Date;
                var book = h.Books[0]; var edition = book.Editions.Single();
                var local = new LocalBook { Book = book, Author = h.Author, Edition = edition, Quality = h.Quality, Path = "/downloads/proved-unit",
                    AlternativeCopyGrabDate = date, AlternativeCopyIncompatibleEditionIds = h.Books.Skip(1).ToDictionary(target => target.Id, target => target.Editions.Single().Id) };
                var file = h.Files.Insert(new BookFile { EditionId = edition.Id, Edition = edition, Author = h.Author, Path = "/library/proved-unit",
                    Quality = h.Quality, MediaInfo = new MediaInfoModel(), Size = 100, Modified = DateTime.UtcNow, DateAdded = DateTime.UtcNow });
                h.Commands.Reset(); var timer = Stopwatch.StartNew();
                new HistoryService(h.History, h.Logger).Handle(new TrackImportedEvent(local, file, new List<BookFile>(), true, new DownloadClientItem { DownloadId = "DOWNLOAD-ONE" }));
                timer.Stop();
                TestContext.WriteLine($"{database.DatabaseType}: importedCopies={copies}, readers={h.Commands.Readers}, nonqueries={h.Commands.Nonqueries}, scalars={h.Commands.Scalars}, returnedHistoryRows={h.Commands.HistoryRows}, historyUpdates={h.Commands.HistoryUpdates}, elapsedMs={timer.ElapsedMilliseconds}");
                Assert.That(h.Commands.HistoryRows, Is.EqualTo(copies));
                Assert.That(h.Commands.Readers, Is.EqualTo(2));
                Assert.That(h.History.FindByDownloadId("DOWNLOAD-ONE").Count(row => row.GetDataValue(EntityHistory.ALTERNATIVE_COPY_INCOMPATIBLE) == bool.TrueString), Is.EqualTo(copies - 1));
            });
        }

        [TestCase(false, EntityHistoryEventType.BookImportIncomplete, false)]
        [TestCase(true, EntityHistoryEventType.BookImportIncomplete, false)]
        [TestCase(false, EntityHistoryEventType.BookFileImported, false)]
        [TestCase(true, EntityHistoryEventType.BookFileImported, false)]
        public void rss_grab_protection_survives_file_and_incomplete_events(bool postgres, EntityHistoryEventType laterEvent, bool accepted)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Library(database, BookMediaType.Audiobook, 1);
                var grab = h.Grabs(date: DateTime.UtcNow.AddHours(-1)).Single();
                grab.Data.Clear();
                h.History.Insert(grab);
                h.History.Insert(new EntityHistory { BookId = grab.BookId, AuthorId = grab.AuthorId,
                    EditionId = grab.EditionId, DownloadId = grab.DownloadId, Date = DateTime.UtcNow,
                    SourceTitle = grab.SourceTitle, Quality = grab.Quality, EventType = laterEvent });
                var decision = h.Specification().IsSatisfiedBy(h.Subject(h.Books[0]), null);
                Assert.That(decision.Accepted, Is.EqualTo(accepted));
                if (!accepted) Assert.That(decision.Reason, Does.Contain("Recent grab"));
            });
        }

        [TestCase(false, true)]
        [TestCase(true, true)]
        [TestCase(false, false)]
        [TestCase(true, false)]
        public void incomplete_event_does_not_restart_the_twelve_hour_grab_window(bool postgres, bool completedDownloadHandling)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Library(database, BookMediaType.Ebook, 1);
                var grab = h.Grabs(date: DateTime.UtcNow.AddHours(-13)).Single();
                grab.Data.Clear();
                h.History.Insert(grab);
                h.History.Insert(new EntityHistory { BookId = grab.BookId, AuthorId = grab.AuthorId,
                    EditionId = grab.EditionId, DownloadId = grab.DownloadId, Date = DateTime.UtcNow,
                    SourceTitle = grab.SourceTitle, Quality = grab.Quality, EventType = EntityHistoryEventType.BookImportIncomplete });
                var decision = h.Specification(completedDownloadHandling).IsSatisfiedBy(h.Subject(h.Books[0]), null);
                Assert.That(decision.Accepted, Is.EqualTo(completedDownloadHandling));
            });
        }

        private sealed class CommandCounts
        {
            public int Readers, Nonqueries, Scalars, HistoryRows, HistoryUpdates;
            public void Reset() { Readers = 0; Nonqueries = 0; Scalars = 0; HistoryRows = 0; HistoryUpdates = 0; }
            public IDbConnection Wrap(IDbConnection connection) => Proxy<IDbConnection>((method, args) =>
            {
                var result = method.Invoke(connection, args);
                return method.Name == nameof(IDbConnection.CreateCommand) ? Wrap((IDbCommand)result) : result;
            });
            private IDbCommand Wrap(IDbCommand command) => Proxy<IDbCommand>((method, args) =>
            {
                if (method.Name == nameof(IDbCommand.ExecuteReader)) Readers++;
                if (method.Name == nameof(IDbCommand.ExecuteNonQuery)) { Nonqueries++; if (command.CommandText.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) && command.CommandText.Contains("\"History\"")) HistoryUpdates++; }
                if (method.Name == nameof(IDbCommand.ExecuteScalar)) Scalars++;
                var result = method.Invoke(command, args);
                if (method.Name != nameof(IDbCommand.ExecuteReader)) return result;
                var reader = (IDataReader)result;
                var history = command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) && command.CommandText.Contains("\"History\"");
                return Proxy<IDataReader>((readMethod, readArgs) =>
                {
                    var value = readMethod.Invoke(reader, readArgs);
                    if (history && readMethod.Name == nameof(IDataReader.Read) && value is true) HistoryRows++;
                    return value;
                });
            });
        }

        private sealed class Library
        {
            public readonly IMainDatabase Database;
            public readonly Logger Logger = LogManager.GetCurrentClassLogger();
            public readonly CommandCounts Commands = new();
            public readonly IEventAggregator Events = Proxy<IEventAggregator>((_, _) => null);
            public readonly HistoryRepository History;
            public readonly BookRepository BookRows;
            public readonly MediaFileRepository Files;
            public readonly Author Author;
            public readonly List<Book> Books = new();
            public readonly QualityModel Quality;
            private readonly EditionRepository _editions;
            public Library(IMainDatabase database, BookMediaType side, int copies = 2)
            {
                Database = Proxy<IMainDatabase>((method, args) => { var result = method.Invoke(database, args); return method.Name == "OpenConnection" ? Commands.Wrap((IDbConnection)result) : result; });
                Quality = new QualityModel(side == BookMediaType.Audiobook ? NzbDrone.Core.Qualities.Quality.M4B : NzbDrone.Core.Qualities.Quality.EPUB);
                Author = new Author { Name = "Suppression Fixture Author", CleanName = "suppressionfixtureauthor", SortName = "Suppression Fixture Author", SortNameLastFirst = "Author, Suppression Fixture", TitleSlug = "suppression-fixture-author", Path = "/fixture/author", Added = DateTime.UtcNow,
                    AudiobookQualityProfile = Profile(BookMediaType.Audiobook), EbookQualityProfile = Profile(BookMediaType.Ebook), AudiobookQualityProfileId = 1, EbookQualityProfileId = 2 };
                new AuthorRepository(Database, Events, Logger).Insert(Author);
                BookRows = new BookRepository(Database, Events); _editions = new EditionRepository(Database, Events);
                History = new HistoryRepository(Database, Events); Files = new MediaFileRepository(Database, Events);
                var books = Enumerable.Range(0, copies).Select(index => NewBook(side, index)).ToList();
                BookRows.InsertMany(books);
                var editions = books.Select(NewEdition).ToList();
                _editions.InsertMany(editions);
                for (var index = 0; index < books.Count; index++) books[index].Editions = new List<Edition> { editions[index] };
                Books.AddRange(books);
            }
            public Book AddBook(BookMediaType side)
            {
                var book = BookRows.Insert(NewBook(side, Books.Count));
                var edition = _editions.Insert(NewEdition(book));
                book.Editions = new List<Edition> { edition }; Books.Add(book); return book;
            }
            private Book NewBook(BookMediaType side, int index) => new Book { AuthorId = Author.Id, Author = Author, Title = "Captured Copy Work", CleanTitle = "capturedcopywork", TitleSlug = "captured-copy-work-" + index, HardcoverBookId = "hc:7000", MediaType = side, AnyEditionOk = false, Added = DateTime.UtcNow };
            private static Edition NewEdition(Book book) => new Edition { BookId = book.Id, Book = book, Title = book.Title, Monitored = true, ForeignEditionId = "hc:edition:" + (10000 + book.Id), ReadingFormatId = book.MediaType == BookMediaType.Audiobook ? 2 : 3, Narrator = book.MediaType == BookMediaType.Audiobook ? "Captured Cast " + book.Id : null, Language = "eng" };
            public RemoteBook Subject(params Book[] books) => Subject(books, "release-one");
            public RemoteBook Subject(Book book, string guid) => Subject(new[] { book }, guid);
            private RemoteBook Subject(Book[] books, string guid)
            {
                var quality = new QualityModel(books[0].MediaType == BookMediaType.Audiobook ? NzbDrone.Core.Qualities.Quality.M4B : NzbDrone.Core.Qualities.Quality.EPUB);
                return new RemoteBook { Author = books[0].Author, Books = books.ToList(), ParsedBookInfo = new ParsedBookInfo { Quality = quality },
                    Release = new TorrentInfo { Guid = guid, IndexerId = 7, DownloadProtocol = DownloadProtocol.Torrent, InfoHash = "CAPTURED-HASH", Title = "Captured copy release" } };
            }
            public List<EntityHistory> Grabs(string guid = "release-one", DateTime? date = null)
            {
                var groupDate = date ?? DateTime.UtcNow.AddDays(-2);
                return Books.Select(book => new EntityHistory
                { BookId = book.Id, AuthorId = Author.Id, EditionId = book.Editions.Single().Id, DownloadId = "DOWNLOAD-ONE", Date = groupDate, EventType = EntityHistoryEventType.Grabbed,
                    SourceTitle = "Captured copy release", Quality = Quality,
                    Data = new Dictionary<string, string> { { EntityHistory.ALTERNATIVE_COPIES, bool.TrueString }, { "Guid", guid }, { EntityHistory.INDEXER_ID, "7" }, { "Protocol", ((int)DownloadProtocol.Torrent).ToString() }, { "TorrentInfoHash", "CAPTURED-HASH" }, { "CustomFormatScore", book.Id == Books[0].Id ? "30" : "90" } } }).ToList();
            }
            public void ChangePin(Book book)
            {
                var previous = book.Editions.Single(); previous.Monitored = false; _editions.Update(previous);
                var selected = _editions.Insert(new Edition { BookId = book.Id, Book = book, Title = book.Title, Monitored = true,
                    ForeignEditionId = "hc:edition:" + (20000 + book.Id), ReadingFormatId = previous.ReadingFormatId, Language = "eng" });
                book.Editions = new List<Edition> { previous, selected };
            }
            public void ChangeOwner(Book book)
            {
                var owner = new Author { Name = "Other Fixture Author", CleanName = "otherfixtureauthor", SortName = "Other Fixture Author", SortNameLastFirst = "Author, Other Fixture", TitleSlug = "other-fixture-author", Path = "/fixture/other-author", Added = DateTime.UtcNow,
                    AudiobookQualityProfile = Profile(BookMediaType.Audiobook), EbookQualityProfile = Profile(BookMediaType.Ebook), AudiobookQualityProfileId = 1, EbookQualityProfileId = 2 };
                new AuthorRepository(Database, Events, Logger).Insert(owner);
                book.AuthorId = owner.Id; book.Author = owner; BookRows.Update(book);
            }
            public HistorySpecification Specification(bool completedDownloadHandling = true)
            {
                var config = Proxy<IConfigService>((method, _) => method.Name == "get_EnableCompletedDownloadHandling" ? completedDownloadHandling : method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null);
                var history = new HistoryService(History, Logger);
                return new HistorySpecification(history, new DownloadHistoryService(new DownloadHistoryRepository(Database, Events), history), new UpgradableSpecification(config, Logger),
                    Proxy<ICustomFormatCalculationService>((_, _) => new List<CustomFormat>()), config, Logger);
            }
            private static QualityProfile Profile(BookMediaType side)
            {
                var quality = side == BookMediaType.Audiobook ? NzbDrone.Core.Qualities.Quality.M4B : NzbDrone.Core.Qualities.Quality.EPUB;
                return new QualityProfile { Id = side == BookMediaType.Audiobook ? 1 : 2, Name = "Captured profile", ProfileType = side == BookMediaType.Audiobook ? ProfileType.Audiobook : ProfileType.Ebook,
                    Cutoff = quality.Id, UpgradeAllowed = true, Items = new List<QualityProfileQualityItem> { new() { Quality = quality, Allowed = true } } };
            }
        }
    }
}
