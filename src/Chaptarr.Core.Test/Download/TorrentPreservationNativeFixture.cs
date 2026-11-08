using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Xml.Linq;
using Chaptarr.Core.Test.Common;
using NLog;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.Clients.Deluge;
using NzbDrone.Core.Download.Clients.QBittorrent;
using NzbDrone.Core.Download.Clients.RTorrent;
using NzbDrone.Core.Download.Clients.Transmission;
using NzbDrone.Core.Download.Clients.UTorrent;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.Indexers.TorrentRss;
using NzbDrone.Core.Parser.Model;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class TorrentPreservationNativeFixture
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        internal static SeedConfigProvider SeedProvider(bool preserve, bool mam = false)
        {
            var generic = new TorrentRssIndexerSettings();
            generic.SeedCriteria.NeverMoveOnImport = preserve;
            generic.SeedCriteria.SeedRatio = 3;
            generic.SeedCriteria.SeedTime = 4320;
            generic.SeedCriteria.DiscographySeedTime = 8640;
            var definition = new IndexerDefinition
            {
                Id = 9,
                Settings = mam ? new MyAnonaMouseSettings { NeverMoveOnImport = preserve, SeedTimeHours = 72 } : generic
            };
            return new SeedConfigProvider(Proxy<IIndexerFactory>((method, args) =>
                (int)args[0] == 9 ? definition : new IndexerDefinition { Id = 10, Settings = new TorrentRssIndexerSettings() }), new CacheManager());
        }

        private static IHttpClient Http(List<HttpRequest> requests, Func<HttpRequest, string> respond, Func<HttpRequest, HttpStatusCode> status = null)
        {
            return Proxy<IHttpClient>((method, args) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(IHttpClient.Execute)));
                var request = (HttpRequest)args[0];
                requests.Add(request);
                var headers = new HttpHeader { ContentType = "application/json" };
                headers.Add("X-Transmission-Session-Id", "test-session");
                return new HttpResponse(request, headers, respond(request), status?.Invoke(request) ?? HttpStatusCode.OK);
            });
        }

        private static string Body(HttpRequest request) => Encoding.UTF8.GetString(request.ContentData ?? Array.Empty<byte>());

        private static void AssertFormValue(HttpRequest request, string name, string value)
        {
            var body = Body(request);
            Assert.That(body.Contains($"{name}={value}") || body.Contains($"name=\"{name}\"\r\n\r\n{value}\r\n"), Is.True, body);
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void shared_provider_should_apply_preservation_per_indexer_and_ignore_seed_goals(bool mam, bool discography)
        {
            var provider = SeedProvider(true, mam);
            var preserved = provider.GetSeedConfiguration(9, discography);
            var ordinary = provider.GetSeedConfiguration(10, discography);
            Assert.Multiple(() =>
            {
                Assert.That(preserved.KeepSeeding, Is.True);
                Assert.That(preserved.Ratio, Is.Null);
                Assert.That(preserved.SeedTime, Is.Null);
                Assert.That(ordinary.KeepSeeding, Is.False);
                Assert.That(SeedProvider(false, mam).GetSeedConfiguration(9, discography).SeedTime,
                    Is.EqualTo(mam && discography ? (TimeSpan?)null : TimeSpan.FromMinutes(discography ? 8640 : 4320)));
            });
        }

        private sealed class QBit : QBittorrent
        {
            public QBit(IQBittorrentProxySelector selector)
                : base(selector, null, null, null, null, null, new CacheManager(), null, Logger)
            {
                Definition = new DownloadClientDefinition
                {
                    Settings = new QBittorrentSettings { Host = "test", Port = 8080, InitialState = (int)QBittorrentState.Start }
                };
            }

            public string Add(RemoteBook book, bool magnet) => magnet
                ? AddFromMagnetLink(book, "abcdef", "magnet:?xt=urn:btih:abcdef&tr=https://example.invalid/announce")
                : AddFromTorrentFile(book, "abcdef", "test.torrent", new byte[] { 1, 2 });
        }

        [TestCase(true, true, "2.11.0")]
        [TestCase(true, false, "2.11.0")]
        [TestCase(false, true, "2.11.0")]
        [TestCase(false, false, "2.11.0")]
        [TestCase(true, true, "2.7.0")]
        [TestCase(true, false, "2.7.0")]
        public void qbit_grab_should_send_torrent_local_limits_on_both_add_routes(bool preserve, bool magnet, string version)
        {
            var requests = new List<HttpRequest>();
            var native = new QBittorrentProxyV2(Http(requests, request => request.Url.FullUri.Contains("webapiVersion") ? version : "{\"dht\":true}"), new CacheManager(), Logger);
            var proxy = Proxy<IQBittorrentProxy>((method, args) => method.Name == nameof(IQBittorrentProxy.IsTorrentLoaded) ? true : method.Invoke(native, args));
            var selector = Proxy<IQBittorrentProxySelector>((method, args) => method.Name == nameof(IQBittorrentProxySelector.GetApiVersion) ? new Version(version) : proxy);
            var client = new QBit(selector);
            var book = new RemoteBook
            {
                Books = new List<Book> { new() { MediaType = BookMediaType.Audiobook } },
                SeedConfiguration = SeedProvider(preserve).GetSeedConfiguration(9, false),
                Release = new TorrentInfo()
            };

            Assert.That(client.Add(book, magnet), Is.EqualTo("abcdef"));
            var limits = requests.Single(request => request.Url.FullUri.Contains(new Version(version) >= new Version(2, 8, 1) ? "/torrents/add" : "/torrents/setShareLimits"));
            AssertFormValue(limits, "ratioLimit", preserve ? "-1" : "3");
            AssertFormValue(limits, "seedingTimeLimit", preserve ? "-1" : "4320");
            if (preserve) AssertFormValue(limits, "inactiveSeedingTimeLimit", "-1");
            else Assert.That(Body(limits), Does.Not.Contain("inactiveSeedingTimeLimit"));
            Assert.That(requests.Any(request => request.Url.FullUri.Contains("setPreferences") || request.Url.FullUri.Contains("setForceStart")), Is.False);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void transmission_should_disable_both_native_stop_modes_only_for_preserved_torrents(bool preserve)
        {
            var requests = new List<HttpRequest>();
            var proxy = new TransmissionProxy(new CacheManager(), Http(requests, _ => "{\"result\":\"success\",\"arguments\":{}}",
                request => request.Method == System.Net.Http.HttpMethod.Get ? HttpStatusCode.Conflict : HttpStatusCode.OK), Logger);
            proxy.SetTorrentSeedingConfiguration("abcdef", SeedProvider(preserve).GetSeedConfiguration(9, false),
                new TransmissionSettings { Host = "test", Port = 9091 });
            var body = JObject.Parse(Body(requests.Last()));
            var args = body["arguments"];
            Assert.Multiple(() =>
            {
                Assert.That(body.Value<string>("method"), Is.EqualTo("torrent-set"));
                Assert.That(args["ids"].Values<string>(), Is.EqualTo(new[] { "abcdef" }));
                Assert.That(args.Value<int>("seedRatioMode"), Is.EqualTo(preserve ? 2 : 1));
                Assert.That(args.Value<int>("seedIdleMode"), Is.EqualTo(preserve ? 2 : 1));
                Assert.That(args["seedRatioLimit"] == null, Is.EqualTo(preserve));
                Assert.That(args["seedIdleLimit"] == null, Is.EqualTo(preserve));
            });
        }

        private sealed class RTorrentClient : RTorrent
        {
            public RTorrentClient(IRTorrentProxy proxy, bool addStopped)
                : base(proxy, null, null, null, null, null, null, null, null, null, Logger, DownloadClientTestRoots.Empty)
            {
                Definition = new DownloadClientDefinition
                {
                    Settings = new RTorrentSettings
                    {
                        Host = "test", AddStopped = addStopped,
                        AudiobookCategory = "audio", EbookCategory = "ebook",
                        AudiobookImportedCategory = "audio-imported", EbookImportedCategory = "ebook-imported",
                        MusicDirectory = "/downloads", OlderTvPriority = (int)RTorrentPriority.High
                    }
                };
            }

            public string Add(RemoteBook book, bool magnet) => magnet
                ? AddFromMagnetLink(book, "abcdef", "magnet:?xt=urn:btih:abcdef")
                : AddFromTorrentFile(book, "abcdef", "Dune.torrent", new byte[] { 1 });
        }

        [Test]
        public void rtorrent_should_apply_preservation_in_the_load_request_and_keep_it_when_relabeling(
            [Values(null, false, true)] bool? preserve,
            [Values(false, true)] bool magnet,
            [Values(false, true)] bool addStopped,
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType mediaType)
        {
            var requests = new List<HttpRequest>();
            var proxy = new RTorrentProxy(Http(requests, request =>
            {
                var rpc = XDocument.Parse(Body(request));
                var method = rpc.Root.Element("methodName").Value;
                var value = method == "d.name" ? new XElement("string", "Dune")
                    : method == "d.custom1.set" ? new XElement("string", rpc.Descendants("param").Last().Value)
                    : new XElement("i4", 0);
                return new XDocument(new XElement("methodResponse", new XElement("params",
                    new XElement("param", new XElement("value", value))))).ToString();
            }));
            var client = new RTorrentClient(proxy, addStopped);
            var book = new RemoteBook
            {
                Books = new List<Book> { new() { MediaType = mediaType } },
                SeedConfiguration = preserve.HasValue ? SeedProvider(preserve.Value).GetSeedConfiguration(9, false) : null
            };

            Assert.That(client.Add(book, magnet), Is.EqualTo("abcdef"));
            var category = mediaType == BookMediaType.Ebook ? "ebook" : "audio";
            var load = XDocument.Parse(Body(requests[0]));
            Assert.That(load.Root.Element("methodName").Value,
                Is.EqualTo(magnet ? (addStopped ? "load.normal" : "load.start") : (addStopped ? "load.raw" : "load.raw_start")));
            var expected = new List<string> { "d.custom1.set=" + category, "d.priority.set=3", "d.directory.set=/downloads" };
            if (preserve == true)
            {
                expected.Insert(0, "d.ignore_commands.set=1");
            }

            Assert.That(load.Descendants("param").Skip(2).Select(param => param.Value), Is.EqualTo(expected));

            var item = new DownloadClientItem { DownloadId = "ABCDEF", Category = category, Title = "Dune" };
            client.MarkItemAsImported(item);
            Assert.That(item.Category, Is.EqualTo(category + "-imported"));
            Assert.That(requests.Select(request => XDocument.Parse(Body(request)).Root.Element("methodName").Value),
                Is.EqualTo(new[] { load.Root.Element("methodName").Value, "d.name", "d.custom1.set", "d.views.push_back_unique" }),
                "Add and relabel must not issue extra stop/start commands or reset the preservation exemption.");
        }

        [Test]
        public void rtorrent_should_propagate_add_failure_without_starting_or_retrying_unprotected(
            [Values(false, true)] bool magnet, [Values(false, true)] bool fault)
        {
            var requests = new List<HttpRequest>();
            var client = new RTorrentClient(new RTorrentProxy(Http(requests, _ =>
                fault
                    ? "<methodResponse><fault><value><struct><member><name>faultCode</name><value><i4>1</i4></value></member><member><name>faultString</name><value><string>Add rejected</string></value></member></struct></value></fault></methodResponse>"
                    : "<methodResponse><params><param><value><i4>1</i4></value></param></params></methodResponse>")), false);
            var book = new RemoteBook
            {
                Books = new List<Book> { new() { MediaType = BookMediaType.Audiobook } },
                SeedConfiguration = SeedProvider(true).GetSeedConfiguration(9, false)
            };

            Assert.Throws<DownloadClientException>(() => client.Add(book, magnet));
            Assert.That(requests, Has.Count.EqualTo(1));
        }

        private sealed class TransmissionClient : Transmission
        {
            public TransmissionClient(ITransmissionProxy proxy)
                : base(proxy, null, null, null, null, null, null, Logger)
            {
                Definition = new DownloadClientDefinition
                {
                    Settings = new TransmissionSettings { Host = "test", Port = 9091 }
                };
            }

            public string Add(RemoteBook book, bool magnet) => magnet
                ? AddFromMagnetLink(book, "abcdef", "magnet:?xt=urn:btih:abcdef")
                : AddFromTorrentFile(book, "abcdef", "Dune.torrent", new byte[] { 1 });
        }

        [Test]
        public void transmission_duplicate_add_should_not_replace_existing_limits(
            [Values(false, true)] bool magnet, [Values(false, true)] bool duplicate, [Values(false, true)] bool preserve)
        {
            var requests = new List<HttpRequest>();
            var proxy = new TransmissionProxy(new CacheManager(), Http(requests, request =>
            {
                var body = Body(request);
                var key = duplicate ? "torrent-duplicate" : "torrent-added";
                return body.Contains("torrent-add")
                    ? "{\"result\":\"success\",\"arguments\":{\"" + key + "\":{\"id\":1,\"hashString\":\"abcdef\",\"name\":\"Dune\"}}}"
                    : "{\"result\":\"success\",\"arguments\":{}}";
            }, request => request.Method == System.Net.Http.HttpMethod.Get ? HttpStatusCode.Conflict : HttpStatusCode.OK), Logger);
            var client = new TransmissionClient(proxy);
            var book = new RemoteBook
            {
                Books = new List<Book> { new() { MediaType = BookMediaType.Audiobook } },
                SeedConfiguration = SeedProvider(preserve).GetSeedConfiguration(9, false)
            };

            Assert.That(client.Add(book, magnet), Is.EqualTo("abcdef"));
            var posts = requests.Where(request => request.Method == System.Net.Http.HttpMethod.Post)
                .Select(request => JObject.Parse(Body(request))).ToList();
            Assert.That(posts.Count(post => post.Value<string>("method") == "torrent-add"), Is.EqualTo(1));
            var settings = posts.Where(post => post.Value<string>("method") == "torrent-set").ToList();
            Assert.That(settings, Has.Count.EqualTo(!duplicate || preserve ? 1 : 0));
            if (preserve)
            {
                Assert.That(settings.Single()["arguments"].Value<int>("seedRatioMode"), Is.EqualTo(2));
                Assert.That(settings.Single()["arguments"].Value<int>("seedIdleMode"), Is.EqualTo(2));
            }
            Assert.That(posts.Any(post => post.Value<string>("method") is "torrent-start" or "torrent-start-now" or "torrent-stop"), Is.False);
        }

        private sealed class DelugeClient : Deluge
        {
            public DelugeClient(IDelugeProxy proxy, IDownloadSeedConfigProvider seedProvider)
                : base(proxy, null, null, null, null, null, null, seedProvider, null, new CacheManager(), Logger)
            {
                Definition = new DownloadClientDefinition
                {
                    Settings = new DelugeSettings
                    {
                        Host = "test", AudiobookCategory = "audio", EbookCategory = "ebook",
                        AudiobookImportedCategory = "audio-imported", EbookImportedCategory = "ebook-imported"
                    }
                };
            }

            public string Add(RemoteBook book, bool magnet) => magnet
                ? AddFromMagnetLink(book, "abcdef", "magnet:?xt=urn:btih:abcdef")
                : AddFromTorrentFile(book, "abcdef", "test.torrent", new byte[] { 1 });
        }

        [TestCase(true, true)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(false, false)]
        public void deluge_should_preserve_seed_settings_when_labels_have_no_queue_rules(bool preserve, bool magnet)
        {
            var requests = new List<HttpRequest>();
            var calls = new List<string>();
            var stopAtRatio = true;
            var removeAtRatio = true;
            var native = new DelugeProxy(new CacheManager(), Http(requests, request =>
            {
                var rpc = JObject.Parse(Body(request));
                var method = rpc.Value<string>("method");
                calls.Add(method);
                object result = null;
                if (method == "auth.login" || method == "web.connected") result = true;
                if (method == "core.add_torrent_file" || method == "core.add_torrent_magnet") result = "abcdef";
                if (method == "core.get_torrent_status") result = new { hash = "abcdef", label = "audio", state = "Seeding" };
                if (method == "label.get_options") result = new { apply_queue = false };
                if (method == "core.set_torrent_options")
                {
                    var options = rpc["params"][1];
                    if (options["stop_at_ratio"] != null) stopAtRatio = options.Value<bool>("stop_at_ratio");
                    if (options["remove_at_ratio"] != null) removeAtRatio = options.Value<bool>("remove_at_ratio");
                }

                return new JObject { ["id"] = rpc["id"], ["result"] = result == null ? JValue.CreateNull() : JToken.FromObject(result), ["error"] = null }.ToString();
            }), Logger);
            var config = SeedProvider(preserve).GetSeedConfiguration(9, false);
            var client = new DelugeClient(native, Proxy<IDownloadSeedConfigProvider>((method, args) => config));
            var remote = new RemoteBook
            {
                Books = new List<Book> { new() { MediaType = BookMediaType.Audiobook } }, SeedConfiguration = config, Release = new TorrentInfo()
            };

            Assert.That(client.Add(remote, magnet), Is.EqualTo("ABCDEF"));
            Assert.That(stopAtRatio, Is.EqualTo(!preserve));
            Assert.That(removeAtRatio, Is.EqualTo(!preserve));
            calls.Clear();
            var item = new DownloadClientItem { DownloadId = "ABCDEF", MediaType = BookMediaType.Audiobook, Category = "audio" };
            client.MarkItemAsImported(item);
            client.MarkItemAsImported(item);
            Assert.That(item.Category, Is.EqualTo("audio-imported"));
            Assert.That(stopAtRatio, Is.EqualTo(!preserve));
            Assert.That(removeAtRatio, Is.EqualTo(!preserve));
            Assert.That(calls.Count(call => call == "label.set_torrent"), Is.EqualTo(1));
            Assert.That(calls, Does.Not.Contain("core.set_torrent_options"));
            Assert.That(calls.Any(call => call == "core.set_config" || call == "core.resume_torrent"), Is.False);
        }

        [Test]
        public void unsupported_native_seed_writer_should_not_block_or_send_a_finite_goal()
        {
            var http = Http(new List<HttpRequest>(), _ => throw new AssertionException("Unsupported seed writer must not alter client settings"));
            var qbit = new QBittorrentProxyV1(http, new CacheManager(), Logger);
            qbit.SetTorrentSeedingConfiguration("abcdef", SeedProvider(true).GetSeedConfiguration(9, false), new QBittorrentSettings());
            var utorrent = new UTorrentProxy(new CacheManager(), http, Logger);
            utorrent.SetTorrentSeedingConfiguration("abcdef", SeedProvider(true).GetSeedConfiguration(9, false), new UTorrentSettings());
        }
    }
}
