using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Common;
using Dapper;
using FluentValidation;
using FluentValidation.Results;
using NLog;
using Newtonsoft.Json;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaCover.Commands;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Common.Cache;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    [NonParallelizable]
    public partial class PersistedAliasPendingLifecycleFixture
    {
        private sealed class Harness
        {
            public readonly IMainDatabase Database;
            public readonly AuthorRepository Authors;
            public readonly BookRepository Books;
            public readonly EditionRepository Editions;
            public readonly ProviderAliasSeed Aliases;
            public readonly PendingAuthorImportRepository Requests;
            public readonly PendingAuthorImportService Pending;
            public readonly BookService BookService;
            public readonly EditionService EditionService;
            public readonly List<Command> Commands = new();
            public readonly List<object> Events = new();
            public readonly Author Author;
            public readonly IAuthorService AuthorService;
            public readonly IEventAggregator EventAggregator;
            public readonly IManageCommandQueue Queue;
            public int? RejectedBookId { get; set; }
            public int? RejectedEditionId { get; set; }
            public bool RejectCoverCommand { get; set; }
            public bool RejectRefreshCommand { get; set; }
            public int CatalogReads;
            public int AliasReads;
            public int RescueReads;
            public int AuthorFetches;
            public int SingleOwnerReads;
            public int BulkOwnerReads;
            public bool WorkReady { get; set; }
            public Action<MonitoringConfig> Configure { get; set; }
            public Action<object> EventPublished { get; set; }

            public Harness(IMainDatabase database)
            {
                Database = database;
                var logger = LogManager.GetCurrentClassLogger();
                EventAggregator = Proxy<IEventAggregator>((method, args) => { Events.Add(args[0]); EventPublished?.Invoke(args[0]); return null; });
                Authors = new AuthorRepository(database, EventAggregator, logger);
                Books = new BookRepository(database, EventAggregator);
                Editions = new EditionRepository(database, EventAggregator);
                Aliases = new ProviderAliasSeed(database, EventAggregator);
                Requests = new PendingAuthorImportRepository(database, EventAggregator);
                Author = Authors.Insert(new Author
                {
                    Name = "Fiona Cole", CleanName = "fionacole", SortName = "fionacole", SortNameLastFirst = "Cole, Fiona",
                    TitleSlug = Guid.NewGuid().ToString("N"), Path = @"C:\Audio\Fiona Cole", Added = DateTime.UtcNow,
                    HardcoverAuthorId = "hc:9001", AudiobookRootFolderPath = @"C:\Audio", EbookRootFolderPath = @"C:\Ebooks",
                    AudiobookQualityProfileId = 1, AudiobookMetadataProfileId = 1, EbookQualityProfileId = 1, EbookMetadataProfileId = 1,
                    AudiobookMonitored = false, EbookMonitored = false
                });
                AuthorService = Proxy<IAuthorService>((method, args) => method.Name switch
                {
                    nameof(IAuthorService.GetAuthor) => ReadOwner((int)args[0]),
                    nameof(IAuthorService.GetAuthors) => Authors.Get((IEnumerable<int>)args[0]).ToList(),
                    nameof(IAuthorService.GetExistingAuthors) => ReadOwners((IEnumerable<int>)args[0]),
                    nameof(IAuthorService.FindByProviderId) => Author,
                    _ => throw new AssertionException("Unexpected author operation " + method.Name)
                });
                Pending = new PendingAuthorImportService(Requests, AuthorService, EventAggregator, logger, Aliases);
                EditionService = new EditionService(Editions, EventAggregator, logger);
                BookService = new BookService(Books, EditionService, EventAggregator, AuthorService,
                    Proxy<IMediaFileService>((method, args) => new List<BookFile>()), null,
                    new SeriesBookLinkRepository(database, EventAggregator), null, logger, providerAliasService: Aliases);
                Queue = Proxy<IManageCommandQueue>((method, args) =>
                {
                    var command = (Command)args[0];
                    command.Trigger = (CommandTrigger)args[2];
                    if (command is BookSearchCommand search && search.BookIds.Contains(RejectedBookId ?? 0))
                        throw new InvalidOperationException("Injected queue rejection");
                    if (command is DownloadBookMediaCommand && RejectCoverCommand || command is RefreshAuthorCommand && RejectRefreshCommand)
                        throw new InvalidOperationException("Injected ancillary queue rejection");
                    Commands.Add(command);
                    return new CommandModel { Id = Commands.Count, Body = command };
                });
            }

            private Author ReadOwner(int id)
            {
                SingleOwnerReads++;
                return Authors.Find(id);
            }

            private List<Author> ReadOwners(IEnumerable<int> ids)
            {
                BulkOwnerReads++;
                return Authors.FindExisting(ids);
            }

            public Book AddBook(BookMediaType media = BookMediaType.Audiobook, string work = "hc:698880", string edition = "hc:edition:2001")
            {
                var book = Books.Insert(new Book
                {
                    AuthorId = Author.Id, Title = "Lovers", CleanTitle = "lovers", TitleSlug = Guid.NewGuid().ToString("N"),
                    HardcoverBookId = work, GoodreadsWorkId = work == "hc:698880" ? "gr:63450459" : null, MediaType = media, Added = DateTime.UtcNow,
                    AnyEditionOk = true, AudiobookMonitored = false, EbookMonitored = false
                });
                EditionService.InsertMany(new List<Edition> { new()
                {
                    BookId = book.Id, Title = "Lovers", ForeignEditionId = edition, TitleSlug = Guid.NewGuid().ToString("N"),
                    ReadingFormatId = media == BookMediaType.Audiobook ? 2 : 3, Narrator = "Fixture Narrator", Language = "eng", Monitored = true
                } });
                return book;
            }

            public PendingAuthorImport Request(List<string> workIds, List<RequestedNarration> narrations = null, bool ebook = false)
            {
                return Requests.Insert(new PendingAuthorImport
                {
                    ProviderId = "hc:9001", ProviderPrefix = "hc", AuthorName = Author.Name, RequestedBy = "ImportList:Shelf",
                    AudiobookStatus = PendingImportStatus.Pending, EbookStatus = ebook ? PendingImportStatus.Pending : PendingImportStatus.NotRequested,
                    OverallStatus = PendingImportStatus.Pending, CreatedAt = DateTime.UtcNow, NextAttemptAt = DateTime.UtcNow.AddMinutes(-1), Version = 1,
                    AudiobookBooksToMonitor = JsonConvert.SerializeObject(workIds), AudiobookBooksToSearch = JsonConvert.SerializeObject(workIds),
                    AudiobookNarrations = narrations == null ? null : JsonConvert.SerializeObject(narrations),
                    EbookBooksToMonitor = ebook ? JsonConvert.SerializeObject(workIds) : null,
                    AudiobookRootFolderPath = @"C:\Audio", EbookRootFolderPath = @"C:\Ebooks"
                });
            }

            public static void MarkExplicitSearches(PendingAuthorImport item)
            {
                var narrations = JsonConvert.DeserializeObject<List<RequestedNarration>>(item.AudiobookNarrations ?? "[]") ?? new();
                var searches = narrations.Where(n => n.Search).Select(n => new RequestedBookSearch
                    { MediaType = n.MediaType, WorkProviderId = n.WorkProviderId, EditionProviderId = n.EditionProviderId }).ToList();
                foreach (var media in new[] { BookMediaType.Audiobook, BookMediaType.Ebook })
                    searches.AddRange((JsonConvert.DeserializeObject<List<string>>((media == BookMediaType.Audiobook
                        ? item.AudiobookBooksToSearch : item.EbookBooksToSearch) ?? "[]") ?? new())
                        .Where(id => !narrations.Any(n => n.MediaType == media && n.WorkProviderId == id))
                        .Select(id => new RequestedBookSearch { MediaType = media, WorkProviderId = id }));
                item.ExplicitBookSearches = RequestedBookSearch.Write(searches);
            }

            public void Run()
            {
                var books = Proxy<IBookService>((method, args) =>
                {
                    if (method.Name == nameof(IBookService.GetBooksByAuthor)) CatalogReads++;
                    if (method.Name == nameof(IBookService.AddWantedEditions) && RejectedEditionId.HasValue)
                    {
                        var requested = (List<Edition>)args[0];
                        var rejected = requested.Single(edition => edition.Id == RejectedEditionId);
                        var callback = (Action<Edition, Exception>)args[1];
                        callback(rejected, new ValidationException(new[] { new ValidationFailure("Edition", "Injected narration rejection") }));
                        return BookService.AddWantedEditions(requested.Where(edition => edition.Id != rejected.Id).ToList(), callback);
                    }
                    return method.Invoke(BookService, args);
                });
                var aliases = Proxy<IProviderAliasService>((method, args) =>
                {
                    if (method.Name == nameof(IProviderAliasService.FindBookAliases)) AliasReads++;
                    return method.Invoke(Aliases, args);
                });
                var library = Proxy<IAuthorLibraryService>((method, args) =>
                {
                    AuthorFetches++;
                    var config = (MonitoringConfig)args[1];
                    var catalog = BookService.GetBooksByAuthor(Author.Id);
                    config.PreviouslyMonitoredEditions = catalog.Where(book => book.IsMonitored()).ToDictionary(book => book.Id, book => BookEditionIdentity.GetMonitoredEdition(book)?.Id);
                    AuthorLibraryService.PrepareRequestedNarrations(new Author { Books = catalog }, config);
                    Configure?.Invoke(config);
                    return Task.FromResult(Author);
                });
                var info = Proxy<IProvideBookInfo>((method, args) =>
                {
                    RescueReads++;
                    if (!WorkReady) throw new BookNotFoundException((string)args[0]);
                    return Tuple.Create(Author.HardcoverAuthorId, new Book { MediaType = (BookMediaType)args[1], Author = Author,
                        RemoteProviderIds = new HashSet<string> { (string)args[0] } }, new List<Author> { Author });
                });
                new ProcessPendingImportsCommandHandler(Pending, library, books, info, Queue, EventAggregator,
                    LogManager.GetCurrentClassLogger(), aliases).Execute(new ProcessPendingImportsCommand());
            }
        }

        [Test]
        public void book_hydration_batches_cold_owner_identity_and_settings(
            [Values(false, true)] bool postgres, [Values(1, 20)] int ownerCount)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var queries = new List<string>();
                var observed = Proxy<IMainDatabase>((method, args) => method.Name == nameof(IMainDatabase.OpenConnection)
                    ? CommandObserver.Wrap<IDbConnection>(database.OpenConnection(), command => queries.Add(command.CommandText))
                    : method.Invoke(database, args));
                var h = new Harness(observed);
                var ids = new List<int>();
                for (var index = 0; index < ownerCount; index++)
                {
                    var owner = index == 0 ? h.Author : h.Authors.Insert(new Author
                    {
                        Name = "Selected Primary " + index, CleanName = "primary" + index, SortName = "primary" + index,
                        SortNameLastFirst = "primary" + index, Path = @"C:\Audio\Primary" + index,
                        TitleSlug = Guid.NewGuid().ToString("N"), HardcoverAuthorId = "hc:" + (9001 + index), Added = DateTime.UtcNow
                    });
                    ids.Add(h.Books.Insert(new Book { AuthorId = owner.Id, Title = "Requested Work " + index,
                        CleanTitle = "requestedwork" + index, TitleSlug = Guid.NewGuid().ToString("N"), HardcoverBookId = "hc:" + (7000 + index),
                        MediaType = BookMediaType.Audiobook, Added = DateTime.UtcNow }).Id);
                }
                var expectedAuthorIds = h.Authors.All().Select(author => author.Id).ToList();
                queries.Clear();
                var hydrated = h.BookService.GetExistingBooks(ids);
                var ownerProjection = hydrated.Select(book => new
                {
                    book.Author.Id, book.Author.Name,
                    ProviderId = AuthorIdentity.GetPreferredProviderId(book.Author),
                    book.Author.AudiobookRootFolderPath, book.Author.EbookRootFolderPath,
                    book.Author.AudiobookQualityProfileId, book.Author.EbookQualityProfileId,
                    book.Author.AudiobookMetadataProfileId, book.Author.EbookMetadataProfileId
                }).ToList();
                var authorQueries = queries.Count(query => System.Text.RegularExpressions.Regex.IsMatch(query, @"FROM\s+[""`]?Authors[""`]?\s", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
                TestContext.Progress.WriteLine($"Routing identity hydrate ({(postgres ? "PostgreSQL" : "SQLite")}): {ownerCount} owners, {h.SingleOwnerReads} single owner calls, {h.BulkOwnerReads} bulk owner calls, {authorQueries} author SELECT statements, {queries.Count} total SQL statements");
                Assert.Multiple(() =>
                {
                    Assert.That(ownerProjection.Select(author => author.Id), Is.EquivalentTo(expectedAuthorIds));
                    Assert.That(ownerProjection.Select(author => author.ProviderId), Is.All.Not.Null);
                    Assert.That(h.SingleOwnerReads, Is.Zero);
                    Assert.That(h.BulkOwnerReads, Is.EqualTo(1));
                    Assert.That(authorQueries, Is.EqualTo(1));
                });
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void existing_author_batch_and_empty_book_hydration_issue_no_sql(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var queries = new List<string>();
                var observed = Proxy<IMainDatabase>((method, args) => method.Name == nameof(IMainDatabase.OpenConnection)
                    ? CommandObserver.Wrap<IDbConnection>(database.OpenConnection(), command => queries.Add(command.CommandText))
                    : method.Invoke(database, args));
                var h = new Harness(observed);
                var authors = new AuthorService(h.Authors, h.EventAggregator, null, null, h.Queue, new CacheManager(), h.Books, null,
                    LogManager.GetCurrentClassLogger(), null, null, new Lazy<IBookService>(() => h.BookService), mainDatabase: h.Database, h.Aliases);
                queries.Clear();
                Assert.That(authors.GetExistingAuthors(Array.Empty<int>()), Is.Empty);
                Assert.That(h.BookService.GetExistingBooks(Array.Empty<int>()), Is.Empty);
                Assert.That(queries, Is.Empty);
                TestContext.Progress.WriteLine($"Empty author/book hydrate ({(postgres ? "PostgreSQL" : "SQLite")}): zero SQL statements");
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void bulk_book_hydration_preserves_missing_author_exception(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var book = h.AddBook();
                var loaded = h.Books.Get(book.Id);
                h.Editions.DeleteMany(h.Editions.All().ToList());
                h.Books.Delete(book.Id);
                h.Authors.Delete(h.Author.Id);
                var snapshot = Proxy<IBookRepository>((method, args) => method.Name is nameof(IBookRepository.Get) or nameof(IBookRepository.FindExisting)
                    ? new List<Book> { loaded } : method.Invoke(h.Books, args));
                var authors = new AuthorService(h.Authors, h.EventAggregator, null, null, h.Queue, new CacheManager(), h.Books, null,
                    LogManager.GetCurrentClassLogger(), null, null, new Lazy<IBookService>(() => h.BookService), mainDatabase: h.Database, h.Aliases);
                var books = new BookService(snapshot, h.EditionService, h.EventAggregator, authors,
                    Proxy<IMediaFileService>((method, args) => new List<BookFile>()), null,
                    new SeriesBookLinkRepository(database, h.EventAggregator), null, LogManager.GetCurrentClassLogger(), providerAliasService: h.Aliases);
                Assert.That(authors.GetExistingAuthors(new[] { h.Author.Id }), Is.Empty);
                Assert.Throws<ModelNotFoundException>(() => books.GetExistingBooks(new[] { book.Id }));
                Assert.Throws<ModelNotFoundException>(() => books.GetBooks(new[] { book.Id }));
                TestContext.Progress.WriteLine($"Missing positive author ({(postgres ? "PostgreSQL" : "SQLite")}): strict and existing-book hydrations retain ModelNotFoundException");
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void pending_secondary_work_alias_survives_reload_without_rescue_and_stays_author_media_scoped(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var requested = h.AddBook();
                var opposite = h.AddBook(BookMediaType.Ebook);
                h.Aliases.SeedAliases("Book", requested.Id, "work", new[] { "hc:698880", "hc:1057591" });
                h.Aliases.SeedAliases("Book", opposite.Id, "edition", new[] { "hc:1057591" });
                h.Request(new List<string> { "hc:1057591" });
                h.Run();
                Assert.Multiple(() =>
                {
                    Assert.That(h.Requests.All(), Is.Empty);
                    Assert.That(h.Books.Get(requested.Id).IsMonitored(), Is.True);
                    Assert.That(h.Books.Get(opposite.Id).IsMonitored(), Is.False);
                    Assert.That(h.RescueReads, Is.Zero);
                    Assert.That(h.CatalogReads, Is.EqualTo(1));
                    Assert.That(h.AliasReads, Is.EqualTo(1));
                    Assert.That(h.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EqualTo(new[] { requested.Id }));
                });
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void edition_alias_gain_withdrawal_and_regional_asin_survive_reload_and_narrow_pin_writes(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var book = h.AddBook();
                var edition = h.Editions.FindRowsByBook(new[] { book.Id }).Single();
                edition.RemoteProviderIds = new HashSet<string> { "gr:44767458", "hc:edition:2001" };
                edition.Asins = new List<string> { "B00REGION1", "B00REGION2" };
                h.EditionService.UpdateMany(new List<Edition> { edition });
                var reopened = new EditionRepository(database, h.EventAggregator);
                Assert.That(reopened.FindAllByProviderIds("gr", new[] { "44767458" })["44767458"].Single().Id, Is.EqualTo(edition.Id));
                Assert.That(reopened.FindAllByAsin("b00region2").Single().Id, Is.EqualTo(edition.Id));
                var selected = reopened.FindRowsByBook(new[] { book.Id }).Single();
                selected.Monitored = true; selected.ManualAdd = true;
                h.EditionService.UpdateSelection(new List<Edition> { selected });
                Assert.That(reopened.FindAllByProviderIds("gr", new[] { "44767458" })["44767458"].Single().Id, Is.EqualTo(edition.Id));
                var remote = RefreshEntityCopy.CloneEdition(selected);
                remote.RemoteProviderIds = new HashSet<string> { "gr:44767459" };
                var refresh = new RefreshEditionService(h.EditionService, Proxy<IMetadataTagService>((method, args) => null), null, database, LogManager.GetCurrentClassLogger());
                refresh.RefreshEditionInfo(new(), new(), new(), new(), new() { selected }, new() { remote }, false, new Dictionary<int, Edition> { [selected.Id] = remote });
                Assert.Multiple(() =>
                {
                    Assert.That(reopened.FindAllByProviderIds("gr", new[] { "44767458", "44767459" })["44767458"], Is.Empty);
                    Assert.That(reopened.FindAllByProviderIds("gr", new[] { "44767459" })["44767459"].Single().Id, Is.EqualTo(edition.Id));
                    Assert.That(reopened.FindAllByAsin("B00REGION2").Single().Id, Is.EqualTo(edition.Id));
                });
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void incomplete_sibling_readiness_pins_ready_narration_and_preserves_remaining_search_across_reopened_attempts(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var book = h.AddBook();
                var opposite = h.AddBook(BookMediaType.Ebook, "hc:7000", "hc:edition:7003");
                var original = h.Editions.FindRowsByBook(new[] { book.Id }).Single();
                var requested = new Edition { BookId = book.Id, Title = "Lovers", ForeignEditionId = "hc:edition:2002",
                    TitleSlug = Guid.NewGuid().ToString("N"), Narrator = "Other Narrator", Language = "eng", ReadingFormatId = 2,
                    RemoteProviderIds = new HashSet<string> { "gr:44767458" } };
                h.EditionService.InsertMany(new() { requested });
                var pending = h.Request(new() { "hc:698880", "hc:7000" }, new() { new()
                    { WorkProviderId = "hc:698880", EditionProviderId = "gr:44767458", RequiresEdition = true, Search = true } });
                h.Run();
                Assert.That(h.Editions.Get(original.Id).Monitored, Is.False);
                Assert.That(h.Editions.Get(requested.Id).ManualAdd, Is.True);
                Assert.That(h.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EqualTo(new[] { book.Id }));
                Assert.That(JsonConvert.DeserializeObject<List<RequestedNarration>>(h.Requests.Get(pending.Id).AudiobookNarrations), Is.Empty);
                Assert.That(h.Requests.Get(pending.Id).IsActive(), Is.True);
                Assert.That(h.Requests.Get(pending.Id).AudiobookBooksToSearch, Is.EqualTo(@"[""hc:7000""]"));
                Assert.That(h.BookService.GetBook(opposite.Id).IsMonitored(), Is.False);
                h.AddBook(work: "hc:7000", edition: "hc:edition:7001");
                h.Pending.RetryNow(pending.Id);
                h.Run();
                Assert.Multiple(() =>
                {
                    Assert.That(h.Requests.All(), Is.Empty);
                    Assert.That(h.Editions.Get(requested.Id).ManualAdd, Is.True);
                    Assert.That(h.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds).Count(id => id == book.Id), Is.EqualTo(1));
                });
                h.Request(new() { "hc:698880" }, new() { new()
                    { WorkProviderId = "hc:698880", EditionProviderId = "gr:44767458", RequiresEdition = true, Search = true } });
                h.Run();
                Assert.That(h.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds).Count(id => id == book.Id), Is.EqualTo(1));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void unavailable_narration_retains_other_target_completion_and_terminal_partial_status(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var unavailable = h.AddBook();
                var available = h.AddBook(work: "hc:7000", edition: "hc:edition:7001");
                var pending = h.Request(new() { "hc:698880", "hc:7000" }, new() { new()
                    { WorkProviderId = "hc:698880", EditionProviderId = "hc:edition:9999", RequiresEdition = true, Search = true } });
                h.Run();
                var stored = h.Requests.Get(pending.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(stored.OverallStatus, Is.EqualTo(PendingImportStatus.PartialSuccess));
                    Assert.That(stored.AudiobookStatus, Is.EqualTo(PendingImportStatus.PartialSuccess));
                    Assert.That(stored.IsComplete(), Is.True);
                    Assert.That(h.Pending.GetDueForProcessing(), Is.Empty);
                    Assert.That(h.Events.OfType<PendingAuthorImportSucceededEvent>().Count(), Is.EqualTo(1));
                    Assert.That(h.Events.OfType<PendingAuthorImportFailedEvent>(), Is.Empty);
                    Assert.That(stored.LastError, Does.Contain("9999"));
                    Assert.That(h.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EqualTo(new[] { available.Id }));
                    Assert.That(h.Books.Get(unavailable.Id).IsMonitored(), Is.True);
                });
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void unavailable_only_narration_is_a_visible_failed_terminal_and_keeps_normal_parent_monitoring(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var book = h.AddBook();
                var pending = h.Request(new() { "hc:698880" }, new()
                    { new() { WorkProviderId = "hc:698880", EditionProviderId = "hc:edition:9999", RequiresEdition = true, Search = true } });
                h.Run();
                var stored = h.Requests.Get(pending.Id);
                var resource = Chaptarr.Api.V1.PendingImport.PendingAuthorImportResourceMapper.ToResource(stored);
                Assert.Multiple(() =>
                {
                    Assert.That(stored.OverallStatus, Is.EqualTo(PendingImportStatus.Failed));
                    Assert.That(resource.OverallStatus, Is.EqualTo("Failed"));
                    Assert.That(resource.LastError, Does.Contain("9999"));
                    Assert.That(h.Pending.GetDueForProcessing(), Is.Empty);
                    Assert.That(h.Commands.OfType<BookSearchCommand>(), Is.Empty);
                    Assert.That(h.Books.Get(book.Id).IsMonitored(), Is.True);
                });
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void author_deletion_cancels_alias_request_and_stale_retry_cannot_rearm_it(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.Aliases.SeedAliases("Author", h.Author.Id, "author", new[] { "hc:9001", "gr:5500" });
                var item = h.Request(new() { "hc:698880" });
                item.ProviderId = "gr:5500";
                h.Requests.Update(item);
                h.Pending.Handle(new AuthorDeletedEvent(h.Author, false, false));
                h.Pending.ScheduleRetry(item, "stale worker");
                Assert.That(h.Requests.Get(item.Id).OverallStatus, Is.EqualTo(PendingImportStatus.Failed));
                Assert.That(h.Pending.IsActiveRequest(item.Id, item.Version), Is.False);
                Assert.That(h.Pending.GetDueForProcessing(), Is.Empty);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void scoped_author_removal_preserves_other_pending_media_and_intentional_readd_does_not_cancel(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var item = h.Request(new() { "hc:698880" }, ebook: true);
                h.Pending.Handle(new AuthorDeletedEvent(h.Author, false, false, preserveRetainedFileHistory: true));
                Assert.That(h.Requests.Get(item.Id).IsActive(), Is.True);
                var changed = AuthorsCopy(h.Author);
                changed.AudiobookRootFolderPath = null; changed.AudiobookQualityProfileId = null;
                h.Pending.Handle(new AuthorEditedEvent(changed, h.Author));
                var stored = h.Requests.Get(item.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(stored.IsActive(), Is.True);
                    Assert.That(stored.HasAudiobook(), Is.False);
                    Assert.That(stored.HasEbook(), Is.True);
                    Assert.That(stored.EbookBooksToMonitor, Is.EqualTo(item.EbookBooksToMonitor));
                    Assert.That(h.Pending.IsActiveRequest(item.Id, item.Version), Is.False);
                });
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void rejected_list_batch_preserves_all_search_intent_before_retry(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.Author.AddOptions = new AddAuthorOptions();
                var first = h.AddBook();
                var second = h.AddBook(work: "hc:7000", edition: "hc:edition:7001");
                var pending = h.Request(new() { "hc:698880", "hc:7000" });
                h.RejectedBookId = first.Id;
                h.Run();
                Assert.Multiple(() =>
                {
                    Assert.That(h.Commands.OfType<BookSearchCommand>(), Is.Empty);
                    Assert.That(h.Commands.OfType<RefreshAuthorCommand>().Count(), Is.EqualTo(1));
                    Assert.That(h.Requests.Get(pending.Id).AudiobookBooksToSearch, Does.Contain("698880").And.Contain("7000"));
                    Assert.That(h.Books.Get(first.Id).AddOptions.SearchForNewBook, Is.True);
                    Assert.That(h.Books.Get(second.Id).AddOptions.SearchForNewBook, Is.True);
                });
                h.RejectedBookId = null;
                h.Pending.RetryNow(pending.Id);
                h.Run();
                Assert.That(h.Commands.OfType<BookSearchCommand>().Single().BookIds, Is.EquivalentTo(new[] { first.Id, second.Id }));
                Assert.That(h.Commands.OfType<BookSearchCommand>().Single().Trigger, Is.EqualTo(CommandTrigger.Unspecified));
                Assert.That(h.Requests.All(), Is.Empty);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void indexed_regional_alias_lookup_and_backfill_cover_full_cohort_without_like_scans(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var statements = new List<string>();
                var observed = Proxy<IMainDatabase>((method, args) => method.Name == nameof(IMainDatabase.OpenConnection)
                    ? CommandObserver.Wrap<IDbConnection>(database.OpenConnection(), command => statements.Add(command.CommandText))
                    : method.Invoke(database, args));
                var h = new Harness(observed);
                var book = h.AddBook();
                var edition = h.Editions.FindRowsByBook(new[] { book.Id }).Single();
                using (var connection = database.OpenConnection())
                {
                    connection.Execute(@"UPDATE ""Editions"" SET ""Asins"" = @asins WHERE ""Id"" = @id", new { id = edition.Id, asins = "[\"B00REGION1\",\"B00REGION2\"]" });
                    connection.Execute(@"DELETE FROM ""ProviderAliasIndex"" WHERE ""EntityType"" = 'Edition' AND ""EntityId"" = @id", new { id = edition.Id });
                    add_import_list_monitoring_options.BackfillEditionAliases(connection, null, LogManager.GetCurrentClassLogger());
                    add_import_list_monitoring_options.BackfillEditionAliases(connection, null, LogManager.GetCurrentClassLogger());
                }
                foreach (var count in new[] { 1, 200 })
                {
                    statements.Clear();
                    var ids = Enumerable.Range(0, count).Select(index => index == 0 ? "B00REGION2" : $"MISS{index}").ToArray();
                    Assert.That(h.Editions.FindAllByProviderIds("az", ids)["B00REGION2"].Single().Id, Is.EqualTo(edition.Id));
                    Assert.That(statements, Has.Count.EqualTo(2));
                    Assert.That(statements.Any(sql => sql.Contains("LIKE", StringComparison.OrdinalIgnoreCase)), Is.False);
                    TestContext.WriteLine($"{(postgres ? "PostgreSQL" : "SQLite")}: {count} ASIN targets, {statements.Count} SELECT statements");
                }
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void narration_writer_rejection_preserves_successful_targets_and_terminal_partial_state(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var first = h.AddBook();
                var second = h.AddBook(work: "hc:7000", edition: "hc:edition:7001");
                var firstEdition = h.Editions.FindRowsByBook(new[] { first.Id }).Single();
                h.RejectedEditionId = firstEdition.Id;
                var pending = h.Request(new() { "hc:698880", "hc:7000" }, new()
                {
                    new() { WorkProviderId = "hc:698880", EditionProviderId = firstEdition.ForeignEditionId, RequiresEdition = true, Search = true },
                    new() { WorkProviderId = "hc:7000", EditionProviderId = "hc:edition:7001", RequiresEdition = true, Search = true }
                });
                h.Run();
                Assert.Multiple(() =>
                {
                    Assert.That(h.Requests.Get(pending.Id).OverallStatus, Is.EqualTo(PendingImportStatus.PartialSuccess));
                    Assert.That(h.Requests.Get(pending.Id).LastError, Does.Contain("Injected narration rejection"));
                    Assert.That(h.Books.Get(second.Id).IsMonitored(), Is.True);
                    Assert.That(h.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EqualTo(new[] { second.Id }));
                    Assert.That(h.Pending.GetDueForProcessing(), Is.Empty);
                });
            });
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void cover_and_refresh_rejections_preserve_search_batch_progress(bool postgres, bool rejectSearch)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.Author.AddOptions = new AddAuthorOptions();
                var first = h.AddBook();
                var second = h.AddBook(work: "hc:7000", edition: "hc:edition:7001");
                h.EditionService.InsertMany(new() { new() { BookId = first.Id, ForeignEditionId = "gr:44767458", Title = "Lovers",
                    ReadingFormatId = 2, Narrator = "Another Narrator", Language = "eng", TitleSlug = Guid.NewGuid().ToString("N") } });
                var pending = h.Request(new() { "hc:698880", "hc:7000" }, new()
                    { new() { WorkProviderId = "hc:698880", EditionProviderId = "gr:44767458", RequiresEdition = true, Search = true } });
                h.RejectedBookId = rejectSearch ? first.Id : null;
                h.RejectCoverCommand = true;
                h.RejectRefreshCommand = true;
                h.Run();
                Assert.Multiple(() =>
                {
                    Assert.That(h.Requests.Get(pending.Id).OverallStatus, Is.EqualTo(PendingImportStatus.Retrying));
                    if (rejectSearch)
                    {
                        Assert.That(h.Requests.Get(pending.Id).AudiobookBooksToSearch, Does.Contain("698880").And.Contain("7000"));
                        Assert.That(h.Commands.OfType<BookSearchCommand>(), Is.Empty);
                    }
                    else
                    {
                        Assert.That(h.Requests.Get(pending.Id).AudiobookBooksToSearch, Is.EqualTo("[]"));
                        Assert.That(h.Commands.OfType<BookSearchCommand>().Single().BookIds, Is.EquivalentTo(new[] { first.Id, second.Id }));
                    }
                    Assert.That(h.Books.Get(first.Id).AddOptions.SearchForNewBook, Is.EqualTo(rejectSearch));
                    Assert.That(h.Books.Get(second.Id).AddOptions.SearchForNewBook, Is.EqualTo(rejectSearch));
                });
                h.RejectedBookId = null;
                h.RejectCoverCommand = false;
                h.RejectRefreshCommand = false;
                h.Pending.RetryNow(pending.Id);
                h.Run();
                Assert.That(h.Commands.OfType<BookSearchCommand>().Single().BookIds, Is.EquivalentTo(new[] { first.Id, second.Id }));
                Assert.That(h.Commands.OfType<BookSearchCommand>().Single().Trigger, Is.EqualTo(CommandTrigger.Unspecified));
                Assert.That(h.Requests.All(), Is.Empty);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void edition_metadata_and_alias_replacement_roll_back_together_through_basic_repository_interface(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var book = h.AddBook();
                var edition = h.Editions.FindRowsByBook(new[] { book.Id }).Single();
                IBasicRepository<Edition> repository = h.Editions;
                edition.RemoteProviderIds = new HashSet<string> { "gr:44767458" };
                repository.Upsert(edition);
                var loaded = repository.Get(edition.Id);
                loaded.Title = "Updated title";
                repository.Update(loaded);
                Assert.That(h.Editions.FindAllByProviderIds("gr", new[] { "44767458" })["44767458"].Single().Id, Is.EqualTo(edition.Id));
                using (var connection = database.OpenConnection())
                {
                    connection.Execute(postgres
                        ? @"ALTER TABLE ""ProviderAliasIndex"" ADD CONSTRAINT reject_secondary_alias CHECK (""NormalizedProviderId"" <> '44767459')"
                        : @"CREATE TRIGGER reject_secondary_alias BEFORE INSERT ON ""ProviderAliasIndex"" WHEN NEW.""NormalizedProviderId"" = '44767459' BEGIN SELECT RAISE(ABORT, 'Injected alias failure'); END");
                }
                loaded.Title = "Must roll back";
                loaded.RemoteProviderIds = new HashSet<string> { "gr:44767459" };
                Assert.That(() => repository.UpdateMany(new List<Edition> { loaded }), Throws.Exception.With.Message.Contains(postgres ? "reject_secondary_alias" : "Injected alias failure"));
                Assert.Multiple(() =>
                {
                    Assert.That(repository.Get(edition.Id).Title, Is.EqualTo("Updated title"));
                    Assert.That(h.Editions.FindAllByProviderIds("gr", new[] { "44767458" })["44767458"].Single().Id, Is.EqualTo(edition.Id));
                    Assert.That(h.Editions.FindAllByProviderIds("gr", new[] { "44767459" })["44767459"], Is.Empty);
                });
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void multiple_requested_aliases_hydrate_each_attested_alias_once_per_edition(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var book = h.AddBook();
                var edition = h.Editions.FindRowsByBook(new[] { book.Id }).Single();
                edition.RemoteProviderIds = new HashSet<string> { "gr:44767458", "gr:44767459", "gr:44767460" };
                h.EditionService.UpdateMany(new() { edition });
                var expectedAliases = h.Aliases.GetAliases("Edition", edition.Id, "edition");
                var aliasRows = 0;
                var observed = Proxy<IMainDatabase>((method, args) =>
                {
                    if (method.Name != nameof(IMainDatabase.OpenConnection)) return method.Invoke(database, args);
                    var connection = database.OpenConnection();
                    return Proxy<IDbConnection>((connectionMethod, connectionArgs) =>
                    {
                        var result = connectionMethod.Invoke(connection, connectionArgs);
                        if (result is not IDbCommand command) return result;
                        return Proxy<IDbCommand>((commandMethod, commandArgs) =>
                        {
                            var commandResult = commandMethod.Invoke(command, commandArgs);
                            if (commandResult is not IDataReader reader || !command.CommandText.Contains("SELECT e.*, a.*", StringComparison.Ordinal)) return commandResult;
                            return Proxy<IDataReader>((readerMethod, readerArgs) =>
                            {
                                var value = readerMethod.Invoke(reader, readerArgs);
                                if (readerMethod.Name == nameof(IDataReader.Read) && value is true) aliasRows++;
                                return value;
                            });
                        });
                    });
                });
                var repository = new EditionRepository(observed, h.EventAggregator);
                var requested = new[] { "44767458", "44767459", "44767460" };
                var matches = repository.FindAllByProviderIds("gr", requested);
                Assert.Multiple(() =>
                {
                    Assert.That(aliasRows, Is.EqualTo(expectedAliases.Count));
                    foreach (var id in requested)
                    {
                        Assert.That(matches[id].Select(match => match.Id), Is.EqualTo(new[] { edition.Id }));
                        Assert.That(matches[id].Single().RemoteProviderIds, Is.EquivalentTo(expectedAliases.Select(alias => $"{alias.Provider}:{alias.NormalizedProviderId}")));
                    }
                });
                TestContext.WriteLine($"{(postgres ? "PostgreSQL" : "SQLite")}: {requested.Length} requested aliases, {aliasRows} complete attested rows returned once");
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void bulk_new_edition_alias_writes_do_not_read_or_delete_existing_aliases(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var book = h.AddBook();
                var statements = new List<string>();
                var observed = Proxy<IMainDatabase>((method, args) => method.Name == nameof(IMainDatabase.OpenConnection)
                    ? CommandObserver.Wrap<IDbConnection>(database.OpenConnection(), command => statements.Add(command.CommandText))
                    : method.Invoke(database, args));
                var repository = new EditionRepository(observed, h.EventAggregator);
                var editions = Enumerable.Range(0, 1000).Select(index => new Edition
                {
                    BookId = book.Id, Title = "Fixture Book", ForeignEditionId = $"hc:edition:{50000 + index}",
                    TitleSlug = Guid.NewGuid().ToString("N"), ReadingFormatId = 2, Narrator = "Fixture Narrator",
                    RemoteProviderIds = new HashSet<string> { $"gr:{60000 + index}" },
                    Asins = new List<string> { $"REGION{index}A", $"REGION{index}B" }
                }).ToList();
                var timer = Stopwatch.StartNew();
                repository.InsertMany(editions);
                timer.Stop();
                Assert.That(statements.Any(sql => sql.Contains("ProviderAliasIndex", StringComparison.OrdinalIgnoreCase) &&
                    !sql.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)), Is.False);
                var aliasStatements = statements.Count(sql => sql.Contains("ProviderAliasIndex", StringComparison.OrdinalIgnoreCase));
                Assert.That(aliasStatements, Is.LessThan(editions.Count));
                Assert.That(h.Aliases.GetAliases("Edition", editions.Select(edition => edition.Id), "edition"), Has.Count.EqualTo(5000));
                TestContext.WriteLine($"{(postgres ? "PostgreSQL" : "SQLite")}: {editions.Count} new editions, 5000 aliases, {statements.Count} INSERT statements ({aliasStatements} alias statements), {timer.ElapsedMilliseconds} ms");
                using var connection = database.OpenConnection();
                var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                timer.Restart();
                add_import_list_monitoring_options.BackfillEditionAliases(connection, null, LogManager.GetCurrentClassLogger());
                timer.Stop();
                var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                Assert.That(h.Aliases.GetAliases("Edition", editions.Select(edition => edition.Id), "edition"), Has.Count.EqualTo(5000));
                TestContext.WriteLine($"{(postgres ? "PostgreSQL" : "SQLite")}: complete backfill of 1001 editions, {timer.ElapsedMilliseconds} ms, {allocated} managed bytes allocated (cumulative, not peak or resident memory)");
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void scoped_author_deletion_cancels_secondary_alias_request_through_real_author_writer_and_event(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.Aliases.SeedAliases("Author", h.Author.Id, "author", new[] { "hc:9001", "gr:5500" });
                var pending = h.Request(new() { "hc:698880" }, ebook: true);
                pending.ProviderId = "gr:5500";
                h.Requests.Update(pending);
                h.EventPublished = evt => { if (evt is AuthorEditedEvent edited) h.Pending.Handle(edited); };
                var service = new AuthorService(h.Authors, h.EventAggregator, null, null, h.Queue, new CacheManager(), h.Books, null,
                    LogManager.GetCurrentClassLogger(), null, null, new Lazy<IBookService>(() => h.BookService), mainDatabase: h.Database, h.Aliases);
                service.DeleteAuthors(new() { h.Author.Id }, false, mediaType: BookMediaType.Audiobook);
                var stored = h.Requests.Get(pending.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(h.Authors.Get(h.Author.Id).AudiobookRootFolderPath, Is.Null);
                    Assert.That(h.Authors.Get(h.Author.Id).EbookRootFolderPath, Is.EqualTo(h.Author.EbookRootFolderPath));
                    Assert.That(stored.AudiobookStatus, Is.EqualTo(PendingImportStatus.NotRequested));
                    Assert.That(stored.EbookStatus, Is.EqualTo(PendingImportStatus.Pending));
                    Assert.That(stored.EbookBooksToMonitor, Is.EqualTo(pending.EbookBooksToMonitor));
                    Assert.That(h.Aliases.GetAliases("Author", h.Author.Id, "author").Select(alias => $"{alias.Provider}:{alias.NormalizedProviderId}"), Does.Contain("gr:5500"));
                });
                var author = h.Authors.Get(h.Author.Id);
                author.RemoteProviderIds = new HashSet<string>();
                service.UpdateAuthor(author);
                Assert.That(h.Aliases.GetAliases("Author", h.Author.Id, "author").Select(alias => $"{alias.Provider}:{alias.NormalizedProviderId}"), Does.Not.Contain("gr:5500"));
                var remote = new Author { RemoteProviderIds = new HashSet<string>() };
                author.UseMetadataFrom(remote);
                Assert.That(author.RemoteProviderIds, Is.Not.Null.And.Empty);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void author_snapshot_preservation_requires_no_alias_reads_for_bulk_author_updates(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                foreach (var count in new[] { 1, 25 })
                {
                    var authors = Enumerable.Range(0, count).Select(index => h.Authors.Insert(new Author
                    {
                        Name = "Fixture Author", CleanName = "fixtureauthor", SortName = "fixtureauthor", SortNameLastFirst = "Author, Fixture",
                        TitleSlug = Guid.NewGuid().ToString("N"), Path = @"C:\Audio\Fixture Author", Added = DateTime.UtcNow,
                        HardcoverAuthorId = $"hc:{20000 + count * 100 + index}"
                    })).ToList();
                    foreach (var author in authors) h.Aliases.SeedAliases("Author", author.Id, "author", new[] { author.HardcoverAuthorId, $"gr:{30000 + author.Id}" });
                    var batchReads = 0;
                    var aliases = Proxy<IProviderAliasService>((method, args) =>
                    {
                        if (method.Name == nameof(IProviderAliasService.GetAliases))
                        {
                            Assert.That(args[1], Is.InstanceOf<IEnumerable<int>>());
                            batchReads++;
                        }
                        return method.Invoke(h.Aliases, args);
                    });
                    var service = new AuthorService(h.Authors, h.EventAggregator, null, null, h.Queue, new CacheManager(), h.Books, null,
                        LogManager.GetCurrentClassLogger(), null, null, new Lazy<IBookService>(() => h.BookService), mainDatabase: h.Database, aliases);
                    service.UpdateAuthors(h.Authors.Get(authors.Select(author => author.Id)).ToList(), false);
                    Assert.That(batchReads, Is.Zero);
                    Assert.That(h.Aliases.GetAliases("Author", authors.Select(author => author.Id), "author").Count(alias => alias.Provider == "gr"), Is.EqualTo(count));
                    TestContext.WriteLine($"{(postgres ? "PostgreSQL" : "SQLite")}: {count} author updates, {batchReads} batch alias read, zero scalar alias reads");
                }
            });
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void manual_narration_search_retains_held_flag_until_its_queue_submission_is_accepted(bool postgres, bool reject)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var book = h.AddBook();
                var item = h.Request(new() { "hc:698880" }, new()
                    { new() { WorkProviderId = "hc:698880", EditionProviderId = "hc:edition:2001", RequiresEdition = true, Search = true } });
                item.RequestedBy = "UserInterface";
                h.Requests.Update(item);
                h.RejectedBookId = reject ? book.Id : null;
                h.Run();
                if (reject)
                {
                    Assert.That(h.Requests.Get(item.Id).OverallStatus, Is.EqualTo(PendingImportStatus.Retrying));
                    Assert.That(h.Commands.OfType<BookSearchCommand>(), Is.Empty);
                }
                else
                {
                    Assert.That(h.Requests.All(), Is.Empty);
                    Assert.That(h.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EqualTo(new[] { book.Id }));
                }
                Assert.That(h.Books.Get(book.Id).AddOptions.SearchForNewBook, Is.EqualTo(reject));
            });
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void automatic_bulk_merge_preserves_pending_demand_and_transfers_persisted_aliases(bool postgres, bool rollback)
        {
            var sink = new NLog.Targets.MemoryTarget { Layout = "${message}|${exception:format=tostring}" };
            var config = new NLog.Config.LoggingConfiguration();
            config.AddRule(NLog.LogLevel.Debug, NLog.LogLevel.Fatal, sink);
            using var logs = new LogFactory { Configuration = config };
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var source = h.Authors.Get(h.Author.Id);
                var book = h.AddBook();
                h.Aliases.SeedAliases("Author", source.Id, "author", new[] { "hc:9001", "gr:5500", "ol:OLSECONDARYA" });
                var target = h.Authors.Insert(new Author
                {
                    Name = source.Name, CleanName = source.CleanName, SortName = source.SortName,
                    TitleSlug = Guid.NewGuid().ToString("N"), Path = source.Path, Added = DateTime.UtcNow,
                    HardcoverAuthorId = "hc:9002", GoodreadsAuthorId = "gr:5500",
                    AudiobookRootFolderPath = source.AudiobookRootFolderPath, EbookRootFolderPath = source.EbookRootFolderPath,
                    AudiobookQualityProfileId = source.AudiobookQualityProfileId, EbookQualityProfileId = source.EbookQualityProfileId,
                    AudiobookMetadataProfileId = source.AudiobookMetadataProfileId, EbookMetadataProfileId = source.EbookMetadataProfileId
                });
                h.Aliases.SeedAliases("Author", target.Id, "author", new[] { "hc:9002", "gr:5500", "gb:SURVIVORSECONDARY" });
                var pending = h.Request(new() { "hc:698880" });
                pending.ProviderId = "gr:5500";
                h.Requests.Update(pending);
                h.EventPublished = evt =>
                {
                    if (evt is AuthorDeletedEvent deleted) h.Pending.Handle(deleted);
                    if (evt is AuthorEditedEvent edited) h.Pending.Handle(edited);
                };
                var authors = new AuthorService(h.Authors, h.EventAggregator, null, null, h.Queue, new CacheManager(), h.Books, null,
                    LogManager.GetCurrentClassLogger(), null, null, new Lazy<IBookService>(() => h.BookService), mainDatabase: h.Database, h.Aliases);
                var exclusions = Proxy<NzbDrone.Core.ImportLists.Exclusions.IImportListExclusionService>((method, args) =>
                    new List<NzbDrone.Core.ImportLists.Exclusions.ImportListExclusion>());
                var sync = Proxy<IAuthorSyncMetadataService>((method, args) => null);
                var published = new Author { Name = target.Name, HardcoverAuthorId = "hc:9002", GoodreadsAuthorId = "gr:5500",
                    RemoteProviderIds = new() { "hc:9001", "hc:9002", "gr:5500", "ol:OLSECONDARYA", "gb:SURVIVORSECONDARY" } };
                var metadata = Proxy<NzbDrone.Core.MetadataSource.IProvideAuthorInfo>((method, args) =>
                    method.Name == "RefreshAuthorInfo" ? NzbDrone.Core.MetadataSource.BookInfo.RefreshResult.Updated(published) : null);
                var refresh = new RefreshAuthorService(MetadataHealthGateStub.Ready(), metadata, authors, h.BookService, h.EditionService, null, null, null,
                    h.EventAggregator, h.Queue, null, new global::NzbDrone.Core.History.HistoryRepository(database, h.EventAggregator), null, null, null, null, exclusions, sync, null, null,
                    logs.GetLogger("Merge"), mainDatabase: database, bookRepository: h.Books, authorRepository: h.Authors);
                if (rollback)
                {
                    using var connection = database.OpenConnection();
                    var abort = postgres
                        ? $@"CREATE FUNCTION ""AliasMergeAbort""() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF OLD.""Id"" = {source.Id} THEN RAISE EXCEPTION 'fixture merge abort'; END IF; RETURN OLD; END; $$;
                            CREATE TRIGGER ""AliasMergeAbort"" BEFORE DELETE ON ""Authors"" FOR EACH ROW EXECUTE FUNCTION ""AliasMergeAbort""();"
                        : $@"CREATE TRIGGER ""AliasMergeAbort"" BEFORE DELETE ON ""Authors"" WHEN OLD.""Id"" = {source.Id} BEGIN SELECT RAISE(ABORT, 'fixture merge abort'); END;";
                    connection.Execute(abort);
                }
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                Assert.That(typeof(RefreshAuthorService).GetMethod("IsMerge", flags).Invoke(refresh, new object[] { source, target }), Is.False);
                {
                    var response = new NzbDrone.Core.MetadataSource.BookInfo.V5.V5AuthorChangesResponse
                    {
                        Merged = new List<NzbDrone.Core.MetadataSource.BookInfo.V5.V5MergedAuthor>
                            { new() { From = "hc:9001", To = "hc:9002" } }
                    };
                    var blocked = (HashSet<string>)typeof(RefreshAuthorService).GetMethod("ApplyServerMerges", flags)
                        .Invoke(refresh, new object[] { response, new List<Author> { source, target } });
                    Assert.That(blocked.Contains("hc:9001"), Is.EqualTo(rollback));
                    if (rollback) Assert.That(sink.Logs, Has.Some.Contains("fixture merge abort"), "Rollback must reach the planted database failure");
                    Assert.That(sink.Logs, Has.None.Contains("NotImplementedException"));
                }
                var stored = h.Requests.Get(pending.Id);
                TestContext.WriteLine($"Q6 {(postgres ? "PostgreSQL" : "SQLite")} request active={stored.IsActive()}, status={stored.OverallStatus}, error={stored.LastError}, deleted events={h.Events.OfType<AuthorDeletedEvent>().Count()}");
                Assert.That(stored.IsActive(), Is.True);
                if (rollback)
                {
                    Assert.That(h.Authors.Find(source.Id), Is.Not.Null);
                    Assert.That(h.Aliases.GetAliases("Author", source.Id, "author"), Has.Count.EqualTo(3));
                    Assert.That(h.Books.Get(book.Id).AuthorId, Is.EqualTo(source.Id));
                    Assert.That(h.Events.OfType<AuthorDeletedEvent>(), Is.Empty);
                    return;
                }
                Assert.That(h.Authors.Find(source.Id), Is.Null);
                Assert.That(h.Books.Get(book.Id).AuthorId, Is.EqualTo(target.Id));
                {
                    TestContext.WriteLine("Bulk merge hc:9001 alias owner local IDs: " + string.Join(",", h.Aliases.FindAuthorIds(new[] { "hc:9001" }).OrderBy(id => id)));
                    Assert.That(authors.FindByProviderId("hc", "9001").Id, Is.EqualTo(target.Id));
                    Assert.That(authors.FindByProviderId("ol", "OLSECONDARYA").Id, Is.EqualTo(target.Id));
                    Assert.That(authors.FindByProviderId("gb", "SURVIVORSECONDARY").Id, Is.EqualTo(target.Id));
                    Assert.That(h.Aliases.GetAliases("Author", source.Id, "author"), Is.Empty);
                    Assert.That(h.Events.OfType<AuthorDeletedEvent>(), Is.Empty);
                }
            });
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void legacy_edition_alias_conversion_reaches_index_without_selecting_scalar(bool postgres, bool rejected)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var book = h.AddBook();
                var stored = h.Editions.FindRowsByBook(new[] { book.Id }).Single();
                h.Aliases.SeedAliases("Edition", stored.Id, "edition", new[] { "gr:8001" });
                var payload = new NzbDrone.Core.MetadataSource.BookInfo.V5.V5Edition
                {
                    Id = "hc:edition:2001", Title = "Lovers", ProviderIdsAllCamel = null,
                    LegacyProviderIds = new Dictionary<string, object>
                        { ["goodreads"] = rejected ? "System.Collections.Generic.List`1[System.String]" : "8001" }
                };
                var proxy = Chaptarr.Core.Test.MetadataSource.BookInfo.BookInfoProxyCanonicalLookupFixture.CreateProxy(null);
                var converted = (Edition)typeof(NzbDrone.Core.MetadataSource.BookInfo.BookInfoProxy)
                    .GetMethod("ConvertV5EditionToDomain", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .Invoke(proxy, new object[] { payload, BookMediaType.Audiobook });
                Assert.That(converted.GoodreadsEditionId, Is.Null);
                if (rejected) Assert.That(converted.RemoteProviderIds, Is.Null);
                stored.UseMetadataFrom(converted);
                h.EditionService.UpdateMany(new() { stored });
                var reopened = new EditionRepository(database, h.EventAggregator);
                Assert.That(reopened.Get(stored.Id).GoodreadsEditionId, Is.Null);
                Assert.That(h.Aliases.GetAliases("Edition", stored.Id, "edition")
                    .Any(alias => alias.Provider == "gr" && alias.NormalizedProviderId == "8001"), Is.True);
                Assert.That(reopened.FindByBook(new[] { book.Id }).Single().RemoteProviderIds, Does.Contain("gr:8001"));
                Assert.That(reopened.FindAllByProviderIds("gr", new[] { "8001" })["8001"].Select(edition => edition.Id), Is.EqualTo(new[] { stored.Id }));
            });
        }

        [Test]
        public void provider_alias_attestation_preserves_unknown_and_withdraws_only_genuine_empty(
            [Values(false, true)] bool postgres, [Values(false, true)] bool authorBoundary,
            [Values("unknown", "empty", "poison", "blank", "mixed")] string providerSet)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                const string poison = "System.Collections.Generic.List`1[System.String]";
                var ids = providerSet switch
                {
                    "unknown" => null,
                    "empty" => new HashSet<string>(),
                    "blank" => new HashSet<string> { " ", "\t" },
                    "mixed" => new HashSet<string> { "gr:8001", poison },
                    _ => new HashSet<string> { poison }
                };
                List<ProviderAlias> aliases;
                if (authorBoundary)
                {
                    h.Aliases.SeedAliases("Author", h.Author.Id, "author", new[] { "hc:9001", "gr:7001" });
                    var local = h.Authors.Get(h.Author.Id);
                    local.UseMetadataFrom(new Author
                        { Name = local.Name, HardcoverAuthorId = local.HardcoverAuthorId, RemoteProviderIds = ids });
                    var service = new AuthorService(h.Authors, h.EventAggregator, null, null, h.Queue, new CacheManager(), h.Books, null,
                        LogManager.GetCurrentClassLogger(), null, null, new Lazy<IBookService>(() => h.BookService), mainDatabase: h.Database, h.Aliases);
                    service.UpdateAuthor(local);
                    aliases = h.Aliases.GetAliases("Author", local.Id, "author");
                }
                else
                {
                    var book = h.AddBook();
                    var local = h.Editions.FindByBook(new[] { book.Id }).Single();
                    local.RemoteProviderIds = new HashSet<string> { "gr:7001" };
                    h.Editions.Update(local);
                    local = h.Editions.FindByBook(new[] { book.Id }).Single();
                    var remote = RefreshEntityCopy.CloneEdition(local);
                    remote.RemoteProviderIds = ids;
                    local.UseMetadataFrom(remote);
                    h.Editions.Update(local);
                    aliases = h.Aliases.GetAliases("Edition", local.Id, "edition");
                }
                var actual = aliases.Select(alias => $"{alias.Provider}:{alias.NormalizedProviderId}").ToList();
                TestContext.WriteLine($"Q14 {(postgres ? "PostgreSQL" : "SQLite")} author={authorBoundary}, source={providerSet}, stored=" + string.Join(",", actual));
                Assert.That(actual.Contains("gr:7001"), Is.EqualTo(providerSet is "unknown" or "poison" or "blank"));
                if (providerSet == "mixed") Assert.That(actual, Does.Contain("gr:8001"));
            });
        }

        private static Author AuthorsCopy(Author author) => new() { Id = author.Id, HardcoverAuthorId = author.HardcoverAuthorId,
            AudiobookRootFolderPath = author.AudiobookRootFolderPath, AudiobookQualityProfileId = author.AudiobookQualityProfileId,
            EbookRootFolderPath = author.EbookRootFolderPath, EbookQualityProfileId = author.EbookQualityProfileId };
    }
}
