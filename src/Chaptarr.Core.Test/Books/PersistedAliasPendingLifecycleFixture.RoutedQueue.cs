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
        private sealed class RoutedQueueHarness
        {
            public readonly Harness Library;
            public readonly Author Source;
            public readonly PendingAuthorImport Request;
            public readonly List<Book> Targets;
            public readonly Book Opposite;
            public readonly List<string> Imports = new();
            public readonly List<(string Work, BookMediaType Media)> WorkReads = new();
            public readonly List<string> Sql = new();
            public bool PrimaryReady = true;
            public bool SourceReady;
            public bool RejectHandoff;
            public Action BeforeHandoff;
            public readonly List<MonitoringConfig> SourceConfigs = new();

            public RoutedQueueHarness(IMainDatabase database, BookMediaType media, int count, bool implicitSearch = false)
            {
                var observed = Proxy<IMainDatabase>((method, args) =>
                {
                    if (method.Name != nameof(IMainDatabase.OpenConnection)) return method.Invoke(database, args);
                    var connection = database.OpenConnection();
                    return Proxy<IDbConnection>((connectionMethod, connectionArgs) =>
                    {
                        var result = connectionMethod.Invoke(connection, connectionArgs);
                        return result is IDbCommand command ? Proxy<IDbCommand>((commandMethod, commandArgs) =>
                        {
                            if (commandMethod.Name is nameof(IDbCommand.ExecuteReader) or nameof(IDbCommand.ExecuteNonQuery) or nameof(IDbCommand.ExecuteScalar))
                                Sql.Add(command.CommandText);
                            return commandMethod.Invoke(command, commandArgs);
                        }) : result;
                    });
                });
                Library = new Harness(observed);
                Source = Library.Authors.Insert(new Author
                {
                    Name = "Reported Contributor", CleanName = "reportedcontributor", SortName = "reportedcontributor",
                    SortNameLastFirst = "Contributor, Reported", TitleSlug = Guid.NewGuid().ToString("N"),
                    Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "reported-contributor"),
                    HardcoverAuthorId = "hc:2222", Added = DateTime.UtcNow
                });
                Targets = Enumerable.Range(0, count).Select(index => Library.AddBook(media, $"hc:{7000 + index}", $"hc:edition:{3000 + index}")).ToList();
                Opposite = Library.AddBook(media == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook,
                    "hc:7000", "hc:edition:4999");
                var narrations = Targets.Select(book => new RequestedNarration
                {
                    MediaType = media, WorkProviderId = book.HardcoverBookId,
                    EditionProviderId = Library.Editions.FindRowsByBook(new[] { book.Id }).Single().ForeignEditionId,
                    NarratorNames = media == BookMediaType.Audiobook ? new() { "Fixture Narrator" } : null,
                    RequiresEdition = true, Search = !implicitSearch
                }).ToList();
                Request = Library.Request(media == BookMediaType.Audiobook ? Targets.Select(book => book.HardcoverBookId).ToList() : new(),
                    narrations, media == BookMediaType.Ebook);
                Request.ProviderId = Source.HardcoverAuthorId;
                Request.RequestedBy = implicitSearch ? "ImportList:Shelf" : "UserInterface";
                Request.AudiobookStatus = media == BookMediaType.Audiobook ? PendingImportStatus.Pending : PendingImportStatus.NotRequested;
                Request.EbookStatus = media == BookMediaType.Ebook ? PendingImportStatus.Pending : PendingImportStatus.NotRequested;
                Request.AudiobookMonitorExistingMode = Request.EbookMonitorExistingMode = MonitorTypes.All;
                Request.AudiobookMonitorNewItems = Request.EbookMonitorNewItems = NewItemMonitorTypes.All;
                Request.AudiobookQualityProfileId = Request.EbookQualityProfileId = 1;
                Request.AudiobookMetadataProfileId = Request.EbookMetadataProfileId = 1;
                Request.SearchForMissingBooks = true;
                if (media == BookMediaType.Ebook)
                    Request.EbookBooksToMonitor = Request.EbookBooksToSearch = JsonConvert.SerializeObject(Targets.Select(book => book.HardcoverBookId));
                if (implicitSearch) Request.AudiobookBooksToSearch = Request.EbookBooksToSearch = null;
                Library.Requests.Update(Request);
            }

            public void Run()
            {
                var library = Proxy<IAuthorLibraryService>((_, args) =>
                {
                    var provider = (string)args[0];
                    Imports.Add(provider);
                    var config = (MonitoringConfig)args[1];
                    if (provider == Source.HardcoverAuthorId)
                    {
                        SourceConfigs.Add(config);
                        return SourceReady ? Task.FromResult(Source) : Task.FromException<Author>(new AuthorNotFoundException(provider));
                    }
                    Assert.That(provider, Is.EqualTo(Library.Author.HardcoverAuthorId));
                    Assert.That(config.SearchForMissingBooks, Is.False);
                    Assert.That(config.DiscoveredAuthorFolderPath, Is.Null);
                    Assert.That(config.AudiobookMonitorExistingMode ?? config.EbookMonitorExistingMode, Is.EqualTo(MonitorTypes.SpecificBook));
                    if (!PrimaryReady) return Task.FromException<Author>(new AuthorNotFoundException(provider));
                    var catalog = Library.BookService.GetBooksByAuthor(Library.Author.Id);
                    config.PreviouslyMonitoredEditions = catalog.Where(book => book.IsMonitored())
                        .ToDictionary(book => book.Id, book => BookEditionIdentity.GetMonitoredEdition(book)?.Id);
                    AuthorLibraryService.PrepareRequestedNarrations(new Author { Books = catalog }, config);
                    return Task.FromResult(Library.Author);
                });
                var info = Proxy<IProvideBookInfo>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(IProvideBookInfo.GetWorkInfo)));
                    var work = (string)args[0];
                    var media = (BookMediaType)args[1];
                    WorkReads.Add((work, media));
                    return Tuple.Create(Library.Author.HardcoverAuthorId,
                        new Book { HardcoverBookId = work, MediaType = media, Author = Library.Author }, new List<Author> { Source, Library.Author });
                });
                var pending = Proxy<IPendingAuthorImportService>((method, args) =>
                {
                    if (method.Name == nameof(IPendingAuthorImportService.EnqueueAsync))
                    {
                        BeforeHandoff?.Invoke();
                        if (RejectHandoff) throw new InvalidOperationException("Injected primary queue write failure");
                    }
                    return method.Invoke(Library.Pending, args);
                });
                new ProcessPendingImportsCommandHandler(pending, library, Library.BookService, info, Library.Queue,
                    Library.EventAggregator, LogManager.GetCurrentClassLogger(), Library.Aliases).Execute(new ProcessPendingImportsCommand());
            }
        }

        [Test]
        public void original_retry_does_not_refetch_primary_or_remonitor_completed_work(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media,
            [Values(1, 20)] int count)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new RoutedQueueHarness(database, media, count);
                h.Run();
                h.Run();
                Assert.That(h.Library.BookService.GetBooks(h.Targets.Select(book => book.Id)).All(book => book.IsMonitored()), Is.True);
                h.Library.BookService.SetMonitoredForMediaType(h.Targets.Select(book => book.Id).ToList(), media == BookMediaType.Audiobook ? "audiobook" : "ebook", false);
                var importsBefore = h.Imports.Count;
                var lookupsBefore = h.WorkReads.Count;
                h.Library.Pending.RetryNow(h.Request.Id);
                h.Sql.Clear();
                var timer = System.Diagnostics.Stopwatch.StartNew();
                h.Run();
                timer.Stop();
                var repeatedImports = h.Imports.Skip(importsBefore).ToList();
                var repeatedLookups = h.WorkReads.Count - lookupsBefore;
                TestContext.Progress.WriteLine($"Original retry ({(postgres ? "PostgreSQL" : "SQLite")}): {media}, {count} targets; {repeatedLookups} work lookups, {repeatedImports.Count(id => id == h.Library.Author.HardcoverAuthorId)} primary imports, {repeatedImports.Count(id => id == h.Source.HardcoverAuthorId)} original imports, {h.Sql.Count} SQL commands, {timer.Elapsed.TotalMilliseconds:F1} ms; native persistence, synthetic metadata/library.");
                Assert.Multiple(() =>
                {
                    Assert.That(repeatedLookups, Is.Zero);
                    Assert.That(repeatedImports, Is.EqualTo(new[] { h.Source.HardcoverAuthorId }));
                    Assert.That(h.Library.BookService.GetBooks(h.Targets.Select(book => book.Id)).All(book => !book.IsMonitored()), Is.True);
                    Assert.That(h.Library.BookService.GetBook(h.Opposite.Id).IsMonitored(), Is.False);
                    Assert.That(h.Library.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EquivalentTo(h.Targets.Select(book => book.Id)));
                    Assert.That(h.Library.Requests.Get(h.Request.Id).IsActive(), Is.True);
                    Assert.That(h.Library.Requests.Get(h.Request.Id).SearchForMissingBooks, Is.True);
                    Assert.That(h.Library.Requests.Get(h.Request.Id).ProviderId, Is.EqualTo(h.Source.HardcoverAuthorId));
                    Assert.That(h.Library.Requests.Get(h.Request.Id).AudiobookMonitorExistingMode, Is.EqualTo(MonitorTypes.All));
                    Assert.That(h.Library.Requests.Get(h.Request.Id).EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.All));
                    Assert.That(h.Library.Editions.FindRowsByBook(h.Targets.Select(book => book.Id)).All(edition => edition.ManualAdd), Is.True);
                    Assert.That(h.Library.Requests.All(), Has.Count.EqualTo(1));
                });
            });
        }

        [Test]
        public void routed_unavailable_primary_owns_exact_persisted_request_and_retries_without_recursive_handoff(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media,
            [Values(false, true)] bool implicitSearch)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new RoutedQueueHarness(database, media, 1, implicitSearch) { PrimaryReady = false };
                h.Run();
                var destination = h.Library.Pending.GetByProviderId(h.Library.Author.HardcoverAuthorId);
                Assert.That(destination, Is.Not.Null);
                Assert.Multiple(() =>
                {
                    Assert.That(h.Imports, Is.EqualTo(new[] { h.Source.HardcoverAuthorId }));
                    Assert.That(JsonConvert.DeserializeObject<List<string>>(media == BookMediaType.Audiobook ? destination.AudiobookBooksToMonitor : destination.EbookBooksToMonitor), Is.EqualTo(new[] { "hc:7000" }));
                    Assert.That(JsonConvert.DeserializeObject<List<string>>(media == BookMediaType.Audiobook ? destination.AudiobookBooksToSearch : destination.EbookBooksToSearch), Is.EqualTo(new[] { "hc:7000" }));
                    Assert.That(JsonConvert.DeserializeObject<List<RequestedNarration>>(destination.AudiobookNarrations).Single().Search, Is.True);
                    Assert.That(destination.SearchForMissingBooks, Is.False);
                    Assert.That(destination.HasAudiobook(), Is.EqualTo(media == BookMediaType.Audiobook));
                    Assert.That(destination.HasEbook(), Is.EqualTo(media == BookMediaType.Ebook));
                    Assert.That(h.Library.Requests.Get(h.Request.Id).AudiobookNarrations, Is.EqualTo("[]"));
                    Assert.That(h.Library.Requests.Get(h.Request.Id).AudiobookBooksToMonitor, Is.EqualTo(media == BookMediaType.Audiobook ? "[]" : h.Request.AudiobookBooksToMonitor));
                    Assert.That(h.Library.Requests.Get(h.Request.Id).EbookBooksToMonitor, Is.EqualTo(media == BookMediaType.Ebook ? "[]" : h.Request.EbookBooksToMonitor));
                });
                h.Run();
                var retrying = h.Library.Requests.Get(destination.Id);
                Assert.That(retrying.IsActive(), Is.True);
                Assert.That(retrying.ProviderId, Is.EqualTo(h.Library.Author.HardcoverAuthorId));
                Assert.That(h.Library.Requests.All(), Has.Count.EqualTo(2));
                h.PrimaryReady = true;
                h.Library.Pending.RetryNow(destination.Id);
                h.Run();
                Assert.That(h.Library.Requests.Find(destination.Id), Is.Null);
                Assert.That(h.Library.Requests.All().Single().Id, Is.EqualTo(h.Request.Id));
                Assert.That(h.Library.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EqualTo(new[] { h.Targets.Single().Id }));
            });
        }

        [Test]
        public void routed_queue_failure_or_concurrent_merge_cannot_discard_source_demand(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media,
            [Values(false, true)] bool merge)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new RoutedQueueHarness(database, media, 1) { RejectHandoff = !merge };
                if (merge) h.BeforeHandoff = () =>
                {
                    var newer = h.Library.Requests.Get(h.Request.Id);
                    if (media == BookMediaType.Audiobook) newer.AudiobookBooksToMonitor = newer.AudiobookBooksToSearch = "[\"hc:7000\",\"hc:7001\"]";
                    else newer.EbookBooksToMonitor = newer.EbookBooksToSearch = "[\"hc:7000\",\"hc:7001\"]";
                    Assert.That(h.Library.Requests.TryUpdateRequest(newer, newer.Version), Is.True);
                };
                h.Run();
                var source = h.Library.Requests.Get(h.Request.Id);
                var destination = h.Library.Pending.GetByProviderId(h.Library.Author.HardcoverAuthorId);
                Assert.Multiple(() =>
                {
                    Assert.That(source.IsActive(), Is.True);
                    Assert.That(JsonConvert.DeserializeObject<List<string>>(media == BookMediaType.Audiobook ? source.AudiobookBooksToMonitor : source.EbookBooksToMonitor),
                        Is.EqualTo(merge ? new[] { "hc:7001" } : new[] { "hc:7000" }));
                    Assert.That(JsonConvert.DeserializeObject<List<RequestedNarration>>(source.AudiobookNarrations).Select(request => request.WorkProviderId),
                        Is.EqualTo(merge ? Array.Empty<string>() : new[] { "hc:7000" }));
                    Assert.That(source.LastError, Does.Contain(merge ? PendingAuthorImportRetryReason.AuthorNotYetAvailable : "Injected primary queue write failure"));
                    if (!merge) Assert.That(source.LastError, Does.Not.Contain("still being prepared"));
                    Assert.That(destination != null, Is.EqualTo(merge));
                    Assert.That(h.Library.Commands.OfType<BookSearchCommand>(), Is.Empty);
                    Assert.That(h.Library.Requests.All(), Has.Count.EqualTo(merge ? 2 : 1));
                });
            });
        }

        [Test]
        public void audiobook_handoff_keeps_original_ebook_null_or_empty_search_scope(
            [Values(false, true)] bool postgres, [Values(false, true)] bool explicitEmpty)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new RoutedQueueHarness(database, BookMediaType.Audiobook, 1);
                h.Request.EbookStatus = PendingImportStatus.Pending;
                h.Request.EbookBooksToMonitor = null;
                h.Request.EbookBooksToSearch = explicitEmpty ? "[]" : null;
                h.Library.Requests.Update(h.Request);
                h.Run();
                var source = h.Library.Requests.Get(h.Request.Id);
                Assert.That(source.EbookBooksToMonitor, Is.Null);
                Assert.That(source.EbookBooksToSearch, Is.EqualTo(h.Request.EbookBooksToSearch));
                Assert.That(source.SearchForMissingBooks, Is.True);
                h.Library.Pending.RetryNow(source.Id);
                h.Run();
                Assert.That(h.SourceConfigs.Last().CreateEbook, Is.True);
                Assert.That(h.SourceConfigs.Last().EbookBooksToMonitor, Is.Null);
                Assert.That(h.SourceConfigs.Last().EbookBooksToSearch, Is.EqualTo(explicitEmpty ? new List<string>() : null));
            });
        }

        [Test]
        public void routed_monitoring_without_exact_search_keeps_original_whole_author_search(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new RoutedQueueHarness(database, media, 1);
                if (media == BookMediaType.Audiobook) h.Request.AudiobookBooksToSearch = "[]";
                else h.Request.EbookBooksToSearch = "[]";
                var narration = JsonConvert.DeserializeObject<List<RequestedNarration>>(h.Request.AudiobookNarrations).Single();
                narration.Search = false;
                h.Request.AudiobookNarrations = JsonConvert.SerializeObject(new[] { narration });
                h.Library.Requests.Update(h.Request);
                h.Run();
                h.Run();
                Assert.That(h.Library.BookService.GetBook(h.Targets.Single().Id).IsMonitored(), Is.True);
                Assert.That(h.Library.Commands.OfType<BookSearchCommand>(), Is.Empty);
                Assert.That(h.Library.Requests.Get(h.Request.Id).SearchForMissingBooks, Is.True);
                Assert.That(h.Library.Requests.Get(h.Request.Id).AudiobookMonitorExistingMode, Is.EqualTo(MonitorTypes.All));
                Assert.That(h.Library.Requests.Get(h.Request.Id).EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.All));
            });
        }

        [Test]
        public void succeeded_original_can_complete_while_normal_primary_request_remains_durable(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new RoutedQueueHarness(database, media, 1) { SourceReady = true };
                h.Run();
                Assert.That(h.Library.Requests.Find(h.Request.Id), Is.Null);
                var destination = h.Library.Pending.GetByProviderId(h.Library.Author.HardcoverAuthorId);
                Assert.That(destination, Is.Not.Null);
                Assert.That(h.Imports, Is.EqualTo(new[] { h.Source.HardcoverAuthorId }));
                Assert.That(h.Library.Commands.OfType<MissingBookSearchCommand>().Single().AuthorId, Is.EqualTo(h.Source.Id));
                h.Run();
                Assert.That(h.Library.Requests.All(), Is.Empty);
                Assert.That(h.Library.BookService.GetBook(h.Targets.Single().Id).AuthorId, Is.EqualTo(h.Library.Author.Id));
                Assert.That(h.Library.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EqualTo(new[] { h.Targets.Single().Id }));
            });
        }
    }
}
