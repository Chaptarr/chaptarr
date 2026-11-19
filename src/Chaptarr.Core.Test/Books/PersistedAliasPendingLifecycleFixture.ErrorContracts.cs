using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Common;
using Chaptarr.Core.Test.MetadataSource.BookInfo;
using FluentValidation;
using FluentValidation.Results;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo;
using Newtonsoft.Json;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Books
{
    public partial class PersistedAliasPendingLifecycleFixture
    {
        private static Author ErrorContractOwner(Harness h)
        {
            return h.Authors.Insert(new Author
            {
                Name = "Selected Primary", CleanName = "selectedprimary", SortName = "selectedprimary", SortNameLastFirst = "Primary, Selected",
                TitleSlug = Guid.NewGuid().ToString("N"), Path = Path.Combine(Path.GetTempPath(), "selected-primary"), Added = DateTime.UtcNow,
                HardcoverAuthorId = "hc:9002", AudiobookRootFolderPath = Path.GetTempPath(), EbookRootFolderPath = Path.GetTempPath(),
                AudiobookQualityProfileId = 1, AudiobookMetadataProfileId = 1, EbookQualityProfileId = 1, EbookMetadataProfileId = 1
            });
        }

        private static PendingAuthorImport ErrorContractRequest(Harness h, BookMediaType media, List<string> works, List<RequestedNarration> editions = null)
        {
            var pending = h.Request(media == BookMediaType.Audiobook ? works : new(), editions, media == BookMediaType.Ebook);
            pending.RequestedBy = "UserInterface";
            pending.AudiobookStatus = media == BookMediaType.Audiobook ? PendingImportStatus.Pending : PendingImportStatus.NotRequested;
            if (media == BookMediaType.Ebook) pending.EbookBooksToMonitor = pending.EbookBooksToSearch = JsonConvert.SerializeObject(works);
            h.Requests.Update(pending);
            return pending;
        }

        [Test]
        public void writer_storage_failure_retries_failed_owner_and_keeps_healthy_selection_and_search(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media,
            [Values("storage", "postcondition", "silent")] string failure)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var owner = ErrorContractOwner(h);
                var failed = h.AddBook(media, "hc:7000", "hc:edition:7000");
                var healthy = h.AddBook(media, "hc:7001", "hc:edition:7001");
                healthy.AuthorId = owner.Id;
                h.Books.SetAuthorId(healthy);
                var opposite = h.AddBook(media == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook, "hc:7000", "hc:edition:7999");
                var editions = h.Editions.FindRowsByBook(new[] { failed.Id, healthy.Id }).ToList();
                var pending = ErrorContractRequest(h, media, new() { "hc:7000", "hc:7001" }, editions.Select(edition => new RequestedNarration
                {
                    MediaType = media, WorkProviderId = edition.BookId == failed.Id ? "hc:7000" : "hc:7001",
                    EditionProviderId = edition.ForeignEditionId, RequiresEdition = true, Search = true
                }).ToList());
                var rejectWrite = true;
                var injected = new InvalidOperationException("Injected storage failure during selection persistence");
                var callbackErrors = new List<Exception>();
                var rejectedMonitoringWrites = 0;
                var editionWriter = Proxy<IEditionService>((method, args) =>
                {
                    if (rejectWrite && failure == "storage" && method.Name == nameof(IEditionService.UpdateSelection) && ((List<Edition>)args[0]).Any(edition => edition.BookId == failed.Id))
                        throw injected;
                    return method.Invoke(h.EditionService, args);
                });
                var bookWriter = Proxy<IBookRepository>((method, args) =>
                {
                    if (rejectWrite && failure == "postcondition" && method.Name == nameof(IBookRepository.SetMonitoring) && ((IEnumerable<Book>)args[0]).Any(book => book.Id == failed.Id))
                    {
                        rejectedMonitoringWrites++;
                        return null;
                    }
                    return method.Invoke(h.Books, args);
                });
                var writer = new BookService(bookWriter, editionWriter, h.EventAggregator, h.AuthorService,
                    Proxy<IMediaFileService>((_, _) => new List<BookFile>()), null, new SeriesBookLinkRepository(database, h.EventAggregator),
                    null, LogManager.GetCurrentClassLogger(), providerAliasService: h.Aliases);
                var books = Proxy<IBookService>((method, args) =>
                {
                    if (method.Name == nameof(IBookService.AddWantedEditions))
                    {
                        var callback = (Action<Edition, Exception>)args[1];
                        var requested = (List<Edition>)args[0];
                        if (rejectWrite && failure == "silent") requested = requested.Where(edition => edition.BookId != failed.Id).ToList();
                        return writer.AddWantedEditions(requested, (edition, error) => { callbackErrors.Add(error); callback(edition, error); });
                    }
                    return method.Invoke(h.BookService, args);
                });
                var imports = new List<string>();
                var library = Proxy<IAuthorLibraryService>((_, args) =>
                {
                    var selected = (string)args[0] == owner.HardcoverAuthorId ? owner : h.Author;
                    imports.Add((string)args[0]);
                    AuthorLibraryService.PrepareRequestedNarrations(new Author { Books = h.BookService.GetBooksByAuthor(selected.Id) }, (MonitoringConfig)args[1]);
                    return Task.FromResult(selected);
                });
                var metadata = Proxy<IProvideBookInfo>((_, args) => Tuple.Create(owner.HardcoverAuthorId,
                    new Book { HardcoverBookId = (string)args[0], MediaType = (BookMediaType)args[1], Author = owner }, new List<Author> { owner }));
                void Run() => new ProcessPendingImportsCommandHandler(h.Pending, library, books, metadata, h.Queue, h.EventAggregator,
                    LogManager.GetCurrentClassLogger(), h.Aliases).Execute(new ProcessPendingImportsCommand());
                Run();
                Assert.That(h.Pending.GetByProviderId(owner.HardcoverAuthorId), Is.Not.Null);
                Run();
                var saved = h.Requests.Get(pending.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(rejectedMonitoringWrites, Is.EqualTo(failure == "postcondition" ? 1 : 0));
                    Assert.That(callbackErrors, Has.Count.EqualTo(failure == "silent" ? 0 : 1));
                    if (failure == "storage") Assert.That(callbackErrors.Single(), Is.SameAs(injected));
                    if (failure == "postcondition") Assert.That(callbackErrors.Single(), Is.InstanceOf<InvalidOperationException>());
                    Assert.That(saved.IsActive(), Is.True, saved.LastError);
                    Assert.That(saved.LastError, Does.Contain(failure == "storage" ? injected.Message : failure == "postcondition" ? "remained unmonitored" : "could not be selected"));
                    Assert.That(saved.OverallStatus, Is.EqualTo(PendingImportStatus.Retrying));
                    Assert.That(JsonConvert.DeserializeObject<List<RequestedNarration>>(saved.AudiobookNarrations).Single(request => request.WorkProviderId == "hc:7000").Search, Is.True);
                    Assert.That(JsonConvert.DeserializeObject<List<RequestedNarration>>(saved.AudiobookNarrations).Any(request => request.WorkProviderId == "hc:7001"), Is.False);
                    Assert.That(h.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EqualTo(new[] { healthy.Id }));
                    Assert.That(h.BookService.GetBook(healthy.Id).AnyEditionOk, Is.False);
                    Assert.That(h.Editions.Get(editions.Single(edition => edition.BookId == healthy.Id).Id).ManualAdd, Is.True);
                    Assert.That(h.BookService.GetBook(opposite.Id).IsMonitored(), Is.False);
                });
                rejectWrite = false;
                h.Pending.RetryNow(pending.Id);
                Run();
                Assert.That(h.Requests.Find(pending.Id), Is.Null);
                Assert.That(h.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EqualTo(new[] { healthy.Id, failed.Id }));
                Assert.That(h.Editions.Get(editions.Single(edition => edition.BookId == failed.Id).Id).ManualAdd, Is.True);
            });
        }

        [Test]
        public void writer_declared_catalog_refusal_is_typed_and_keeps_healthy_request(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var healthy = h.AddBook(media, "hc:7000", "hc:edition:7000");
                var selected = h.Editions.FindRowsByBook(new[] { healthy.Id }).Single();
                var missing = new Edition { Id = 9999, BookId = 9999, ForeignEditionId = "hc:edition:7999" };
                var failures = new List<(int EditionId, Exception Error)>();
                var result = h.BookService.AddWantedEditions(new() { missing, selected }, (edition, error) => failures.Add((edition.Id, error)));
                Assert.That(result.Keys, Is.EqualTo(new[] { selected.Id }));
                Assert.That(failures.Select(failure => failure.EditionId), Is.EqualTo(new[] { missing.Id }));
                Assert.That(failures.Single().Error, Is.InstanceOf<ValidationException>());
                Assert.That(((ValidationException)failures.Single().Error).Errors.Single().PropertyName, Is.EqualTo("Edition"));
                Assert.That(h.BookService.GetBook(healthy.Id).AnyEditionOk, Is.False);
                Assert.That(h.Editions.Get(selected.Id).ManualAdd, Is.True);
            });
        }

        [Test]
        public void declared_work_terminal_exclusion_and_bad_request_isolate_failed_work_from_healthy_group(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media,
            [Values("excluded", "bad-request", "terminal")] string failure)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var healthyOwner = ErrorContractOwner(h);
                var healthy = h.AddBook(media, "hc:7001", "hc:edition:7001");
                healthy.AuthorId = healthyOwner.Id;
                h.Books.SetAuthorId(healthy);
                var opposite = h.AddBook(media == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook, "hc:7001", "hc:edition:7999");
                var excluded = new Author { HardcoverAuthorId = "hc:9003", Name = "Excluded Primary" };
                var pending = ErrorContractRequest(h, media, new() { "hc:7000", "hc:7001" });
                if (failure == "excluded")
                {
                    pending.RequestedBy = "ImportList:Shelf";
                    h.Requests.Update(pending);
                }
                var attempts = new List<string>();
                var lookupCalls = new List<string>();
                var library = Proxy<IAuthorLibraryService>((_, args) =>
                {
                    attempts.Add((string)args[0]);
                    if ((string)args[0] == excluded.HardcoverAuthorId)
                        throw new ValidationException(new[] { new ValidationFailure("Author", "Author is excluded") { ErrorCode = AuthorLibraryService.ImportListAuthorExcludedErrorCode } });
                    return Task.FromResult((string)args[0] == healthyOwner.HardcoverAuthorId ? healthyOwner : h.Author);
                });
                var metadata = Proxy<IProvideBookInfo>((_, args) =>
                {
                    var work = (string)args[0];
                    lookupCalls.Add(work);
                    if (work == "hc:7000" && failure == "bad-request") throw new NzbDrone.Core.Exceptions.BadRequestException(work);
                    if (work == "hc:7000" && failure == "terminal") throw new WorkRescueTerminalException(work, "Declared terminal refusal");
                    var owner = work == "hc:7000" ? excluded : healthyOwner;
                    return Tuple.Create(owner.HardcoverAuthorId, new Book { HardcoverBookId = work, MediaType = (BookMediaType)args[1], Author = owner }, new List<Author> { owner });
                });
                void Run() => new ProcessPendingImportsCommandHandler(h.Pending, library, h.BookService, metadata, h.Queue, h.EventAggregator,
                    LogManager.GetCurrentClassLogger(), h.Aliases).Execute(new ProcessPendingImportsCommand());
                Run();
                var refusedId = failure == "excluded" ? h.Pending.GetByProviderId(excluded.HardcoverAuthorId).Id : pending.Id;
                Assert.That(h.Pending.GetByProviderId(healthyOwner.HardcoverAuthorId), Is.Not.Null);
                Run();
                var saved = h.Requests.Get(refusedId);
                Assert.Multiple(() =>
                {
                    Assert.That(lookupCalls, Is.EqualTo(failure == "excluded" ? new[] { "hc:7000", "hc:7001", "hc:7000" } : new[] { "hc:7000", "hc:7001" }));
                    Assert.That(attempts.Last(), Is.EqualTo(healthyOwner.HardcoverAuthorId));
                    Assert.That(saved.OverallStatus, Is.EqualTo(failure == "bad-request" ? PendingImportStatus.Retrying :
                        failure == "terminal" ? PendingImportStatus.PartialSuccess : PendingImportStatus.Failed));
                    Assert.That(h.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EqualTo(new[] { healthy.Id }));
                    Assert.That(h.BookService.GetBook(healthy.Id).IsMonitored(), Is.True);
                    Assert.That(h.BookService.GetBook(opposite.Id).IsMonitored(), Is.False);
                    Assert.That(saved.IsActive(), Is.EqualTo(failure == "bad-request"));
                });
                if (failure == "bad-request")
                {
                    h.Pending.RetryNow(pending.Id);
                    Run();
                    Assert.That(h.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EqualTo(new[] { healthy.Id }));
                    Assert.That(h.Requests.Get(pending.Id).IsActive(), Is.True);
                }
            });
        }

        [Test]
        public void native_edition_only_not_ready_keeps_typed_unavailable_without_inventing_work_demand(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var requests = new List<HttpRequest>();
                var http = Proxy<IHttpClient>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(IHttpClient.Get)));
                    var request = (HttpRequest)args[0];
                    requests.Add(request);
                    return new HttpResponse(request, new HttpHeader { ContentType = "application/json" }, "null", HttpStatusCode.Accepted);
                });
                var info = BookInfoProxyCanonicalLookupFixture.CreateProxy(http);
                var libraryCalls = 0;
                var library = Proxy<IAuthorLibraryService>((_, _) => { libraryCalls++; throw new AssertionException("No work identity was established"); });
                var service = new AddBookService(h.AuthorService, library, h.BookService, null, info,
                    Proxy<IImportListExclusionService>((_, _) => new List<ImportListExclusion>()), null, null, null, null, null,
                    h.EditionService, null, null, null, LogManager.GetCurrentClassLogger(), h.Queue);
                var requested = new Book
                {
                    MediaType = media, Author = new Author { HardcoverAuthorId = "hc:9003" },
                    Editions = new() { new() { ForeignEditionId = "hc:edition:7000", HardcoverEditionId = "hc:edition:7000", Monitored = true } }
                };
                var error = Assert.ThrowsAsync<BookInfoException>(async () => await service.AddBook(requested));
                Assert.Multiple(() =>
                {
                    Assert.That(error.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
                    Assert.That(requests, Has.Count.EqualTo(1));
                    Assert.That(Uri.UnescapeDataString(requests.Single().Url.ToString()), Does.Contain("/book/hc:7000"));
                    Assert.That(libraryCalls, Is.Zero);
                    Assert.That(h.Requests.All(), Is.Empty);
                    Assert.That(h.Books.All(), Is.Empty);
                    Assert.That(h.Authors.All(), Has.Count.EqualTo(1));
                });
            });
        }
    }
}
