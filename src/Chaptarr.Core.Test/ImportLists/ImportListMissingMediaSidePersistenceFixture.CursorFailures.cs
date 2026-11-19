using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Common;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.ImportLists.Hardcover.Library;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RootFolders;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.ImportLists
{
    public partial class ImportListMissingMediaSidePersistenceFixture
    {
        [TestCase(false, BookMediaType.Audiobook)]
        [TestCase(true, BookMediaType.Audiobook)]
        [TestCase(false, BookMediaType.Ebook)]
        [TestCase(true, BookMediaType.Ebook)]
        public void actual_chaptarr_writer_refusal_preserves_each_explicit_media_request(bool postgres, BookMediaType side)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new StoredListLibrary(database);
                var book = EbookPinCatalog(side);
                if (side == BookMediaType.Ebook)
                {
                    book.AnyEditionOk = false;
                    book.Editions[0].ManualAdd = true;
                }
                h.Save(book);
                var reports = EbookPinSource(h, new[] { book }, side == BookMediaType.Ebook ? "ebook" : "audiobook");
                var attempts = 0;
                var books = Proxy<IBookService>((method, args) =>
                {
                    if (method.Name == nameof(IBookService.AddWantedEditions) && side == BookMediaType.Audiobook)
                    {
                        attempts++;
                        foreach (var edition in (List<Edition>)args[0])
                            ((Action<Edition, Exception>)args[1])(edition, new IOException("Temporary wanted-edition write failure"));
                        return new Dictionary<int, Book>();
                    }
                    return method.Invoke(h.Books, args);
                });
                CursorRequestSync(h, reports, books, h.Pending, side).Execute(new ImportListSyncCommand(1));
                var saved = h.Requests.GetByProviderId(h.Author.GoodreadsAuthorId);
                Assert.That(saved, Is.Not.Null, "A failed writer must preserve the explicit request before normal list processing continues.");
                var request = Newtonsoft.Json.JsonConvert.DeserializeObject<List<RequestedNarration>>(saved.AudiobookNarrations).Single();
                Assert.Multiple(() =>
                {
                    Assert.That(request.MediaType, Is.EqualTo(side));
                    Assert.That(request.WorkProviderId, Is.EqualTo(book.GoodreadsWorkId));
                    Assert.That(request.EditionProviderId, Is.EqualTo(book.Editions[1].ForeignEditionId));
                    Assert.That(request.RequiresEdition, Is.True);
                    Assert.That(request.Search, Is.True);
                    Assert.That(h.Commands.OfType<BookSearchCommand>(), Is.Empty);
                    Assert.That(h.Books.GetBook(book.Id).AnyEditionOk, Is.EqualTo(book.AnyEditionOk));
                    Assert.That(BookEditionIdentity.GetMonitoredEdition(h.Books.GetBook(book.Id)).Id, Is.EqualTo(book.Editions[0].Id));
                });
                h.Process(saved, new Author { Name = h.Author.Name, Overview = string.Empty, GoodreadsAuthorId = h.Author.GoodreadsAuthorId,
                    Books = new List<Book> { EbookPinCatalog(side) }, Series = new List<Series>() });
                if (side == BookMediaType.Ebook)
                {
                    var failed = h.Requests.Get(saved.Id);
                    Assert.That(failed.OverallStatus, Is.EqualTo(PendingImportStatus.Failed));
                    Assert.That(failed.EbookStatus, Is.EqualTo(PendingImportStatus.Failed));
                    Assert.That(failed.LastError, Does.Contain(request.EditionProviderId));
                    Assert.That(h.Commands.OfType<BookSearchCommand>(), Is.Empty);
                }
                else
                {
                    Assert.That(attempts, Is.EqualTo(1));
                    Assert.That(h.Requests.All(), Is.Empty);
                    var selected = h.Books.GetBook(book.Id);
                    Assert.That(BookEditionIdentity.GetMonitoredEdition(selected).ForeignEditionId, Is.EqualTo(request.EditionProviderId));
                    Assert.That(selected.AnyEditionOk, Is.False);
                    Assert.That(h.Commands.OfType<BookSearchCommand>().Select(command => command.BookIds.Single()), Is.EqualTo(new[] { book.Id }));
                }
                TestContext.WriteLine($"{database.DatabaseType}: actual Chaptarr {side} request {request.WorkProviderId}/{request.EditionProviderId} persisted after refusal; pending processed through the shared writer.");
            });
        }

        [TestCase(false, false, false)]
        [TestCase(true, false, false)]
        [TestCase(false, true, false)]
        [TestCase(true, true, false)]
        [TestCase(false, false, true)]
        [TestCase(true, false, true)]
        [TestCase(false, true, true)]
        [TestCase(true, true, true)]
        public void actual_hardcover_audio_writer_refusal_commits_only_after_durable_acceptance(bool postgres, bool enqueueFails, bool transient)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new StoredListLibrary(database);
                var book = EbookPinCatalog(BookMediaType.Audiobook, "1001");
                book.SetMonitored(false);
                h.Save(book);
                var neighbor = EbookPinCatalog(BookMediaType.Audiobook, "1002");
                neighbor.SetMonitored(true);
                h.Save(neighbor);
                h.Author.AudiobookRootFolderPath = @"C:\RemovedAudio".AsOsAgnostic();
                h.Authors.Update(h.Author);
                var realBooks = new BookService(h.BookRows, h.Editions, h.Events, h.AuthorService, h.Files, h.Roots,
                    new SeriesBookLinkRepository(h.Database, h.Events), null, h.Logger, providerAliasService: h.Aliases);
                var writerCalls = 0;
                var books = Proxy<IBookService>((method, args) =>
                {
                    if (method.Name == nameof(IBookService.AddWantedEditions))
                    {
                        writerCalls++;
                        if (transient)
                        {
                            var requested = (List<Edition>)args[0];
                            var rejected = requested.Single(edition => edition.BookId == book.Id);
                            ((Action<Edition, Exception>)args[1])(rejected, new IOException("Temporary wanted-edition write failure"));
                            return realBooks.AddWantedEditions(requested.Where(edition => edition.BookId != book.Id).ToList(), (Action<Edition, Exception>)args[1]);
                        }
                    }
                    return method.Invoke(realBooks, args);
                });
                var state = new HardcoverLibraryImportListStateRepository(h.Database, h.Events);
                var initial = DateTime.UtcNow.AddHours(-2);
                var definition = CursorDefinition();
                state.Insert(new HardcoverLibraryImportListState { ImportListId = 1, HardcoverUserId = 5,
                    SettingsSignature = "owned:False;wtr:True;cr:False;read:False;ma:True;me:False", CursorUpdatedAt = initial,
                    CursorUserBookId = 10, LastFullSyncAt = initial.AddDays(-1), CreatedAt = initial });
                var httpCalls = 0;
                var afterIds = new List<int>();
                var provider = new HardcoverLibraryImportList(Proxy<IHttpClient>((method, args) =>
                {
                    var http = (HttpRequest)args[0];
                    using var content = JsonDocument.Parse(Encoding.UTF8.GetString(http.ContentData));
                    var query = content.RootElement.GetProperty("query").GetString();
                    var afterId = query.Contains("query UserBooksDelta") ? content.RootElement.GetProperty("variables").GetProperty("afterId").GetInt32() : 0;
                    if (!query.Contains("query Me")) afterIds.Add(afterId);
                    httpCalls++;
                    var body = query.Contains("query Me") ? "{\"data\":{\"me\":[{\"id\":5,\"username\":\"isolated\"}]}}" :
                        afterId >= 12 ? "{\"data\":{\"user_books\":[]}}" : JsonSerializer.Serialize(new { data = new { user_books = new[] { book, neighbor }.Select((source, index) => new
                        {
                            id = 11 + index, updated_at = initial.AddHours(1), status_id = 1, edition_id = int.Parse(source.HardcoverBookId.Substring(3) + "202"),
                            edition = new { reading_format_id = 2 },
                            book = new { id = int.Parse(source.HardcoverBookId.Substring(3)), title = source.Title, contributions = new[] { new
                            { author_id = 562254, contribution = "Author", author = new { id = 562254, name = h.Author.Name } } } }
                        }).ToArray() } });
                    return new HttpResponse(http, new HttpHeader { ContentType = "application/json" }, body, HttpStatusCode.OK);
                }), null, null, null, null, null, state, Proxy<IImportListStatusService>((_, _) => null),
                    Proxy<IConfigService>((method, _) => throw new AssertionException(method.Name)), null, h.Logger) { Definition = definition };
                var factory = Proxy<IImportListFactory>((method, _) => method.Name switch
                {
                    nameof(IImportListFactory.Get) => definition,
                    nameof(IImportListFactory.GetInstance) => provider,
                    _ => throw new AssertionException(method.Name)
                });
                var fetcher = new FetchAndParseImportListService(factory, Proxy<IImportListStatusService>((_, _) => null), h.Logger);
                var enqueueCalls = 0;
                var rejectEnqueue = enqueueFails;
                var pending = Proxy<IPendingAuthorImportService>((method, args) =>
                {
                    if (method.Name != nameof(IPendingAuthorImportService.EnqueueAsync)) return method.Invoke(h.Pending, args);
                    enqueueCalls++;
                    return rejectEnqueue ? Task.FromResult(0) : method.Invoke(h.Pending, args);
                });
                var metadataCalls = 0;
                var metadata = Proxy<IProvideBookInfo>((method, args) =>
                {
                    metadataCalls++;
                    var source = new[] { book, neighbor }.Single(candidate => candidate.Editions.Any(edition => BookEditionIdentity.EditionMatchesProviderId(edition, (string)args[0])));
                    return Tuple.Create(h.Author.GoodreadsAuthorId, source, new List<Author> { h.Author });
                });
                void Sync() => new ImportListSyncService(factory, new ImportListExclusionService(new ImportListExclusionRepository(h.Database, h.Events), h.Logger),
                    fetcher, metadata, h.AuthorService, books, h.Editions, pending, h.Roots, new RootFolderSettingsResolver(h.Roots), h.Events, h.Queue, h.Logger,
                    new ImportListBookIdentityCacheRepository(h.Database, h.Events)).Execute(new ImportListSyncCommand(1));
                Sync();
                Assert.Multiple(() =>
                {
                    Assert.That(writerCalls, Is.EqualTo(1));
                    Assert.That(enqueueCalls, Is.EqualTo(1));
                    Assert.That(state.GetByImportListId(1).CursorUserBookId, Is.EqualTo(enqueueFails ? 10 : 12));
                    Assert.That(h.Requests.All(), Has.Count.EqualTo(enqueueFails ? 0 : 1));
                    Assert.That(h.Commands.OfType<BookSearchCommand>().Select(command => command.BookIds.Single()), Is.EqualTo(new[] { neighbor.Id }));
                    Assert.That(BookEditionIdentity.GetMonitoredEdition(h.Books.GetBook(neighbor.Id)).Id, Is.EqualTo(neighbor.Editions[1].Id));
                    Assert.That(BookEditionIdentity.GetMonitoredEdition(h.Books.GetBook(book.Id)).Id, Is.EqualTo(book.Editions[0].Id));
                });
                if (enqueueFails)
                {
                    rejectEnqueue = false;
                    Sync();
                    Assert.Multiple(() =>
                    {
                        Assert.That(enqueueCalls, Is.EqualTo(2));
                        Assert.That(writerCalls, Is.EqualTo(2));
                        Assert.That(state.GetByImportListId(1).CursorUserBookId, Is.EqualTo(12));
                        Assert.That(h.Requests.All(), Has.Count.EqualTo(1));
                        Assert.That(h.Commands.OfType<BookSearchCommand>().Select(command => command.BookIds.Single()), Is.EqualTo(new[] { neighbor.Id }));
                    });
                }
                if (!enqueueFails)
                {
                    var saved = h.Requests.All().Single();
                    var request = Newtonsoft.Json.JsonConvert.DeserializeObject<List<RequestedNarration>>(saved.AudiobookNarrations).Single();
                    Assert.That(request.EditionProviderId, Is.EqualTo("hc:1001202"));
                    Assert.That(request.WorkProviderId, Is.EqualTo("hc:1001"));
                    Assert.That(request.MediaType, Is.EqualTo(BookMediaType.Audiobook));
                    Assert.That(request.RequiresEdition, Is.True);
                    Assert.That(request.Search, Is.True);
                    h.Author.AudiobookRootFolderPath = @"C:\ExistingAudio".AsOsAgnostic();
                    h.AuthorService.UpdateAuthor(h.Author);
                    saved.AudiobookRootFolderPath = h.Author.AudiobookRootFolderPath;
                    var remote = EbookPinCatalog(BookMediaType.Audiobook, "1001");
                    if (!transient) remote.Editions.RemoveAt(1);
                    h.Process(saved, new Author { Name = h.Author.Name, Overview = string.Empty, GoodreadsAuthorId = h.Author.GoodreadsAuthorId,
                        Books = new List<Book> { remote, EbookPinCatalog(BookMediaType.Audiobook, "1002") }, Series = new List<Series>() });
                    if (!transient)
                    {
                        Assert.That(h.Requests.Get(saved.Id).OverallStatus, Is.EqualTo(PendingImportStatus.Failed));
                        Assert.That(h.Requests.Get(saved.Id).LastError, Does.Contain(request.EditionProviderId));
                    }
                    var lookupsBefore = metadataCalls;
                    Sync();
                    Assert.Multiple(() =>
                    {
                        Assert.That(writerCalls, Is.EqualTo(1), "A committed delta must not replay the refused writer after durable handoff.");
                        Assert.That(enqueueCalls, Is.EqualTo(1));
                        Assert.That(metadataCalls, Is.EqualTo(lookupsBefore));
                        Assert.That(h.Requests.All(), Has.Count.EqualTo(transient ? 0 : 1));
                        Assert.That(state.GetByImportListId(1).CursorUserBookId, Is.EqualTo(12));
                        Assert.That(afterIds.Last(), Is.EqualTo(12));
                    });
                }
                TestContext.WriteLine($"{database.DatabaseType}: HC audio refusal transient={transient}, enqueueFailed={enqueueFails}, writer calls={writerCalls}, enqueues={enqueueCalls}, HTTP requests={httpCalls}, metadata reads={metadataCalls}, cursor={state.GetByImportListId(1).CursorUserBookId}.");
            });
        }

        [TestCase(false, BookMediaType.Audiobook)]
        [TestCase(true, BookMediaType.Audiobook)]
        [TestCase(false, BookMediaType.Ebook)]
        [TestCase(true, BookMediaType.Ebook)]
        public void refused_edition_batch_should_preserve_all_requests_with_one_author_enqueue(bool postgres, BookMediaType side)
        {
            var readerCounts = new List<int>();
            foreach (var count in new[] { 1, 25 })
            {
                DualDatabaseFixture.Run(postgres, database =>
                {
                    var h = new StoredListLibrary(database);
                    var catalog = Enumerable.Range(1, count).Select(index => EbookPinCatalog(side, (1000 + index).ToString())).ToList();
                    foreach (var book in catalog)
                    {
                        if (side == BookMediaType.Ebook) { book.AnyEditionOk = false; book.Editions[0].ManualAdd = true; }
                        h.Save(book);
                    }
                    var reports = EbookPinSource(h, catalog, side == BookMediaType.Ebook ? "ebook" : "audiobook");
                    var enqueues = 0;
                    var pending = Proxy<IPendingAuthorImportService>((method, args) =>
                    {
                        if (method.Name == nameof(IPendingAuthorImportService.EnqueueAsync)) enqueues++;
                        return method.Invoke(h.Pending, args);
                    });
                    var writes = 0;
                    var books = Proxy<IBookService>((method, args) =>
                    {
                        if (method.Name == nameof(IBookService.AddWantedEditions))
                        {
                            writes++;
                            if (side == BookMediaType.Audiobook)
                            {
                                foreach (var edition in (List<Edition>)args[0])
                                    ((Action<Edition, Exception>)args[1])(edition, new IOException("Temporary wanted-edition write failure"));
                                return new Dictionary<int, Book>();
                            }
                        }
                        return method.Invoke(h.Books, args);
                    });
                    h.ReaderStatements.Clear();
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    CursorRequestSync(h, reports, books, pending, side).Execute(new ImportListSyncCommand(1));
                    timer.Stop();
                    readerCounts.Add(h.ReaderStatements.Count);
                    var saved = h.Requests.All().Single();
                    var requests = Newtonsoft.Json.JsonConvert.DeserializeObject<List<RequestedNarration>>(saved.AudiobookNarrations);
                    Assert.Multiple(() =>
                    {
                        Assert.That(writes, Is.EqualTo(1));
                        Assert.That(enqueues, Is.EqualTo(1));
                        Assert.That(requests, Has.Count.EqualTo(count));
                        Assert.That(requests.Select(request => request.EditionProviderId), Is.EquivalentTo(catalog.Select(book => book.Editions[1].ForeignEditionId)));
                        Assert.That(requests.Select(request => request.MediaType), Is.All.EqualTo(side));
                        Assert.That(requests.Select(request => request.Search), Is.All.True);
                        Assert.That(h.Commands.OfType<BookSearchCommand>(), Is.Empty);
                    });
                    TestContext.WriteLine($"{database.DatabaseType}: {side}, {count} refused requests, {readerCounts.Last()} database readers, {writes} writer batches, {enqueues} author enqueues, {timer.ElapsedMilliseconds} ms.");
                });
            }
            Assert.That(readerCounts[1], Is.EqualTo(readerCounts[0]), "Compatible requests must retain the existing batch query boundary.");
        }

        private static ImportListDefinition CursorDefinition() => new()
        {
            Id = 1, Name = "Cursor Refusal List", Implementation = nameof(HardcoverLibraryImportList), EnableAutomaticAdd = true,
            ShouldMonitor = ImportListMonitorType.SpecificBook, ShouldMonitorExisting = true, ShouldSearch = true,
            MonitoringOptions = new ImportListMonitoringOptions { AddMissingAuthors = true, AudiobookMonitored = false, AudiobookMonitorNewItems = NewItemMonitorTypes.None },
            RootFolderPath = @"C:\Audio".AsOsAgnostic(), Settings = new HardcoverLibraryImportListSettings
            { ApiToken = "isolated-test-token", ImportWantToRead = true, ImportCurrentlyReading = false, ImportRead = false, ImportOwned = false,
                MonitorAudiobooks = true, MonitorEbooks = false, AudiobookRootFolderPath = @"C:\Audio".AsOsAgnostic() }
        };

        private static ImportListSyncService CursorRequestSync(StoredListLibrary h, List<ImportListItemInfo> reports, IBookService books,
            IPendingAuthorImportService pending, BookMediaType side)
        {
            var definition = CursorDefinition();
            definition.Implementation = nameof(NzbDrone.Core.ImportLists.Chaptarr.ChaptarrImport);
            definition.Settings = new NzbDrone.Core.ImportLists.Chaptarr.ChaptarrSettings { MonitorAudiobooks = side == BookMediaType.Audiobook,
                MonitorEbooks = side == BookMediaType.Ebook, AudiobookRootFolderPath = @"C:\Audio".AsOsAgnostic(), EbookRootFolderPath = @"C:\Ebooks".AsOsAgnostic() };
            var provider = new NzbDrone.Core.ImportLists.Chaptarr.ChaptarrImport(null, null, null, null, null, null, null, null, null, h.Logger) { Definition = definition };
            return new ImportListSyncService(Proxy<IImportListFactory>((method, _) => method.Name switch
                { nameof(IImportListFactory.Get) => definition, nameof(IImportListFactory.GetInstance) => provider, _ => throw new AssertionException(method.Name) }),
                new ImportListExclusionService(new ImportListExclusionRepository(h.Database, h.Events), h.Logger),
                Proxy<IFetchAndParseImportList>((_, _) => reports), Proxy<IProvideBookInfo>((method, _) => throw new AssertionException(method.Name)),
                h.AuthorService, books, h.Editions, pending, h.Roots, new RootFolderSettingsResolver(h.Roots), h.Events, h.Queue, h.Logger,
                new ImportListBookIdentityCacheRepository(h.Database, h.Events));
        }
    }
}
