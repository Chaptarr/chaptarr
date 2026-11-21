using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Chaptarr.Api.V1.Queue;
using Chaptarr.Core.Test.Common;
using Newtonsoft.Json.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.Clients.Deluge;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Queue;
using NzbDrone.Core.RemotePathMappings;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class DelugeBulkCategoryLiveFixture
    {
        [Test, Explicit("Requires authorized disposable torrents and isolated probe labels")]
        public async Task bulk_category_change_should_preserve_torrents_and_measure_poll_contention()
        {
            var input = Environment.GetEnvironmentVariable("CHAPTARR_DELUGE_BULK_PROBE");
            if (string.IsNullOrEmpty(input)) Assert.Ignore("Live disposable probe not supplied");
            var probe = JObject.Parse(input);
            var settings = probe["settings"].ToObject<DelugeSettings>();
            var torrents = probe["torrents"].ToObject<List<Dictionary<string, string>>>();
            var labels = probe["labels"].Values<string>().ToArray();
            Assert.That(torrents.Count, Is.EqualTo(4));
            Assert.That(labels.Length == 3 && labels.All(x => x.StartsWith("chaptarr_probe_", StringComparison.Ordinal)), Is.True);
            settings.AudiobookCategory = labels[0];
            settings.EbookCategory = settings.MusicCategory = null;
            var logger = LogManager.GetCurrentClassLogger();
            var cache = new CacheManager();
            var calls = new ConcurrentDictionary<string, int>();
            using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
            using var http = new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            var transport = Proxy<IHttpClient>((method, args) =>
            {
                var request = (HttpRequest)args[0];
                var rpc = JObject.Parse(System.Text.Encoding.UTF8.GetString(request.ContentData)).Value<string>("method");
                calls.AddOrUpdate(rpc, 1, (_, count) => count + 1);
                using var outgoing = new HttpRequestMessage(request.Method, request.Url.FullUri);
                outgoing.Headers.UserAgent.ParseAdd("Chaptarr/0.9.1025");
                outgoing.Content = new ByteArrayContent(request.ContentData);
                outgoing.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                using var response = http.Send(outgoing);
                return new HttpResponse(request, new HttpHeader(), response.Content.ReadAsStringAsync().GetAwaiter().GetResult(), response.StatusCode);
            });
            var native = new DelugeProxy(cache, transport, logger);
            var history = new ConcurrentDictionary<string, DownloadHistory>(StringComparer.OrdinalIgnoreCase);
            foreach (var torrent in torrents)
            {
                var actual = native.GetTorrent(torrent["hash"], settings);
                Assert.That(actual.Name, Is.EqualTo(torrent["name"]));
                Assert.That(actual.Size, Is.EqualTo(1048576));
                history[torrent["hash"]] = new DownloadHistory
                {
                    Id = history.Count + 1, DownloadId = torrent["hash"].ToUpperInvariant(), DownloadClientId = 5,
                    EventType = DownloadHistoryEventType.DownloadGrabbed,
                    Data = new Dictionary<string, string> { [DownloadHistory.KeepSeedingKey] = bool.TrueString }
                };
            }

            DownloadHistory Clone(DownloadHistory row) => Json.Deserialize<DownloadHistory>(row.ToJson());
            var reads = 0;
            var repository = Proxy<IDownloadHistoryRepository>((method, args) =>
            {
                if (method.Name == nameof(IDownloadHistoryRepository.FindByDownloadId)) return new List<DownloadHistory> { Clone(history[(string)args[0]]) };
                if (method.Name == nameof(IDownloadHistoryRepository.GetPendingLabelChanges))
                {
                    Interlocked.Increment(ref reads);
                    return history.Values.Where(x => x.Data.ContainsKey(DownloadHistory.PendingLabelChangeKey)).Select(Clone).ToList();
                }
                if (method.Name == "SetFields")
                {
                    var row = (DownloadHistory)args[0];
                    history[row.DownloadId] = Clone(row);
                    return null;
                }
                throw new AssertionException(method.Name);
            });
            using var swapEntered = new ManualResetEventSlim();
            using var releaseSwap = new ManualResetEventSlim();
            var delayOnce = 0;
            var proxy = Proxy<IDelugeProxy>((method, args) =>
            {
                if (method.Name == nameof(IDelugeProxy.SetTorrentLabel) && Interlocked.Exchange(ref delayOnce, 0) == 1)
                {
                    swapEntered.Set();
                    if (!releaseSwap.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Probe did not release the delayed operation");
                }
                try { return method.Invoke(native, args); }
                catch (System.Reflection.TargetInvocationException ex) { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
            });
            var definition = new DownloadClientDefinition { Id = 5, Name = "Disposable Deluge probe", Settings = settings };
            Deluge Client() => new(proxy, null, null, null, null,
                Proxy<IRemotePathMappingService>((method, args) => args.OfType<OsPath>().Single()), null,
                Proxy<IDownloadSeedConfigProvider>((method, args) => new TorrentSeedConfiguration { KeepSeeding = true }), repository, cache, logger)
                { Definition = definition };
            var tracked = new Dictionary<string, TrackedDownload>(StringComparer.OrdinalIgnoreCase);
            var stopped = new ConcurrentBag<string>();
            var tracking = Proxy<ITrackedDownloadService>((method, args) => method.Name switch
            {
                nameof(ITrackedDownloadService.Find) => tracked[(string)args[0]],
                nameof(ITrackedDownloadService.StopTracking) => Stop((string)args[0]),
                nameof(ITrackedDownloadService.GetTrackedDownloads) => new List<TrackedDownload>(),
                nameof(ITrackedDownloadService.TrackDownload) => null,
                nameof(ITrackedDownloadService.UpdateTrackable) => null,
                _ => throw new AssertionException(method.Name)
            });
            object Stop(string hash) { stopped.Add(hash); return null; }
            var profiles = new QualityProfileService(Proxy<IProfileRepository>((method, args) => new List<QualityProfile>
                { new() { Id = 1, Items = new() { new() { Quality = Quality.M4B, Allowed = true } } } }), null, null, null, null, null, logger);
            var controller = new QueueController(global::Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<global::NzbDrone.Core.Books.ISeriesBookLinkService>((_, _) => throw new global::System.InvalidOperationException("Series batch loading was not expected in this fixture")), Proxy<IDownloadImportModeResolver>((method, args) => true), null,
                Proxy<IQueueService>((method, args) => new NzbDrone.Core.Queue.Queue { Id = (int)args[0], DownloadId = torrents[(int)args[0] - 1]["hash"] }),
                Proxy<IPendingReleaseService>((method, args) => method.Name == nameof(IPendingReleaseService.GetPendingQueue) ? new List<NzbDrone.Core.Queue.Queue>() : null),
                profiles, tracking, null, null, Proxy<IProvideDownloadClient>((method, args) => Client()), null, null);
            var otherPolled = false;
            var other = Proxy<IDownloadClient>((method, args) => method.Name switch
            {
                "get_Definition" => new DownloadClientDefinition { Id = 6, Name = "Poll sentinel" },
                nameof(IDownloadClient.GetItems) => OtherItems(),
                _ => throw new AssertionException(method.Name)
            });
            IEnumerable<DownloadClientItem> OtherItems() { otherPolled = true; return Array.Empty<DownloadClientItem>(); }
            var monitoring = new DownloadMonitoringService(
                Proxy<IDownloadClientStatusService>((method, args) => method.Name == nameof(IDownloadClientStatusService.GetBlockedProviders) ? new List<DownloadClientStatus>() : null),
                Proxy<IDownloadClientFactory>((method, args) => new List<IDownloadClient> { Client(), other }),
                Proxy<IEventAggregator>((method, args) => null), Proxy<IManageCommandQueue>((method, args) => null),
                Proxy<IConfigService>((method, args) => true), null, null, tracking, logger);
            DelugeTorrent Wait(string hash, bool paused)
            {
                for (var attempt = 0; attempt < 80; attempt++)
                {
                    var item = native.GetTorrent(hash, settings);
                    if (paused ? item.Paused && item.State == "Paused" : !item.Paused && item.State == "Seeding") return item;
                    Thread.Sleep(100);
                }
                throw new AssertionException("Probe state did not settle");
            }
            var results = new List<object>();
            foreach (var phase in new[] { "queue_off", "queue_on", "delayed_queue_on" })
            {
                swapEntered.Reset();
                releaseSwap.Reset();
                tracked.Clear();
                stopped.Clear();
                for (var i = 0; i < torrents.Count; i++)
                {
                    var hash = torrents[i]["hash"];
                    native.SetTorrentPaused(hash, true, settings);
                    Wait(hash, true);
                    native.SetTorrentLabel(hash, labels[0], settings);
                    native.SetTorrentSeedingConfiguration(hash, new TorrentSeedConfiguration { KeepSeeding = true }, settings);
                    native.SetTorrentConfiguration(hash, "auto_managed", false, settings);
                    if (i != 3) { native.SetTorrentPaused(hash, false, settings); Wait(hash, false); }
                    tracked[hash] = new TrackedDownload { DownloadClient = 5, DownloadItem = new DownloadClientItem
                        { DownloadId = hash, Title = torrents[i]["name"], Category = labels[0], MediaType = NzbDrone.Core.Books.BookMediaType.Audiobook } };
                }
                settings.AudiobookImportedCategory = phase == "queue_off" ? labels[1] : labels[2];
                Client().RecoverPendingOperations();
                calls.Clear();
                reads = 0;
                otherPolled = false;
                var timer = Stopwatch.StartNew();
                var pollMs = 0L;
                var pollBlocked = false;
                delayOnce = phase == "delayed_queue_on" ? 1 : 0;
                var bulk = Task.Run(() => controller.RemoveMany(new QueueBulkResource { Ids = new List<int> { 1, 2, 3, 4, 1 } }, removeFromClient: false, changeCategory: true));
                Task poll = Task.CompletedTask;
                try
                {
                    if (phase == "delayed_queue_on")
                    {
                        Assert.That(swapEntered.Wait(TimeSpan.FromSeconds(10)), Is.True);
                        poll = Task.Run(() => { var watch = Stopwatch.StartNew(); monitoring.Execute(new RefreshMonitoredDownloadsCommand()); pollMs = watch.ElapsedMilliseconds; });
                        await Task.Delay(2000);
                        pollBlocked = !poll.IsCompleted && !otherPolled;
                        releaseSwap.Set();
                    }
                    await Task.WhenAll(bulk, poll).WaitAsync(TimeSpan.FromSeconds(45));
                }
                finally { releaseSwap.Set(); await Task.WhenAll(bulk, poll).WaitAsync(TimeSpan.FromSeconds(45)); }
                var elapsed = timer.ElapsedMilliseconds;
                var phaseCalls = calls.ToDictionary(x => x.Key, x => x.Value);
                Assert.That(stopped.Count, Is.EqualTo(4));
                Assert.That(history.Values.Any(x => x.Data.ContainsKey(DownloadHistory.PendingLabelChangeKey)), Is.False);
                for (var i = 0; i < torrents.Count; i++)
                {
                    var actual = Wait(torrents[i]["hash"], i == 3);
                    Assert.That(actual.Label, Is.EqualTo(settings.AudiobookImportedCategory));
                    Assert.That(actual.StopAtRatio || actual.RemoveAtRatio || actual.IsAutoManaged, Is.False);
                }
                if (phase == "queue_off") Assert.That(phaseCalls.ContainsKey("core.pause_torrent"), Is.False);
                results.Add(new { phase, torrents = 4, elapsedMs = elapsed, pollMs, pollBlocked, otherPolled, historyReads = reads, calls = phaseCalls });
            }
            TestContext.WriteLine("BULK_DELUGE_RESULT=" + results.ToJson());
        }
    }
}
