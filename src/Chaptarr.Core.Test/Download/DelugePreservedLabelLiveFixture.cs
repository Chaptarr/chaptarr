using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Threading;
using Chaptarr.Core.Test.Common;
using Newtonsoft.Json.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.Clients.Deluge;
using NzbDrone.Core.Download.History;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class DelugePreservedLabelLiveFixture
    {
        [Test, Explicit("Requires an authorized disposable, trackerless torrent and probe labels")]
        public void application_should_preserve_seeding_and_manual_pause_during_live_label_changes()
        {
            var input = Environment.GetEnvironmentVariable("CHAPTARR_DELUGE_PROBE");
            if (string.IsNullOrEmpty(input)) Assert.Ignore("Live disposable probe not supplied");
            var probe = JObject.Parse(input);
            var settings = probe["settings"].ToObject<DelugeSettings>();
            var hash = probe.Value<string>("hash");
            var labels = probe["labels"].Values<string>().ToArray();
            Assert.That(labels, Has.Length.EqualTo(3));
            Assert.That(labels.All(label => label.StartsWith("chaptarr_probe_", StringComparison.Ordinal)), Is.True);
            settings.AudiobookCategory = labels[0];
            settings.EbookCategory = null;
            settings.MusicCategory = null;
            settings.AudiobookImportedCategory = labels[1];
            var logger = LogManager.GetCurrentClassLogger();
            using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
            using var http = new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            var transport = Proxy<IHttpClient>((method, args) =>
            {
                var request = (HttpRequest)args[0];
                using var outgoing = new HttpRequestMessage(request.Method, request.Url.FullUri);
                outgoing.Headers.UserAgent.ParseAdd("Chaptarr/0.9.1025");
                outgoing.Content = new ByteArrayContent(request.ContentData);
                outgoing.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                using var response = http.Send(outgoing);
                return new HttpResponse(request, new HttpHeader(), response.Content.ReadAsStringAsync().GetAwaiter().GetResult(), response.StatusCode);
            });
            var native = new DelugeProxy(new CacheManager(), transport, logger);
            var initial = native.GetTorrent(hash, settings);
            Assert.That(initial.Name, Is.EqualTo(probe.Value<string>("name")));
            Assert.That(initial.Hash, Is.EqualTo(hash));
            Assert.That(initial.Size, Is.EqualTo(1048576));
            var grab = new DownloadHistory
            {
                Id = 1, DownloadId = hash.ToUpperInvariant(), DownloadClientId = 5, EventType = DownloadHistoryEventType.DownloadGrabbed,
                Data = new Dictionary<string, string> { [DownloadHistory.KeepSeedingKey] = bool.TrueString }
            };
            var history = Proxy<IDownloadHistoryRepository>((method, args) => method.Name switch
            {
                nameof(IDownloadHistoryRepository.FindByDownloadId) => new List<DownloadHistory> { Json.Deserialize<DownloadHistory>(grab.ToJson()) },
                nameof(IDownloadHistoryRepository.GetPendingLabelChanges) => grab.Data.ContainsKey(DownloadHistory.PendingLabelChangeKey)
                    ? new List<DownloadHistory> { Json.Deserialize<DownloadHistory>(grab.ToJson()) } : new List<DownloadHistory>(),
                "SetFields" => Save((DownloadHistory)args[0]),
                _ => throw new AssertionException(method.Name)
            });
            object Save(DownloadHistory value)
            {
                grab = Json.Deserialize<DownloadHistory>(value.ToJson());
                return null;
            }

            var failSettingsOnce = false;
            string lostReply = null;
            var rpcCalls = new List<string>();
            var proxy = Proxy<IDelugeProxy>((method, args) =>
            {
                rpcCalls.Add(method.Name);
                if (method.Name == nameof(IDelugeProxy.SetTorrentLabel) &&
                    (native.GetTorrent(hash, settings).Label == labels[2] || (string)args[1] == labels[2]))
                {
                    var guarded = native.GetTorrent(hash, settings);
                    Assert.That(guarded.Paused && guarded.State == "Paused", Is.True,
                        "Native ratio-removal state must be safe before applying label limits");
                }

                if (failSettingsOnce && method.Name == nameof(IDelugeProxy.SetTorrentSeedingConfiguration))
                {
                    failSettingsOnce = false;
                    throw new DownloadClientUnavailableException("Injected outage after the live label change");
                }

                try
                {
                    var result = method.Invoke(native, args);
                    if (lostReply != null && method.Name == nameof(IDelugeProxy.SetTorrentPaused) &&
                        (bool)args[1] == (lostReply == "pause"))
                    {
                        lostReply = null;
                        throw new DownloadClientUnavailableException("Injected lost reply after the live pause/resume RPC");
                    }

                    return result;
                }
                catch (System.Reflection.TargetInvocationException ex)
                {
                    ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                    throw;
                }
            });
            Deluge Client() => new(proxy, null, null, null, null, null, null,
                Proxy<IDownloadSeedConfigProvider>((method, args) => new TorrentSeedConfiguration { KeepSeeding = true }), history, new CacheManager(), logger)
            {
                Definition = new DownloadClientDefinition { Id = 5, Settings = settings }
            };
            DelugeTorrent Wait(Func<DelugeTorrent, bool> predicate)
            {
                for (var attempt = 0; attempt < 40; attempt++)
                {
                    var torrent = native.GetTorrent(hash, settings);
                    if (predicate(torrent)) return torrent;
                    Thread.Sleep(100);
                }

                throw new AssertionException("Probe state did not settle");
            }

            foreach (var test in new[]
                     {
                         (From: labels[0], To: labels[1], Paused: false, Outage: false, LostReply: (string)null),
                         (From: labels[1], To: labels[2], Paused: false, Outage: false, LostReply: (string)null),
                         (From: labels[2], To: labels[0], Paused: false, Outage: false, LostReply: (string)null),
                         (From: labels[0], To: labels[2], Paused: true, Outage: false, LostReply: (string)null),
                         (From: labels[2], To: labels[1], Paused: false, Outage: true, LostReply: (string)null),
                         (From: labels[1], To: labels[2], Paused: false, Outage: true, LostReply: (string)null),
                         (From: labels[1], To: labels[2], Paused: false, Outage: false, LostReply: "pause"),
                         (From: labels[2], To: labels[1], Paused: false, Outage: false, LostReply: "resume")
                     })
            {
                native.SetTorrentPaused(hash, true, settings);
                Wait(t => t.Paused && t.State == "Paused");
                native.SetTorrentLabel(hash, test.From, settings);
                native.SetTorrentSeedingConfiguration(hash, new TorrentSeedConfiguration { KeepSeeding = true }, settings);
                native.SetTorrentConfiguration(hash, "auto_managed", false, settings);
                if (!test.Paused) native.SetTorrentPaused(hash, false, settings);
                Wait(t => test.Paused ? t.State == "Paused" : t.State == "Seeding");
                settings.AudiobookImportedCategory = test.To;
                rpcCalls.Clear();
                failSettingsOnce = test.Outage;
                lostReply = test.LostReply;
                var item = new DownloadClientItem { DownloadId = hash.ToUpperInvariant(), Category = test.From, MediaType = BookMediaType.Audiobook };
                Client().MarkItemAsImported(item);
                if (test.Outage || test.LostReply != null)
                {
                    if (test.Outage)
                    {
                        var interrupted = native.GetTorrent(hash, settings);
                        TestContext.WriteLine("LIVE_DELUGE interrupted state={0} paused={1} autoManaged={2} stopAtRatio={3} removeAtRatio={4}",
                            interrupted.State, interrupted.Paused, interrupted.IsAutoManaged, interrupted.StopAtRatio, interrupted.RemoveAtRatio);
                        Assert.That(interrupted.Paused, Is.True);
                        Thread.Sleep(6000);
                        Assert.That(native.GetTorrent(hash, settings).Paused, Is.True, "An outage must stay paused across Deluge's ratio-check tick");
                    }

                    Assert.That(grab.Data.ContainsKey(DownloadHistory.PendingLabelChangeKey), Is.True);
                    Client().RecoverPendingOperations();
                }

                var after = Wait(t => test.Paused ? t.State == "Paused" : t.State == "Seeding");
                Assert.That(after.Label, Is.EqualTo(test.To));
                Assert.That(after.StopAtRatio || after.RemoveAtRatio || after.IsAutoManaged, Is.False);
                Assert.That(grab.Data.ContainsKey(DownloadHistory.PendingLabelChangeKey), Is.False);
                if (test.From != labels[2] && test.To != labels[2]) Assert.That(rpcCalls, Does.Not.Contain(nameof(IDelugeProxy.SetTorrentPaused)));
                TestContext.WriteLine("LIVE_DELUGE fromQueue={0} toQueue={1} originallyPaused={2} outage={3} state={4} lostReply={5} verified=true",
                    test.From == labels[2], test.To == labels[2], test.Paused, test.Outage, after.State, test.LostReply ?? "none");
            }
        }
    }
}
