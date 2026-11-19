using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Common;
using Chaptarr.Core.Test.MetadataSource.BookInfo;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.ImportLists.Hardcover.Library;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RootFolders;
using static Chaptarr.Core.Test.Common.InterfaceProxy;
using static Chaptarr.Core.Test.Books.AuthorLibraryServiceSpecificBookMonitoringFixture;

namespace Chaptarr.Core.Test.ImportLists
{
    [TestFixture]
    [NonParallelizable]
    public partial class HardcoverLibraryCursorAcceptanceFixture
    {
        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void fetch_should_leave_cursor_provisional_until_items_are_accepted(bool postgres, bool empty)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var events = Proxy<IEventAggregator>((_, _) => null);
                var repository = new HardcoverLibraryImportListStateRepository(database, events);
                var definition = Definition(1, empty);
                var initial = DateTime.UtcNow.AddHours(-2);
                SeedState(repository, definition, initial, empty);
                var requests = new ConcurrentBag<string>();
                var provider = Provider(definition, repository, initial, empty ? "empty" : "positive", requests);
                var items = provider.Fetch();
                Assert.Multiple(() =>
                {
                    Assert.That(items, Has.Count.EqualTo(empty ? 0 : 1));
                    Assert.That(repository.GetByImportListId(1).CursorUpdatedAt, Is.EqualTo(initial).Within(TimeSpan.FromMilliseconds(1)));
                    Assert.That(repository.GetByImportListId(1).CursorUserBookId, Is.EqualTo(10));
                    Assert.That(repository.GetByImportListId(1).LastFullSyncAt, empty ? Is.Null : Is.Not.Null);
                    Assert.That(requests, Has.Count.EqualTo(2));
                });
            });
        }

        [TestCase(false, false, "transient")]
        [TestCase(true, false, "transient")]
        [TestCase(false, true, "transient")]
        [TestCase(true, true, "transient")]
        [TestCase(false, false, "proof-conflict")]
        [TestCase(true, false, "proof-conflict")]
        [TestCase(false, true, "proof-conflict")]
        [TestCase(true, true, "proof-conflict")]
        [TestCase(false, false, "unexpected")]
        [TestCase(true, false, "unexpected")]
        [TestCase(false, true, "unexpected")]
        [TestCase(true, true, "unexpected")]
        [TestCase(false, false, "null-response")]
        [TestCase(true, false, "null-response")]
        [TestCase(false, true, "null-response")]
        [TestCase(true, true, "null-response")]
        [TestCase(false, false, "enqueue-zero")]
        [TestCase(true, false, "enqueue-zero")]
        [TestCase(false, false, "positive")]
        [TestCase(true, false, "positive")]
        [TestCase(false, true, "positive")]
        [TestCase(true, true, "positive")]
        [TestCase(false, false, "absence")]
        [TestCase(true, false, "absence")]
        [TestCase(false, false, "excluded")]
        [TestCase(true, false, "excluded")]
        [TestCase(false, false, "disabled")]
        [TestCase(true, false, "disabled")]
        [TestCase(false, false, "empty")]
        [TestCase(true, false, "empty")]
        [TestCase(false, false, "fetch-failure")]
        [TestCase(true, false, "fetch-failure")]
        [TestCase(false, false, "partial-fetch-failure")]
        [TestCase(true, false, "partial-fetch-failure")]
        [TestCase(false, true, "healthy-neighbor")]
        [TestCase(true, true, "healthy-neighbor")]
        [TestCase(false, true, "proof-neighbor")]
        [TestCase(true, true, "proof-neighbor")]
        public void actual_sync_should_commit_only_accepted_list_demand(bool postgres, bool scheduled, string outcome)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var logs = new LogFactory();
                var memory = new NLog.Targets.MemoryTarget { Layout = "${level}|${message}|${exception:message}" };
                var logging = new NLog.Config.LoggingConfiguration(logs);
                logging.AddRule(LogLevel.Info, LogLevel.Fatal, memory);
                logs.Configuration = logging;
                var logger = logs.GetLogger("cursor-acceptance");
                var events = Proxy<IEventAggregator>((_, _) => null);
                var commands = new ConcurrentBag<Command>();
                var commandQueue = Proxy<IManageCommandQueue>((_, args) =>
                {
                    var command = (Command)args[0];
                    commands.Add(command);
                    return new CommandModel { Id = commands.Count, Body = command };
                });
                var authorRows = new AuthorRepository(database, events, logger);
                var bookRows = new BookRepository(database, events);
                var editionRows = new EditionRepository(database, events);
                var fileRows = new MediaFileRepository(database, events);
                var files = Proxy<IMediaFileService>((method, args) => method.Name == nameof(IMediaFileService.GetFilesByBooks)
                    ? fileRows.GetFilesByBooks((List<int>)args[0]) : throw new AssertionException(method.Name));
                var roots = new StubRootFolderService(BuildAudiobookRoot(@"C:\Audio".AsOsAgnostic()), BuildEbookRoot(@"C:\Ebooks".AsOsAgnostic()));
                var aliases = new ProviderAliasService(new ProviderAliasRepository(database, events));
                var primary = authorRows.Insert(new Author
                {
                    Name = "Selected Primary", CleanName = "selectedprimary", SortName = "Selected Primary", TitleSlug = "selected-primary",
                    HardcoverAuthorId = "hc:777", Path = @"C:\Audio\Selected Primary".AsOsAgnostic(),
                    AudiobookRootFolderPath = @"C:\Audio".AsOsAgnostic(), EbookRootFolderPath = @"C:\Ebooks".AsOsAgnostic(),
                    AudiobookQualityProfileId = 1, AudiobookMetadataProfileId = 1,
                    AudiobookMonitored = true, Added = DateTime.UtcNow
                });
                IBookService books = null;
                var authors = new AuthorService(authorRows, events, new StubAuthorPathBuilder(), roots, commandQueue, new CacheManager(),
                    bookRows, files, logger, null, null, new Lazy<IBookService>(() => books), mainDatabase: database, aliases);
                var editions = new EditionService(editionRows, events, logger);
                books = new BookService(bookRows, editions, events, authors, files, roots,
                    new SeriesBookLinkRepository(database, events), null, logger, providerAliasService: aliases);
                var stateRows = new HardcoverLibraryImportListStateRepository(database, events);
                var definition = Definition(1, outcome == "empty");
                if (outcome == "disabled") ((HardcoverLibraryImportListSettings)definition.Settings).MonitorAudiobooks = false;
                if (outcome == "partial-fetch-failure") ((HardcoverLibraryImportListSettings)definition.Settings).ImportOwned = true;
                var initial = DateTime.UtcNow.AddHours(-2);
                SeedState(stateRows, definition, initial, outcome == "empty");
                var providerRequests = new ConcurrentBag<string>();
                var provider = Provider(definition, stateRows, initial, outcome, providerRequests);
                var neighborDefinition = Definition(2, false);
                var neighbor = Provider(neighborDefinition, stateRows, initial, "positive", providerRequests);
                if (outcome is "healthy-neighbor" or "proof-neighbor") SeedState(stateRows, neighborDefinition, initial);
                var instanceReads = 0;
                var factory = Proxy<IImportListFactory>((method, args) => method.Name switch
                {
                    nameof(IImportListFactory.Get) => definition,
                    nameof(IImportListFactory.GetInstance) => CountInstance((ImportListDefinition)args[0]),
                    nameof(IImportListFactory.AutomaticAddEnabled) => outcome is "healthy-neighbor" or "proof-neighbor"
                        ? new List<IImportList> { provider, neighbor } : new List<IImportList> { provider },
                    _ => throw new AssertionException(method.Name)
                });
                IImportList CountInstance(ImportListDefinition requested)
                {
                    instanceReads++;
                    return Provider(requested, stateRows, initial, requested.Id == definition.Id ? outcome : "positive", providerRequests);
                }
                var status = Proxy<IImportListStatusService>((_, _) => null);
                var fetcher = new FetchAndParseImportListService(factory, status, logger);
                var metadataCalls = new ConcurrentBag<(string Method, string Id, BookMediaType Media)>();
                var unavailable = outcome is "transient" or "unexpected" or "null-response";
                var proofMetadata = BookInfoProxyCanonicalLookupFixture.CreateProxy(Proxy<IHttpClient>((method, args) =>
                    new HttpResponse((HttpRequest)args[0], new HttpHeader { ContentType = "application/json" },
                        "{\"work\":{\"id\":2002,\"title\":\"Different Work\",\"hardcoverWorkId\":\"2002\"},\"editions\":[{\"id\":9001,\"title\":\"Different Work\",\"language\":\"eng\",\"formatType\":\"audiobook\",\"readingFormatId\":2,\"providerIds\":{\"hardcover\":\"9001\"}}],\"authors\":[{\"id\":777,\"name\":\"Selected Primary\",\"providerIds\":{\"hc\":\"777\"}}],\"series\":[]}", HttpStatusCode.OK)));
                var metadata = Proxy<IProvideBookInfo>((method, args) =>
                {
                    metadataCalls.Add((method.Name, args[0]?.ToString(), (BookMediaType)args[1]));
                    if (unavailable)
                    {
                        if (outcome == "unexpected") throw new InvalidOperationException("Unexpected metadata failure");
                        if (outcome == "null-response") return BookInfoProxyCanonicalLookupFixture.CreateProxy(Proxy<IHttpClient>((_, requestArgs) =>
                            new HttpResponse((HttpRequest)requestArgs[0], new HttpHeader { ContentType = "application/json" }, "null", HttpStatusCode.OK)))
                            .GetWorkInfo((string)args[0], (BookMediaType)args[1]);
                        throw new BookInfoException("Metadata work remains pending");
                    }
                    if (outcome == "healthy-neighbor" && args[0]?.ToString() == "hc:1001") throw new BookInfoException("Metadata work remains pending");
                    if (outcome == "proof-conflict" || outcome == "proof-neighbor" && args[0]?.ToString() == "hc:1001") return proofMetadata.GetWorkInfo((string)args[0], (BookMediaType)args[1]);
                    if (outcome == "absence") throw new NzbDrone.Core.Exceptions.BookNotFoundException(args[0]?.ToString());
                    return Tuple.Create(primary.HardcoverAuthorId, new Book
                    {
                        Author = primary, Title = "Listed Work", HardcoverBookId = args[0]?.ToString(),
                        MediaType = BookMediaType.Audiobook, Editions = new List<Edition>()
                    }, new List<Author> { primary });
                });
                var pendingRows = new PendingAuthorImportRepository(database, events);
                var durablePending = new PendingAuthorImportService(pendingRows, authors, events, logger, aliases);
                var enqueueCalls = new ConcurrentBag<string>();
                var pending = Proxy<IPendingAuthorImportService>((method, args) =>
                {
                    if (method.Name != nameof(IPendingAuthorImportService.EnqueueAsync)) throw new AssertionException(method.Name);
                    enqueueCalls.Add((string)args[0]);
                    return outcome == "enqueue-zero" ? Task.FromResult(0) :
                        durablePending.EnqueueAsync((string)args[0], (MonitoringConfig)args[1], (string)args[2]);
                });
                var exclusionRows = new ImportListExclusionRepository(database, events);
                if (outcome == "excluded") exclusionRows.Insert(new ImportListExclusion { ForeignId = "hc:1001", Name = "Listed Work", MediaType = BookMediaType.Audiobook });
                var exclusions = new ImportListExclusionService(exclusionRows, logger);
                void Sync()
                {
                    var service = new ImportListSyncService(factory, exclusions, fetcher, metadata, authors, books, editions, pending, roots,
                        new RootFolderSettingsResolver(roots), events, commandQueue, logger, new ImportListBookIdentityCacheRepository(database, events));
                    if (scheduled) service.Execute(new HardcoverLibrarySyncCommand { Trigger = CommandTrigger.Manual });
                    else service.Execute(new ImportListSyncCommand(definition.Id));
                }
                Sync();
                var held = outcome is "transient" or "unexpected" or "null-response" or "enqueue-zero" or "fetch-failure" or "partial-fetch-failure" or "healthy-neighbor";
                var saved = stateRows.GetByImportListId(1);
                var acceptedDemand = outcome is "positive" or "partial-fetch-failure" or "healthy-neighbor" or "proof-neighbor";
                var expectedEnqueues = acceptedDemand || outcome == "enqueue-zero" ? 1 : 0;
                Assert.Multiple(() =>
                {
                    if (outcome == "empty")
                    {
                        Assert.That(saved.CursorUpdatedAt, Is.GreaterThan(initial));
                        Assert.That(saved.LastFullSyncAt, Is.Not.Null);
                    }
                    else
                    {
                        Assert.That(saved.CursorUpdatedAt, Is.EqualTo(held ? initial : initial.AddHours(1)).Within(TimeSpan.FromMilliseconds(1)));
                    }
                    Assert.That(saved.CursorUserBookId, Is.EqualTo(outcome == "empty" ? 0 : held ? 10 : 11));
                    Assert.That(instanceReads, Is.EqualTo(scheduled ? 0 : 1));
                    Assert.That(enqueueCalls, Has.Count.EqualTo(expectedEnqueues));
                    Assert.That(enqueueCalls, Is.All.EqualTo(primary.HardcoverAuthorId));
                    Assert.That(pendingRows.All(), Has.Count.EqualTo(acceptedDemand ? 1 : 0));
                    Assert.That(authorRows.All(), Has.Count.EqualTo(1));
                    Assert.That(bookRows.All(), Is.Empty);
                    Assert.That(editionRows.All(), Is.Empty);
                    Assert.That(commands, Is.All.TypeOf<ProcessPendingImportsCommand>());
                    Assert.That(commands, Has.Count.EqualTo(acceptedDemand ? 1 : 0));
                });
                if (outcome is "healthy-neighbor" or "proof-neighbor")
                {
                    Assert.That(stateRows.GetByImportListId(2).CursorUserBookId, Is.EqualTo(11));
                    Assert.That(pendingRows.All().Single().AudiobookBooksToMonitor, Does.Contain("hc:1002").And.Not.Contain("hc:1001"));
                }
                if (outcome is "transient" or "unexpected" or "null-response")
                {
                    unavailable = false;
                    Sync();
                    Sync();
                    Assert.Multiple(() =>
                    {
                        Assert.That(metadataCalls, Has.Count.EqualTo(2));
                        Assert.That(metadataCalls.Select(call => call.Method), Is.All.EqualTo(nameof(IProvideBookInfo.GetWorkInfo)));
                        Assert.That(metadataCalls.Select(call => call.Id), Is.All.EqualTo("hc:1001"));
                        Assert.That(metadataCalls.Select(call => call.Media), Is.All.EqualTo(BookMediaType.Audiobook));
                        Assert.That(pendingRows.All(), Has.Count.EqualTo(1));
                        Assert.That(stateRows.GetByImportListId(1).CursorUserBookId, Is.EqualTo(11));
                        Assert.That(enqueueCalls, Is.EqualTo(new[] { primary.HardcoverAuthorId }));
                    });
                }
                if (outcome == "proof-conflict")
                {
                    Sync();
                    Assert.Multiple(() =>
                    {
                        Assert.That(metadataCalls, Has.Count.EqualTo(1), "Rejected immutable source evidence must not hold the delta for automatic replay.");
                        Assert.That(memory.Logs.Count(message => message.StartsWith("Warn|") && message.Contains("rejected: requested work does not match")), Is.EqualTo(1));
                        Assert.That(memory.Logs.Any(message => message.Contains("1 source evidence rejections")), Is.True);
                        Assert.That(memory.Logs.Any(message => message.Contains("keeping the previous cursor")), Is.False);
                        Assert.That(stateRows.GetByImportListId(1).CursorUserBookId, Is.EqualTo(11));
                        Assert.That(pendingRows.All(), Is.Empty);
                    });
                }
                TestContext.WriteLine("{0}: {1}, {2}, provider requests {3}, metadata reads {4}, durable demands {5}",
                    postgres ? "PostgreSQL" : "SQLite", scheduled ? "scheduled" : "manual", outcome,
                    providerRequests.Count, metadataCalls.Count, pendingRows.All().Count());
            });
        }

        private static ImportListDefinition Definition(int id, bool empty) => new()
        {
            Id = id, Name = "Cursor List", Implementation = nameof(HardcoverLibraryImportList), EnableAutomaticAdd = true,
            ShouldMonitor = ImportListMonitorType.SpecificBook, ShouldMonitorExisting = true, RootFolderPath = @"C:\Audio".AsOsAgnostic(),
            MonitoringOptions = new ImportListMonitoringOptions { AddMissingAuthors = true },
            Settings = new HardcoverLibraryImportListSettings { ApiToken = "isolated-test-token", ImportWantToRead = true,
                ImportCurrentlyReading = false, ImportRead = false, ImportOwned = false, MonitorAudiobooks = true, MonitorEbooks = false,
                AudiobookRootFolderPath = @"C:\Audio".AsOsAgnostic() }
        };

        [TestCase(false)]
        [TestCase(true)]
        public void failed_fetch_should_discard_an_earlier_uncommitted_cursor(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var events = Proxy<IEventAggregator>((_, _) => null);
                var rows = new HardcoverLibraryImportListStateRepository(database, events);
                var definition = Definition(1, false);
                var initial = DateTime.UtcNow.AddHours(-2);
                SeedState(rows, definition, initial);
                var provider = Provider(definition, rows, initial, "positive", new ConcurrentBag<string>());
                Assert.That(provider.Fetch(), Has.Count.EqualTo(1));
                ((HardcoverLibraryImportListSettings)definition.Settings).ApiToken = null;
                Assert.That(provider.Fetch(), Is.Empty);
                provider.CommitState();
                Assert.That(rows.GetByImportListId(1).CursorUserBookId, Is.EqualTo(10));
                Assert.That(rows.GetByImportListId(1).CursorUpdatedAt, Is.EqualTo(initial).Within(TimeSpan.FromMilliseconds(1)));
            });
        }

        private static void SeedState(HardcoverLibraryImportListStateRepository rows, ImportListDefinition definition, DateTime initial, bool full = false)
        {
            var settings = (HardcoverLibraryImportListSettings)definition.Settings;
            rows.Insert(new HardcoverLibraryImportListState
            {
                ImportListId = definition.Id, HardcoverUserId = 5,
                SettingsSignature = $"owned:{settings.ImportOwned};wtr:True;cr:False;read:False;ma:{settings.MonitorAudiobooks};me:False",
                CursorUpdatedAt = initial, CursorUserBookId = 10, OwnedCursorListBookId = 0,
                LastFullSyncAt = full ? null : initial.AddDays(-1), CreatedAt = initial
            });
        }

        private static HardcoverLibraryImportList Provider(ImportListDefinition definition,
            HardcoverLibraryImportListStateRepository rows, DateTime initial, string outcome, ConcurrentBag<string> requests)
        {
            var http = Proxy<IHttpClient>((method, args) =>
            {
                var request = (HttpRequest)args[0];
                using var content = JsonDocument.Parse(Encoding.UTF8.GetString(request.ContentData));
                var query = content.RootElement.GetProperty("query").GetString();
                requests.Add(query.Contains("query Me") ? "Me" : "UserBooks");
                var passedCursor = query.Contains("query UserBooksDelta") && content.RootElement.GetProperty("variables").GetProperty("afterId").GetInt32() >= 11;
                var body = query.Contains("query Me") ? "{\"data\":{\"me\":[{\"id\":5,\"username\":\"isolated\"}]}}" :
                    outcome == "fetch-failure" || outcome == "partial-fetch-failure" && query.Contains("query OwnedList")
                        ? "{\"errors\":[{\"message\":\"isolated upstream failure\"}]}" :
                    outcome == "empty" || passedCursor ? "{\"data\":{\"user_books\":[]}}" :
                    JsonSerializer.Serialize(new { data = new { user_books = new[] { new
                    {
                        id = 11, updated_at = initial.AddHours(1), status_id = 1,
                        book = new { id = 1000 + definition.Id, title = "Listed Work", contributions = new[] { new
                        {
                            author_id = 888, contribution = "Author", author = new { id = 888, name = "Listed Contributor" }
                        } } }
                    } } } });
                return new HttpResponse(request, new HttpHeader { ContentType = "application/json" }, body, HttpStatusCode.OK);
            });
            var status = Proxy<IImportListStatusService>((_, _) => null);
            return new HardcoverLibraryImportList(http, null, null, null, null, null, rows, status,
                Proxy<IConfigService>((method, _) => throw new AssertionException(method.Name)), null, LogManager.GetCurrentClassLogger())
                { Definition = definition };
        }
    }
}
