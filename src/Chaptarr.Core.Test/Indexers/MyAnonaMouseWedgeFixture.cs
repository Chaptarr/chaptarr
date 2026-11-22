using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Chaptarr.Http.ClientSchema;
using Newtonsoft.Json;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.HealthCheck.Checks;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.Indexers.Newznab;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.ThingiProvider.Events;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    public class MyAnonaMouseWedgeFixture
    {
        private const string MinimalTorrent = "d8:announce14:http://tracker4:infod6:lengthi1e4:name1:x12:piece lengthi16384e6:pieces20:12345678901234567890ee";

        private sealed class ImmediateSummaryThrottle : IMyAnonaMouseAccountSummaryThrottle
        {
            public Task<MyAnonaMouseAccountStatus> SendAsync(MamConnection connection, Func<Task<MyAnonaMouseAccountStatus>> send) => send();
        }

        private sealed class SummaryClock : TimeProvider
        {
            private long _ticks;

            public override long TimestampFrequency => TimeSpan.TicksPerSecond;
            public override long GetTimestamp() => _ticks;
            public void Advance(TimeSpan elapsed) => _ticks += elapsed.Ticks;
        }

        private class IndexerHttpClientProxy : DispatchProxy
        {
            public Queue<Func<HttpRequest, HttpResponse>> Responses { get; } = new Queue<Func<HttpRequest, HttpResponse>>();
            public List<string> Requests { get; } = new List<string>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IIndexerHttpClient.ExecuteAsync) && args?[0] is HttpRequest request)
                {
                    Requests.Add(request.Url.FullUri);
                    return Task.FromResult(Responses.Dequeue()(request));
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }

        private class IndexerHttpClientFactoryProxy : DispatchProxy
        {
            public IIndexerHttpClient Client { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IIndexerHttpClientFactory.GetClient))
                {
                    return Client;
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }

        private sealed class TestableMyAnonaMouse : MyAnonaMouse
        {
            public TestableMyAnonaMouse(IIndexerHttpClientFactory httpClientFactory, IMyAnonaMouseAccountSummaryThrottle throttle, Logger logger)
                : base(httpClientFactory, null, null, null, throttle, logger)
            {
            }

            protected override Task<IList<ReleaseInfo>> FetchPage(IndexerRequest request, IParseIndexerResponse parser)
            {
                return Task.FromResult<IList<ReleaseInfo>>(new List<ReleaseInfo> { new ReleaseInfo() });
            }
        }

        private class IndexerFactoryProxy : DispatchProxy
        {
            public IIndexer Indexer { get; set; }
            public List<string> PersistedMamIds { get; } = new List<string>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IIndexerFactory.GetAvailableProviders):
                        return new List<IIndexer> { Indexer };
                    case nameof(IIndexerFactory.All):
                        return new List<IndexerDefinition> { (IndexerDefinition)Indexer.Definition };
                    case nameof(IIndexerFactory.Update):
                        PersistedMamIds.Add(((MyAnonaMouseSettings)((IndexerDefinition)args[0]).Settings).MamId);
                        return null;
                    default:
                        throw new NotImplementedException(targetMethod?.Name);
                }
            }
        }

        [Test]
        public void follow_mam_preferences_should_strip_internal_markers_without_forcing_wedge()
        {
            var indexer = CreateIndexer(new MyAnonaMouseSettings
            {
                UseFreeleechWedge = (int)MyAnonaMouseFreeleechWedgeAction.Never
            });

            var request = indexer.GetDownloadRequest(EligibleAudiobookUrl());

            Assert.That(request.Url.Query, Does.Not.Contain("canUseToken"));
            Assert.That(request.Url.Query, Does.Not.Contain("isAudiobook"));
            Assert.That(request.Url.Query, Does.Not.Contain("fl"));
        }

        [Test]
        public void prefer_wedge_should_force_freeleech_for_eligible_audiobook()
        {
            var indexer = CreateIndexer(new MyAnonaMouseSettings
            {
                UseFreeleechWedge = (int)MyAnonaMouseFreeleechWedgeAction.Preferred,
                UseFreeleechOnlyForAudiobooks = true
            });

            var request = indexer.GetDownloadRequest(EligibleAudiobookUrl());

            Assert.That(request.Url.Query, Does.Contain("tid=42"));
            Assert.That(request.Url.Query, Does.Contain("fl"));
            Assert.That(request.Url.Query, Does.Not.Contain("canUseToken"));
            Assert.That(request.Url.Query, Does.Not.Contain("isAudiobook"));
        }

        [Test]
        public void audiobook_only_should_not_force_wedge_for_ebook()
        {
            var indexer = CreateIndexer(new MyAnonaMouseSettings
            {
                UseFreeleechWedge = (int)MyAnonaMouseFreeleechWedgeAction.Preferred,
                UseFreeleechOnlyForAudiobooks = true
            });

            var request = indexer.GetDownloadRequest("https://www.myanonamouse.net/tor/download.php?tid=42&canUseToken=true");

            Assert.That(request.Url.Query, Does.Not.Contain("fl"));
        }

        [Test]
        public void prefer_wedge_should_support_ebooks_when_audiobook_only_is_disabled()
        {
            var indexer = CreateIndexer(new MyAnonaMouseSettings
            {
                UseFreeleechWedge = (int)MyAnonaMouseFreeleechWedgeAction.Preferred,
                UseFreeleechOnlyForAudiobooks = false
            });

            var request = indexer.GetDownloadRequest("https://www.myanonamouse.net/tor/download.php?tid=42&canUseToken=true");

            Assert.That(request.Url.Query, Does.Contain("fl"));
        }

        [Test]
        public void legacy_required_value_should_not_reenable_wedge_use()
        {
            var settings = new MyAnonaMouseSettings { UseFreeleechWedge = 2 };

            Assert.That(settings.UseFreeleechWedge, Is.EqualTo((int)MyAnonaMouseFreeleechWedgeAction.Never));
        }

        [Test]
        public void required_wedge_should_not_be_offered_in_the_client_schema()
        {
            var getSelectOptions = typeof(SchemaBuilder).GetMethod("GetSelectOptions", BindingFlags.NonPublic | BindingFlags.Static);

            var options = (List<SelectOption>)getSelectOptions?.Invoke(null, new object[] { typeof(MyAnonaMouseFreeleechWedgeAction) });

            Assert.That(options?.Select(option => option.Value), Is.EqualTo(new[] { 0, 1 }));
        }

        [Test]
        public async Task preferred_wedge_should_retry_without_fl_when_mam_does_not_return_a_torrent()
        {
            var client = DispatchProxy.Create<IIndexerHttpClient, IndexerHttpClientProxy>();
            var clientState = (IndexerHttpClientProxy)(object)client;
            clientState.Responses.Enqueue(request => new HttpResponse(
                request,
                new HttpHeader { ContentType = "text/html" },
                "Unable to apply wedge",
                HttpStatusCode.OK));
            clientState.Responses.Enqueue(request => new HttpResponse(
                request,
                new HttpHeader { ContentType = "application/x-bittorrent" },
                Encoding.ASCII.GetBytes(MinimalTorrent),
                HttpStatusCode.OK));

            var indexer = CreateIndexer(new MyAnonaMouseSettings
            {
                UseFreeleechWedge = (int)MyAnonaMouseFreeleechWedgeAction.Preferred,
                UseFreeleechOnlyForAudiobooks = true
            }, client);
            var request = indexer.GetDownloadRequest(EligibleAudiobookUrl());

            var response = await indexer.ExecuteDownloadRequestAsync(request);

            Assert.That(response.Headers.ContentType, Is.EqualTo("application/x-bittorrent"));
            Assert.That(clientState.Requests, Has.Count.EqualTo(2));
            Assert.That(clientState.Requests[0], Does.Contain("fl"));
            Assert.That(clientState.Requests[1], Does.Not.Contain("fl"));
        }

        [Test]
        public async Task successful_wedge_download_should_not_retry()
        {
            var client = DispatchProxy.Create<IIndexerHttpClient, IndexerHttpClientProxy>();
            var clientState = (IndexerHttpClientProxy)(object)client;
            clientState.Responses.Enqueue(request => new HttpResponse(
                request,
                new HttpHeader { ContentType = "application/x-bittorrent" },
                Encoding.ASCII.GetBytes(MinimalTorrent),
                HttpStatusCode.OK));

            var indexer = CreateIndexer(new MyAnonaMouseSettings
            {
                UseFreeleechWedge = (int)MyAnonaMouseFreeleechWedgeAction.Preferred
            }, client);

            await indexer.ExecuteDownloadRequestAsync(indexer.GetDownloadRequest(EligibleAudiobookUrl()));

            Assert.That(clientState.Requests, Has.Count.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task account_status_should_use_mam_unsatisfied_count_limit_and_snapshot(bool nested)
        {
            var client = DispatchProxy.Create<IIndexerHttpClient, IndexerHttpClientProxy>();
            var clientState = (IndexerHttpClientProxy)(object)client;
            clientState.Responses.Enqueue(request => new HttpResponse(
                request,
                new HttpHeader { ContentType = "application/json" },
                nested
                    ? "{\"uid\":42,\"classname\":\"Elite VIP\",\"snatch_summary\":{\"created\":1785171600,\"unsat\":{\"count\":196,\"limit\":200}}}"
                    : "{\"uid\":42,\"classname\":\"Elite VIP\",\"created\":1785171600,\"unsat\":{\"count\":196,\"limit\":200}}",
                HttpStatusCode.OK));

            var settings = new MyAnonaMouseSettings { MamId = "secret" };
            var indexer = CreateIndexer(settings, client);

            var status = await indexer.RefreshAccountStatus();

            Assert.Multiple(() =>
            {
                Assert.That(status.UserClass, Is.EqualTo("Elite VIP"));
                Assert.That(status.UnsatisfiedCount, Is.EqualTo(196));
                Assert.That(status.UnsatisfiedLimit, Is.EqualTo(200));
                Assert.That(status.SnapshotCreatedUtc, Is.EqualTo(DateTimeOffset.FromUnixTimeSeconds(1785171600).UtcDateTime));
                Assert.That(settings.UnsatisfiedCount, Is.EqualTo(196));
                Assert.That(settings.UnsatisfiedLimit, Is.EqualTo(200));
                Assert.That(settings.UnsatisfiedSnapshotUtc, Is.EqualTo(status.SnapshotCreatedUtc));
                Assert.That(settings.UnsatisfiedStatusRefreshedUtc, Is.EqualTo(status.RefreshedUtc));
                Assert.That(clientState.Requests.Single(), Does.EndWith("/jsonLoad.php?snatch_summary&pretty"));
            });
        }

        [TestCase("{\"uid\":42,\"classname\":\"User\",\"created\":1785171600,\"unsat\":{\"limit\":200}}")]
        [TestCase("{\"uid\":42,\"classname\":\"User\",\"created\":1785171600,\"unsat\":{\"count\":null,\"limit\":200}}")]
        public void account_status_should_reject_missing_or_null_count_without_overwriting_cached_settings(string content)
        {
            var client = CreateClient(request => JsonResponse(request, content));
            var settings = new MyAnonaMouseSettings
            {
                MamId = "fixture-token",
                UserClass = "Prior",
                IsVip = true,
                UnsatisfiedCount = 17,
                UnsatisfiedLimit = 99,
                UnsatisfiedSnapshotUtc = DateTime.UtcNow.AddHours(-1),
                UnsatisfiedStatusRefreshedUtc = DateTime.UtcNow.AddMinutes(-1)
            };
            var priorSnapshot = settings.UnsatisfiedSnapshotUtc;
            var priorRefresh = settings.UnsatisfiedStatusRefreshedUtc;
            var indexer = CreateIndexer(settings, client);

            Assert.ThrowsAsync<MyAnonaMouseAccountSummaryException>(async () => await indexer.RefreshAccountStatus());

            Assert.Multiple(() =>
            {
                Assert.That(settings.UserClass, Is.EqualTo("Prior"));
                Assert.That(settings.IsVip, Is.True);
                Assert.That(settings.UnsatisfiedCount, Is.EqualTo(17));
                Assert.That(settings.UnsatisfiedLimit, Is.EqualTo(99));
                Assert.That(settings.UnsatisfiedSnapshotUtc, Is.EqualTo(priorSnapshot));
                Assert.That(settings.UnsatisfiedStatusRefreshedUtc, Is.EqualTo(priorRefresh));
            });
        }

        [Test]
        public async Task account_status_should_accept_an_explicit_zero_count()
        {
            var created = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds();
            var client = CreateClient(request => JsonResponse(request,
                $"{{\"uid\":42,\"classname\":\"User\",\"created\":{created},\"unsat\":{{\"count\":0,\"limit\":50}}}}"));
            var settings = new MyAnonaMouseSettings { MamId = "fixture-token" };
            var indexer = CreateIndexer(settings, client);

            var status = await indexer.RefreshAccountStatus();

            Assert.Multiple(() =>
            {
                Assert.That(status.UnsatisfiedCount, Is.Zero);
                Assert.That(status.UnsatisfiedLimit, Is.EqualTo(50));
                Assert.That(settings.UnsatisfiedCount, Is.Zero);
            });
        }

        [TestCase("42")]
        [TestCase("\"42\"")]
        public async Task account_status_should_store_the_user_id_whether_mam_sends_text_or_a_number(string uid)
        {
            var created = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds();
            var settings = new MyAnonaMouseSettings { MamId = "fixture-token" };
            var indexer = CreateIndexer(settings, CreateClient(request => JsonResponse(request, $"{{\"uid\":{uid},\"classname\":\"User\",\"created\":{created},\"unsat\":{{\"count\":3,\"limit\":50}}}}")));

            await indexer.RefreshAccountStatus();

            Assert.That(settings.UserId, Is.EqualTo("42"));
        }

        [Test]
        public async Task account_status_without_a_user_id_should_refresh_and_leave_the_account_unidentified()
        {
            var settings = new MyAnonaMouseSettings { MamId = "fixture-token", UserId = "42" };
            var indexer = CreateIndexer(settings, CreateSummaryClient("missing-uid"));

            await indexer.RefreshAccountStatus();

            Assert.Multiple(() =>
            {
                Assert.That(settings.UnsatisfiedCount, Is.EqualTo(3));
                Assert.That(settings.UserId, Is.Null);
            });
        }

        [Test]
        public async Task account_status_should_send_a_rotated_mam_id_on_the_next_request()
        {
            var sentMamIds = new List<string>();
            var client = DispatchProxy.Create<IIndexerHttpClient, IndexerHttpClientProxy>();
            var clientState = (IndexerHttpClientProxy)(object)client;
            clientState.Responses.Enqueue(request =>
            {
                sentMamIds.Add(request.Cookies["mam_id"]);
                return ValidSummaryResponse(request, "mam_id=rotated-token; expires=Sat, 24 Oct 2026 03:00:00 GMT; Max-Age=1296000; path=/");
            });
            clientState.Responses.Enqueue(request =>
            {
                sentMamIds.Add(request.Cookies["mam_id"]);
                return ValidSummaryResponse(request);
            });
            var settings = new MyAnonaMouseSettings { MamId = "original-token" };
            var indexer = CreateIndexer(settings, client);

            await indexer.RefreshAccountStatus();
            await indexer.RefreshAccountStatus();

            Assert.Multiple(() =>
            {
                Assert.That(sentMamIds, Is.EqualTo(new[] { "original-token", "rotated-token" }));
                Assert.That(settings.MamId, Is.EqualTo("rotated-token"));
            });
        }

        [Test]
        public void account_status_refresh_should_persist_a_rotated_mam_id()
        {
            var indexer = CreateIndexerWithAccountSummary(new MyAnonaMouseSettings { MamId = "original-token" }, "mam_id=rotated-token; path=/");
            var factory = DispatchProxy.Create<IIndexerFactory, IndexerFactoryProxy>();
            var factoryState = (IndexerFactoryProxy)(object)factory;
            factoryState.Indexer = indexer;

            new MyAnonaMouseAccountStatusService(factory, Proxy<IEventAggregator>((_, _) => null), LogManager.GetCurrentClassLogger())
                .Execute(new RefreshMyAnonaMouseAccountStatusCommand());

            Assert.That(factoryState.PersistedMamIds, Is.EqualTo(new[] { "rotated-token" }));
        }

        [TestCase(null)]
        [TestCase("mam_id=; path=/")]
        [TestCase("mam_id=   ; path=/")]
        [TestCase("mam_ssl=1; path=/")]
        public async Task account_status_should_keep_the_current_mam_id_without_a_usable_replacement(string setCookie)
        {
            var client = CreateClient(request => ValidSummaryResponse(request, setCookie));
            var settings = new MyAnonaMouseSettings { MamId = "original-token" };
            var indexer = CreateIndexer(settings, client);

            await indexer.RefreshAccountStatus();

            Assert.That(settings.MamId, Is.EqualTo("original-token"));
        }

        [TestCase("http-failure")]
        [TestCase("invalid-summary")]
        public void failed_account_status_refresh_should_not_persist_a_rotated_mam_id(string scenario)
        {
            var client = CreateClient(request =>
            {
                var response = scenario == "http-failure"
                    ? new HttpResponse(request, new HttpHeader { ContentType = "application/json" }, "", HttpStatusCode.Forbidden)
                    : JsonResponse(request, "{\"uid\":42,\"classname\":\"User\"}");
                response.Headers.Add("Set-Cookie", "mam_id=rotated-token; path=/");
                return response;
            });
            var indexer = CreateIndexer(new MyAnonaMouseSettings { MamId = "original-token" }, client);
            var factory = DispatchProxy.Create<IIndexerFactory, IndexerFactoryProxy>();
            var factoryState = (IndexerFactoryProxy)(object)factory;
            factoryState.Indexer = indexer;

            new MyAnonaMouseAccountStatusService(factory, Proxy<IEventAggregator>((_, _) => null), LogManager.GetCurrentClassLogger())
                .Execute(new RefreshMyAnonaMouseAccountStatusCommand());

            Assert.That(factoryState.PersistedMamIds, Is.Empty);
            Assert.That(((MyAnonaMouseSettings)indexer.Definition.Settings).MamId, Is.EqualTo("original-token"));
        }

        [TestCase("malformed")]
        [TestCase("missing-count")]
        [TestCase("http-failure")]
        [TestCase("stale")]
        public void test_connection_should_fail_when_protection_is_enabled_and_summary_is_unavailable(string scenario)
        {
            var settings = new MyAnonaMouseSettings
            {
                MamId = "fixture-token",
                ProtectUnsatisfiedSlots = true,
                UnsatisfiedCount = 77,
                UnsatisfiedLimit = 200
            };
            var indexer = CreateTestIndexer(settings, CreateSummaryClient(scenario));

            var result = indexer.Test();

            Assert.Multiple(() =>
            {
                Assert.That(result.IsValid, Is.False);
                Assert.That(result.Errors.Select(error => error.ErrorMessage), Has.Member("MAM account status is unavailable or stale. Cannot verify unsatisfied-slot protection."));
                Assert.That(indexer.Definition.Message?.Type, Is.Not.EqualTo(ProviderMessageType.Info));
                Assert.That((indexer.Definition.Message?.Message ?? string.Empty), Does.Not.Contain("77/200"));
            });
        }

        [TestCase("malformed")]
        [TestCase("missing-count")]
        [TestCase("http-failure")]
        [TestCase("stale")]
        public void test_connection_should_warn_without_stale_counts_when_protection_is_disabled(string scenario)
        {
            var settings = new MyAnonaMouseSettings
            {
                MamId = "fixture-token",
                ProtectUnsatisfiedSlots = false,
                UnsatisfiedCount = 77,
                UnsatisfiedLimit = 200
            };
            var indexer = CreateTestIndexer(settings, CreateSummaryClient(scenario));

            var result = indexer.Test();

            Assert.Multiple(() =>
            {
                Assert.That(result.IsValid, Is.True);
                Assert.That(indexer.Definition.Message?.Type, Is.EqualTo(ProviderMessageType.Warning));
                Assert.That(indexer.Definition.Message?.Message, Does.Contain("MAM account status is unavailable or stale"));
                Assert.That((indexer.Definition.Message?.Message ?? string.Empty), Does.Not.Contain("77/200"));
                Assert.That((indexer.Definition.Message?.Message ?? string.Empty), Does.Not.Contain("3/50"));
            });
        }

        [Test]
        public void test_connection_should_succeed_with_fresh_summary_and_updated_counts()
        {
            var settings = new MyAnonaMouseSettings
            {
                MamId = "fixture-token",
                ProtectUnsatisfiedSlots = true,
                UnsatisfiedCount = 77,
                UnsatisfiedLimit = 200
            };
            var indexer = CreateTestIndexer(settings, CreateSummaryClient("fresh"));

            var result = indexer.Test();

            Assert.Multiple(() =>
            {
                Assert.That(result.IsValid, Is.True);
                Assert.That(settings.UnsatisfiedCount, Is.EqualTo(3));
                Assert.That(settings.UnsatisfiedLimit, Is.EqualTo(50));
                Assert.That(indexer.Definition.Message?.Type, Is.EqualTo(ProviderMessageType.Info));
                Assert.That(indexer.Definition.Message?.Message, Does.Contain("3/50"));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void account_status_warning_should_identify_indexer_without_exposing_response_content(bool background)
        {
            const string privateValue = "fixture-private-response";
            var messages = new MemoryTarget { Layout = "${level}|${message}|${exception:format=tostring}" };
            var configuration = new LoggingConfiguration();
            configuration.AddRule(LogLevel.Warn, LogLevel.Fatal, messages);
            using var logs = new LogFactory { Configuration = configuration };
            var logger = logs.GetLogger("AccountStatusDiagnostic");
            var client = CreateClient(request => JsonResponse(request,
                $"{{\"uid\":42,\"classname\":\"User\",\"created\":1785171600,\"unsat\":{{\"count\":\"{privateValue}\",\"limit\":200}}}}"));
            var indexer = CreateTestIndexer(new MyAnonaMouseSettings { MamId = "fixture-token" }, client, logger);
            indexer.Definition.Name = "Fixture MAM";

            if (background)
            {
                var factory = DispatchProxy.Create<IIndexerFactory, IndexerFactoryProxy>();
                ((IndexerFactoryProxy)(object)factory).Indexer = indexer;
                new MyAnonaMouseAccountStatusService(factory, Proxy<IEventAggregator>((_, _) => null), logger)
                    .Execute(new RefreshMyAnonaMouseAccountStatusCommand());
            }
            else
            {
                Assert.That(indexer.Test().IsValid, Is.False);
            }

            Assert.That(messages.Logs, Has.Count.EqualTo(1));
            Assert.Multiple(() =>
            {
                Assert.That(messages.Logs[0], Does.StartWith("Warn|"));
                Assert.That(messages.Logs[0], Does.Contain("Fixture MAM"));
                Assert.That(messages.Logs[0], Does.Contain(background ? "could not be decoded" : nameof(MyAnonaMouseAccountSummaryException)));
                Assert.That(messages.Logs[0], Does.Not.Contain(privateValue));
                Assert.That(messages.Logs[0], Does.Not.Contain("fixture-token"));
            });
        }

        [TestCase("unreadable-private-content")]
        [TestCase("{\"uid\":42,\"created\":1785171600,\"unsat\":{\"count\":1,\"limit\":50}}")]
        [TestCase("{\"classname\":\"User\",\"created\":1785171600}")]
        [TestCase("{\"classname\":\"User\",\"created\":1785171600,\"unsat\":{\"count\":1,\"limit\":0}}")]
        [TestCase("{\"classname\":\"User\",\"unsat\":{\"count\":1,\"limit\":50}}")]
        [TestCase("{\"classname\":\"User\",\"created\":9223372036854775807,\"unsat\":{\"count\":1,\"limit\":50}}")]
        [TestCase(null)] // HTTP 404
        public void account_summary_health_warning_follows_refresh_results(string unreadableReply)
        {
            const string privateResponse = "fixture-private-response";
            HttpResponse UnreadableResponse(HttpRequest request)
            {
                var statusCode = unreadableReply == null ? HttpStatusCode.NotFound : HttpStatusCode.OK;
                Assert.That(request.SuppressHttpErrorStatusCodes, Is.EqualTo(new[] { HttpStatusCode.NotFound }));
                var response = new HttpResponse(request, new HttpHeader(), unreadableReply ?? privateResponse, statusCode);
                response.Headers.Add("Set-Cookie", "mam_id=unverified-token; path=/");
                return response;
            }

            var client = CreateClient(request => new HttpResponse(request, new HttpHeader(), "", HttpStatusCode.Forbidden));
            var clientState = (IndexerHttpClientProxy)(object)client;
            clientState.Responses.Enqueue(UnreadableResponse);
            clientState.Responses.Enqueue(UnreadableResponse);
            clientState.Responses.Enqueue(_ => throw new TimeoutException());
            clientState.Responses.Enqueue(request => ValidSummaryResponse(request, "mam_id=rotated-token; path=/"));

            var indexer = CreateIndexer(new MyAnonaMouseSettings { MamId = "fixture-token" }, client);
            var definition = (IndexerDefinition)indexer.Definition;
            definition.Name = "Fixture MAM";
            definition.EnableAutomaticSearch = true;
            var factory = DispatchProxy.Create<IIndexerFactory, IndexerFactoryProxy>();
            var factoryState = (IndexerFactoryProxy)(object)factory;
            factoryState.Indexer = indexer;
            var logger = LogManager.GetCurrentClassLogger();

            HealthCheckService health = null;
            MyAnonaMouseAccountStatusService refresh = null;
            var summaryHealthChanges = 0;
            var events = Proxy<IEventAggregator>((_, args) =>
            {
                switch (args[0])
                {
                    case MyAnonaMouseAccountSummaryHealthChangedEvent changed:
                        summaryHealthChanges++;
                        health.HandleAsync(changed);
                        break;
                    case HealthCheckCompleteEvent complete:
                        refresh.Handle(complete);
                        break;
                    case ProviderUpdatedEvent<IIndexer> updated:
                        health.HandleAsync(updated);
                        break;
                }

                return null;
            });

            refresh = new MyAnonaMouseAccountStatusService(factory, events, logger);
            var cache = new CacheManager();
            var localization = new LocalizationService(
                Proxy<IConfigService>((_, _) => (int)NzbDrone.Core.Languages.Language.English),
                Proxy<IAppFolderInfo>((_, _) => TestContext.CurrentContext.TestDirectory), cache, logger);
            var check = new MyAnonaMouseAccountSummaryCheck(factory, refresh, localization);
            health = new HealthCheckService(new[] { check }, null, null, events, cache,
                Proxy<IRuntimeInfo>((_, _) => DateTime.UtcNow), logger);
            var lastAttempt = typeof(MyAnonaMouseAccountStatusService).GetField("_lastRefreshAttemptUtc", BindingFlags.Instance | BindingFlags.NonPublic);

            void Refresh()
            {
                lastAttempt.SetValue(refresh, DateTime.MinValue);
                refresh.Execute(new RefreshMyAnonaMouseAccountStatusCommand());
            }

            Refresh();
            Assert.That(health.Results(), Is.Empty, "An HTTP 403 is not an unreadable account summary.");

            Refresh();
            var warning = health.Results().Single();
            Assert.Multiple(() =>
            {
                Assert.That(warning.Type, Is.EqualTo(HealthCheckResult.Warning));
                Assert.That(warning.Message, Does.Contain("Fixture MAM").And.Contain("Please report this"));
                Assert.That(warning.Message, Does.Not.Contain("fixture-token").And.Not.Contain(unreadableReply ?? privateResponse));
                Assert.That(summaryHealthChanges, Is.EqualTo(1));
                Assert.That(clientState.Requests, Has.Count.EqualTo(2));
                Assert.That(factoryState.PersistedMamIds, Is.Empty);
                Assert.That(((MyAnonaMouseSettings)definition.Settings).MamId, Is.EqualTo("fixture-token"));
            });

            definition.EnableAutomaticSearch = false;
            events.PublishEvent(new ProviderUpdatedEvent<IIndexer>(definition));
            Assert.That(health.Results(), Is.Empty);
            definition.EnableAutomaticSearch = true;
            events.PublishEvent(new ProviderUpdatedEvent<IIndexer>(definition));
            Assert.That(health.Results().Single().Message, Is.EqualTo(warning.Message));
            Assert.That(clientState.Requests, Has.Count.EqualTo(2));

            var testedSettings = new MyAnonaMouseSettings { MamId = "fixture-token" };
            var testedIndexer = CreateTestIndexer(testedSettings, CreateSummaryClient("fresh"));
            Assert.That(testedIndexer.Test().IsValid, Is.True);
            Assert.That(health.Results().Single().Message, Is.EqualTo(warning.Message));

            definition.Settings = testedSettings;
            events.PublishEvent(new ProviderUpdatedEvent<IIndexer>(definition));
            Assert.That(health.Results(), Is.Empty);

            Refresh();
            Assert.That(health.Results().Single().Message, Is.EqualTo(warning.Message));
            Refresh();
            Assert.That(summaryHealthChanges, Is.EqualTo(2), "The new unreadable reply raises the warning again, and the timeout leaves it in place.");
            Assert.That(health.Results().Single().Message, Is.EqualTo(warning.Message));

            Refresh();
            Assert.Multiple(() =>
            {
                Assert.That(health.Results(), Is.Empty);
                Assert.That(summaryHealthChanges, Is.EqualTo(3));
                Assert.That(clientState.Requests, Has.Count.EqualTo(5));
                Assert.That(factoryState.PersistedMamIds, Is.EqualTo(new[] { "rotated-token" }));
            });
        }

        [Test]
        public void account_summary_warning_should_clear_if_the_same_id_is_now_a_different_indexer_type()
        {
            var definition = new IndexerDefinition
            {
                Id = 9,
                Name = "Fixture indexer",
                EnableAutomaticSearch = true,
                Settings = new MyAnonaMouseSettings()
            };
            var check = new MyAnonaMouseAccountSummaryCheck(
                Proxy<IIndexerFactory>((_, _) => new List<IndexerDefinition> { definition }),
                Proxy<IMyAnonaMouseAccountStatusService>((_, _) => new Dictionary<int, DateTime> { [definition.Id] = DateTime.UtcNow }),
                Proxy<ILocalizationService>((_, _) => "Unreadable summary for {0}"));

            Assert.That(check.Check().Type, Is.EqualTo(HealthCheckResult.Warning));

            definition.Settings = new NewznabSettings();

            Assert.That(check.Check().Type, Is.EqualTo(HealthCheckResult.Ok));
        }

        [Test]
        public void unsatisfied_slot_protection_should_default_on_for_legacy_settings_json()
        {
            var settings = JsonConvert.DeserializeObject<MyAnonaMouseSettings>("{\"mamId\":\"secret\"}");

            Assert.Multiple(() =>
            {
                Assert.That(settings.ProtectUnsatisfiedSlots, Is.True);
                Assert.That(settings.UnsatisfiedSlotReserve, Is.EqualTo(5));
                Assert.That(settings.ManualGrabBuffer, Is.Zero);
            });
        }

        [TestCase("timeout")]
        [TestCase("http-403")]
        [TestCase("malformed")]
        [TestCase("stale")]
        public void a_failed_manual_check_waits_then_fetches_a_new_summary(string failure)
        {
            var clock = new SummaryClock();
            var waits = new List<TimeSpan>();
            var throttle = CreateSummaryThrottle(clock, waits);
            var client = CreateClient(request => ValidSummaryResponse(request));
            var clientState = (IndexerHttpClientProxy)(object)client;
            clientState.Responses.Enqueue(request => failure switch
            {
                "timeout" => throw new TimeoutException(),
                "http-403" => new HttpResponse(request, new HttpHeader(), "", HttpStatusCode.Forbidden),
                "stale" => JsonResponse(request, "{\"uid\":42,\"classname\":\"User\",\"created\":1600000000,\"unsat\":{\"count\":3,\"limit\":50}}"),
                _ => JsonResponse(request, "not-json")
            });
            clientState.Responses.Enqueue(request => ValidSummaryResponse(request));
            var indexer = CreateTestIndexer(new MyAnonaMouseSettings { MamId = "fixture-token" }, client, throttle: throttle);

            Assert.That(indexer.Test().IsValid, Is.True);
            clock.Advance(TimeSpan.FromHours(1));
            Assert.That(indexer.Test().IsValid, Is.False);
            clock.Advance(TimeSpan.FromSeconds(18));
            if (failure == "stale")
            {
                Assert.That(indexer.Test().IsValid, Is.False);
                Assert.That(waits, Is.Empty);
                Assert.That(clientState.Requests, Has.Count.EqualTo(2));
                clock.Advance(TimeSpan.FromSeconds(42));
            }

            Assert.That(indexer.Test().IsValid, Is.True);
            Assert.That(waits, Is.EqualTo(failure == "stale" ? Array.Empty<TimeSpan>() : new[] { TimeSpan.FromSeconds(42) }));
            Assert.That(clientState.Requests, Has.Count.EqualTo(3));
        }

        [TestCase(true, ProviderMessageType.Info, "User Class: User")]
        [TestCase(false, ProviderMessageType.Warning, "account status is unavailable or stale")]
        public void indexer_factory_keeps_the_mam_test_message(bool validSummary, ProviderMessageType type, string message)
        {
            var logger = LogManager.GetCurrentClassLogger();
            var indexer = CreateTestIndexer(new MyAnonaMouseSettings { MamId = "fixture-token", ProtectUnsatisfiedSlots = false },
                CreateSummaryClient(validSummary ? "fresh" : "malformed"));
            var definition = (IndexerDefinition)indexer.Definition;
            definition.Id = 0;
            definition.Implementation = nameof(TestableMyAnonaMouse);
            var factory = new IndexerFactory(null, null, new[] { indexer }, Proxy<IServiceProvider>((_, _) => indexer), null, logger);

            Assert.That(factory.Test(definition).IsValid, Is.True);
            Assert.That(definition.Message?.Type, Is.EqualTo(type));
            Assert.That(definition.Message?.Message, Does.Contain(message));
        }

        [Test]
        public void background_refresh_reuses_a_recent_success_without_changing_its_fetch_time()
        {
            var clock = new SummaryClock();
            var throttle = new MyAnonaMouseAccountSummaryThrottle(clock, _ => throw new InvalidOperationException("Unexpected wait"));
            var client = CreateClient(request =>
            {
                Assert.That(request.RateLimitKey, Is.EqualTo("9"));
                Assert.That(request.AllowAutoRedirect, Is.False);
                return ValidSummaryResponse(request, "mam_id=rotated-token; path=/");
            });
            var tested = new MyAnonaMouseSettings { MamId = "fixture-token" };
            Assert.That(CreateTestIndexer(tested, client, throttle: throttle).Test().IsValid, Is.True);
            clock.Advance(TimeSpan.FromSeconds(18));
            var factory = DispatchProxy.Create<IIndexerFactory, IndexerFactoryProxy>();
            var factoryState = (IndexerFactoryProxy)(object)factory;
            var stored = new MyAnonaMouseSettings { MamId = "fixture-token" };
            factoryState.Indexer = CreateIndexer(stored, client, throttle);
            new MyAnonaMouseAccountStatusService(factory, Proxy<IEventAggregator>((_, _) => null), LogManager.GetCurrentClassLogger())
                .Execute(new RefreshMyAnonaMouseAccountStatusCommand());

            Assert.That(((IndexerHttpClientProxy)(object)client).Requests, Has.Count.EqualTo(1));
            Assert.That(stored.UnsatisfiedStatusRefreshedUtc, Is.EqualTo(tested.UnsatisfiedStatusRefreshedUtc));
            Assert.That(factoryState.PersistedMamIds, Is.EqualTo(new[] { "rotated-token" }));
        }

        [TestCase("cookie")]
        [TestCase("proxy")]
        public async Task a_changed_connection_waits_then_fetches_an_independent_summary(string changedField)
        {
            var clock = new SummaryClock();
            var waits = new List<TimeSpan>();
            var throttle = CreateSummaryThrottle(clock, waits);
            var client = CreateClient(request => ValidSummaryResponse(request));
            var clientState = (IndexerHttpClientProxy)(object)client;
            clientState.Responses.Enqueue(request =>
            {
                Assert.That(request.Cookies["mam_id"], Is.EqualTo(changedField == "cookie" ? "different-token" : "fixture-token"));
                return ValidSummaryResponse(request);
            });
            var first = CreateIndexer(new MyAnonaMouseSettings { MamId = "fixture-token" }, client, throttle);
            await first.RefreshAccountStatus();
            clock.Advance(TimeSpan.FromSeconds(25));

            var next = CreateIndexer(new MyAnonaMouseSettings { MamId = changedField == "cookie" ? "different-token" : "fixture-token" }, client, throttle);
            ((IndexerDefinition)next.Definition).ProxyId = changedField == "proxy" ? 5 : null;
            await next.RefreshAccountStatus();
            Assert.That(waits, Is.EqualTo(new[] { TimeSpan.FromSeconds(35) }));
            Assert.That(clientState.Requests, Has.Count.EqualTo(2));
        }

        [Test]
        public async Task refresh_with_the_issued_cookie_reuses_the_recent_reply()
        {
            var clock = new SummaryClock();
            var throttle = new MyAnonaMouseAccountSummaryThrottle(clock, _ => throw new InvalidOperationException("Unexpected wait"));
            var client = CreateClient(request =>
            {
                Assert.That(request.Cookies["mam_id"], Is.EqualTo("fixture-token"));
                return ValidSummaryResponse(request, "mam_id=rotated-token; path=/");
            });
            var stored = new MyAnonaMouseSettings { MamId = "fixture-token" };
            var indexer = CreateIndexer(stored, client, throttle);

            var first = await indexer.RefreshAccountStatus();
            clock.Advance(TimeSpan.FromSeconds(20));
            Assert.That(stored.MamId, Is.EqualTo("rotated-token"));
            var second = await indexer.RefreshAccountStatus();

            Assert.That(second, Is.SameAs(first));
            Assert.That(stored.UnsatisfiedStatusRefreshedUtc, Is.EqualTo(first.RefreshedUtc));
            Assert.That(((IndexerHttpClientProxy)(object)client).Requests, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task the_cooldown_runs_from_completion_of_a_slow_request()
        {
            var clock = new SummaryClock();
            var waits = new List<TimeSpan>();
            var throttle = CreateSummaryThrottle(clock, waits);
            var response = new TaskCompletionSource<MyAnonaMouseAccountStatus>();
            var connection = new MamConnection("https://mam.example", "fixture-token", true, null);
            var first = throttle.SendAsync(connection, () => response.Task);
            clock.Advance(TimeSpan.FromSeconds(75));
            var second = throttle.SendAsync(connection with { MamId = "other-token" }, () => response.Task);
            Assert.That(second.IsCompleted, Is.False);
            response.SetResult(new MyAnonaMouseAccountStatus());
            await first;
            await second;
            Assert.That(waits, Is.EqualTo(new[] { TimeSpan.FromMinutes(1) }));
        }

        private static MyAnonaMouseAccountSummaryThrottle CreateSummaryThrottle(SummaryClock clock, List<TimeSpan> waits)
        {
            return new MyAnonaMouseAccountSummaryThrottle(clock, remaining =>
            {
                waits.Add(remaining);
                clock.Advance(remaining);
                return Task.CompletedTask;
            });
        }

        [Test]
        public void unsatisfied_slot_protection_should_preserve_an_explicit_advanced_opt_out()
        {
            var settings = JsonConvert.DeserializeObject<MyAnonaMouseSettings>("{\"mamId\":\"secret\",\"protectUnsatisfiedSlots\":false}");

            Assert.That(settings.ProtectUnsatisfiedSlots, Is.False);
        }

        internal static MyAnonaMouse CreateIndexerWithAccountSummary(MyAnonaMouseSettings settings, string setCookie = null)
        {
            return CreateIndexer(settings, CreateClient(request => ValidSummaryResponse(request, setCookie)));
        }

        private static MyAnonaMouse CreateIndexer(MyAnonaMouseSettings settings, IIndexerHttpClient client = null, IMyAnonaMouseAccountSummaryThrottle throttle = null)
        {
            var factory = DispatchProxy.Create<IIndexerHttpClientFactory, IndexerHttpClientFactoryProxy>();
            ((IndexerHttpClientFactoryProxy)(object)factory).Client = client;

            return new MyAnonaMouse(factory, null, null, null, throttle ?? new ImmediateSummaryThrottle(), LogManager.GetCurrentClassLogger())
            {
                Definition = new IndexerDefinition
                {
                    Id = 9,
                    Name = "MyAnonaMouse",
                    Implementation = nameof(MyAnonaMouse),
                    Settings = settings
                }
            };
        }

        private static TestableMyAnonaMouse CreateTestIndexer(MyAnonaMouseSettings settings, IIndexerHttpClient client, Logger logger = null, IMyAnonaMouseAccountSummaryThrottle throttle = null)
        {
            var factory = DispatchProxy.Create<IIndexerHttpClientFactory, IndexerHttpClientFactoryProxy>();
            ((IndexerHttpClientFactoryProxy)(object)factory).Client = client;
            return new TestableMyAnonaMouse(factory, throttle ?? new ImmediateSummaryThrottle(), logger ?? LogManager.GetCurrentClassLogger())
            {
                Definition = new IndexerDefinition
                {
                    Id = 9,
                    Name = "MyAnonaMouse",
                    Implementation = nameof(MyAnonaMouse),
                    Settings = settings
                }
            };
        }

        private static IIndexerHttpClient CreateSummaryClient(string scenario)
        {
            var created = scenario == "stale"
                ? DateTimeOffset.UtcNow.AddHours(-3).ToUnixTimeSeconds()
                : DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds();
            var client = DispatchProxy.Create<IIndexerHttpClient, IndexerHttpClientProxy>();
            ((IndexerHttpClientProxy)(object)client).Responses.Enqueue(request => scenario switch
            {
                "malformed" => JsonResponse(request, "not-json"),
                "missing-uid" => JsonResponse(request, $"{{\"classname\":\"User\",\"created\":{created},\"unsat\":{{\"count\":3,\"limit\":50}}}}"),
                "missing-count" => JsonResponse(request, $"{{\"uid\":42,\"classname\":\"User\",\"created\":{created},\"unsat\":{{\"limit\":50}}}}"),
                "http-failure" => new HttpResponse(request, new HttpHeader { ContentType = "application/json" }, "", HttpStatusCode.ServiceUnavailable),
                _ => JsonResponse(request, $"{{\"uid\":42,\"classname\":\"User\",\"created\":{created},\"unsat\":{{\"count\":3,\"limit\":50}}}}")
            });
            return client;
        }

        private static IIndexerHttpClient CreateClient(Func<HttpRequest, HttpResponse> response)
        {
            var client = DispatchProxy.Create<IIndexerHttpClient, IndexerHttpClientProxy>();
            ((IndexerHttpClientProxy)(object)client).Responses.Enqueue(response);
            return client;
        }

        private static HttpResponse JsonResponse(HttpRequest request, string content)
        {
            return new HttpResponse(request, new HttpHeader { ContentType = "application/json" }, content, HttpStatusCode.OK);
        }

        private static HttpResponse ValidSummaryResponse(HttpRequest request, string setCookie = null)
        {
            var created = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds();
            var response = JsonResponse(request, $"{{\"uid\":42,\"classname\":\"User\",\"created\":{created},\"unsat\":{{\"count\":3,\"limit\":50}}}}");
            if (setCookie != null)
            {
                response.Headers.Add("Set-Cookie", setCookie);
            }

            return response;
        }

        private static string EligibleAudiobookUrl()
        {
            return "https://www.myanonamouse.net/tor/download.php?tid=42&canUseToken=true&isAudiobook=true";
        }
    }
}
