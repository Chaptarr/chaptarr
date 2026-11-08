using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Common;
using NLog;
using Newtonsoft.Json;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.MetadataSource;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Books
{
    public partial class PersistedAliasPendingLifecycleFixture
    {
        [Test]
        public void original_author_outcome_does_not_discard_independent_work_progress(
            [Values(false, true)] bool postgres,
            [Values("ready", "unknown-work", "search-rejected", "primary-unready", "same-terminal-primary", "source-side-excluded")] string scenario)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                if (scenario == "source-side-excluded")
                {
                    global::Chaptarr.Core.Test.ImportLists.ImportListMissingMediaSidePersistenceFixture.VerifyIndependentSourceExclusion(database);
                    return;
                }
                var queries = new List<string>();
                var observed = Proxy<IMainDatabase>((method, args) => method.Name == nameof(IMainDatabase.OpenConnection)
                    ? CommandObserver.Wrap<IDbConnection>(database.OpenConnection(), command => queries.Add(command.CommandText))
                    : method.Invoke(database, args));
                var h = new Harness(observed);
                var source = h.Authors.Insert(new Author
                {
                    Name = "Reported Contributor", CleanName = "reportedcontributor", SortName = "reportedcontributor",
                    SortNameLastFirst = "Contributor, Reported", TitleSlug = Guid.NewGuid().ToString("N"),
                    Path = @"C:\Audio\Reported Contributor", HardcoverAuthorId = "hc:2222", Added = DateTime.UtcNow
                });
                var audio = h.AddBook(BookMediaType.Audiobook, "hc:7000", "hc:edition:3000");
                var ebook = h.AddBook(BookMediaType.Ebook, "hc:7000", "hc:edition:4000");
                var unrelated = h.AddBook(BookMediaType.Audiobook, "hc:7999", "hc:edition:4999");
                var casts = new List<RequestedNarration>
                {
                    new() { WorkProviderId = "hc:7000", EditionProviderId = "hc:edition:3000", MediaType = BookMediaType.Audiobook,
                        NarratorNames = new() { "Fixture Narrator" }, RequiresEdition = true, Search = true },
                    new() { WorkProviderId = "hc:7000", EditionProviderId = "hc:edition:4000", MediaType = BookMediaType.Ebook,
                        RequiresEdition = true, Search = true }
                };
                var works = new List<string> { "hc:7000" };
                if (scenario == "unknown-work") works.Add("hc:7001");
                var item = h.Request(works, casts, ebook: true);
                item.ProviderId = source.HardcoverAuthorId;
                item.RequestedBy = "UserInterface";
                item.AudiobookMonitorExistingMode = item.EbookMonitorExistingMode = MonitorTypes.All;
                item.AudiobookMonitorNewItems = item.EbookMonitorNewItems = NewItemMonitorTypes.All;
                item.SearchForMissingBooks = true;
                item.EbookBooksToSearch = JsonConvert.SerializeObject(works);
                item.AudiobookQualityProfileId = item.EbookQualityProfileId = 1;
                item.AudiobookMetadataProfileId = item.EbookMetadataProfileId = 1;
                Harness.MarkExplicitSearches(item);
                h.Requests.Update(item);
                var imports = new List<string>();
                var catalogReads = 0;
                var workReads = new List<(string Work, BookMediaType Media)>();
                var primaryReady = scenario != "primary-unready";
                var library = Proxy<IAuthorLibraryService>((method, args) =>
                {
                    var provider = (string)args[0];
                    var config = (MonitoringConfig)args[1];
                    imports.Add(provider);
                    if (provider == source.HardcoverAuthorId)
                    {
                        Assert.That(config.SearchForMissingBooks, Is.True);
                        return Task.FromException<Author>(new AuthorTerminalException("author_provider_record_missing", provider, null,
                            "The source author record is unavailable.", true));
                    }
                    Assert.That(provider, Is.EqualTo(h.Author.HardcoverAuthorId));
                    Assert.That(config.SearchForMissingBooks, Is.False);
                    Assert.That(config.AudiobookMonitorExistingMode, Is.EqualTo(MonitorTypes.SpecificBook));
                    Assert.That(config.EbookMonitorExistingMode, Is.EqualTo(MonitorTypes.SpecificBook));
                    Assert.That(config.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
                    Assert.That(config.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
                    if (!primaryReady) return Task.FromException<Author>(new AuthorNotFoundException(provider));
                    var catalog = h.BookService.GetBooksByAuthor(h.Author.Id);
                    config.PreviouslyMonitoredEditions = catalog.Where(book => book.IsMonitored())
                        .ToDictionary(book => book.Id, book => BookEditionIdentity.GetMonitoredEdition(book)?.Id);
                    AuthorLibraryService.PrepareRequestedNarrations(new Author { Books = catalog }, config);
                    return Task.FromResult(h.Author);
                });
                var books = Proxy<IBookService>((method, args) =>
                {
                    if (method.Name == nameof(IBookService.GetBooksByAuthor)) catalogReads++;
                    return method.Invoke(h.BookService, args);
                });
                var info = Proxy<IProvideBookInfo>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(IProvideBookInfo.GetWorkInfo)));
                    var work = (string)args[0];
                    var media = (BookMediaType)args[1];
                    workReads.Add((work, media));
                    if (work == "hc:7001") throw new BookNotFoundException(work);
                    var owner = scenario == "same-terminal-primary" ? source : h.Author;
                    return Tuple.Create(owner.HardcoverAuthorId,
                        new Book { HardcoverBookId = work, MediaType = media, Author = owner }, new List<Author> { source, h.Author });
                });
                void Run() => new ProcessPendingImportsCommandHandler(h.Pending, library, books, info, h.Queue,
                    h.EventAggregator, LogManager.GetCurrentClassLogger(), h.Aliases).Execute(new ProcessPendingImportsCommand());
                if (scenario == "search-rejected") h.RejectedBookId = audio.Id;
                queries.Clear();
                var timer = System.Diagnostics.Stopwatch.StartNew();
                Run();
                timer.Stop();
                var firstQueries = queries.Count;
                var firstWorkReads = workReads.Count;
                var firstImports = imports.Count;
                var firstCatalogReads = catalogReads;
                var destination = h.Pending.GetByProviderId(h.Author.HardcoverAuthorId);
                if (scenario != "same-terminal-primary")
                {
                    Assert.That(destination, Is.Not.Null);
                    Run();
                }
                var saved = h.Requests.Get(item.Id);
                if (scenario != "same-terminal-primary")
                {
                    Assert.That(workReads, Does.Contain(("hc:7000", BookMediaType.Audiobook)));
                    Assert.That(workReads, Does.Contain(("hc:7000", BookMediaType.Ebook)));
                }
                Assert.That(saved.ProviderId, Is.EqualTo(source.HardcoverAuthorId));
                Assert.That(saved.SearchForMissingBooks, Is.True);
                Assert.That(saved.AudiobookMonitorExistingMode, Is.EqualTo(MonitorTypes.All));
                Assert.That(saved.EbookMonitorExistingMode, Is.EqualTo(MonitorTypes.All));
                Assert.That(h.BookService.GetBook(unrelated.Id).IsMonitored(), Is.False);
                if (scenario == "same-terminal-primary")
                {
                    Assert.That(saved.OverallStatus, Is.EqualTo(PendingImportStatus.Failed));
                    Assert.That(saved.LastError, Does.Contain("author_provider_record_missing"));
                    Assert.That(imports, Is.EqualTo(new[] { source.HardcoverAuthorId }));
                    Assert.That(h.Commands.OfType<BookSearchCommand>(), Is.Empty);
                    Assert.That(h.Editions.Get(h.Editions.All().Single(edition => edition.BookId == audio.Id).Id).ManualAdd, Is.False);
                }
                else if (scenario == "primary-unready")
                {
                    Assert.That(saved.OverallStatus, Is.EqualTo(PendingImportStatus.Failed));
                    Assert.That(h.Requests.Get(destination.Id).IsActive(), Is.True);
                    Assert.That(JsonConvert.DeserializeObject<List<string>>(h.Requests.Get(destination.Id).AudiobookBooksToSearch), Does.Contain("hc:7000"));
                    Assert.That(h.Commands.OfType<BookSearchCommand>(), Is.Empty);
                    primaryReady = true;
                    h.Pending.RetryNow(destination.Id);
                    Run();
                    Assert.That(h.Requests.Get(item.Id).OverallStatus, Is.EqualTo(PendingImportStatus.Failed));
                }
                else
                {
                    Assert.That(h.BookService.GetBooks(new[] { audio.Id, ebook.Id }).All(book => book.AuthorId == h.Author.Id && book.IsMonitored()), Is.True);
                    Assert.That(h.Editions.All().Where(edition => edition.BookId == audio.Id || edition.BookId == ebook.Id).All(edition => edition.ManualAdd), Is.True);
                    if (scenario == "unknown-work")
                    {
                        Assert.That(saved.IsActive(), Is.True);
                        Assert.That(saved.LastError, Does.Contain("hc:7001"));
                        Assert.That(JsonConvert.DeserializeObject<List<string>>(saved.AudiobookBooksToSearch), Is.EqualTo(new[] { "hc:7001" }));
                        Assert.That(JsonConvert.DeserializeObject<List<string>>(saved.EbookBooksToSearch), Is.EqualTo(new[] { "hc:7001" }));
                    }
                    else if (scenario == "search-rejected")
                    {
                        var retrying = h.Requests.Get(destination.Id);
                        Assert.That(saved.OverallStatus, Is.EqualTo(PendingImportStatus.Failed));
                        Assert.That(retrying.IsActive(), Is.True);
                        Assert.That(JsonConvert.DeserializeObject<List<string>>(retrying.AudiobookBooksToSearch), Is.EqualTo(new[] { "hc:7000" }));
                        Assert.That(JsonConvert.DeserializeObject<List<string>>(retrying.EbookBooksToSearch), Is.Empty);
                        Assert.That(JsonConvert.DeserializeObject<List<RequestedNarration>>(retrying.AudiobookNarrations).Single(request => request.MediaType == BookMediaType.Audiobook).Search, Is.True);
                        h.RejectedBookId = null;
                        h.Pending.RetryNow(destination.Id);
                        Run();
                        Assert.That(h.Requests.Get(item.Id).OverallStatus, Is.EqualTo(PendingImportStatus.Failed));
                        Assert.That(JsonConvert.DeserializeObject<List<string>>(h.Requests.Get(item.Id).AudiobookBooksToSearch), Is.Empty);
                    }
                    else
                    {
                        Assert.That(saved.OverallStatus, Is.EqualTo(PendingImportStatus.Failed));
                        Assert.That(JsonConvert.DeserializeObject<List<string>>(saved.AudiobookBooksToSearch), Is.Empty);
                        Assert.That(JsonConvert.DeserializeObject<List<string>>(saved.EbookBooksToSearch), Is.Empty);
                    }
                }
                if (scenario != "same-terminal-primary")
                    Assert.That(h.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EquivalentTo(new[] { audio.Id, ebook.Id }));
                Assert.That(h.Commands.OfType<MissingBookSearchCommand>().Select(command => command.AuthorId),
                    Is.Empty);
                Assert.That(h.Requests.All().Select(request => request.Id), Is.EqualTo(new[] { item.Id }));
                TestContext.Progress.WriteLine($"Original unavailable routing ({(postgres ? "PostgreSQL" : "SQLite")}): {scenario}; first attempt {firstWorkReads} work/media lookups, {firstImports} author calls, {firstCatalogReads} handler catalog reads, {firstQueries} SQL commands, {timer.Elapsed.TotalMilliseconds:F1} ms; metadata/library transport is synthetic, writers/reload are native.");
            });
        }
    }
}

namespace Chaptarr.Core.Test.ImportLists
{
    public partial class ImportListMissingMediaSidePersistenceFixture
    {
        internal static void VerifyIndependentSourceExclusion(IMainDatabase database)
        {
            var stored = new StoredListLibrary(database);
            var owner = stored.Authors.Insert(new Author
            {
                Name = "Selected Primary", CleanName = "selectedprimary", SortName = "selectedprimary",
                SortNameLastFirst = "Primary, Selected", TitleSlug = "selected-primary", Path = @"C:\Audio\Selected Primary",
                Added = DateTime.UtcNow, GoodreadsAuthorId = "gr:58", AudiobookQualityProfileId = 9, EbookQualityProfileId = 9,
                AudiobookMetadataProfileId = 8, EbookMetadataProfileId = 8,
                AudiobookRootFolderPath = stored.Author.AudiobookRootFolderPath, EbookRootFolderPath = stored.Author.EbookRootFolderPath,
                AudiobookMonitored = false, EbookMonitored = false
            });
            var sources = new[] { BookMediaType.Audiobook, BookMediaType.Ebook }.Select(side => CatalogBook(side, "Routed work", "7000")).ToList();
            foreach (var book in sources)
            {
                book.AuthorId = owner.Id;
                book.Author = owner;
                stored.BookRows.Insert(book);
                foreach (var edition in book.Editions) edition.BookId = book.Id;
                stored.Editions.InsertMany(book.Editions);
            }
            var exclusions = new NzbDrone.Core.ImportLists.Exclusions.ImportListExclusionService(
                new NzbDrone.Core.ImportLists.Exclusions.ImportListExclusionRepository(stored.Database, stored.Events), stored.Logger);
            exclusions.Add(new NzbDrone.Core.ImportLists.Exclusions.ImportListExclusion
            {
                ForeignId = stored.Author.GoodreadsAuthorId, MediaType = BookMediaType.Audiobook, Name = stored.Author.Name
            });
            var requested = sources.Select(book => new RequestedNarration
            {
                MediaType = book.MediaType, WorkProviderId = book.GoodreadsWorkId, EditionProviderId = book.Editions.Single().ForeignEditionId,
                NarratorNames = book.MediaType == BookMediaType.Audiobook ? new() { "Fixture Narrator" } : null, RequiresEdition = true, Search = true
            }).ToList();
            var pending = stored.Requests.Insert(new PendingAuthorImport
            {
                ProviderId = stored.Author.GoodreadsAuthorId, ProviderPrefix = "gr", AuthorName = stored.Author.Name,
                RequestedBy = "ImportList:Independent source", CreatedAt = DateTime.UtcNow, NextAttemptAt = DateTime.UtcNow.AddMinutes(-1), Version = 1,
                OverallStatus = PendingImportStatus.Pending, AudiobookStatus = PendingImportStatus.Pending, EbookStatus = PendingImportStatus.Pending,
                AudiobookBooksToMonitor = "[\"gr:7000\"]", AudiobookBooksToSearch = "[\"gr:7000\"]",
                EbookBooksToMonitor = "[\"gr:7000\"]", EbookBooksToSearch = "[\"gr:7000\"]",
                AudiobookNarrations = JsonConvert.SerializeObject(requested), SearchForMissingBooks = true,
                AudiobookQualityProfileId = 9, EbookQualityProfileId = 9, AudiobookMetadataProfileId = 8, EbookMetadataProfileId = 8,
                AudiobookRootFolderPath = stored.Author.AudiobookRootFolderPath, EbookRootFolderPath = stored.Author.EbookRootFolderPath
            });
            var authorLookups = new List<string>();
            var authorInfo = Proxy<IProvideAuthorInfo>((_, args) =>
            {
                authorLookups.Add((string)args[0]);
                var source = (string)args[0] == owner.GoodreadsAuthorId ? owner : stored.Author;
                return new Author
                {
                    Name = source.Name, GoodreadsAuthorId = source.GoodreadsAuthorId,
                    Books = source == owner ? sources.Select(book => CatalogBook(book.MediaType, book.Title, "7000")).ToList() : new(),
                    Series = new List<Series>()
                };
            });
            var library = new AuthorLibraryService(stored.AuthorService, authorInfo, stored.Books, null, stored.Editions, null,
                new global::Chaptarr.Core.Test.Books.AuthorLibraryServiceSpecificBookMonitoringFixture.StubMetadataProfileService(),
                new global::Chaptarr.Core.Test.TestQualityProfileService(),
                stored.Paths, stored.Roots, stored.Queue, stored.Events, stored.Pending, stored.Database, exclusions,
                new EditionMetadataProfileFilter(new global::Chaptarr.Core.Test.Books.TestTermMatcherService()), null,
                stored.Logger, new EditionSelector(stored.Logger));
            var workReads = new List<BookMediaType>();
            var info = Proxy<IProvideBookInfo>((_, args) =>
            {
                var media = (BookMediaType)args[1];
                workReads.Add(media);
                var book = CatalogBook(media, "Routed work", "7000");
                book.Author = owner;
                return Tuple.Create(owner.GoodreadsAuthorId, book, new List<Author> { owner });
            });
            new ProcessPendingImportsCommandHandler(stored.Pending, library, stored.Books, info, stored.Queue,
                stored.Events, stored.Logger, stored.Aliases).Execute(new ProcessPendingImportsCommand());
            Assert.That(stored.Pending.GetByProviderId(owner.GoodreadsAuthorId), Is.Not.Null);
            new ProcessPendingImportsCommandHandler(stored.Pending, library, stored.Books, info, stored.Queue,
                stored.Events, stored.Logger, stored.Aliases).Execute(new ProcessPendingImportsCommand());
            var saved = stored.Requests.Get(pending.Id);
            var catalog = stored.Books.GetBooksByAuthor(owner.Id);
            Assert.Multiple(() =>
            {
                Assert.That(workReads, Is.EquivalentTo(new[] { BookMediaType.Audiobook, BookMediaType.Ebook }));
                Assert.That(authorLookups, Is.EqualTo(new[] { stored.Author.GoodreadsAuthorId, owner.GoodreadsAuthorId }));
                Assert.That(saved.OverallStatus, Is.EqualTo(PendingImportStatus.PartialSuccess));
                Assert.That(saved.AudiobookStatus, Is.EqualTo(PendingImportStatus.Failed));
                Assert.That(saved.EbookStatus, Is.EqualTo(PendingImportStatus.Succeeded));
                Assert.That(saved.LastError, Does.Contain("excluded").And.Contain(stored.Author.GoodreadsAuthorId));
                Assert.That(catalog, Has.Count.EqualTo(2));
                Assert.That(catalog.All(book => book.IsMonitored() && book.Editions.Single().ManualAdd), Is.True);
                Assert.That(stored.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EquivalentTo(catalog.Select(book => book.Id)));
                Assert.That(stored.Commands.OfType<MissingBookSearchCommand>(), Is.Empty);
                Assert.That(stored.Requests.All().Select(request => request.Id), Is.EqualTo(new[] { pending.Id }));
            });
        }
    }
}
