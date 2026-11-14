using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Chaptarr.Core.Test.Common;
using Newtonsoft.Json;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.MediaFiles.Events;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Books
{
    public partial class PersistedAliasPendingLifecycleFixture
    {
        [TestCase(false, BookMediaType.Audiobook, false, false)]
        [TestCase(true, BookMediaType.Audiobook, false, false)]
        [TestCase(false, BookMediaType.Audiobook, true, false)]
        [TestCase(true, BookMediaType.Audiobook, true, false)]
        [TestCase(false, BookMediaType.Ebook, false, false)]
        [TestCase(true, BookMediaType.Ebook, false, false)]
        [TestCase(false, BookMediaType.Audiobook, true, true)]
        [TestCase(true, BookMediaType.Audiobook, true, true)]
        public void pending_held_search_acknowledges_ready_rows_before_sibling_retries(
            bool postgres, BookMediaType media, bool narration, bool rejectRefresh)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.Author.AudiobookMonitored = media == BookMediaType.Audiobook;
                h.Author.EbookMonitored = media == BookMediaType.Ebook;
                h.Author.AddOptions = new AddAuthorOptions { SearchForMissingBooks = true };
                h.Authors.Update(h.Author);
                var ready = h.AddBook(media, "hc:7000", "hc:edition:7000");
                ready.ReleaseDate = DateTime.UtcNow.AddDays(-1);
                h.Books.Update(ready);
                var narrations = narration ? new List<RequestedNarration>
                {
                    new() { WorkProviderId = "hc:7000", EditionProviderId = "hc-ed:7000",
                        RequiresEdition = true, Search = true, NarratorNames = new List<string> { "Fixture Narrator" } }
                } : null;
                var request = h.Request(new List<string> { "hc:7000", "hc:7001", "hc:7002" }, narrations);
                request.SearchForMissingBooks = true;
                request.AudiobookBooksToSearch = null;
                if (media == BookMediaType.Ebook)
                {
                    request.AudiobookStatus = PendingImportStatus.NotRequested;
                    request.AudiobookBooksToMonitor = null;
                    request.EbookStatus = PendingImportStatus.Pending;
                    request.EbookBooksToMonitor = JsonConvert.SerializeObject(new[] { "hc:7000", "hc:7001", "hc:7002" });
                }
                h.Requests.Update(request);
                h.Configure = config => config.SearchForMissingBooks = false;
                h.RejectRefreshCommand = rejectRefresh;
                h.Run();
                var retained = h.Requests.Get(request.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(retained.IsActive(), Is.True);
                    Assert.That(ReadSideSearch(retained, media), Is.EqualTo(new[] { "hc:7001", "hc:7002" }));
                    Assert.That(h.Commands.OfType<BookSearchCommand>(), Is.Empty);
                    Assert.That(h.BookService.GetBook(ready.Id).AddOptions.SearchForNewBook, Is.False);
                    Assert.That(h.Authors.Get(h.Author.Id).AddOptions.SearchForMissingBooks, Is.True);
                    if (narration)
                        Assert.That(JsonConvert.DeserializeObject<List<RequestedNarration>>(retained.AudiobookNarrations), Is.Empty);
                });
                if (rejectRefresh)
                {
                    h.RejectRefreshCommand = false;
                    h.Pending.RetryNow(request.Id);
                    h.Run();
                    Assert.That(h.Commands.OfType<BookSearchCommand>(), Is.Empty);
                    Assert.That(ReadSideSearch(h.Requests.Get(request.Id), media), Is.EqualTo(new[] { "hc:7001", "hc:7002" }));
                }
                Assert.That(h.Books.GetMissingBookSearchTargets(null, h.Author.Id).Select(target => target.BookId), Is.EqualTo(new[] { ready.Id }));
                CompleteHeldScan(h);
                Assert.Multiple(() =>
                {
                    Assert.That(h.Commands.OfType<MissingBookSearchCommand>().Single().AuthorId, Is.EqualTo(h.Author.Id));
                    Assert.That(h.Commands.OfType<BookSearchCommand>(), Is.Empty);
                    Assert.That(h.Authors.Get(h.Author.Id).AddOptions, Is.Null);
                    Assert.That(h.Author.AddOptions, Is.Null);
                });

                var late = h.AddBook(media, "hc:7001", "hc:edition:7001");
                h.Pending.RetryNow(request.Id);
                h.Run();
                Assert.That(h.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EqualTo(new[] { late.Id }));
                Assert.That(ReadSideSearch(h.Requests.Get(request.Id), media), Is.EqualTo(new[] { "hc:7002" }));
                Assert.That(h.BookService.GetBook(ready.Id).AddOptions.SearchForNewBook, Is.False);

                var last = h.AddBook(media, "hc:7002", "hc:edition:7002");
                h.Pending.RetryNow(request.Id);
                h.Run();
                Assert.Multiple(() =>
                {
                    Assert.That(h.Requests.Find(request.Id), Is.Null);
                    Assert.That(h.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EqualTo(new[] { late.Id, last.Id }));
                    Assert.That(h.Commands.OfType<MissingBookSearchCommand>().Count(), Is.EqualTo(1));
                    Assert.That(h.BookService.GetBooks(new[] { ready.Id, late.Id, last.Id }).All(book => book.MediaType == media && book.IsMonitored()), Is.True);
                });
            });
        }

        [Test]
        public void pending_held_search_keeps_rejected_paused_side_and_native_demand(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media,
            [Values(false, true)] bool native)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.Author.AudiobookMonitored = media != BookMediaType.Audiobook || native;
                h.Author.EbookMonitored = media != BookMediaType.Ebook || native;
                h.Author.AddOptions = new AddAuthorOptions { SearchForMissingBooks = true };
                h.Authors.Update(h.Author);
                var requested = h.AddBook(media, "hc:7000", "hc:edition:7000");
                var request = h.Request(new List<string> { "hc:7000" });
                if (media == BookMediaType.Ebook)
                {
                    request.AudiobookStatus = PendingImportStatus.NotRequested;
                    request.AudiobookBooksToMonitor = request.AudiobookBooksToSearch = null;
                    request.EbookStatus = PendingImportStatus.Pending;
                    request.EbookBooksToMonitor = request.EbookBooksToSearch = JsonConvert.SerializeObject(new[] { "hc:7000" });
                }
                if (native)
                {
                    request.RequestedBy = "UserInterface";
                    Harness.MarkExplicitSearches(request);
                }
                h.Requests.Update(request);
                h.RejectedBookId = requested.Id;
                h.Run();
                Assert.Multiple(() =>
                {
                    Assert.That(ReadSideSearch(h.Requests.Get(request.Id), media), Is.EqualTo(new[] { "hc:7000" }));
                    Assert.That(h.Requests.Get(request.Id).IsActive(), Is.True);
                    Assert.That(h.Commands.OfType<BookSearchCommand>(), Is.Empty);
                    Assert.That(h.Authors.Get(h.Author.Id).AddOptions.SearchForMissingBooks, Is.True);
                    if (!native) Assert.That(h.BookService.GetBook(requested.Id).AddOptions.SearchForNewBook, Is.True);
                });
                h.RejectedBookId = null;
                h.Pending.RetryNow(request.Id);
                h.Run();
                Assert.Multiple(() =>
                {
                    Assert.That(h.Requests.Find(request.Id), Is.Null);
                    Assert.That(h.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EqualTo(new[] { requested.Id }));
                    Assert.That(h.BookService.GetBook(requested.Id).AddOptions.SearchForNewBook, Is.False);
                    Assert.That(h.Authors.Get(h.Author.Id).AddOptions.SearchForMissingBooks, Is.True);
                });
            });
        }

        [Test]
        public void pending_held_search_acknowledgment_stays_batched(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media)
        {
            var counts = new List<int>();
            foreach (var count in new[] { 1, 20 })
            {
                DualDatabaseFixture.Run(postgres, database =>
                {
                    var queries = new List<string>();
                    var observed = Proxy<IMainDatabase>((method, args) => method.Name == nameof(IMainDatabase.OpenConnection)
                        ? CommandObserver.Wrap<IDbConnection>(database.OpenConnection(), command => queries.Add(command.CommandText))
                        : method.Invoke(database, args));
                    var h = new Harness(observed);
                    h.Author.AudiobookMonitored = media == BookMediaType.Audiobook;
                    h.Author.EbookMonitored = media == BookMediaType.Ebook;
                    h.Author.AddOptions = new AddAuthorOptions { SearchForMissingBooks = true };
                    h.Authors.Update(h.Author);
                    var ids = Enumerable.Range(0, count).Select(index => "hc:" + (7000 + index)).ToList();
                    foreach (var id in ids)
                    {
                        var book = h.AddBook(media, id, "hc:edition:" + id.Substring(3));
                        book.ReleaseDate = DateTime.UtcNow.AddDays(-1);
                        h.Books.Update(book);
                    }
                    ids.Add("hc:8000");
                    var request = h.Request(ids);
                    if (media == BookMediaType.Ebook)
                    {
                        request.AudiobookStatus = PendingImportStatus.NotRequested;
                        request.AudiobookBooksToMonitor = request.AudiobookBooksToSearch = null;
                        request.EbookStatus = PendingImportStatus.Pending;
                        request.EbookBooksToMonitor = request.EbookBooksToSearch = JsonConvert.SerializeObject(ids);
                    }
                    h.Requests.Update(request);
                    queries.Clear();
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    h.Run();
                    timer.Stop();
                    var commandCount = queries.Count;
                    counts.Add(commandCount);
                    Assert.Multiple(() =>
                    {
                        Assert.That(h.Commands.OfType<BookSearchCommand>(), Is.Empty);
                        Assert.That(ReadSideSearch(h.Requests.Get(request.Id), media), Is.EqualTo(new[] { "hc:8000" }));
                        Assert.That(h.CatalogReads, Is.EqualTo(1));
                        Assert.That(h.AuthorFetches, Is.EqualTo(1));
                    });
                    TestContext.Progress.WriteLine($"Held search ({database.DatabaseType}): {media}, {count} ready targets, {commandCount} SQL commands, {timer.Elapsed.TotalMilliseconds:F1} ms");
                });
            }
            Assert.That(counts[1], Is.EqualTo(counts[0]), "Ready-row growth must not add per-row database queries.");
        }

        private static List<string> ReadSideSearch(PendingAuthorImport request, BookMediaType media)
        {
            return JsonConvert.DeserializeObject<List<string>>((media == BookMediaType.Audiobook
                ? request.AudiobookBooksToSearch : request.EbookBooksToSearch) ?? "[]");
        }

        private static void CompleteHeldScan(Harness h)
        {
            var logger = LogManager.GetCurrentClassLogger();
            var authors = new AuthorService(h.Authors, h.EventAggregator, null, null, h.Queue, new CacheManager(), h.Books, null,
                logger, null, null, new Lazy<IBookService>(() => h.BookService), mainDatabase: h.Database, h.Aliases);
            new AuthorScannedHandler(new BookMonitoredService(authors, h.BookService, logger), authors, h.Queue,
                new BookAddedService(new CacheManager(), h.Queue, h.BookService, logger), h.EventAggregator, logger, h.BookService)
                .Handle(new AuthorScannedEvent(h.Author));
        }
    }
}
