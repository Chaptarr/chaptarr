using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Common;
using NLog;
using NzbDrone.Common.Cache;
using Newtonsoft.Json;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Messaging.Commands;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Books
{
    public partial class PersistedAliasPendingLifecycleFixture
    {
        private static void SetCorrectionSide(PendingAuthorImport item, BookMediaType media, IEnumerable<string> monitor, IEnumerable<string> search)
        {
            var monitoring = monitor == null ? null : JsonConvert.SerializeObject(monitor);
            var searching = search == null ? null : JsonConvert.SerializeObject(search);
            if (media == BookMediaType.Audiobook)
            {
                item.AudiobookBooksToMonitor = monitoring;
                item.AudiobookBooksToSearch = searching;
            }
            else
            {
                item.EbookBooksToMonitor = monitoring;
                item.EbookBooksToSearch = searching;
            }
        }

        private static PendingAuthorImport CorrectionRequest(Harness h, BookMediaType media, List<string> ids, List<RequestedNarration> narrations = null)
        {
            var item = h.Request(media == BookMediaType.Audiobook ? ids : new(), narrations, media == BookMediaType.Ebook);
            item.AudiobookStatus = media == BookMediaType.Audiobook ? PendingImportStatus.Pending : PendingImportStatus.NotRequested;
            item.EbookStatus = media == BookMediaType.Ebook ? PendingImportStatus.Pending : PendingImportStatus.NotRequested;
            item.RequestedBy = "UserInterface";
            item.AudiobookMonitorExistingMode = item.EbookMonitorExistingMode = null;
            item.AudiobookMonitorNewItems = item.EbookMonitorNewItems = null;
            SetCorrectionSide(item, media, ids, ids);
            Harness.MarkExplicitSearches(item);
            h.Requests.Update(item);
            return item;
        }

        private static void RunLifecycleCorrection(Harness h, IPendingAuthorImportService pending = null, IManageCommandQueue queue = null,
            Author source = null, Func<string, BookMediaType, Tuple<string, Book, List<Author>>> lookup = null, Exception originalFailure = null, IBookService books = null)
        {
            var library = Proxy<IAuthorLibraryService>((_, args) =>
            {
                if (source != null && (string)args[0] == source.HardcoverAuthorId)
                    return Task.FromException<Author>(originalFailure ?? new AuthorNotFoundException(source.HardcoverAuthorId));
                var config = (MonitoringConfig)args[1];
                var catalog = h.BookService.GetBooksByAuthor(h.Author.Id);
                config.PreviouslyMonitoredEditions = catalog.Where(book => book.IsMonitored())
                    .ToDictionary(book => book.Id, book => BookEditionIdentity.GetMonitoredEdition(book)?.Id);
                AuthorLibraryService.PrepareRequestedNarrations(new Author { Books = catalog }, config);
                h.Configure?.Invoke(config);
                return Task.FromResult(h.Author);
            });
            var info = Proxy<IProvideBookInfo>((_, args) =>
            {
                var work = (string)args[0];
                var media = (BookMediaType)args[1];
                if (lookup != null) return lookup(work, media);
                if (source == null && !h.WorkReady) throw new BookNotFoundException(work);
                return Tuple.Create(h.Author.HardcoverAuthorId, new Book { HardcoverBookId = work, MediaType = media, Author = h.Author }, new List<Author> { h.Author });
            });
            new ProcessPendingImportsCommandHandler(pending ?? h.Pending, library, books ?? h.BookService, info, queue ?? h.Queue,
                h.EventAggregator, LogManager.GetCurrentClassLogger(), h.Aliases).Execute(new ProcessPendingImportsCommand());
        }

        [Test]
        public void acknowledged_progress_baseline_preserves_monitor_and_search_demand_readded_after_a_conflict(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.AddBook(media, "hc:7000", "hc:edition:3000");
                h.AddBook(media, "hc:7001", "hc:edition:3001");
                var item = CorrectionRequest(h, media, new() { "hc:7000", "hc:7001" });
                h.Configure = _ =>
                {
                    var newer = h.Requests.Get(item.Id);
                    SetCorrectionSide(newer, media, new[] { "hc:7000", "hc:7001", "hc:7002" }, new[] { "hc:7000", "hc:7001", "hc:7002" });
                    Assert.That(h.Requests.TryUpdateRequest(newer, newer.Version), Is.True);
                };
                var searches = 0;
                var queue = Proxy<IManageCommandQueue>((method, args) =>
                {
                    if (args[0] is BookSearchCommand)
                    {
                        searches++;
                        var newer = h.Requests.Get(item.Id);
                        SetCorrectionSide(newer, media, new[] { "hc:7000", "hc:7002" }, new[] { "hc:7000", "hc:7001", "hc:7002" });
                        Assert.That(h.Requests.TryUpdateRequest(newer, newer.Version), Is.True);
                    }
                    return method.Invoke(h.Queue, args);
                });
                RunLifecycleCorrection(h, queue: queue);
                var saved = h.Requests.Get(item.Id);
                var monitor = JsonConvert.DeserializeObject<List<string>>(media == BookMediaType.Audiobook ? saved.AudiobookBooksToMonitor : saved.EbookBooksToMonitor);
                Assert.Multiple(() =>
                {
                    Assert.That(searches, Is.EqualTo(2));
                    Assert.That(saved.IsActive(), Is.True);
                    Assert.That(monitor, Is.EquivalentTo(new[] { "hc:7000", "hc:7002" }));
                    Assert.That(ReadSideSearch(saved, media), Is.EquivalentTo(new[] { "hc:7000", "hc:7002" }));
                });
            });
        }

        [Test]
        public void progress_conflict_retains_prepared_metadata_without_consuming_concurrent_cast_or_search_intent(
            [Values(false, true)] bool postgres, [Values("unchanged", "new_cast", "new_search", "new_cast_and_search")] string change)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var addedSearch = change.Contains("search");
                var changedCast = change.Contains("cast");
                var original = new RequestedNarration { WorkProviderId = "hc:7000", EditionProviderId = "hc:edition:3000", Search = !addedSearch };
                var item = CorrectionRequest(h, BookMediaType.Audiobook, new() { "hc:7000" }, new() { original });
                var previous = JsonConvert.DeserializeObject<PendingAuthorImport>(JsonConvert.SerializeObject(item));
                var newer = h.Requests.Get(item.Id);
                newer.AudiobookBooksToMonitor = "[\"hc:7000\",\"hc:7002\"]";
                var savedRequests = JsonConvert.DeserializeObject<List<RequestedNarration>>(newer.AudiobookNarrations);
                savedRequests[0].Search = true;
                if (changedCast)
                {
                    savedRequests[0].RequiresEdition = true;
                    savedRequests[0].NarratorNames = new() { "New Requested Narrator" };
                }
                newer.AudiobookNarrations = JsonConvert.SerializeObject(savedRequests);
                Assert.That(h.Requests.TryUpdateRequest(newer, newer.Version), Is.True);
                original.RequiresEdition = true;
                original.NarratorNames = new() { "Prepared Narrator" };
                item.AudiobookNarrations = JsonConvert.SerializeObject(new[] { original });
                item.AudiobookBooksToMonitor = "[]";
                Assert.That(h.Pending.SaveProgress(item, previous), Is.True);
                var prepared = JsonConvert.DeserializeObject<List<RequestedNarration>>(h.Requests.Get(item.Id).AudiobookNarrations).Single();
                Assert.Multiple(() =>
                {
                    Assert.That(prepared.RequiresEdition, Is.True);
                    Assert.That(prepared.NarratorNames, Is.EqualTo(new[] { changedCast ? "New Requested Narrator" : "Prepared Narrator" }));
                    Assert.That(prepared.Search, Is.True);
                    Assert.That(item.Version, Is.EqualTo(previous.Version));
                });
                previous = JsonConvert.DeserializeObject<PendingAuthorImport>(JsonConvert.SerializeObject(item));
                item.AudiobookNarrations = "[]";
                Assert.That(h.Pending.SaveProgress(item, previous), Is.True);
                var remaining = JsonConvert.DeserializeObject<List<RequestedNarration>>(h.Requests.Get(item.Id).AudiobookNarrations);
                Assert.Multiple(() =>
                {
                    Assert.That(remaining, Has.Count.EqualTo(change == "unchanged" ? 0 : 1));
                    Assert.That(h.Requests.Get(item.Id).AudiobookBooksToMonitor, Is.EqualTo("[\"hc:7002\"]"));
                    if (remaining.Count > 0)
                    {
                        Assert.That(remaining[0].Search, Is.True);
                        Assert.That(remaining[0].NarratorNames, Is.EqualTo(new[] { changedCast ? "New Requested Narrator" : "Prepared Narrator" }));
                    }
                });
            });
        }

        [Test]
        public void typed_search_acknowledgement_without_id_vector_changes_keeps_prepared_baseline(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var narration = TypedPendingEdition(media, "hc:7000", "hc:edition:3000");
                narration.RequiresEdition = false;
                var item = CorrectionRequest(h, media, new(), new() { narration });
                var previous = JsonConvert.DeserializeObject<PendingAuthorImport>(JsonConvert.SerializeObject(item));
                var newer = h.Requests.Get(item.Id);
                SetCorrectionSide(newer, media, new[] { "hc:7002" }, Array.Empty<string>());
                Assert.That(h.Requests.TryUpdateRequest(newer, newer.Version), Is.True);
                narration.RequiresEdition = true;
                narration.NarratorNames = media == BookMediaType.Audiobook ? new() { "Prepared Narrator" } : null;
                narration.Search = false;
                item.AudiobookNarrations = JsonConvert.SerializeObject(new[] { narration });
                Assert.That(h.Pending.SaveProgress(item, previous), Is.True);
                var saved = JsonConvert.DeserializeObject<List<RequestedNarration>>(h.Requests.Get(item.Id).AudiobookNarrations).Single();
                Assert.Multiple(() =>
                {
                    Assert.That(saved.Search, Is.False);
                    Assert.That(saved.RequiresEdition, Is.True);
                    Assert.That(saved.NarratorNames ?? new(), Is.EqualTo(narration.NarratorNames ?? new()));
                    Assert.That(item.Version, Is.EqualTo(previous.Version));
                    Assert.That(ReadSideSearch(h.Requests.Get(item.Id), media), Is.Empty);
                });
                previous = JsonConvert.DeserializeObject<PendingAuthorImport>(JsonConvert.SerializeObject(item));
                item.AudiobookNarrations = "[]";
                Assert.That(h.Pending.SaveProgress(item, previous), Is.True);
                Assert.Multiple(() =>
                {
                    Assert.That(JsonConvert.DeserializeObject<List<RequestedNarration>>(h.Requests.Get(item.Id).AudiobookNarrations), Is.Empty);
                    Assert.That(JsonConvert.DeserializeObject<List<string>>(media == BookMediaType.Audiobook ? h.Requests.Get(item.Id).AudiobookBooksToMonitor : h.Requests.Get(item.Id).EbookBooksToMonitor), Is.EqualTo(new[] { "hc:7002" }));
                });
            });
        }

        [Test]
        public void source_cancellation_or_merge_during_lookup_stops_foreign_primary_handoff(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media,
            [Values("cancel", "merge", "unchanged")] string change)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new RoutedQueueHarness(database, media, 1);
                h.Request.SearchForMissingBooks = false;
                h.Request.AudiobookMonitorExistingMode = h.Request.EbookMonitorExistingMode = null;
                h.Library.Requests.Update(h.Request);
                RunLifecycleCorrection(h.Library, source: h.Source, lookup: (work, requestedMedia) =>
                {
                    if (change == "cancel") h.Library.Pending.Cancel(h.Request.Id);
                    if (change == "merge")
                    {
                        var newer = h.Library.Requests.Get(h.Request.Id);
                        SetCorrectionSide(newer, media, new[] { "hc:7000", "hc:7002" }, new[] { "hc:7000", "hc:7002" });
                        Assert.That(h.Library.Requests.TryUpdateRequest(newer, newer.Version), Is.True);
                    }
                    return Tuple.Create(h.Library.Author.HardcoverAuthorId, new Book { HardcoverBookId = work, MediaType = requestedMedia, Author = h.Library.Author }, new List<Author> { h.Library.Author });
                });
                var destination = h.Library.Pending.GetByProviderId(h.Library.Author.HardcoverAuthorId);
                Assert.That(destination != null, Is.EqualTo(change == "unchanged"));
                if (change == "merge")
                {
                    var saved = h.Library.Requests.Get(h.Request.Id);
                    Assert.That(saved.IsActive(), Is.True);
                    Assert.That(ReadSideSearch(saved, media), Is.EquivalentTo(new[] { "hc:7000", "hc:7002" }));
                }
                if (change == "cancel") Assert.That(h.Library.Requests.Get(h.Request.Id).IsActive(), Is.False);
            });
        }

        [Test]
        public void rejected_search_retry_preserves_later_unmonitor_without_repeating_a_pin(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media,
            [Values(false, true)] bool typed, [Values(false, true)] bool unmonitor)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database) { WorkReady = false };
                var ready = h.AddBook(media, "hc:7000", "hc:edition:3000");
                var narrations = typed ? new List<RequestedNarration> { TypedPendingEdition(media, "hc:7000", "hc:edition:3000") } : null;
                if (typed && media == BookMediaType.Audiobook) narrations[0].NarratorNames = new() { "Fixture Narrator" };
                var item = CorrectionRequest(h, media, new() { "hc:7000", "hc:7001" }, narrations);
                h.RejectedBookId = ready.Id;
                RunLifecycleCorrection(h);
                Assert.That(ReadSideSearch(h.Requests.Get(item.Id), media), Does.Contain("hc:7000"));
                var rows = h.Books.All().Count();
                var pinned = BookEditionIdentity.GetMonitoredEdition(h.BookService.GetBook(ready.Id)).Id;
                if (unmonitor) h.BookService.SetMonitoredForMediaType(new List<int> { ready.Id }, media == BookMediaType.Audiobook ? "audiobook" : "ebook", false);
                h.RejectedBookId = null;
                h.Pending.RetryNow(item.Id);
                RunLifecycleCorrection(h);
                new BookAddedService(new CacheManager(), h.Queue, h.BookService, LogManager.GetCurrentClassLogger())
                    .SearchForRecentlyAdded(h.Author.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(h.BookService.GetBook(ready.Id).IsMonitored(), Is.EqualTo(!unmonitor));
                    Assert.That(h.Books.All().Count(), Is.EqualTo(rows));
                    Assert.That(BookEditionIdentity.GetMonitoredEdition(h.BookService.GetBook(ready.Id)).Id, Is.EqualTo(pinned));
                    Assert.That(ReadSideSearch(h.Requests.Get(item.Id), media), Does.Not.Contain("hc:7000"));
                    Assert.That(h.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EqualTo(unmonitor ? Array.Empty<int>() : new[] { ready.Id }));
                });
            });
        }

        [Test]
        public void search_only_typed_retry_does_not_restore_a_superseded_pin(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database) { WorkReady = false };
                var ready = h.AddBook(media, "hc:7000", "hc:edition:3000");
                var narration = TypedPendingEdition(media, "hc:7000", "hc:edition:3000");
                if (media == BookMediaType.Audiobook) narration.NarratorNames = new() { "Fixture Narrator" };
                var item = CorrectionRequest(h, media, new() { "hc:7000", "hc:7001" }, new() { narration });
                h.RejectedBookId = ready.Id;
                RunLifecycleCorrection(h);
                var replacement = AddPendingEdition(h, ready, "hc:edition:3999");
                foreach (var edition in h.Editions.FindRowsByBook(new[] { ready.Id }))
                {
                    edition.Monitored = edition.Id == replacement.Id;
                    edition.ManualAdd = edition.Monitored;
                    h.Editions.Update(edition);
                }
                var rows = h.Books.All().Count();
                h.RejectedBookId = null;
                h.Pending.RetryNow(item.Id);
                RunLifecycleCorrection(h);
                new BookAddedService(new CacheManager(), h.Queue, h.BookService, LogManager.GetCurrentClassLogger())
                    .SearchForRecentlyAdded(h.Author.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(h.Books.All().Count(), Is.EqualTo(rows));
                    Assert.That(BookEditionIdentity.GetMonitoredEdition(h.BookService.GetBook(ready.Id)).Id, Is.EqualTo(replacement.Id));
                    Assert.That(h.BookService.GetBook(ready.Id).IsMonitored(), Is.True);
                    Assert.That(ReadSideSearch(h.Requests.Get(item.Id), media), Does.Not.Contain("hc:7000"));
                    Assert.That(h.Commands.OfType<BookSearchCommand>(), Is.Empty);
                });
            });
        }

        [Test]
        public void routed_search_only_requests_preserve_completed_monitoring_and_selection(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media,
            [Values(false, true)] bool typed, [Values(false, true)] bool existingOtherSide)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new RoutedQueueHarness(database, media, 1);
                h.Request.SearchForMissingBooks = false;
                h.Request.AudiobookMonitorExistingMode = h.Request.EbookMonitorExistingMode = null;
                if (!typed) h.Request.AudiobookNarrations = null;
                SetCorrectionSide(h.Request, media, Array.Empty<string>(), new[] { "hc:7000" });
                h.Library.Requests.Update(h.Request);
                if (existingOtherSide)
                {
                    var otherMedia = media == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook;
                    var parked = CorrectionRequest(h.Library, otherMedia, new() { "hc:7999" });
                    parked.NextAttemptAt = DateTime.UtcNow.AddHours(1);
                    h.Library.Requests.Update(parked);
                }
                RunLifecycleCorrection(h.Library, source: h.Source);
                var destination = h.Library.Pending.GetByProviderId(h.Library.Author.HardcoverAuthorId);
                Assert.That(destination, Is.Not.Null);
                var monitor = JsonConvert.DeserializeObject<List<string>>((media == BookMediaType.Audiobook ? destination.AudiobookBooksToMonitor : destination.EbookBooksToMonitor) ?? "[]");
                Assert.That(monitor, Is.Empty);
                if (existingOtherSide)
                {
                    var otherIds = JsonConvert.DeserializeObject<List<string>>(media == BookMediaType.Audiobook ? destination.EbookBooksToMonitor : destination.AudiobookBooksToMonitor);
                    Assert.That(otherIds, Is.EqualTo(new[] { "hc:7999" }));
                }
                var rows = h.Library.Books.All().Count();
                h.Library.Pending.RetryNow(destination.Id);
                RunLifecycleCorrection(h.Library);
                Assert.Multiple(() =>
                {
                    Assert.That(h.Library.Books.All().Count(), Is.EqualTo(rows));
                    Assert.That(h.Library.BookService.GetBook(h.Targets[0].Id).IsMonitored(), Is.False);
                    Assert.That(h.Library.BookService.GetBook(h.Targets[0].Id).AnyEditionOk, Is.True);
                    Assert.That(h.Library.Commands.OfType<BookSearchCommand>(), Is.Empty);
                    Assert.That(h.Library.BookService.GetBook(h.Opposite.Id).IsMonitored(), Is.False);
                });
            });
        }

        [Test]
        public void routed_search_only_merge_keeps_legacy_same_side_monitor_demand_separate(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var routed = new RoutedQueueHarness(database, media, 1);
                routed.Request.SearchForMissingBooks = false;
                routed.Request.AudiobookMonitorExistingMode = routed.Request.EbookMonitorExistingMode = null;
                SetCorrectionSide(routed.Request, media, Array.Empty<string>(), new[] { "hc:7000" });
                routed.Library.Requests.Update(routed.Request);
                var legacy = TypedPendingEdition(media, "hc:7999", "hc:edition:3999");
                if (media == BookMediaType.Audiobook) legacy.NarratorNames = new() { "Legacy Narrator" };
                var parked = CorrectionRequest(routed.Library, media, new(), new() { legacy });
                SetCorrectionSide(parked, media, null, null);
                parked.NextAttemptAt = DateTime.UtcNow.AddHours(1);
                routed.Library.Requests.Update(parked);
                RunLifecycleCorrection(routed.Library, source: routed.Source);
                var destination = routed.Library.Pending.GetByProviderId(routed.Library.Author.HardcoverAuthorId);
                Assert.That(destination.Id, Is.EqualTo(parked.Id));
                var monitor = JsonConvert.DeserializeObject<List<string>>((media == BookMediaType.Audiobook ? destination.AudiobookBooksToMonitor : destination.EbookBooksToMonitor) ?? "[]");
                routed.Library.Pending.RetryNow(destination.Id);
                RunLifecycleCorrection(routed.Library);
                Assert.Multiple(() =>
                {
                    Assert.That(monitor, Is.EqualTo(new[] { "hc:7999" }));
                    Assert.That(routed.Library.BookService.GetBook(routed.Targets[0].Id).IsMonitored(), Is.False);
                    Assert.That(routed.Library.BookService.GetBook(routed.Targets[0].Id).AnyEditionOk, Is.True);
                    Assert.That(routed.Library.Commands.OfType<BookSearchCommand>(), Is.Empty);
                    Assert.That(JsonConvert.DeserializeObject<List<string>>(media == BookMediaType.Audiobook ? routed.Library.Requests.Get(destination.Id).AudiobookBooksToMonitor : routed.Library.Requests.Get(destination.Id).EbookBooksToMonitor), Is.EqualTo(new[] { "hc:7999" }));
                });
            });
        }

        [Test]
        public void search_only_retry_rechecks_withdrawal_when_selected_rows_are_reloaded(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media,
            [Values(false, true)] bool typed)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var ready = h.AddBook(media, "hc:7000", "hc:edition:3000");
                var narrations = typed ? new List<RequestedNarration> { TypedPendingEdition(media, "hc:7000", "hc:edition:3000") } : null;
                if (typed && media == BookMediaType.Audiobook) narrations[0].NarratorNames = new() { "Fixture Narrator" };
                var item = CorrectionRequest(h, media, new() { "hc:7000" }, narrations);
                h.RejectedBookId = ready.Id;
                RunLifecycleCorrection(h);
                Assert.That(h.Requests.Get(item.Id).IsActive(), Is.True);
                h.RejectedBookId = null;
                h.Pending.RetryNow(item.Id);
                var changed = false;
                var books = Proxy<IBookService>((method, args) =>
                {
                    if (method.Name == nameof(IBookService.GetBooks) && !changed)
                    {
                        changed = true;
                        h.BookService.SetMonitoredForMediaType(new List<int> { ready.Id }, media == BookMediaType.Audiobook ? "audiobook" : "ebook", false);
                    }
                    return method.Invoke(h.BookService, args);
                });
                RunLifecycleCorrection(h, books: books);
                Assert.Multiple(() =>
                {
                    Assert.That(changed, Is.True);
                    Assert.That(h.BookService.GetBook(ready.Id).IsMonitored(), Is.False);
                    Assert.That(h.Commands.OfType<BookSearchCommand>(), Is.Empty);
                    Assert.That(h.Requests.Find(item.Id), Is.Null);
                });
            });
        }

        [Test]
        public void withdrawing_old_pin_preserves_a_current_search_for_the_same_book_row(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var ready = h.AddBook(media, "hc:7000", "hc:edition:3000");
                var original = TypedPendingEdition(media, "hc:7000", "hc:edition:3000");
                if (media == BookMediaType.Audiobook) original.NarratorNames = new() { "Fixture Narrator" };
                var item = CorrectionRequest(h, media, new() { "hc:7000" }, new() { original });
                h.RejectedBookId = ready.Id;
                RunLifecycleCorrection(h);
                var replacement = AddPendingEdition(h, ready, "hc:edition:3999");
                foreach (var edition in h.Editions.FindRowsByBook(new[] { ready.Id }))
                {
                    edition.Monitored = edition.Id == replacement.Id;
                    edition.ManualAdd = edition.Monitored;
                    h.Editions.Update(edition);
                }
                var next = TypedPendingEdition(media, "hc:7000", "hc:edition:3999");
                if (media == BookMediaType.Audiobook) next.NarratorNames = new() { "Requested Narrator" };
                var newer = h.Requests.Get(item.Id);
                var narrations = JsonConvert.DeserializeObject<List<RequestedNarration>>(newer.AudiobookNarrations);
                narrations.Add(next);
                newer.AudiobookNarrations = JsonConvert.SerializeObject(narrations);
                Assert.That(h.Requests.TryUpdateRequest(newer, newer.Version), Is.True);
                h.Pending.RetryNow(item.Id);
                RunLifecycleCorrection(h);
                var remaining = JsonConvert.DeserializeObject<List<RequestedNarration>>(h.Requests.Get(item.Id).AudiobookNarrations).Single();
                Assert.Multiple(() =>
                {
                    Assert.That(remaining.EditionProviderId, Is.EqualTo(next.EditionProviderId));
                    Assert.That(remaining.Search, Is.True);
                    Assert.That(h.BookService.GetBook(ready.Id).AddOptions.SearchForNewBook, Is.True);
                    Assert.That(BookEditionIdentity.GetMonitoredEdition(h.BookService.GetBook(ready.Id)).Id, Is.EqualTo(replacement.Id));
                    Assert.That(h.Commands.OfType<BookSearchCommand>(), Is.Empty);
                });
                h.RejectedBookId = null;
                new BookAddedService(new CacheManager(), h.Queue, h.BookService, LogManager.GetCurrentClassLogger())
                    .SearchForRecentlyAdded(h.Author.Id);
                Assert.That(h.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EqualTo(new[] { ready.Id }));
            });
        }

        [Test]
        public void withdrawn_held_searches_are_cleared_in_one_batch_unless_current_demand_changed(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media,
            [Values(1, 20)] int count, [Values("unchanged", "merge", "cancel")] string change)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var measured = new RoutedQueueHarness(database, media, count);
                var h = measured.Library;
                h.Requests.Delete(measured.Request.Id);
                var ids = measured.Targets.Select(book => book.HardcoverBookId).ToList();
                var narrations = measured.Targets.Select(book =>
                {
                    var narration = TypedPendingEdition(media, book.HardcoverBookId, h.Editions.FindRowsByBook(new[] { book.Id }).Single().ForeignEditionId);
                    if (media == BookMediaType.Audiobook) narration.NarratorNames = new() { "Fixture Narrator" };
                    return narration;
                }).ToList();
                ids.Add("hc:8999");
                var item = CorrectionRequest(h, media, ids, narrations);
                h.Author.AddOptions = new AddAuthorOptions { SearchForMissingBooks = true };
                h.Authors.Update(h.Author);
                var rejected = Proxy<IManageCommandQueue>((method, args) =>
                {
                    if (args[0] is BookSearchCommand) throw new InvalidOperationException("Injected queue rejection");
                    return method.Invoke(h.Queue, args);
                });
                RunLifecycleCorrection(h, queue: rejected);
                Assert.That(measured.Targets.Select(book => h.BookService.GetBook(book.Id).AddOptions.SearchForNewBook), Is.All.True);
                h.BookService.SetMonitoredForMediaType(measured.Targets.Select(book => book.Id).ToList(), media == BookMediaType.Audiobook ? "audiobook" : "ebook", false);
                h.Pending.RetryNow(item.Id);
                var changed = false;
                var writes = 0;
                var books = Proxy<IBookService>((method, args) =>
                {
                    if (method.Name == nameof(IBookService.GetBooks) && !changed)
                    {
                        changed = true;
                        if (change == "cancel") h.Pending.Cancel(item.Id);
                        if (change == "merge")
                        {
                            var newer = h.Requests.Get(item.Id);
                            SetCorrectionSide(newer, media, ids, ids);
                            Assert.That(h.Requests.TryUpdateRequest(newer, newer.Version), Is.True);
                        }
                    }
                    if (method.Name == nameof(IBookService.SetAddOptions)) writes++;
                    return method.Invoke(h.BookService, args);
                });
                measured.Sql.Clear();
                RunLifecycleCorrection(h, books: books);
                var sql = measured.Sql.ToList();
                TestContext.Progress.WriteLine($"Withdrawn held search ({database.DatabaseType}): {media}, {count} rows, {change}, {sql.Count} native commands, {writes} SetAddOptions batches.");
                Assert.Multiple(() =>
                {
                    Assert.That(writes, Is.EqualTo(change == "unchanged" ? 1 : 0));
                    Assert.That(h.Commands.OfType<BookSearchCommand>(), Is.Empty);
                    Assert.That(measured.Targets.Select(book => h.BookService.GetBook(book.Id).AddOptions.SearchForNewBook), Is.All.EqualTo(change != "unchanged"));
                    Assert.That(h.AuthorService.GetAuthor(h.Author.Id).AddOptions.SearchForMissingBooks, Is.True);
                    Assert.That(h.BookService.GetBook(measured.Opposite.Id).IsMonitored(), Is.False);
                    if (change == "merge")
                    {
                        Assert.That(ReadSideSearch(h.Requests.Get(item.Id), media), Is.EquivalentTo(ids));
                        Assert.That(h.Requests.Get(item.Id).Version, Is.GreaterThan(item.Version));
                    }
                    if (change == "cancel") Assert.That(h.Requests.Get(item.Id).IsActive(), Is.False);
                });
                if (change != "cancel")
                {
                    new BookAddedService(new CacheManager(), h.Queue, h.BookService, LogManager.GetCurrentClassLogger())
                        .SearchForRecentlyAdded(h.Author.Id);
                    Assert.That(h.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds),
                        Is.EquivalentTo(change == "merge" ? measured.Targets.Select(book => book.Id) : Array.Empty<int>()));
                }
            });
        }

        [Test]
        public void initial_typed_request_without_monitor_vectors_still_selects_and_monitors_its_edition(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var ready = h.AddBook(media, "hc:7000", "hc:edition:3000");
                var narration = TypedPendingEdition(media, "hc:7000", "hc:edition:3000");
                if (media == BookMediaType.Audiobook) narration.NarratorNames = new() { "Fixture Narrator" };
                var item = CorrectionRequest(h, media, new(), new() { narration });
                SetCorrectionSide(item, media, null, null);
                h.Requests.Update(item);
                RunLifecycleCorrection(h);
                var saved = h.BookService.GetBook(ready.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(h.Requests.Find(item.Id), Is.Null);
                    Assert.That(saved.IsMonitored(), Is.True);
                    Assert.That(saved.AnyEditionOk, Is.False);
                    Assert.That(h.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EqualTo(new[] { ready.Id }));
                });
            });
        }

        [Test]
        public void cleanup_failure_does_not_prevent_the_due_import_batch(
            [Values(false, true)] bool postgres, [Values(false, true)] bool failCleanup)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var ready = h.AddBook(work: "hc:7000", edition: "hc:edition:3000");
                var item = CorrectionRequest(h, BookMediaType.Audiobook, new() { "hc:7000" });
                var cleanupCalls = 0;
                var pending = Proxy<IPendingAuthorImportService>((method, args) =>
                {
                    if (method.Name == nameof(IPendingAuthorImportService.CleanupOldCompleted))
                    {
                        cleanupCalls++;
                        if (failCleanup) throw new InvalidOperationException("Injected cleanup failure");
                    }
                    return method.Invoke(h.Pending, args);
                });
                RunLifecycleCorrection(h, pending);
                Assert.Multiple(() =>
                {
                    Assert.That(cleanupCalls, Is.EqualTo(1));
                    Assert.That(h.Requests.Find(item.Id), Is.Null);
                    Assert.That(h.BookService.GetBook(ready.Id).IsMonitored(), Is.True);
                    Assert.That(h.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EqualTo(new[] { ready.Id }));
                });
            });
        }

        [Test]
        public void original_declared_refusal_keeps_its_reason_and_real_handoff_while_not_ready_keeps_retrying(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media,
            [Values(false, true)] bool terminal)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new RoutedQueueHarness(database, media, 1);
                h.Request.SearchForMissingBooks = true;
                h.Request.AudiobookMonitorExistingMode = h.Request.EbookMonitorExistingMode = null;
                h.Library.Requests.Update(h.Request);
                var refusal = new AuthorTerminalException("author_no_primary_works", h.Request.ProviderId, null, "Declared provider refusal", false);
                RunLifecycleCorrection(h.Library, source: h.Source, originalFailure: terminal ? refusal : null);
                var source = h.Library.Requests.Get(h.Request.Id);
                var destination = h.Library.Pending.GetByProviderId(h.Library.Author.HardcoverAuthorId);
                Assert.Multiple(() =>
                {
                    Assert.That(destination, Is.Not.Null);
                    Assert.That(source.OverallStatus, Is.EqualTo(terminal ? PendingImportStatus.Failed : PendingImportStatus.Retrying));
                    Assert.That(source.LastError, Is.EqualTo(terminal ? refusal.Message : PendingAuthorImportRetryReason.AuthorNotYetAvailable));
                    Assert.That(source.SearchForMissingBooks, Is.True);
                    Assert.That(ReadSideSearch(destination, media), Is.EqualTo(new[] { "hc:7000" }));
                    Assert.That(destination.SearchForMissingBooks, Is.False);
                });
            });
        }
    }
}


namespace Chaptarr.Core.Test.ImportLists
{
    public partial class ImportListMissingMediaSidePersistenceFixture
    {
        [Test]
        public void full_pending_worker_author_refresh_preserves_search_only_monitor_and_pin_choices(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media,
            [Values("unchanged", "unmonitor", "repin")] string change)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var stored = new StoredListLibrary(database);
                var local = CatalogBook(media, "Requested Work", "7000");
                local.Editions.Single().Monitored = true;
                stored.Save(local);
                var requestedEdition = local.Editions.Single();
                var other = CatalogBook(media == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook, "Requested Work", "7000");
                other.Editions.Single().Monitored = true;
                stored.Save(other);
                Author Remote()
                {
                    var book = CatalogBook(media, "Requested Work", "7000");
                    book.Editions.Add(new Edition { Title = book.Title, ForeignEditionId = "hc:edition:3999", ReadingFormatId = media == BookMediaType.Audiobook ? 2 : 3,
                        Narrator = media == BookMediaType.Audiobook ? "Changed Narrator" : null, Language = "eng" });
                    var otherSide = CatalogBook(other.MediaType, "Requested Work", "7000");
                    return new Author { Name = stored.Author.Name, GoodreadsAuthorId = stored.Author.GoodreadsAuthorId, Books = new() { book, otherSide }, Series = new() };
                }
                var authorInfo = Proxy<IProvideAuthorInfo>((_, _) => Remote());
                var profile = new global::Chaptarr.Core.Test.Books.AuthorLibraryServiceSpecificBookMonitoringFixture.StubMetadataProfileService();
                var filter = new EditionMetadataProfileFilter(new global::Chaptarr.Core.Test.Books.TestTermMatcherService());
                var selector = new EditionSelector(stored.Logger);
                var exclusions = Proxy<IImportListExclusionService>((method, _) => method.ReturnType == typeof(List<ImportListExclusion>) ? new List<ImportListExclusion>() : null);
                var covers = Proxy<IMapCoversToLocal>((_, _) => null);
                var refreshEditions = new RefreshEditionService(stored.Editions, Proxy<IMetadataTagService>((_, _) => null), null, database, stored.Logger);
                var refreshBooks = new RefreshBookService(stored.Books, stored.AuthorService, stored.Roots, stored.Editions, authorInfo, null,
                    refreshEditions, stored.Files, null, stored.Events, null, selector, filter, covers, stored.Logger, mainDatabase: null);
                var config = Proxy<IConfigService>((method, _) => method.ReturnType == typeof(bool) ? false : method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null);
                var refresh = new RefreshAuthorService(MetadataHealthGateStub.Ready(), authorInfo, stored.AuthorService, stored.Books, stored.Editions, profile, refreshBooks, null,
                    stored.Events, stored.Queue, stored.Files, new global::NzbDrone.Core.History.HistoryRepository(database, stored.Events), stored.Roots, null, Proxy<IMonitorNewBookService>((_, _) => false), config,
                    exclusions, null, null, null, stored.Logger, editionSelector: selector, mainDatabase: database, bookRepository: new BookRepository(database, stored.Events), authorRepository: new AuthorRepository(database, stored.Events, stored.Logger));
                var library = new AuthorLibraryService(stored.AuthorService, authorInfo, stored.Books, null, stored.Editions, null, profile,
                    new global::Chaptarr.Core.Test.TestQualityProfileService(), stored.Paths, stored.Roots, stored.Queue, stored.Events, stored.Pending, database,
                    exclusions, filter, null, stored.Logger, selector, refresh);
                var item = stored.Requests.Insert(new PendingAuthorImport
                {
                    ProviderId = stored.Author.GoodreadsAuthorId, ProviderPrefix = "gr", AuthorName = stored.Author.Name, RequestedBy = "UserInterface",
                    OverallStatus = PendingImportStatus.Pending, AudiobookStatus = media == BookMediaType.Audiobook ? PendingImportStatus.Pending : PendingImportStatus.NotRequested,
                    EbookStatus = media == BookMediaType.Ebook ? PendingImportStatus.Pending : PendingImportStatus.NotRequested,
                    CreatedAt = DateTime.UtcNow, NextAttemptAt = DateTime.UtcNow.AddMinutes(-1), Version = 1,
                    AudiobookBooksToMonitor = media == BookMediaType.Audiobook ? "[\"gr:7000\"]" : null,
                    AudiobookBooksToSearch = media == BookMediaType.Audiobook ? "[\"gr:7000\"]" : null,
                    EbookBooksToMonitor = media == BookMediaType.Ebook ? "[\"gr:7000\"]" : null,
                    EbookBooksToSearch = media == BookMediaType.Ebook ? "[\"gr:7000\"]" : null,
                    AudiobookNarrations = JsonConvert.SerializeObject(new[] { new RequestedNarration { MediaType = media, WorkProviderId = "gr:7000",
                        EditionProviderId = requestedEdition.ForeignEditionId, NarratorNames = media == BookMediaType.Audiobook ? new() { "Fixture Narrator" } : null, RequiresEdition = true, Search = true } }),
                    AudiobookQualityProfileId = 9, EbookQualityProfileId = 9, AudiobookMetadataProfileId = 8, EbookMetadataProfileId = 8,
                    AudiobookRootFolderPath = stored.Author.AudiobookRootFolderPath, EbookRootFolderPath = stored.Author.EbookRootFolderPath
                });
                bool reject = true;
                var queue = Proxy<IManageCommandQueue>((method, args) =>
                {
                    if (reject && args[0] is BookSearchCommand) throw new InvalidOperationException("Injected queue rejection");
                    return method.Invoke(stored.Queue, args);
                });
                var workInfo = Proxy<IProvideBookInfo>((_, _) => throw new AssertionException("The existing requested work should resolve from the native catalog"));
                var handler = new ProcessPendingImportsCommandHandler(stored.Pending, library, stored.Books, workInfo, queue, stored.Events, stored.Logger, stored.Aliases);
                handler.Execute(new ProcessPendingImportsCommand());
                Assert.That(stored.Requests.Get(item.Id).IsActive(), Is.True);
                var acknowledged = stored.Requests.Get(item.Id);
                Assert.That(media == BookMediaType.Audiobook ? acknowledged.AudiobookBooksToMonitor : acknowledged.EbookBooksToMonitor, Is.EqualTo("[]"));
                var selected = BookEditionIdentity.GetMonitoredEdition(stored.Books.GetBook(local.Id)).Id;
                if (change == "unmonitor") stored.Books.SetMonitoredForMediaType(new List<int> { local.Id }, media == BookMediaType.Audiobook ? "audiobook" : "ebook", false);
                if (change == "repin")
                {
                    var editions = stored.Editions.GetEditionsByBook(local.Id);
                    selected = editions.Single(edition => edition.ForeignEditionId == "hc:edition:3999").Id;
                    foreach (var edition in editions)
                    {
                        edition.Monitored = edition.Id == selected;
                        edition.ManualAdd = edition.Monitored;
                    }
                    stored.Editions.UpdateSelection(editions);
                }
                var rows = stored.BookRows.All().Count();
                reject = false;
                stored.Pending.RetryNow(item.Id);
                handler.Execute(new ProcessPendingImportsCommand());
                var saved = stored.Books.GetBook(local.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(stored.Requests.Find(item.Id), Is.Null);
                    Assert.That(stored.BookRows.All().Count(), Is.EqualTo(rows));
                    Assert.That(saved.IsMonitored(), Is.EqualTo(change != "unmonitor"));
                    Assert.That(BookEditionIdentity.GetMonitoredEdition(saved).Id, Is.EqualTo(selected));
                    Assert.That(stored.Books.GetBook(other.Id).IsMonitored(), Is.False);
                    Assert.That(stored.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EqualTo(change == "unchanged" ? new[] { local.Id } : Array.Empty<int>()));
                });
            });
        }
    }
}
