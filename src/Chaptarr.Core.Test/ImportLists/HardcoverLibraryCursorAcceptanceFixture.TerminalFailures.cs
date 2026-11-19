using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
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
using NzbDrone.Core.Datastore;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.ImportLists.Hardcover.Library;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RootFolders;
using static Chaptarr.Core.Test.Common.InterfaceProxy;
using static Chaptarr.Core.Test.Books.AuthorLibraryServiceSpecificBookMonitoringFixture;

namespace Chaptarr.Core.Test.ImportLists
{
    public partial class HardcoverLibraryCursorAcceptanceFixture
    {
        [TestCase(false, "terminal")]
        [TestCase(true, "terminal")]
        [TestCase(false, "invalid")]
        [TestCase(true, "invalid")]
        [TestCase(false, "plain400")]
        [TestCase(true, "plain400")]
        [TestCase(false, "plain409")]
        [TestCase(true, "plain409")]
        [TestCase(false, "pending")]
        [TestCase(true, "pending")]
        [TestCase(false, "malformed")]
        [TestCase(true, "malformed")]
        [TestCase(false, "null")]
        [TestCase(true, "null")]
        [TestCase(false, "unexpected")]
        [TestCase(true, "unexpected")]
        public void actual_metadata_failures_should_commit_only_completed_rejections_and_stop_overdue_full_replay(bool postgres, string outcome)
        {
            var readerCounts = new List<List<int>>();
            foreach (var count in new[] { 1, 25 })
            {
                DualDatabaseFixture.Run(postgres, originalDatabase =>
                {
                    var readers = new List<string>();
                    var database = Proxy<IMainDatabase>((method, args) =>
                    {
                        var result = method.Invoke(originalDatabase, args);
                        return method.Name == nameof(IMainDatabase.OpenConnection)
                            ? CommandObserver.Wrap((IDbConnection)result, command => readers.Add(command.CommandText)) : result;
                    });
                    using var logs = new LogFactory();
                    var memory = new NLog.Targets.MemoryTarget { Layout = "${level}|${message}|${exception:message}" };
                    var logging = new NLog.Config.LoggingConfiguration(logs);
                    logging.AddRule(LogLevel.Info, LogLevel.Fatal, memory);
                    logs.Configuration = logging;
                    var logger = logs.GetLogger("terminal-cursor");
                    var events = Proxy<IEventAggregator>((_, _) => null);
                    var commands = new ConcurrentBag<Command>();
                    var queue = Proxy<IManageCommandQueue>((_, args) =>
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
                        AudiobookQualityProfileId = 1, AudiobookMetadataProfileId = 1, AudiobookMonitored = true, Added = DateTime.UtcNow
                    });
                    IBookService books = null;
                    var authors = new AuthorService(authorRows, events, new StubAuthorPathBuilder(), roots, queue, new CacheManager(),
                        bookRows, files, logger, null, null, new Lazy<IBookService>(() => books), mainDatabase: database, aliases);
                    var editions = new EditionService(editionRows, events, logger);
                    books = new BookService(bookRows, editions, events, authors, files, roots,
                        new SeriesBookLinkRepository(database, events), null, logger, providerAliasService: aliases);
                    var stateRows = new HardcoverLibraryImportListStateRepository(database, events);
                    var definition = Definition(1, false);
                    var initial = DateTime.UtcNow.AddHours(-2);
                    SeedState(stateRows, definition, initial);
                    var initialState = stateRows.GetByImportListId(1);
                    initialState.LastFullSyncAt = initial.AddDays(-8);
                    stateRows.Update(initialState);
                    var lastFull = stateRows.GetByImportListId(1).LastFullSyncAt;
                    var providerRequests = new ConcurrentBag<string>();
                    var provider = TerminalProvider(definition, stateRows, initial, count, providerRequests, logger);
                    var factory = Proxy<IImportListFactory>((method, _) => method.Name switch
                    {
                        nameof(IImportListFactory.Get) => definition,
                        nameof(IImportListFactory.GetInstance) => provider,
                        _ => throw new AssertionException(method.Name)
                    });
                    var fetcher = new FetchAndParseImportListService(factory, Proxy<IImportListStatusService>((_, _) => null), logger);
                    var metadataRequests = new ConcurrentBag<string>();
                    var metadata = BookInfoProxyCanonicalLookupFixture.CreateProxy(Proxy<IHttpClient>((method, args) =>
                    {
                        var request = (HttpRequest)args[0];
                        var requestedUrl = Uri.UnescapeDataString(request.Url.ToString());
                        metadataRequests.Add(requestedUrl);
                        var healthy = requestedUrl.Contains("hc:9999", StringComparison.Ordinal);
                        if (!healthy && outcome == "unexpected") throw new InvalidOperationException("Unexpected metadata failure");
                        var status = healthy ? HttpStatusCode.OK : outcome switch
                        {
                            "terminal" or "plain409" => HttpStatusCode.Conflict,
                            "invalid" or "plain400" => HttpStatusCode.BadRequest,
                            "pending" => HttpStatusCode.Accepted,
                            _ => HttpStatusCode.OK
                        };
                        var body = healthy ? "{\"work\":{\"id\":9999,\"title\":\"Healthy Listed Work\",\"hardcoverWorkId\":\"9999\"},\"editions\":[{\"id\":99991,\"title\":\"Healthy Listed Work\",\"language\":\"eng\",\"formatType\":\"audiobook\",\"readingFormatId\":2,\"narrator\":\"Fixture Narrator\",\"providerIds\":{\"hardcover\":\"99991\"}}],\"authors\":[{\"id\":777,\"name\":\"Selected Primary\",\"providerIds\":{\"hc\":\"777\"}}],\"series\":[]}"
                            : outcome is "terminal" or "invalid" ? JsonSerializer.Serialize(new
                            {
                                code = outcome == "terminal" ? "work_rescue_terminal" : "invalid_provider_id",
                                providerId = Uri.UnescapeDataString(request.Url.Path.Split('/').Last()),
                                message = outcome == "terminal" ? "Declared work rescue stop" : "Invalid provider identifier",
                                retryable = false
                            })
                            : outcome == "plain409" ? "Declared work rescue stop"
                            : outcome == "plain400" ? "Invalid provider identifier"
                            : outcome == "malformed" ? "not JSON" : "null";
                        return new HttpResponse(request, new HttpHeader { ContentType = "application/json" }, body, status);
                    }));
                    var pendingRows = new PendingAuthorImportRepository(database, events);
                    var pending = new PendingAuthorImportService(pendingRows, authors, events, logger, aliases);
                    var exclusions = new ImportListExclusionService(new ImportListExclusionRepository(database, events), logger);
                    var costs = new List<int>();
                    void Sync()
                    {
                        readers.Clear();
                        new ImportListSyncService(factory, exclusions, fetcher, metadata, authors, books, editions, pending, roots,
                            new RootFolderSettingsResolver(roots), events, queue, logger,
                            new ImportListBookIdentityCacheRepository(database, events)).Execute(new ImportListSyncCommand(1));
                        costs.Add(readers.Count);
                    }
                    var completed = outcome is "terminal" or "invalid";
                    Sync();
                    var saved = stateRows.GetByImportListId(1);
                    Assert.Multiple(() =>
                    {
                        Assert.That(saved.CursorUserBookId, Is.EqualTo(completed ? 11 + count : 10));
                        Assert.That(saved.CursorUpdatedAt, Is.EqualTo(completed ? initial.AddHours(1) : initial).Within(TimeSpan.FromMilliseconds(1)));
                        Assert.That(saved.LastFullSyncAt, completed ? Is.GreaterThan(lastFull) : Is.EqualTo(lastFull));
                        Assert.That(pendingRows.All(), Has.Count.EqualTo(1), "A failed independent work must not prevent durable acceptance of the healthy work.");
                        var request = pendingRows.All().Single();
                        Assert.That(request.ProviderId, Is.EqualTo(primary.HardcoverAuthorId));
                        Assert.That(JsonSerializer.Deserialize<List<string>>(request.AudiobookBooksToMonitor), Is.EqualTo(new[] { "hc:9999" }));
                        Assert.That(request.AudiobookNarrations, Is.Null);
                        Assert.That(request.EbookStatus, Is.EqualTo(PendingImportStatus.NotRequested));
                        Assert.That(authorRows.All(), Has.Count.EqualTo(1));
                        Assert.That(bookRows.All(), Is.Empty);
                        Assert.That(editionRows.All(), Is.Empty);
                        Assert.That(metadataRequests, Has.Count.EqualTo(count + 1));
                        Assert.That(providerRequests.Count(value => value == "Full"), Is.EqualTo(1));
                    });
                    Sync();
                    Assert.Multiple(() =>
                    {
                        Assert.That(stateRows.GetByImportListId(1).CursorUserBookId, Is.EqualTo(completed ? 11 + count : 10));
                        Assert.That(providerRequests.Count(value => value == "Full"), Is.EqualTo(completed ? 1 : 2));
                        Assert.That(providerRequests.Count(value => value == "Delta"), Is.EqualTo(completed ? 1 : 0));
                        Assert.That(metadataRequests.Count(value => !value.Contains("hc:9999", StringComparison.Ordinal)), Is.EqualTo(completed ? count : count * 2));
                        Assert.That(pendingRows.All(), Has.Count.EqualTo(1));
                        Assert.That(commands, Is.All.TypeOf<ProcessPendingImportsCommand>());
                        Assert.That(commands, Has.Count.EqualTo(completed ? 1 : 2));
                        var reason = outcome == "terminal" ? "Declared work rescue stop" : "Invalid provider identifier";
                        Assert.That(memory.Logs.Count(value => value.StartsWith("Warn|", StringComparison.Ordinal) && value.Contains("rejected:", StringComparison.Ordinal) && value.Contains(reason, StringComparison.Ordinal)), Is.EqualTo(completed ? count : 0));
                        Assert.That(memory.Logs.Any(value => value.Contains($"{count} source evidence rejections", StringComparison.Ordinal)), Is.EqualTo(completed));
                        Assert.That(memory.Logs.Any(value => value.Contains("cursor unchanged", StringComparison.Ordinal)), Is.EqualTo(!completed));
                    });
                    readerCounts.Add(costs);
                    TestContext.WriteLine("{0}/{1}: {2} refused works + one healthy work; readers [{3}], metadata HTTP requests {4}, full reads {5}, durable requests {6}",
                        postgres ? "PostgreSQL" : "SQLite", outcome, count, string.Join(",", costs), metadataRequests.Count,
                        providerRequests.Count(value => value == "Full"), pendingRows.All().Count());
                });
            }
            Assert.That(readerCounts[1], Is.EqualTo(readerCounts[0]), "Database readers must remain batched as independent source refusals grow from one to 25.");
        }

        private static HardcoverLibraryImportList TerminalProvider(ImportListDefinition definition,
            HardcoverLibraryImportListStateRepository rows, DateTime initial, int count, ConcurrentBag<string> requests, Logger logger)
        {
            var http = Proxy<IHttpClient>((_, args) =>
            {
                var request = (HttpRequest)args[0];
                using var content = JsonDocument.Parse(Encoding.UTF8.GetString(request.ContentData));
                var query = content.RootElement.GetProperty("query").GetString();
                var me = query.Contains("query Me", StringComparison.Ordinal);
                var delta = query.Contains("query UserBooksDelta", StringComparison.Ordinal);
                var firstPage = !me && !delta && content.RootElement.GetProperty("variables").GetProperty("beforeId").GetInt32() == int.MaxValue;
                requests.Add(me ? "Me" : delta ? "Delta" : firstPage ? "Full" : "FullPage");
                var body = me ? "{\"data\":{\"me\":[{\"id\":5,\"username\":\"isolated\"}]}}" : delta || !firstPage
                    ? "{\"data\":{\"user_books\":[]}}"
                    : JsonSerializer.Serialize(new { data = new { user_books = Enumerable.Range(0, count + 1).Select(index => new
                    {
                        id = 11 + index, updated_at = initial.AddHours(1), status_id = 1,
                        book = new { id = index == count ? 9999 : 2001 + index, title = index == count ? "Healthy Listed Work" : "Refused Listed Work",
                            contributions = new[] { new { author_id = 888, contribution = "Author", author = new { id = 888, name = "Listed Contributor" } } } }
                    }).ToArray() } });
                return new HttpResponse(request, new HttpHeader { ContentType = "application/json" }, body, HttpStatusCode.OK);
            });
            return new HardcoverLibraryImportList(http, null, null, null, null, null, rows,
                Proxy<IImportListStatusService>((_, _) => null), Proxy<IConfigService>((method, _) => throw new AssertionException(method.Name)), null, logger)
                { Definition = definition };
        }
    }
}
