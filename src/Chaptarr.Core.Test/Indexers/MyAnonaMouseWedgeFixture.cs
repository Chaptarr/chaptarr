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
using NzbDrone.Common.Http;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.ThingiProvider;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    public class MyAnonaMouseWedgeFixture
    {
        private const string MinimalTorrent = "d8:announce14:http://tracker4:infod6:lengthi1e4:name1:x12:piece lengthi16384e6:pieces20:12345678901234567890ee";

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
            public TestableMyAnonaMouse(IIndexerHttpClientFactory httpClientFactory, Logger logger)
                : base(httpClientFactory, null, null, null, null, logger)
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

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IIndexerFactory.GetAvailableProviders))
                {
                    return new List<IIndexer> { Indexer };
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }

        private class ReservationRepositoryProxy : DispatchProxy
        {
            public List<MamUnsatisfiedSlotReservation> Rows { get; } = new List<MamUnsatisfiedSlotReservation>();
            public int UpdateCount { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IMamUnsatisfiedSlotReservationRepository.Find):
                        return Rows.SingleOrDefault(row => row.IndexerId == (int)args[0] && row.TorrentId == (string)args[1]);
                    case nameof(IMamUnsatisfiedSlotReservationRepository.Update):
                        UpdateCount++;
                        return args[0];
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

            var repository = DispatchProxy.Create<IMamUnsatisfiedSlotReservationRepository, ReservationRepositoryProxy>();
            var repositoryState = (ReservationRepositoryProxy)(object)repository;
            repositoryState.Rows.Add(new MamUnsatisfiedSlotReservation
            {
                Id = 1, IndexerId = 9, TorrentId = "42", ReservedUtc = DateTime.UtcNow
            });

            var indexer = CreateIndexer(new MyAnonaMouseSettings
            {
                UseFreeleechWedge = (int)MyAnonaMouseFreeleechWedgeAction.Preferred,
                UseFreeleechOnlyForAudiobooks = true
            }, client, repository);
            var request = indexer.GetDownloadRequest(EligibleAudiobookUrl());

            var response = await indexer.ExecuteDownloadRequestAsync(request);

            Assert.That(response.Headers.ContentType, Is.EqualTo("application/x-bittorrent"));
            Assert.That(clientState.Requests, Has.Count.EqualTo(2));
            Assert.That(clientState.Requests[0], Does.Contain("fl"));
            Assert.That(clientState.Requests[1], Does.Not.Contain("fl"));
            Assert.That(repositoryState.Rows.Single().ConfirmedUtc, Is.Not.Null);
            Assert.That(repositoryState.UpdateCount, Is.EqualTo(1));
        }

        [Test]
        public async Task html_response_should_not_confirm_that_mam_served_a_torrent()
        {
            var client = DispatchProxy.Create<IIndexerHttpClient, IndexerHttpClientProxy>();
            var clientState = (IndexerHttpClientProxy)(object)client;
            clientState.Responses.Enqueue(request => new HttpResponse(
                request,
                new HttpHeader { ContentType = "text/html" },
                "Not signed in",
                HttpStatusCode.OK));

            var repository = DispatchProxy.Create<IMamUnsatisfiedSlotReservationRepository, ReservationRepositoryProxy>();
            var repositoryState = (ReservationRepositoryProxy)(object)repository;
            repositoryState.Rows.Add(new MamUnsatisfiedSlotReservation
            {
                Id = 1, IndexerId = 9, TorrentId = "42", ReservedUtc = DateTime.UtcNow
            });

            var indexer = CreateIndexer(new MyAnonaMouseSettings(), client, repository);

            await indexer.ExecuteDownloadRequestAsync(indexer.GetDownloadRequest(EligibleAudiobookUrl()));

            Assert.That(repositoryState.Rows.Single().ConfirmedUtc, Is.Null);
            Assert.That(repositoryState.UpdateCount, Is.Zero);
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
                    ? "{\"classname\":\"Elite VIP\",\"snatch_summary\":{\"created\":1785171600,\"unsat\":{\"count\":196,\"limit\":200}}}"
                    : "{\"classname\":\"Elite VIP\",\"created\":1785171600,\"unsat\":{\"count\":196,\"limit\":200}}",
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

        [TestCase("{\"classname\":\"User\",\"created\":1785171600,\"unsat\":{\"limit\":200}}")]
        [TestCase("{\"classname\":\"User\",\"created\":1785171600,\"unsat\":{\"count\":null,\"limit\":200}}")]
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

            Assert.ThrowsAsync<JsonSerializationException>(async () => await indexer.RefreshAccountStatus());

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
                $"{{\"classname\":\"User\",\"created\":{created},\"unsat\":{{\"count\":0,\"limit\":50}}}}"));
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
                $"{{\"classname\":\"User\",\"created\":1785171600,\"unsat\":{{\"count\":\"{privateValue}\",\"limit\":200}}}}"));
            var indexer = CreateTestIndexer(new MyAnonaMouseSettings { MamId = "fixture-token" }, client, logger);
            indexer.Definition.Name = "Fixture MAM";

            if (background)
            {
                var factory = DispatchProxy.Create<IIndexerFactory, IndexerFactoryProxy>();
                ((IndexerFactoryProxy)(object)factory).Indexer = indexer;
                new MyAnonaMouseAccountStatusService(factory, null, logger).Execute(new RefreshMyAnonaMouseAccountStatusCommand());
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
                Assert.That(messages.Logs[0], Does.Contain(nameof(JsonReaderException)));
                Assert.That(messages.Logs[0], Does.Not.Contain(privateValue));
                Assert.That(messages.Logs[0], Does.Not.Contain("fixture-token"));
            });
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

        [Test]
        public void unsatisfied_slot_protection_should_preserve_an_explicit_advanced_opt_out()
        {
            var settings = JsonConvert.DeserializeObject<MyAnonaMouseSettings>("{\"mamId\":\"secret\",\"protectUnsatisfiedSlots\":false}");

            Assert.That(settings.ProtectUnsatisfiedSlots, Is.False);
        }

        private static MyAnonaMouse CreateIndexer(MyAnonaMouseSettings settings, IIndexerHttpClient client = null, IMamUnsatisfiedSlotReservationRepository repository = null)
        {
            var factory = DispatchProxy.Create<IIndexerHttpClientFactory, IndexerHttpClientFactoryProxy>();
            ((IndexerHttpClientFactoryProxy)(object)factory).Client = client;

            return new MyAnonaMouse(factory, null, null, null, repository, LogManager.GetCurrentClassLogger())
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

        private static TestableMyAnonaMouse CreateTestIndexer(MyAnonaMouseSettings settings, IIndexerHttpClient client, Logger logger = null)
        {
            var factory = DispatchProxy.Create<IIndexerHttpClientFactory, IndexerHttpClientFactoryProxy>();
            ((IndexerHttpClientFactoryProxy)(object)factory).Client = client;
            return new TestableMyAnonaMouse(factory, logger ?? LogManager.GetCurrentClassLogger())
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
                "missing-count" => JsonResponse(request, $"{{\"classname\":\"User\",\"created\":{created},\"unsat\":{{\"limit\":50}}}}"),
                "http-failure" => new HttpResponse(request, new HttpHeader { ContentType = "application/json" }, "", HttpStatusCode.ServiceUnavailable),
                _ => JsonResponse(request, $"{{\"classname\":\"User\",\"created\":{created},\"unsat\":{{\"count\":3,\"limit\":50}}}}")
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

        private static string EligibleAudiobookUrl()
        {
            return "https://www.myanonamouse.net/tor/download.php?tid=42&canUseToken=true&isAudiobook=true";
        }
    }
}
