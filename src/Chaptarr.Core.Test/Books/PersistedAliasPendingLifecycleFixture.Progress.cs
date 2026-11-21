using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Common;
using NLog;
using Newtonsoft.Json;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.MetadataSource;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Books
{
    public partial class PersistedAliasPendingLifecycleFixture
    {
        [Test]
        public void completed_handoff_survives_later_cancellation_or_request_merge(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media,
            [Values(false, true)] bool merge)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new RoutedQueueHarness(database, media, 2);
                var other = new Author { Name = "Other Selected Primary", GoodreadsAuthorId = "gr:58" };
                var calls = 0;
                var pending = Proxy<IPendingAuthorImportService>((method, args) =>
                {
                    if (method.Name == nameof(IPendingAuthorImportService.EnqueueAsync))
                    {
                        if (++calls == 2)
                        {
                            var persisted = h.Library.Requests.Get(h.Request.Id);
                            Assert.That(JsonConvert.DeserializeObject<List<string>>(media == BookMediaType.Audiobook
                                ? persisted.AudiobookBooksToMonitor : persisted.EbookBooksToMonitor), Is.EqualTo(merge ? new[] { "hc:7001", "hc:7002" } : new[] { "hc:7001" }));
                            throw new OperationCanceledException("Injected second destination cancellation");
                        }
                        if (merge)
                        {
                            var newer = h.Library.Requests.Get(h.Request.Id);
                            if (media == BookMediaType.Audiobook) newer.AudiobookBooksToMonitor = newer.AudiobookBooksToSearch = "[\"hc:7000\",\"hc:7001\",\"hc:7002\"]";
                            else newer.EbookBooksToMonitor = newer.EbookBooksToSearch = "[\"hc:7000\",\"hc:7001\",\"hc:7002\"]";
                            Assert.That(h.Library.Requests.TryUpdateRequest(newer, newer.Version), Is.True);
                        }
                    }
                    return method.Invoke(h.Library.Pending, args);
                });
                var info = Proxy<IProvideBookInfo>((_, args) =>
                {
                    var owner = (string)args[0] == "hc:7000" ? h.Library.Author : other;
                    return Tuple.Create(AuthorIdentity.GetPreferredProviderId(owner), new Book
                    { HardcoverBookId = (string)args[0], MediaType = (BookMediaType)args[1], Author = owner }, new List<Author> { owner });
                });
                var library = Proxy<IAuthorLibraryService>((_, args) => Task.FromException<Author>(new AuthorNotFoundException((string)args[0])));
                new ProcessPendingImportsCommandHandler(pending, library, h.Library.BookService, info, h.Library.Queue,
                    h.Library.EventAggregator, LogManager.GetCurrentClassLogger(), h.Library.Aliases).Execute(new ProcessPendingImportsCommand());
                var source = h.Library.Requests.Get(h.Request.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(calls, Is.EqualTo(merge ? 1 : 2));
                    Assert.That(source.IsActive(), Is.True);
                    Assert.That(JsonConvert.DeserializeObject<List<string>>(media == BookMediaType.Audiobook ? source.AudiobookBooksToMonitor : source.EbookBooksToMonitor),
                        Is.EqualTo(merge ? new[] { "hc:7001", "hc:7002" } : new[] { "hc:7001" }));
                    Assert.That(JsonConvert.DeserializeObject<List<RequestedNarration>>(source.AudiobookNarrations).Select(request => request.WorkProviderId), Is.EqualTo(new[] { "hc:7001" }));
                    Assert.That(h.Library.Pending.GetByProviderId(h.Library.Author.HardcoverAuthorId), Is.Not.Null);
                    Assert.That(h.Library.Pending.GetByProviderId(other.GoodreadsAuthorId), Is.Null);
                    Assert.That(h.Library.Events.OfType<PendingAuthorImportFailedEvent>(), Is.Empty);
                });
                h.BeforeHandoff = null;
                h.Run();
                Assert.That(h.Library.Requests.Get(h.Request.Id).IsActive(), Is.True);
                Assert.That(JsonConvert.DeserializeObject<List<string>>(media == BookMediaType.Audiobook
                    ? h.Library.Requests.Get(h.Request.Id).AudiobookBooksToMonitor : h.Library.Requests.Get(h.Request.Id).EbookBooksToMonitor), Is.Empty);
                h.Run();
                Assert.That(h.Library.BookService.GetBooks(h.Targets.Select(book => book.Id)).All(book => book.IsMonitored()), Is.True);
                Assert.That(h.Library.Pending.GetByProviderId(h.Library.Author.HardcoverAuthorId) != null, Is.EqualTo(merge));
                if (merge) Assert.That(JsonConvert.DeserializeObject<List<string>>(media == BookMediaType.Audiobook
                    ? h.Library.Pending.GetByProviderId(h.Library.Author.HardcoverAuthorId).AudiobookBooksToMonitor
                    : h.Library.Pending.GetByProviderId(h.Library.Author.HardcoverAuthorId).EbookBooksToMonitor), Is.EqualTo(new[] { "hc:7002" }));
            });
        }

        [Test]
        public void metadata_enrichment_and_concurrent_cast_demand_survive_fulfillment_subtraction([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new RoutedQueueHarness(database, BookMediaType.Audiobook, 2);
                h.Source.AudiobookQualityProfileId = h.Library.Author.AudiobookQualityProfileId;
                h.Source.AudiobookMetadataProfileId = h.Library.Author.AudiobookMetadataProfileId;
                h.Source.AudiobookRootFolderPath = h.Library.Author.AudiobookRootFolderPath;
                h.Library.Authors.Update(h.Source);
                var local = h.Targets.First();
                local.AuthorId = h.Source.Id;
                h.Library.Books.SetAuthorId(local);
                h.Request.SearchForMissingBooks = false;
                h.Request.AudiobookMonitorExistingMode = MonitorTypes.SpecificBook;
                var narrations = JsonConvert.DeserializeObject<List<RequestedNarration>>(h.Request.AudiobookNarrations);
                narrations.ForEach(request => { request.NarratorNames = null; request.RequiresEdition = false; });
                h.Request.AudiobookNarrations = JsonConvert.SerializeObject(narrations);
                h.Library.Requests.Update(h.Request);
                var library = Proxy<IAuthorLibraryService>((_, args) =>
                {
                    var config = (MonitoringConfig)args[1];
                    AuthorLibraryService.PrepareRequestedNarrations(new Author { Books = h.Library.BookService.GetBooksByAuthor(h.Source.Id) }, config);
                    return Task.FromResult(h.Source);
                });
                var info = Proxy<IProvideBookInfo>((_, args) => Tuple.Create(h.Library.Author.HardcoverAuthorId,
                    new Book { HardcoverBookId = (string)args[0], MediaType = BookMediaType.Audiobook, Author = h.Library.Author }, new List<Author> { h.Library.Author }));
                var pending = Proxy<IPendingAuthorImportService>((method, args) =>
                {
                    if (method.Name == nameof(IPendingAuthorImportService.EnqueueAsync))
                    {
                        var newer = h.Library.Requests.Get(h.Request.Id);
                        newer.AudiobookBooksToMonitor = newer.AudiobookBooksToSearch = "[\"hc:7000\",\"hc:7001\",\"hc:7002\"]";
                        var newerNarrations = JsonConvert.DeserializeObject<List<RequestedNarration>>(newer.AudiobookNarrations);
                        var changed = newerNarrations.Single(request => request.WorkProviderId == "hc:7001");
                        changed.EditionProviderId = "hc:edition:4001";
                        changed.NarratorNames = new() { "New Requested Narrator" };
                        changed.RequiresEdition = true;
                        newer.AudiobookNarrations = JsonConvert.SerializeObject(newerNarrations);
                        Assert.That(h.Library.Requests.TryUpdateRequest(newer, newer.Version), Is.True);
                    }
                    return method.Invoke(h.Library.Pending, args);
                });
                new ProcessPendingImportsCommandHandler(pending, library, h.Library.BookService, info, h.Library.Queue,
                    h.Library.EventAggregator, LogManager.GetCurrentClassLogger(), h.Library.Aliases).Execute(new ProcessPendingImportsCommand());
                var saved = h.Library.Requests.Get(h.Request.Id);
                Assert.That(saved.IsActive(), Is.True);
                Assert.That(JsonConvert.DeserializeObject<List<string>>(saved.AudiobookBooksToMonitor), Is.EqualTo(new[] { "hc:7002" }), saved.LastError);
                var remaining = JsonConvert.DeserializeObject<List<RequestedNarration>>(saved.AudiobookNarrations).Single();
                Assert.Multiple(() =>
                {
                    Assert.That(remaining.WorkProviderId, Is.EqualTo("hc:7001"));
                    Assert.That(remaining.EditionProviderId, Is.EqualTo("hc:edition:4001"));
                    Assert.That(remaining.NarratorNames, Is.EqualTo(new[] { "New Requested Narrator" }));
                    Assert.That(remaining.Search, Is.True);
                    Assert.That(h.Library.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EqualTo(new[] { local.Id }));
                });
                h.Library.BookService.SetMonitoredForMediaType(new List<int> { local.Id }, "audiobook", false);
                h.SourceReady = true;
                h.Run();
                Assert.That(h.Library.Requests.Find(h.Request.Id), Is.Null);
                Assert.That(h.Library.BookService.GetBook(local.Id).IsMonitored(), Is.False);
                Assert.That(h.Library.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds).Count(id => id == local.Id), Is.EqualTo(1));
                var receiving = h.Library.Pending.GetByProviderId(h.Library.Author.HardcoverAuthorId);
                Assert.That(receiving, Is.Not.Null);
                Assert.That(JsonConvert.DeserializeObject<List<RequestedNarration>>(receiving.AudiobookNarrations).Any(request =>
                    request.EditionProviderId == "hc:edition:4001" && request.NarratorNames.SequenceEqual(new[] { "New Requested Narrator" }) && request.Search), Is.True);
            });
        }

        [Test]
        public void works_only_handoff_completes_for_direct_or_bounded_list_scope_even_with_terminal_original(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media,
            [Values(false, true)] bool list, [Values(false, true)] bool terminal, [Values(1, 20)] int count)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new RoutedQueueHarness(database, media, count);
                h.Request.SearchForMissingBooks = false;
                h.Request.RequestedBy = list ? "ImportList:Bounded missing works" : "UserInterface";
                h.Request.AudiobookMonitorExistingMode = h.Request.EbookMonitorExistingMode = null;
                h.Request.AudiobookMonitored = h.Request.EbookMonitored = true;
                h.Library.Requests.Update(h.Request);
                var imports = 0;
                var library = Proxy<IAuthorLibraryService>((_, args) =>
                {
                    imports++;
                    return Task.FromException<Author>(terminal
                        ? new AuthorTerminalException("author_no_primary_works", h.Request.ProviderId, null, "Original has no primary works", false)
                        : new AuthorNotFoundException((string)args[0]));
                });
                var info = Proxy<IProvideBookInfo>((_, args) => Tuple.Create(h.Library.Author.HardcoverAuthorId,
                    new Book { HardcoverBookId = (string)args[0], MediaType = media, Author = h.Library.Author }, new List<Author> { h.Library.Author }));
                h.Sql.Clear();
                new ProcessPendingImportsCommandHandler(h.Library.Pending, library, h.Library.BookService, info, h.Library.Queue,
                    h.Library.EventAggregator, LogManager.GetCurrentClassLogger(), h.Library.Aliases).Execute(new ProcessPendingImportsCommand());
                var updates = h.Sql.Count(sql => sql.Contains("UPDATE \"PendingAuthorImport\"", StringComparison.Ordinal));
                TestContext.Progress.WriteLine($"Works-only handoff ({(postgres ? "PostgreSQL" : "SQLite")}): {count} {media} targets, {h.Sql.Count} native SQL commands, {updates} source CAS updates, {imports} source imports.");
                Assert.Multiple(() =>
                {
                    Assert.That(h.Library.Requests.Find(h.Request.Id), Is.Null);
                    Assert.That(h.Library.Pending.GetByProviderId(h.Library.Author.HardcoverAuthorId), Is.Not.Null);
                    Assert.That(h.Library.Events.OfType<PendingAuthorImportFailedEvent>(), Is.Empty);
                    Assert.That(h.Library.Events.OfType<PendingAuthorImportSucceededEvent>().Single().PendingImport.OverallStatus, Is.EqualTo(PendingImportStatus.Succeeded));
                    Assert.That(updates, Is.EqualTo(1));
                });
            });
        }

        [Test]
        public void first_accepted_search_is_durable_before_later_cancellation([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var first = h.AddBook(BookMediaType.Audiobook, "hc:7000", "hc:edition:3000");
                var second = h.AddBook(BookMediaType.Audiobook, "hc:7001", "hc:edition:3001");
                var request = h.Request(new() { "hc:7000", "hc:7001" });
                request.RequestedBy = "UserInterface";
                Harness.MarkExplicitSearches(request);
                h.Requests.Update(request);
                var library = Proxy<IAuthorLibraryService>((_, _) => Task.FromResult(h.Author));
                var calls = 0;
                var queue = Proxy<NzbDrone.Core.Messaging.Commands.IManageCommandQueue>((method, args) =>
                {
                    if (args[0] is BookSearchCommand && ++calls == 2)
                    {
                        Assert.That(JsonConvert.DeserializeObject<List<string>>(h.Requests.Get(request.Id).AudiobookBooksToSearch), Is.EqualTo(new[] { "hc:7001" }));
                        throw new OperationCanceledException("Injected second search cancellation");
                    }
                    return method.Invoke(h.Queue, args);
                });
                new ProcessPendingImportsCommandHandler(h.Pending, library, h.BookService, null, queue,
                    h.EventAggregator, LogManager.GetCurrentClassLogger(), h.Aliases).Execute(new ProcessPendingImportsCommand());
                Assert.That(h.Requests.Get(request.Id).IsActive(), Is.True);
                h.BookService.SetMonitoredForMediaType(new List<int> { first.Id }, "audiobook", false);
                h.Run();
                Assert.That(h.Requests.Find(request.Id), Is.Null);
                Assert.That(h.BookService.GetBook(first.Id).IsMonitored(), Is.False);
                Assert.That(h.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EqualTo(new[] { first.Id, second.Id }));
            });
        }

        [Test]
        public void user_cancellation_during_handoff_preserves_destination_without_retrying_source([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new RoutedQueueHarness(database, BookMediaType.Audiobook, 1);
                var pending = Proxy<IPendingAuthorImportService>((method, args) =>
                {
                    var result = method.Invoke(h.Library.Pending, args);
                    if (method.Name == nameof(IPendingAuthorImportService.EnqueueAsync)) h.Library.Pending.Cancel(h.Request.Id);
                    return result;
                });
                var library = Proxy<IAuthorLibraryService>((_, args) => Task.FromException<Author>(new AuthorNotFoundException((string)args[0])));
                var info = Proxy<IProvideBookInfo>((_, args) => Tuple.Create(h.Library.Author.HardcoverAuthorId,
                    new Book { HardcoverBookId = (string)args[0], MediaType = BookMediaType.Audiobook, Author = h.Library.Author }, new List<Author> { h.Library.Author }));
                new ProcessPendingImportsCommandHandler(pending, library, h.Library.BookService, info, h.Library.Queue,
                    h.Library.EventAggregator, LogManager.GetCurrentClassLogger(), h.Library.Aliases).Execute(new ProcessPendingImportsCommand());
                var source = h.Library.Requests.Get(h.Request.Id);
                Assert.That(source.IsActive(), Is.False);
                Assert.That(source.LastError, Does.Contain("Cancelled"));
                Assert.That(source.AttemptCount, Is.Zero);
                Assert.That(h.Library.Pending.GetByProviderId(h.Library.Author.HardcoverAuthorId), Is.Not.Null);
                Assert.That(h.Library.Events.OfType<PendingAuthorImportFailedEvent>(), Is.Empty);
                h.BeforeHandoff = null;
                h.Run();
                Assert.That(h.Library.BookService.GetBook(h.Targets.Single().Id).IsMonitored(), Is.True);
                Assert.That(h.Library.Requests.Find(source.Id).IsActive(), Is.False);
            });
        }

        [Test]
        public void applied_local_monitoring_and_search_are_removed_before_unavailable_sibling_retry(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var ready = h.AddBook(media, "hc:7000", "hc:edition:3000");
                var request = h.Request(new() { "hc:7000", "hc:7001" }, new()
                {
                    new() { MediaType = media, WorkProviderId = "hc:7000", EditionProviderId = "hc:edition:3000", RequiresEdition = true, Search = true }
                }, media == BookMediaType.Ebook);
                if (media == BookMediaType.Ebook)
                {
                    request.AudiobookStatus = PendingImportStatus.NotRequested;
                    request.EbookBooksToMonitor = request.EbookBooksToSearch = "[\"hc:7000\",\"hc:7001\"]";
                    h.Requests.Update(request);
                }
                h.Run();
                var saved = h.Requests.Get(request.Id);
                Assert.That(JsonConvert.DeserializeObject<List<string>>(media == BookMediaType.Audiobook ? saved.AudiobookBooksToMonitor : saved.EbookBooksToMonitor), Is.EqualTo(new[] { "hc:7001" }));
                Assert.That(JsonConvert.DeserializeObject<List<RequestedNarration>>(saved.AudiobookNarrations), Is.Empty);
                h.BookService.SetMonitoredForMediaType(new List<int> { ready.Id }, media == BookMediaType.Audiobook ? "audiobook" : "ebook", false);
                h.Pending.RetryNow(request.Id);
                h.Run();
                Assert.Multiple(() =>
                {
                    Assert.That(h.BookService.GetBook(ready.Id).IsMonitored(), Is.False);
                    Assert.That(h.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EqualTo(new[] { ready.Id }));
                    Assert.That(h.Requests.Get(request.Id).IsActive(), Is.True);
                });
            });
        }

        [Test]
        public void actual_add_book_work_scope_ignores_inherited_author_defaults_after_full_handoff(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media,
            [Values(false, true)] bool broadCatalog)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new RoutedQueueHarness(database, media, 1);
                h.Library.Requests.Delete(h.Request.Id);
                h.Source.AudiobookMonitored = h.Source.EbookMonitored = true;
                h.Source.AudiobookMonitorNewItems = h.Source.EbookMonitorNewItems = NewItemMonitorTypes.All;
                h.Source.AddOptions = new AddAuthorOptions { Monitor = broadCatalog ? MonitorTypes.All : MonitorTypes.SpecificBook };
                h.Library.Authors.Update(h.Source);
                var initialBooks = Proxy<IBookService>((method, _) => method.Name == nameof(IBookService.FindByProviderId) ? null
                    : throw new AssertionException(method.Name));
                var initialInfo = Proxy<IProvideBookInfo>((_, args) => throw new BookNotFoundException((string)args[0]));
                var initialLibrary = Proxy<IAuthorLibraryService>((_, args) => Task.FromResult(new Author
                { Id = -h.Library.Pending.EnqueueAsync((string)args[0], (MonitoringConfig)args[1], "UserInterface").GetAwaiter().GetResult() }));
                var exclusions = Proxy<IImportListExclusionService>((method, _) => method.Name == nameof(IImportListExclusionService.FindByForeignId)
                    ? new List<ImportListExclusion>() : throw new AssertionException(method.Name));
                var add = new AddBookService(null, initialLibrary, initialBooks, null, initialInfo, exclusions,
                    null, null, null, null, null, null, null, null, null, LogManager.GetCurrentClassLogger(), null);
                var request = new Book { Title = "Requested Work", HardcoverBookId = "hc:7000", MediaType = media,
                    Author = h.Source, AddOptions = new AddBookOptions { SearchForNewBook = true } };
                var queued = Assert.ThrowsAsync<PendingBookRequestException>(() => add.AddBook(request));
                var original = h.Library.Requests.Get(queued.PendingId);
                Assert.That(media == BookMediaType.Audiobook ? original.AudiobookMonitorExistingMode : original.EbookMonitorExistingMode,
                    Is.EqualTo(broadCatalog ? MonitorTypes.All : MonitorTypes.SpecificBook));
                h.Run();
                Assert.Multiple(() =>
                {
                    Assert.That(h.Library.Requests.Find(queued.PendingId) != null, Is.EqualTo(broadCatalog));
                    Assert.That(h.Library.Pending.GetByProviderId(h.Library.Author.HardcoverAuthorId), Is.Not.Null);
                    Assert.That(h.Library.Events.OfType<PendingAuthorImportFailedEvent>(), Is.Empty);
                });
                h.Run();
                Assert.That(h.Library.BookService.GetBook(h.Targets.Single().Id).IsMonitored(), Is.True);
            });
        }

        [Test]
        public void idle_command_uses_existing_completed_retention_without_touching_future_active_demand([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var old = h.Request(new());
                old.OverallStatus = old.AudiobookStatus = PendingImportStatus.Failed;
                old.CreatedAt = DateTime.UtcNow.AddDays(-31);
                old.UpdatedAt = old.CreatedAt;
                h.Requests.Update(old);
                var completed = h.Request(new());
                completed.OverallStatus = completed.AudiobookStatus = PendingImportStatus.Succeeded;
                completed.CreatedAt = DateTime.UtcNow.AddDays(-31);
                completed.UpdatedAt = completed.CreatedAt;
                h.Requests.Update(completed);
                var future = h.Request(new() { "hc:7000" });
                future.ProviderId = "hc:2222";
                future.NextAttemptAt = DateTime.UtcNow.AddDays(1);
                h.Requests.Update(future);
                h.Run();
                Assert.That(h.Requests.Find(old.Id)?.OverallStatus, Is.EqualTo(PendingImportStatus.Failed));
                Assert.That(h.Requests.Find(completed.Id), Is.Null);
                Assert.That(h.Requests.Find(future.Id).IsActive(), Is.True);
            });
        }

        [Test]
        public void pending_ingress_uses_shared_canonical_ids_and_drops_null_narration_entries([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var config = new MonitoringConfig { RequestedBy = "UserInterface", CreateEbook = false,
                    AudiobookBooksToMonitor = new() { "hc:7000" }, AudiobookNarrations = new()
                    { null, new() { WorkProviderId = "hc:7000", EditionProviderId = "hc:edition:3000" } } };
                var id = h.Pending.EnqueueAsync(" HC : 2222 ", config, "UserInterface").GetAwaiter().GetResult();
                Assert.That(h.Pending.GetByProviderId("hc:2222").Id, Is.EqualTo(id));
                Assert.That(JsonConvert.DeserializeObject<List<RequestedNarration>>(h.Requests.Get(id).AudiobookNarrations), Has.Count.EqualTo(1));
                foreach (var invalid in new[] { "hc:", "hc:1:2", "unprefixed", " " })
                    Assert.Throws<InvalidProviderIdException>(() => h.Pending.EnqueueAsync(invalid, config, "UserInterface").GetAwaiter().GetResult());
                var az = h.Pending.EnqueueAsync("az:b000abc", config, "UserInterface").GetAwaiter().GetResult();
                Assert.That(h.Requests.Get(az).ProviderId, Is.EqualTo("az:B000ABC"));
            });
        }

        [Test]
        public void active_null_narration_does_not_block_valid_sibling_or_merge([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var book = h.AddBook(BookMediaType.Audiobook, "hc:7000", "hc:edition:3000");
                var request = h.Request(new() { "hc:7000" });
                request.AudiobookNarrations = "[null]";
                h.Requests.Update(request);
                h.Pending.EnqueueAsync(request.ProviderId, new MonitoringConfig { CreateEbook = false,
                    AudiobookBooksToMonitor = new() { "hc:7000" }, AudiobookNarrations = new()
                    { null, new() { WorkProviderId = "hc:7000", EditionProviderId = "hc:edition:3000", Search = true } } }, "UserInterface").GetAwaiter().GetResult();
                h.Run();
                Assert.That(h.Requests.Find(request.Id), Is.Null);
                Assert.That(h.BookService.GetBook(book.Id).IsMonitored(), Is.True);
                Assert.That(h.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EqualTo(new[] { book.Id }));
            });
        }

        [Test]
        public void concurrent_narration_only_search_upgrade_survives_completed_monitoring([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var book = h.AddBook(BookMediaType.Audiobook, "hc:7000", "hc:edition:3000");
                var request = h.Request(new() { "hc:7000" }, new()
                { new() { WorkProviderId = "hc:7000", EditionProviderId = "hc:edition:3000", NarratorNames = new() { "Fixture Narrator" }, RequiresEdition = true, Search = false } });
                request.RequestedBy = "UserInterface";
                request.AudiobookBooksToSearch = "[]";
                h.Requests.Update(request);
                h.Configure = _ => h.Pending.EnqueueAsync(request.ProviderId, new MonitoringConfig
                {
                    CreateAudiobook = true, CreateEbook = false, RequestedBy = "UserInterface",
                    AudiobookNarrations = new() { new() { WorkProviderId = "hc:7000", EditionProviderId = "hc:edition:3000",
                        NarratorNames = new() { "Fixture Narrator" }, RequiresEdition = true, Search = true } }
                }, "UserInterface").GetAwaiter().GetResult();
                h.Run();
                var saved = h.Requests.Get(request.Id);
                Assert.That(saved.IsActive(), Is.True);
                Assert.That(saved.AudiobookBooksToMonitor, Is.EqualTo("[]"));
                Assert.That(saved.AudiobookBooksToSearch, Is.EqualTo("[]"));
                Assert.That(JsonConvert.DeserializeObject<List<RequestedNarration>>(saved.AudiobookNarrations).Single().Search, Is.True);
                Assert.That(h.Commands.OfType<BookSearchCommand>(), Is.Empty);
                h.Configure = null;
                h.Run();
                Assert.That(h.Requests.Find(request.Id), Is.Null);
                Assert.That(h.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EqualTo(new[] { book.Id }));
            });
        }

        [Test]
        public void raw_null_active_narration_does_not_block_generic_monitoring_or_search(
            [Values(false, true)] bool postgres, [Values("[null]", "null")] string nullJson)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var book = h.AddBook(BookMediaType.Audiobook, "hc:7000", "hc:edition:3000");
                var request = h.Request(new() { "hc:7000" });
                request.AudiobookNarrations = nullJson;
                h.Requests.Update(request);
                h.Run();
                Assert.That(h.Requests.Find(request.Id), Is.Null);
                Assert.That(h.BookService.GetBook(book.Id).IsMonitored(), Is.True);
                Assert.That(h.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EqualTo(new[] { book.Id }));
            });
        }

        [Test]
        public void original_typed_invalid_id_stops_automatic_retry([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var request = h.Request(new());
                var library = Proxy<IAuthorLibraryService>((_, _) => Task.FromException<Author>(new InvalidProviderIdException(request.ProviderId, "Declared invalid original author")));
                new ProcessPendingImportsCommandHandler(h.Pending, library, h.BookService, null, h.Queue,
                    h.EventAggregator, LogManager.GetCurrentClassLogger(), h.Aliases).Execute(new ProcessPendingImportsCommand());
                Assert.That(h.Requests.Get(request.Id).OverallStatus, Is.EqualTo(PendingImportStatus.Failed));
                Assert.That(h.Requests.Get(request.Id).AttemptCount, Is.Zero);
                Assert.That(h.Events.OfType<PendingAuthorImportFailedEvent>().Count(), Is.EqualTo(1));
            });
        }
        [Test]
        public void pending_worker_name_enrichment_uses_only_the_fetched_original_identity(
            [Values(false, true)] bool postgres, [Values(false, true)] bool sameProvider)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var request = h.Request(new() { "hc:7000" });
                request.AuthorName = "Stored Search Name";
                if (!sameProvider) request.ProviderId = "hc:2222";
                h.Requests.Update(request);
                h.Run();
                var saved = h.Requests.Get(request.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(saved.AuthorName, Is.EqualTo(sameProvider ? h.Author.Name : request.AuthorName));
                    Assert.That(saved.ProviderId, Is.EqualTo(request.ProviderId));
                    Assert.That(saved.IsActive(), Is.True);
                    Assert.That(h.AuthorFetches, Is.EqualTo(1));
                    Assert.That(h.RescueReads, Is.EqualTo(1));
                });
            });
        }

    }
}
