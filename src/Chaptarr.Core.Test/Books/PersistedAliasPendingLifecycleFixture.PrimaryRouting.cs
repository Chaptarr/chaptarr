using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Common;
using Newtonsoft.Json;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Messaging.Commands;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Books
{
    public partial class PersistedAliasPendingLifecycleFixture
    {
        [Test]
        public void persisted_native_request_uses_proven_primary_and_existing_book_writer(
            [Values(false, true)] bool postgres,
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media,
            [Values(false, true)] bool editionOnly)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var requested = new Book { HardcoverBookId = editionOnly ? null : "hc:7000", MediaType = media,
                    Author = new Author { HardcoverAuthorId = "hc:2222", Name = "Reported Contributor",
                        AudiobookRootFolderPath = Path.GetTempPath(), EbookRootFolderPath = Path.GetTempPath(),
                        AudiobookQualityProfileId = 1, AudiobookMetadataProfileId = 1, EbookQualityProfileId = 1, EbookMetadataProfileId = 1 },
                    AddOptions = new AddBookOptions { SearchForNewBook = true },
                    Editions = editionOnly ? new List<Edition> { new() { ForeignEditionId = "hc:edition:2001", HardcoverEditionId = "hc:edition:2001", Monitored = true } } : null };
                var reads = 0;
                var info = Proxy<IProvideBookInfo>((method, args) =>
                {
                    reads++;
                    Assert.That(method.Name, Is.EqualTo(editionOnly ? nameof(IProvideBookInfo.GetEditionInfo) : nameof(IProvideBookInfo.GetWorkInfo)));
                    return Tuple.Create(h.Author.HardcoverAuthorId, new Book { HardcoverBookId = "hc:7000", MediaType = media, Author = h.Author,
                        Editions = new List<Edition> { new() { ForeignEditionId = "hc:edition:2001" } } }, new List<Author> { requested.Author, h.Author });
                });
                var library = Proxy<IAuthorLibraryService>((method, args) =>
                {
                    Assert.That((string)args[0], Is.EqualTo(h.Author.HardcoverAuthorId));
                    var config = (MonitoringConfig)args[1];
                    Assert.That(config.AuthorName, Is.EqualTo(h.Author.Name));
                    h.AddBook(media, "hc:7000");
                    return Task.FromResult(h.Author);
                });
                var exclusions = Proxy<IImportListExclusionService>((method, args) => new List<ImportListExclusion>());
                var service = new AddBookService(h.AuthorService, library, h.BookService, null, info, exclusions,
                    null, null, null, null, null, h.EditionService, null, null, null, LogManager.GetCurrentClassLogger(), h.Queue);
                var book = service.AddBook(requested).GetAwaiter().GetResult();
                var saved = h.BookService.GetBook(book.Id);
                Assert.That(saved.AuthorId, Is.EqualTo(h.Author.Id));
                Assert.That(saved.MediaType, Is.EqualTo(media));
                Assert.That(saved.IsMonitored(), Is.True);
                Assert.That(saved.AddOptions.SearchForNewBook, Is.False);
                var search = h.Commands.OfType<BookSearchCommand>().Single();
                Assert.That(search.BookIds, Is.EqualTo(new[] { saved.Id }));
                Assert.That(search.Trigger, Is.EqualTo(CommandTrigger.Manual));
                Assert.That(reads, Is.EqualTo(1));
            });
        }

        [Test]
        public void persisted_pending_primary_routing_finishes_after_original_author_demand(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media,
            [Values(1, 20)] int count)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var queries = new List<string>();
                var observed = Proxy<IMainDatabase>((method, args) => method.Name == nameof(IMainDatabase.OpenConnection)
                    ? CommandObserver.Wrap<IDbConnection>(database.OpenConnection(), command => queries.Add(command.CommandText))
                    : method.Invoke(database, args));
                var h = new Harness(observed);
                var source = h.Authors.Insert(new Author { Name = "Reported Contributor", CleanName = "reportedcontributor",
                    SortName = "reportedcontributor", SortNameLastFirst = "Contributor, Reported", TitleSlug = Guid.NewGuid().ToString("N"),
                    Path = Path.Combine(Path.GetTempPath(), "reported-contributor"), Added = DateTime.UtcNow, HardcoverAuthorId = "hc:2222" });
                var targets = Enumerable.Range(0, count).Select(index => h.AddBook(media, $"hc:{7000 + index}", $"hc:edition:{3000 + index}")).ToList();
                var opposite = h.AddBook(media == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook, "hc:7000", "hc:edition:5000");
                var request = h.Request(media == BookMediaType.Audiobook ? targets.Select(book => book.HardcoverBookId).ToList() : new List<string>(), ebook: media == BookMediaType.Ebook);
                request.ProviderId = source.HardcoverAuthorId;
                request.RequestedBy = "UserInterface";
                request.AudiobookStatus = media == BookMediaType.Audiobook ? PendingImportStatus.Pending : PendingImportStatus.NotRequested;
                request.EbookStatus = media == BookMediaType.Ebook ? PendingImportStatus.Pending : PendingImportStatus.NotRequested;
                request.AudiobookMonitorExistingMode = request.EbookMonitorExistingMode = MonitorTypes.All;
                request.AudiobookMonitorNewItems = request.EbookMonitorNewItems = NewItemMonitorTypes.All;
                request.SearchForMissingBooks = true;
                if (media == BookMediaType.Ebook) request.EbookBooksToMonitor = request.EbookBooksToSearch = JsonConvert.SerializeObject(targets.Select(book => book.HardcoverBookId));
                h.Requests.Update(request);
                var imports = new List<string>();
                var catalogs = 0;
                var workReads = 0;
                var library = Proxy<IAuthorLibraryService>((method, args) =>
                {
                    var provider = (string)args[0];
                    var config = (MonitoringConfig)args[1];
                    imports.Add(provider);
                    if (provider == source.HardcoverAuthorId)
                    {
                        Assert.That(config.SearchForMissingBooks, Is.True);
                        Assert.That(media == BookMediaType.Audiobook ? config.AudiobookMonitorExistingMode : config.EbookMonitorExistingMode, Is.EqualTo(MonitorTypes.All));
                        return Task.FromResult(source);
                    }
                    Assert.That(provider, Is.EqualTo(h.Author.HardcoverAuthorId));
                    Assert.That(config.SearchForMissingBooks, Is.False);
                    Assert.That(media == BookMediaType.Audiobook ? config.AudiobookMonitorExistingMode : config.EbookMonitorExistingMode, Is.EqualTo(MonitorTypes.SpecificBook));
                    Assert.That(media == BookMediaType.Audiobook ? config.AudiobookMonitorNewItems : config.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
                    return Task.FromResult(h.Author);
                });
                var books = Proxy<IBookService>((method, args) => { if (method.Name == nameof(IBookService.GetBooksByAuthor)) catalogs++; return method.Invoke(h.BookService, args); });
                var info = Proxy<IProvideBookInfo>((method, args) => { workReads++; return Tuple.Create(h.Author.HardcoverAuthorId,
                    new Book { HardcoverBookId = (string)args[0], MediaType = (BookMediaType)args[1], Author = h.Author }, new List<Author> { source, h.Author }); });
                queries.Clear();
                var attempt = System.Diagnostics.Stopwatch.StartNew();
                var handler = new ProcessPendingImportsCommandHandler(h.Pending, library, books, info, h.Queue, h.EventAggregator, LogManager.GetCurrentClassLogger(), h.Aliases);
                handler.Execute(new ProcessPendingImportsCommand());
                Assert.That(h.Pending.GetByProviderId(h.Author.HardcoverAuthorId), Is.Not.Null);
                Assert.That(imports, Is.EqualTo(new[] { source.HardcoverAuthorId }));
                handler.Execute(new ProcessPendingImportsCommand());
                attempt.Stop();
                var commandCount = queries.Count;
                Assert.That(h.Requests.Find(request.Id), Is.Null, h.Requests.Find(request.Id)?.LastError);
                Assert.That(imports, Is.EqualTo(new[] { source.HardcoverAuthorId, h.Author.HardcoverAuthorId }));
                Assert.That(catalogs, Is.EqualTo(2));
                Assert.That(workReads, Is.EqualTo(count));
                Assert.That(h.BookService.GetBooks(targets.Select(book => book.Id)).All(book => book.AuthorId == h.Author.Id && book.IsMonitored()), Is.True);
                Assert.That(h.BookService.GetBook(opposite.Id).IsMonitored(), Is.False);
                Assert.That(h.Commands.OfType<MissingBookSearchCommand>().Single().AuthorId, Is.EqualTo(source.Id));
                Assert.That(h.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EquivalentTo(targets.Select(book => book.Id)));
                TestContext.Progress.WriteLine($"Primary routing ({(postgres ? "PostgreSQL" : "SQLite")}): {media}, {count} work/media targets, {workReads} work lookups, {imports.Count} author imports, {catalogs} catalog loads, {commandCount} SQL commands, {attempt.Elapsed.TotalMilliseconds:F1} ms handler time (synthetic metadata/library)");
            });
        }

        [Test]
        public void pending_partial_list_progress_preserves_unready_implicit_search(
            [Values(false, true)] bool postgres, [Values(false, true)] bool heldAuthorSearch)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.Author.AudiobookMonitored = heldAuthorSearch;
                h.Author.AddOptions = heldAuthorSearch ? new AddAuthorOptions { SearchForMissingBooks = true } : null;
                h.Authors.Update(h.Author);
                var ready = h.AddBook(work: "hc:7000", edition: "hc:edition:7000");
                ready.ReleaseDate = DateTime.UtcNow.AddDays(-1);
                h.Books.Update(ready);
                var pending = h.Request(new() { "hc:7000", "hc:7001" });
                pending.AudiobookBooksToSearch = null;
                pending.SearchForMissingBooks = true;
                h.Requests.Update(pending);
                h.Configure = config => config.SearchForMissingBooks = false;
                h.Run();
                Assert.That(h.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EqualTo(heldAuthorSearch ? Array.Empty<int>() : new[] { ready.Id }));
                var saved = h.Requests.Get(pending.Id);
                Assert.That(saved.SearchForMissingBooks, Is.False);
                Assert.That(saved.AudiobookBooksToSearch, Is.EqualTo(@"[""hc:7001""]"));
                if (heldAuthorSearch)
                {
                    Assert.That(h.Books.GetMissingBookSearchTargets(null, h.Author.Id).Select(target => target.BookId), Is.EqualTo(new[] { ready.Id }));
                    CompleteHeldScan(h);
                    Assert.That(h.Commands.OfType<MissingBookSearchCommand>().Count(), Is.EqualTo(1));
                    Assert.That(h.Commands.OfType<BookSearchCommand>(), Is.Empty);
                }
                var late = h.AddBook(work: "hc:7001", edition: "hc:edition:7001");
                h.Pending.RetryNow(pending.Id);
                h.Run();
                Assert.That(h.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EqualTo(heldAuthorSearch ? new[] { late.Id } : new[] { ready.Id, late.Id }));
                Assert.That(h.Requests.Find(pending.Id), Is.Null);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void pending_unready_sibling_keeps_rejected_search_and_does_not_repeat_accepted_search(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var accepted = h.AddBook(work: "hc:7000", edition: "hc:edition:7000");
                var rejected = h.AddBook(work: "hc:7001", edition: "hc:edition:7001");
                var pending = h.Request(new() { "hc:7000", "hc:7001", "hc:7002" });
                pending.RequestedBy = "UserInterface";
                Harness.MarkExplicitSearches(pending);
                h.Requests.Update(pending);
                h.RejectedBookId = rejected.Id;
                h.Run();
                Assert.That(h.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EqualTo(new[] { accepted.Id }));
                Assert.That(h.Requests.Get(pending.Id).AudiobookBooksToSearch, Is.EqualTo(@"[""hc:7001"",""hc:7002""]"));
                Assert.That(h.Requests.Get(pending.Id).IsActive(), Is.True);
                h.RejectedBookId = null;
                h.Pending.RetryNow(pending.Id);
                h.Run();
                Assert.That(h.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EqualTo(new[] { accepted.Id, rejected.Id }));
                Assert.That(h.Requests.Get(pending.Id).AudiobookBooksToSearch, Is.EqualTo(@"[""hc:7002""]"));
                Assert.That(h.Requests.Get(pending.Id).IsActive(), Is.True);
                var formerlyMissing = h.AddBook(work: "hc:7002", edition: "hc:edition:7002");
                h.Pending.RetryNow(pending.Id);
                h.Run();
                Assert.That(h.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EqualTo(new[] { accepted.Id, rejected.Id, formerlyMissing.Id }));
                Assert.That(h.Requests.Find(pending.Id), Is.Null);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void pending_partial_progress_does_not_overwrite_newer_merged_demand(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var ready = h.AddBook(work: "hc:7000", edition: "hc:edition:7000");
                var pending = h.Request(new() { "hc:7000", "hc:7001" });
                pending.RequestedBy = "UserInterface";
                h.Requests.Update(pending);
                var library = Proxy<IAuthorLibraryService>((method, args) => Task.FromResult(h.Author));
                var info = Proxy<IProvideBookInfo>((method, args) =>
                {
                    h.Pending.EnqueueAsync(h.Author.HardcoverAuthorId, new MonitoringConfig { CreateAudiobook = true, CreateEbook = false,
                        RequestedBy = "UserInterface", AudiobookBooksToMonitor = new() { "hc:7002" }, AudiobookBooksToSearch = new() { "hc:7002" } }, "UserInterface")
                        .GetAwaiter().GetResult();
                    throw new BookNotFoundException((string)args[0]);
                });
                new ProcessPendingImportsCommandHandler(h.Pending, library, h.BookService, info, h.Queue, h.EventAggregator, LogManager.GetCurrentClassLogger(), h.Aliases)
                    .Execute(new ProcessPendingImportsCommand());
                var saved = h.Requests.Get(pending.Id);
                Assert.That(saved.Version, Is.GreaterThan(pending.Version));
                Assert.That(saved.IsActive(), Is.True);
                Assert.That(JsonConvert.DeserializeObject<List<string>>(saved.AudiobookBooksToMonitor), Is.EquivalentTo(new[] { "hc:7001", "hc:7002" }));
                Assert.That(JsonConvert.DeserializeObject<List<string>>(saved.AudiobookBooksToSearch), Does.Contain("hc:7002"));
                Assert.That(h.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EqualTo(new[] { ready.Id }));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void legacy_original_unavailable_rows_cannot_distinguish_book_preferences_from_merged_author_demand(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var pending = new PendingAuthorImportService(h.Requests,
                    Proxy<IAuthorService>((method, args) => null), h.EventAggregator, LogManager.GetCurrentClassLogger(), h.Aliases);
                MonitoringConfig Config(bool book) => new() { CreateAudiobook = true, CreateEbook = false,
                    AudiobookMonitored = true, AudiobookMonitorExistingMode = MonitorTypes.All, AudiobookMonitorNewItems = NewItemMonitorTypes.All,
                    AudiobookQualityProfileId = 1, AudiobookMetadataProfileId = 1, AudiobookRootFolderPath = Path.GetTempPath(),
                    RequestedBy = "UserInterface", AuthorName = "Reported Contributor",
                    AudiobookBooksToMonitor = book ? new List<string> { "hc:7000" } : null,
                    AudiobookBooksToSearch = book ? new List<string> { "hc:7000" } : null };
                var id = pending.EnqueueAsync("hc:2222", Config(true), "UserInterface").GetAwaiter().GetResult();
                var before = JsonConvert.SerializeObject(h.Requests.Find(id));
                Assert.That(pending.EnqueueAsync("hc:2222", Config(false), "UserInterface").GetAwaiter().GetResult(), Is.EqualTo(id));
                Assert.That(JsonConvert.SerializeObject(h.Requests.Find(id)), Is.EqualTo(before));
                var calls = 0;
                var library = Proxy<IAuthorLibraryService>((method, args) => Task.FromException<Author>(new AuthorNotFoundException((string)args[0])));
                var info = Proxy<IProvideBookInfo>((method, args) => { calls++; return Tuple.Create(h.Author.HardcoverAuthorId,
                    new Book { HardcoverBookId = "hc:7000", MediaType = BookMediaType.Audiobook, Author = h.Author }, new List<Author> { h.Author }); });
                new ProcessPendingImportsCommandHandler(pending, library, h.BookService, info, h.Queue, h.EventAggregator, LogManager.GetCurrentClassLogger(), h.Aliases)
                    .Execute(new ProcessPendingImportsCommand());
                Assert.That(h.Requests.Find(id).IsActive(), Is.True);
                Assert.That(h.Requests.Find(id).ProviderId, Is.EqualTo("hc:2222"));
                Assert.That(calls, Is.EqualTo(1));
                Assert.That(h.Commands.OfType<BookSearchCommand>(), Is.Empty);
                TestContext.Progress.WriteLine($"Legacy demand ({(postgres ? "PostgreSQL" : "SQLite")}): complete book-only row equals independently merged author-demand row; original-unavailable request retained. Row={before}");
            });
        }
    }
}
