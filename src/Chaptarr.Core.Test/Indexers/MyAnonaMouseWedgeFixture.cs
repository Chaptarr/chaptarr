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
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;

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

        [Test]
        public async Task account_status_should_use_mam_unsatisfied_count_limit_and_snapshot()
        {
            var client = DispatchProxy.Create<IIndexerHttpClient, IndexerHttpClientProxy>();
            var clientState = (IndexerHttpClientProxy)(object)client;
            clientState.Responses.Enqueue(request => new HttpResponse(
                request,
                new HttpHeader { ContentType = "application/json" },
                "{\"classname\":\"Elite VIP\",\"created\":1785171600,\"unsat\":{\"count\":196,\"limit\":200}}",
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

        private static string EligibleAudiobookUrl()
        {
            return "https://www.myanonamouse.net/tor/download.php?tid=42&canUseToken=true&isAudiobook=true";
        }
    }
}
