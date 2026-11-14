using System;
using System.Net;
using System.Collections.Generic;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo;
using NzbDrone.Core.Messaging.Events;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.MetadataSource.BookInfo
{
    [TestFixture]
    public class AuthorRefreshHttpFailureFixture
    {
        private static IEnumerable<TestCaseData> Cases()
        {
            foreach (var postgres in new[] { false, true })
            foreach (var item in new[] { (404, "{}", 3600, true), (400, "{}", 300, true), (409, "{}", 300, true),
                (429, "{}", 3600, false), (503, "{}", 60, false), (200, "{}", 300, true), (200, "not json", 300, true), (0, "", 60, false) })
                yield return new TestCaseData(item.Item1, item.Item2, item.Item3, item.Item4, postgres);
        }

        [TestCaseSource(nameof(Cases))]
        public void real_http_failures_keep_their_status_schedule_and_health_state(int status, string body,
            int delaySeconds, bool healthy, bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                var config = Proxy<IConfigService>((method, _) => method.Name == "get_MetadataServerUrl"
                    ? "http://metadata.example" : throw new AssertionException(method.Name));
                var health = new MetadataServerHealthService(logger);
                var gate = new MetadataServerHealthGate(config, health, logger);
                var requests = 0;
                var http = Proxy<IHttpClient>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(IHttpClient.Get)));
                    requests++;
                    var request = (HttpRequest)args[0];
                    Assert.That(request.SuppressHttpError, Is.False);
                    if (status == 0) throw new WebException("Connection refused", WebExceptionStatus.ConnectFailure);
                    var response = new HttpResponse(request, new HttpHeader { ContentType = "application/json" }, body, (HttpStatusCode)status);
                    // Same exception contract as HttpClient.ExecuteAsync, not a returned error response.
                    if (status == 429) throw new TooManyRequestsException(request, response);
                    if (response.HasHttpError) throw new HttpException(request, response);
                    return response;
                });
                var proxy = new BookInfoProxy(http, null, null, null, null, null, null, null, config, null, logger,
                    new CacheManager(), gate);
                var events = Proxy<IEventAggregator>((_, _) => null);
                var author = new AuthorRepository(database, events, logger).Insert(new Author {
                    Name = "Frank Herbert", CleanName = "frankherbert", SortName = "frankherbert",
                    TitleSlug = "frank-herbert", Path = "/library/Frank Herbert", Added = DateTime.UtcNow });
                var repository = new AuthorSyncMetadataRepository(database, events);
                var sync = new AuthorSyncMetadataService(repository, logger);
                var row = sync.CreateOrUpdateSyncMetadata(author.Id, "gr:58", "old-etag");
                row.SyncFailureCount = 3;
                repository.Update(row);
                var start = DateTime.UtcNow;
                var result = proxy.RefreshAuthorInfo("gr:58", "old-etag", forceRefresh: true);
                sync.UpdateSyncResult(author.Id, false, error: result.Message, httpStatus: (int?)result.HttpStatus ?? 0);
                var end = DateTime.UtcNow;
                var saved = repository.FindByAuthorId(author.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(result.Reason, Is.EqualTo(RefreshReason.Error));
                    Assert.That((int?)result.HttpStatus ?? 0, Is.EqualTo(status));
                    Assert.That(result.Terminal, Is.Null);
                    Assert.That(saved.LastHttpStatus, Is.EqualTo(status));
                    Assert.That(saved.SyncFailureCount, Is.EqualTo(healthy ? 4 : 3));
                    Assert.That(saved.ETag, Is.EqualTo("old-etag"));
                    Assert.That(saved.NextSyncNotBefore, Is.InRange(start.AddSeconds(delaySeconds * 0.8), end.AddSeconds(delaySeconds * 1.2)));
                    Assert.That(health.GetStatus(gate.SourceName).IsHealthy, Is.EqualTo(healthy));
                    Assert.That(health.GetStatus(gate.SourceName).IsRateLimited, Is.EqualTo(status == 429));
                    Assert.That(repository.GetDueForSync(), Is.Empty);
                });
                if (!healthy)
                {
                    var held = proxy.RefreshAuthorInfo("gr:58", forceRefresh: true);
                    Assert.That(held.HttpStatus, Is.Null, "A gate refusal has no server response");
                    Assert.That(requests, Is.EqualTo(1));
                }
            });
        }
    }
}
