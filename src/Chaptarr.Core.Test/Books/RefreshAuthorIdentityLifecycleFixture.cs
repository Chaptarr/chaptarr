using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Common;
using Chaptarr.Core.Test.MetadataSource.BookInfo;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MetadataSource.BookInfo;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class RefreshAuthorIdentityLifecycleFixture
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private sealed class Refresh : RefreshAuthorService
        {
            public bool FailChildren;
            public int AddedEvents;
            public Refresh(BookInfoProxy info, IAuthorService authors, IAuthorSyncMetadataService sync, Logger logger)
                : base(MetadataHealthGateStub.Ready(), info, authors, null, null, null, null, null, InterfaceProxy.Proxy<IEventAggregator>((_, _) => null),
                    null, null, global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoHistoryRepositoryCalls(), null, null, null, null, null, sync, null, null, logger, bookRepository: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoBookRepositoryCalls(), authorRepository: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoPendingAuthorSetup()) { }
            protected override List<Book> GetRemoteChildren(Author local, Author remote, RefreshContext context) => new();
            protected override List<Book> GetLocalChildren(Author entity, List<Book> remote, RefreshContext context) => new();
            protected override void PrepareChildrenForRefresh(Author entity, SortedChildren children, List<Book> remote, RefreshContext context) { }
            protected override bool RefreshChildren(SortedChildren children, List<Book> remote, Author author, bool force, bool tags, DateTime? last, RefreshContext context)
            {
                if (FailChildren) throw new InvalidOperationException("planted child failure");
                return false;
            }
            protected override void PublishChildrenUpdatedEvent(Author author, List<Book> added, List<Book> updated, List<Book> deleted) => AddedEvents++;
            public void Run(Author author)
            {
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var args = new object[] { author, false, null, true, null };
                var fetched = typeof(RefreshAuthorService).GetMethod("GetSkyhookData", flags).Invoke(this, args);
                typeof(RefreshAuthorService).GetMethod("ApplyFetchedAuthor", flags).Invoke(this, new[] { args[0], fetched, (object)CancellationToken.None });
            }
        }

        [Test]
        public void only_typed_permanent_missing_response_withdraws_id_and_ack_waits_for_children(
            [Values(false, true)] bool postgres,
            [Values("retired", "html404", "outage", "reopenable", "redirect", "child-failure")] string response)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var rows = new AuthorRepository(database, null, Logger);
                var local = rows.Insert(new Author { Name = "Frank Herbert", CleanName = "frankherbert", Overview = "", SortName = "herbertfrank",
                    TitleSlug = "frank-herbert", Path = TestContext.CurrentContext.TestDirectory, HardcoverAuthorId = "hc:123", GoodreadsAuthorId = "gr:58",
                    RemoteProviderIds = new() { "hc:123", "gr:58" }, Added = DateTime.UtcNow });
                var aliases = new ProviderAliasService(new ProviderAliasRepository(database, null));
                var syncRows = new AuthorSyncMetadataRepository(database, null);
                syncRows.Insert(new AuthorSyncMetadata { AuthorId = local.Id, ExternalAuthorId = "hc:123", ETag = "old" });
                var sync = new AuthorSyncMetadataService(syncRows, Logger);
                var authors = InterfaceProxy.Proxy<IAuthorService>((method, args) => method.Name switch
                {
                    nameof(IAuthorService.GetAllAuthors) => rows.All().ToList(),
                    nameof(IAuthorService.GetAuthor) => rows.Get((int)args[0]),
                    nameof(IAuthorService.UpdateAuthor) => rows.Update((Author)args[0]),
                    _ => throw new AssertionException(method.Name)
                });
                var requests = new List<string>();
                var client = InterfaceProxy.Proxy<IHttpClient>((method, args) =>
                {
                    var request = (HttpRequest)args[0];
                    requests.Add(request.Url.ToString());
                    var oldId = Uri.UnescapeDataString(request.Url.ToString()).Contains("gr:58");
                    var status = HttpStatusCode.OK;
                    var body = "{\"author\":{\"id\":\"hc:123\",\"name\":\"Frank Herbert\",\"slug\":\"frank-herbert\"},\"books\":[],\"series\":[]}";
                    if (oldId)
                    {
                        status = response == "outage" ? HttpStatusCode.BadGateway : HttpStatusCode.NotFound;
                        body = response == "html404" ? "<html>Not found</html>" : response == "outage" ? "Unavailable" :
                            "{\"code\":\"author_provider_record_missing\",\"providerId\":\"gr:58\",\"message\":\"Provider record permanently removed\",\"retryable\":false,\"reopenable\":" +
                            (response == "reopenable" ? "true" : "false") + ",\"resolvedProviderId\":" + (response == "redirect" ? "\"gr:59\"" : "null") + "}";
                    }
                    var result = new HttpResponse(request, new HttpHeader { ["ETag"] = "new", ContentType = "application/json" }, body, status);
                    return method.Name.EndsWith("Async", StringComparison.Ordinal) ? Task.FromResult(result) : result;
                });
                using var factory = new LogFactory();
                var sink = new NLog.Targets.MemoryTarget { Layout = "${message} ${exception:format=tostring}" };
                var logging = new NLog.Config.LoggingConfiguration();
                logging.AddRuleForAllLevels(sink);
                factory.Configuration = logging;
                var refresh = new Refresh(BookInfoProxyCanonicalLookupFixture.CreateProxy(client), authors, sync, factory.GetLogger("identity-lifecycle")) { FailChildren = response == "child-failure" };
                if (refresh.FailChildren)
                    Assert.That(() => refresh.Run(local), Throws.TypeOf<TargetInvocationException>().With.InnerException.TypeOf<InvalidOperationException>().And.InnerException.Message.EqualTo("planted child failure"));
                else Assert.DoesNotThrow(() => refresh.Run(local));
                var accepted = response == "retired";
                Assert.Multiple(() =>
                {
                    Assert.That(requests.Any(url => Uri.UnescapeDataString(url).Contains("gr:58")), Is.True, "real RefreshAuthorInfo must fetch the dropped ID");
                    Assert.That(rows.Get(local.Id).RemoteProviderIds, Is.EquivalentTo(accepted ? new[] { "hc:123" } : new[] { "hc:123", "gr:58" }));
                    Assert.That(aliases.FindAuthorIds(new[] { "gr:58" }), accepted ? Is.Empty : Is.EqualTo(new[] { local.Id }));
                    Assert.That(syncRows.All().Single().ETag, Is.EqualTo(accepted ? "new" : response == "outage" ? "old" : null),
                        "An unapplied payload is never acknowledged; a failed local reconciliation must be fetched again, not skipped by a 304");
                    Assert.That(refresh.AddedEvents, Is.EqualTo(accepted || refresh.FailChildren ? 1 : 0));
                    Assert.That(string.Join("\n", sink.Logs), Does.Contain(accepted || refresh.FailChildren ? "no longer served" : "Cannot verify ownership").And.Contain("gr:58"));
                });
            });
        }
    }
}
