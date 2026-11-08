using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using Chaptarr.Core.Test.Common;
using Dapper;
using Newtonsoft.Json;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo;
using NzbDrone.Core.MetadataSource.BookInfo.V5;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Profiles.Metadata;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Books
{
    public partial class AuthorSyncOptionsFixture
    {
        private static RefreshAuthorService HydrationRefresh(Harness h, IMainDatabase database, IProvideAuthorInfo info,
            RescanAfterRefreshType rescanPolicy = RescanAfterRefreshType.Always)
        {
            var files = Proxy<IMediaFileService>((_, _) => new List<BookFile>());
            var selector = new EditionSelector(h.Logger);
            var filter = new EditionMetadataProfileFilter(new TestTermMatcherService());
            var editions = new RefreshEditionService(h.Editions, Proxy<IMetadataTagService>((_, _) => null), null, database, h.Logger);
            var books = new RefreshBookService(h.Books, h.Authors, h.Roots, h.Editions, info, null,
                editions, files, null, h.Events, null, selector, filter, Proxy<IMapCoversToLocal>((_, _) => null), h.Logger, mainDatabase: null);
            var config = Proxy<IConfigService>((method, _) => method.Name == "get_RescanAfterRefresh"
                ? rescanPolicy
                : method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null);
            return new RefreshAuthorService(MetadataHealthGateStub.Ready(), info, h.Authors, h.Books, h.Editions, h.Profiles, books, null,
                h.Events, h.Queue, files, new global::NzbDrone.Core.History.HistoryRepository(database, h.Events), h.Roots, null, Proxy<IMonitorNewBookService>((_, _) => false), config,
                Proxy<IImportListExclusionService>((_, _) => new List<ImportListExclusion>()),
                new AuthorSyncMetadataService(new AuthorSyncMetadataRepository(database, h.Events), h.Logger),
                null, null, h.Logger, editionSelector: selector, mainDatabase: database, bookRepository: new BookRepository(database, h.Events), authorRepository: new AuthorRepository(database, h.Events, h.Logger));
        }

        [Test]
        public void refresh_recovers_saved_setup_only_after_catalog_reconciliation(
            [Values(false, true)] bool postgres,
            [Values("error", "notFound", "unchanged", "exception", "write")] string unavailable,
            [Values("audiobook", "ebook")] string missingSide)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var messages = new MemoryTarget { Layout = "${level}|${message}|${exception:format=tostring}" };
                var logConfig = new LoggingConfiguration();
                logConfig.AddRule(LogLevel.Warn, LogLevel.Fatal, messages);
                using var logFactory = new LogFactory { Configuration = logConfig };
                var h = new Harness(database, logger: logFactory.GetLogger("HydrationRecovery"));
                h.Import(h.Request(missingSide == "ebook" ? "audiobook" : "ebook", false));
                h.Scan();
                h.Commands.Clear();
                h.MetadataError = new BookInfoException("Metadata unavailable");
                var request = h.Request(missingSide, false);
                request.SearchForMissingBooks = true;
                h.Import(request);
                var sync = new AuthorSyncMetadataService(new AuthorSyncMetadataRepository(database, h.Events), h.Logger);
                sync.CreateOrUpdateSyncMetadata(h.Author.Id, "hc:123456", "existing-etag");
                var ready = false;
                var calls = 0;
                var info = Proxy<IProvideAuthorInfo>((method, args) =>
                {
                    if (method.Name == nameof(IProvideAuthorInfo.GetAuthorInfo))
                        return h.Info.GetAuthorInfo((string)args[0]);
                    Assert.That(method.Name, Is.EqualTo(nameof(IProvideAuthorInfo.RefreshAuthorInfo)));
                    Assert.That(args[1], Is.EqualTo(unavailable == "write" && calls == 2 ? null : "existing-etag"),
                        "A failed catalog write clears the ETag so the next refresh requests the complete payload.");
                    Assert.That(args[4], Is.True, "An unchanged server catalog still needs to populate the missing local format.");
                    calls++;
                    if (ready) return RefreshResult.Updated(h.Info.GetAuthorInfo("hc:123456"), "existing-etag");
                    return unavailable switch
                    {
                        "error" => RefreshResult.Error("Unavailable", HttpStatusCode.ServiceUnavailable),
                        "notFound" => RefreshResult.NotFound(),
                        "unchanged" => RefreshResult.NoChanges("existing-etag"),
                        _ => throw new BookInfoException("Metadata unavailable")
                    };
                });
                var refresh = HydrationRefresh(h, database, info);
                refresh.Execute(new RefreshAuthorCommand(h.Author.Id) { Trigger = CommandTrigger.Manual });
                Assert.That(h.Commands.OfType<RescanFoldersCommand>().Count(), Is.EqualTo(1));
                h.Scan();
                Assert.That(h.Author.AddOptions?.SearchForMissingBooks, Is.True);
                Assert.That(h.Author.AddOptions?.AwaitingMetadata, Is.True);
                Assert.That(h.SearchTargets, Is.Empty);

                ready = true;
                h.MetadataError = null;
                if (unavailable == "write")
                {
                    using var connection = database.OpenConnection();
                    connection.Execute(postgres
                        ? "CREATE FUNCTION reject_catalog_insert() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'fixture catalog failure'; END; $$; CREATE TRIGGER reject_catalog_insert BEFORE INSERT ON \"Books\" FOR EACH ROW EXECUTE FUNCTION reject_catalog_insert();"
                        : "CREATE TRIGGER reject_catalog_insert BEFORE INSERT ON Books BEGIN SELECT RAISE(ABORT, 'fixture catalog failure'); END;");
                    try
                    {
                        refresh.Execute(new RefreshAuthorCommand(h.Author.Id) { Trigger = CommandTrigger.Manual });
                        h.Scan();
                        Assert.That(messages.Logs.Any(message => message.Contains("fixture catalog failure")), Is.True);
                        Assert.That(h.Author.AddOptions?.AwaitingMetadata, Is.True);
                        Assert.That(h.Author.AddOptions?.SearchForMissingBooks, Is.True);
                        Assert.That(h.SearchTargets, Is.Empty);
                    }
                    finally
                    {
                        connection.Execute(postgres
                            ? "DROP TRIGGER reject_catalog_insert ON \"Books\"; DROP FUNCTION reject_catalog_insert();"
                            : "DROP TRIGGER reject_catalog_insert;");
                    }
                }
                refresh.Execute(new RefreshAuthorCommand(h.Author.Id) { Trigger = CommandTrigger.Manual });
                Assert.That(calls, Is.EqualTo(unavailable == "write" ? 3 : 2));
                Assert.That(h.Author.AddOptions?.AwaitingMetadata, Is.False, string.Join(Environment.NewLine, messages.Logs));
                Assert.That(h.Author.AddOptions?.SearchForMissingBooks, Is.True);
                Assert.That(h.Books.GetBooksByAuthor(h.Author.Id).Select(book => book.MediaType).Distinct().Count(), Is.EqualTo(2));
                Assert.That(h.SearchTargets, Is.Empty);
                h.Scan();
                h.Scan();
                Assert.That(h.Author.AddOptions, Is.Null);
                Assert.That(h.SearchTargets, Has.Count.EqualTo(1));
                Assert.That(h.SearchTargets.Single(), Is.EqualTo(h.BookRows.GetBooksByAuthorId(h.Author.Id)
                    .Where(book => book.MediaType == (missingSide == "ebook" ? BookMediaType.Ebook : BookMediaType.Audiobook)).Select(book => book.Id).ToArray()));
            });
        }

        [Test]
        public void bulk_refresh_keeps_unavailable_setup_and_finishes_an_empty_ready_catalog([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var messages = new MemoryTarget { Layout = "${level}|${message}|${exception:format=tostring}" };
                var logConfig = new LoggingConfiguration();
                logConfig.AddRule(LogLevel.Warn, LogLevel.Fatal, messages);
                using var logFactory = new LogFactory { Configuration = logConfig };
                var h = new Harness(database, logger: logFactory.GetLogger("BulkHydrationRecovery"));
                h.Import(h.Request("audiobook", false));
                h.Scan();
                h.MetadataError = new BookInfoException("Metadata unavailable");
                var request = h.Request("ebook", false);
                request.SearchForMissingBooks = true;
                h.Import(request);
                Author AddOther(string id, bool awaiting) => h.Authors.AddAuthor(new Author
                {
                    Name = "Other Writer " + id, Overview = string.Empty, HardcoverAuthorId = "hc:" + id,
                    AudiobookRootFolderPath = h.AudioRoot.Path, AudiobookQualityProfileId = 3, AudiobookMetadataProfileId = 2,
                    AudiobookMonitored = true, SyncMonitoredAcrossFormats = false,
                    AddOptions = awaiting ? new AddAuthorOptions
                    {
                        AwaitingMetadata = true, AudiobookMonitor = MonitorTypes.All, SearchForMissingBooks = true
                    } : null
                }, false);
                var ready = AddOther("654321", true);
                var settled = AddOther("654322", false);
                var sync = new AuthorSyncMetadataService(new AuthorSyncMetadataRepository(database, h.Events), h.Logger);
                foreach (var author in new[] { h.Author, ready, settled })
                    sync.CreateOrUpdateSyncMetadata(author.Id, author.HardcoverAuthorId, "existing-etag");

                var diffs = new List<V5AuthorETag>();
                var fetched = new List<string>();
                var http = Proxy<IHttpClient>((method, args) =>
                {
                    var call = (HttpRequest)args[0];
                    var headers = new HttpHeader { ContentType = "application/json" };
                    if (method.Name == nameof(IHttpClient.Execute))
                    {
                        diffs.AddRange(JsonConvert.DeserializeObject<V5AuthorChangesRequest>(Encoding.UTF8.GetString(call.ContentData)).Items);
                        return new HttpResponse(call, headers, JsonConvert.SerializeObject(new V5AuthorChangesResponse
                        {
                            Changed = diffs.Where(item => item.ETag == string.Empty)
                                .Select(item => new V5ChangedAuthor { RequestedId = item.RequestedId, CanonicalId = item.RequestedId, ETag = "existing-etag" }).ToList()
                        }), HttpStatusCode.OK);
                    }
                    Assert.That(method.Name, Is.EqualTo(nameof(IHttpClient.Get)));
                    Assert.That(call.Headers.GetSingleValue("If-None-Match"), Is.Null);
                    fetched.Add(Uri.UnescapeDataString(call.Url.ToString()));
                    if (fetched.Last().Contains("hc:123456"))
                        return new HttpResponse(call, headers, "{}", HttpStatusCode.Accepted);
                    return new HttpResponse(call, headers, JsonConvert.SerializeObject(new V5AuthorResponse
                    {
                        Author = new V5AuthorData { Id = "hc:654321", HardcoverAuthorId = "hc:654321", Name = ready.Name, Bio = string.Empty }
                    }), HttpStatusCode.OK);
                });
                var config = Proxy<IConfigService>((method, _) => method.Name == "get_MetadataServerUrl" ? "https://metadata.invalid" : null);
                var info = new BookInfoProxy(http, null, null, null, null, h.Authors, h.Books, h.Editions, config, null,
                    h.Logger, new CacheManager(), new MetadataServerHealthGate(config, new MetadataServerHealthService(h.Logger), h.Logger));
                HydrationRefresh(h, database, info).Execute(new BulkRefreshAuthorCommand(new List<int> { h.Author.Id, ready.Id, settled.Id }));
                Assert.That(diffs.Single(item => item.RequestedId == "hc:123456").ETag, Is.Empty);
                Assert.That(diffs.Single(item => item.RequestedId == "hc:654321").ETag, Is.Empty);
                Assert.That(diffs.Single(item => item.RequestedId == "hc:654322").ETag, Is.EqualTo("existing-etag"));
                Assert.That(fetched, Has.Count.EqualTo(2));
                Assert.That(h.Author.AddOptions?.AwaitingMetadata, Is.True);
                Assert.That(h.Authors.GetAuthor(ready.Id).AddOptions?.AwaitingMetadata, Is.False, string.Join(Environment.NewLine, messages.Logs));
                Assert.That(h.Authors.GetAuthor(settled.Id).AddOptions, Is.Null);
                Assert.That(h.SearchTargets, Is.Empty);
                h.Scan();
                var handler = new AuthorScannedHandler(new BookMonitoredService(h.Authors, h.Books, h.Logger), h.Authors, h.Queue,
                    Proxy<IBookAddedService>((_, _) => null), h.Events, h.Logger, h.Books);
                handler.Handle(new AuthorScannedEvent(h.Authors.GetAuthor(ready.Id)));
                Assert.That(h.Author.AddOptions?.SearchForMissingBooks, Is.True);
                Assert.That(h.Authors.GetAuthor(ready.Id).AddOptions, Is.Null);
                Assert.That(h.SearchTargets, Has.Count.EqualTo(1));
                Assert.That(h.SearchTargets.Single(), Is.Empty, "An empty published catalog is still a completed hydration.");
            });
        }

        [Test]
        public void failed_hydration_preserves_setup_across_scan_and_retry(
            [Values(false, true)] bool postgres,
            [Values("audiobook", "ebook")] string missingSide,
            [Values(false, true)] bool search,
            [Values(false, true)] bool skipped)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.Import(h.Request(missingSide == "ebook" ? "audiobook" : "ebook", false));
                h.Scan();
                h.Commands.Clear();
                h.MetadataError = new BookInfoException("Metadata is temporarily unavailable");
                var request = h.Request(missingSide, false);
                request.SearchForMissingBooks = search;
                h.Import(request);
                var saved = h.Author.AddOptions;

                for (var attempt = 0; attempt < 2; attempt++)
                {
                    var handler = new AuthorScannedHandler(new BookMonitoredService(h.Authors, h.Books, h.Logger),
                        h.Authors, h.Queue, Proxy<IBookAddedService>((_, _) => null), h.Events, h.Logger, h.Books);
                    if (skipped)
                        handler.Handle(new AuthorScanSkippedEvent(h.Author, AuthorScanSkippedReason.NoFolderEvidence));
                    else
                        handler.Handle(new AuthorScannedEvent(h.Author));

                    var persisted = new AuthorRepository(database, h.Events, h.Logger).Get(h.Author.Id).AddOptions;
                    Assert.That(persisted, Is.Not.Null, "A disk scan cannot acknowledge an unloaded catalog.");
                    Assert.That(persisted.SearchForMissingBooks, Is.EqualTo(search));
                    Assert.That(persisted.AudiobookMonitor, Is.EqualTo(saved.AudiobookMonitor));
                    Assert.That(persisted.EbookMonitor, Is.EqualTo(saved.EbookMonitor));
                    Assert.That(h.Commands.OfType<MissingBookSearchCommand>(), Is.Empty);
                }

                h.MetadataError = null;
                h.Import(request);
                Assert.That(h.Books.GetBooksByAuthor(h.Author.Id).Select(book => book.MediaType).Distinct().Count(), Is.EqualTo(2));
                h.Scan();
                h.Scan();
                Assert.That(h.Author.AddOptions, Is.Null);
                Assert.That(h.SearchTargets, Has.Count.EqualTo(search ? 1 : 0));
                if (search)
                {
                    var requestedMediaType = missingSide == "ebook" ? BookMediaType.Ebook : BookMediaType.Audiobook;
                    Assert.That(h.SearchTargets.Single(), Is.EqualTo(h.BookRows.GetBooksByAuthorId(h.Author.Id)
                        .Where(book => book.MediaType == requestedMediaType).Select(book => book.Id).ToArray()));
                }
            });
        }

    }
}
