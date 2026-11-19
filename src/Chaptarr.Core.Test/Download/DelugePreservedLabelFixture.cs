using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
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
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Messaging.Events;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public partial class DelugePreservedLabelFixture
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private sealed class Session
        {
            public string Label = "audio";
            public string State = "Seeding";
            public bool Paused;
            public bool AutoManaged = true;
            public bool Stop;
            public bool Remove;
            public bool OldQueue;
            public bool NewQueue = true;
            public bool IgnorePause;
            public int PauseStatusLag;
            public bool UnsettledState;
            private bool _pauseRequested;
            public bool IgnoreSeedSettings;
            public bool IgnoreLabel;
            public bool IgnoreAutoManaged;
            public bool IgnoreResume;
            public string RejectCall;
            public bool Missing;
            public string FailBefore;
            public string FailAfter;
            public readonly List<string> Calls = new();

            public IDelugeProxy Proxy()
            {
                var http = InterfaceProxy.Proxy<IHttpClient>((method, args) =>
                {
                    var request = (HttpRequest)args[0];
                    var rpc = JObject.Parse(Encoding.UTF8.GetString(request.ContentData));
                    var name = rpc.Value<string>("method");
                    Calls.Add(name);
                    if (FailBefore == name)
                    {
                        FailBefore = null;
                        throw new WebException("Injected connection outage");
                    }

                    if (RejectCall == name) throw new DelugeException("Label plugin refused the command", 4);
                    object result = null;
                    var parameters = rpc["params"];
                    switch (name)
                    {
                        case "auth.login":
                        case "web.connected":
                            result = true;
                            break;
                        case "label.get_options":
                            result = new { apply_queue = parameters[0].Value<string>() == "audio" ? OldQueue : NewQueue };
                            break;
                        case "core.get_torrent_status":
                            Assert.That(parameters[0].Value<string>(), Is.EqualTo("abcdef"));
                            if (_pauseRequested && PauseStatusLag-- <= 0) { Paused = true; if (!UnsettledState) State = "Paused"; }
                            result = Missing ? new { } : (object)new
                            {
                                hash = "abcdef", label = Label, state = State, paused = Paused,
                                is_auto_managed = AutoManaged, stop_at_ratio = Stop, remove_at_ratio = Remove
                            };
                            break;
                        case "core.pause_torrent":
                        case "core.resume_torrent":
                            Assert.That(parameters[0].Values<string>(), Is.EqualTo(new[] { "abcdef" }));
                            if (name == "core.pause_torrent" && !IgnorePause) _pauseRequested = true;
                            if (name == "core.resume_torrent" && !IgnoreResume) { _pauseRequested = false; Paused = false; State = AutoManaged ? "Queued" : "Seeding"; }
                            break;
                        case "label.set_torrent":
                            Assert.That(parameters[0].Value<string>(), Is.EqualTo("abcdef"));
                            if (OldQueue || NewQueue)
                            {
                                Assert.That(Paused, Is.True, "Queue limits must never apply while the torrent is running");
                                Assert.That(State, Is.EqualTo("Paused"), "Deluge ratio removal uses its display state");
                                Stop = Remove = true;
                                AutoManaged = !AutoManaged;
                            }

                            if (!IgnoreLabel) Label = parameters[1].Value<string>();
                            break;
                        case "core.set_torrent_options":
                            Assert.That(parameters[0].Values<string>(), Is.EqualTo(new[] { "abcdef" }));
                            var options = parameters[1];
                            if (options["stop_at_ratio"] != null && !IgnoreSeedSettings) Stop = options.Value<bool>("stop_at_ratio");
                            if (options["remove_at_ratio"] != null && !IgnoreSeedSettings) Remove = options.Value<bool>("remove_at_ratio");
                            if (options["auto_managed"] != null && !IgnoreAutoManaged) AutoManaged = options.Value<bool>("auto_managed");
                            break;
                        case "web.update_ui":
                            result = new { torrents = new Dictionary<string, object>() };
                            break;
                        default:
                            throw new AssertionException("Unexpected Deluge operation: " + name);
                    }

                    if (FailAfter == name)
                    {
                        FailAfter = null;
                        throw new WebException("Injected response loss");
                    }

                    return new HttpResponse(request, new HttpHeader(), new JObject
                    {
                        ["id"] = rpc["id"], ["result"] = result == null ? JValue.CreateNull() : JToken.FromObject(result), ["error"] = null
                    }.ToString(), HttpStatusCode.OK);
                });
                return new DelugeProxy(new CacheManager(), http, Logger);
            }
        }

        private sealed class History
        {
            public DownloadHistory Saved = Grab();
            public bool FailWrite;
            public bool LoseWriteReply;
            public bool FailRead;
            public int PendingReads;
            public IDownloadHistoryRepository Repository => Proxy<IDownloadHistoryRepository>((method, args) =>
            {
                if (method.Name == nameof(IDownloadHistoryRepository.FindByDownloadId)) return new List<DownloadHistory> { Clone(Saved) };
                if (method.Name == nameof(IDownloadHistoryRepository.GetPendingLabelChanges))
                {
                    PendingReads++;
                    if (FailRead) throw new InvalidOperationException("Database unavailable");
                    return Saved.Data.ContainsKey(DownloadHistory.PendingLabelChangeKey) ? new List<DownloadHistory> { Clone(Saved) } : new List<DownloadHistory>();
                }
                if (method.Name == "SetFields")
                {
                    if (FailWrite) throw new InvalidOperationException("Database unavailable");
                    Saved = Clone((DownloadHistory)args[0]);
                    if (LoseWriteReply) throw new InvalidOperationException("Database reply lost");
                    return null;
                }

                throw new AssertionException(method.Name);
            });
            private static DownloadHistory Clone(DownloadHistory value) => Json.Deserialize<DownloadHistory>(value.ToJson());
        }

        private static DownloadHistory Grab() => new()
        {
            Id = 1, DownloadId = "ABCDEF", DownloadClientId = 5, AuthorId = 42, BookId = 77,
            Date = DateTime.UtcNow, EventType = DownloadHistoryEventType.DownloadGrabbed,
            Protocol = DownloadProtocol.Torrent, SourceTitle = "Frank Herbert - Dune",
            Data = new Dictionary<string, string> { [DownloadHistory.KeepSeedingKey] = bool.TrueString }
        };

        private static Deluge Client(Session session, IDownloadHistoryRepository repository, bool preserve = true, ICacheManager cache = null)
        {
            return new Deluge(session.Proxy(), null, null, null, null, null, null,
                Proxy<IDownloadSeedConfigProvider>((method, args) => new TorrentSeedConfiguration { KeepSeeding = preserve }), repository, cache ?? new CacheManager(), Logger)
            {
                Definition = new DownloadClientDefinition
                {
                    Id = 5,
                    Settings = new DelugeSettings { Host = "test", AudiobookCategory = "audio", AudiobookImportedCategory = "audio-imported" }
                }
            };
        }

        private static DownloadClientItem Item() => new() { DownloadId = "ABCDEF", Category = "audio", MediaType = BookMediaType.Audiobook };

        [Test]
        public void idle_polls_should_query_once_across_client_instances_and_check_again_after_a_swap()
        {
            var session = new Session();
            var history = new History();
            var cache = new CacheManager();
            for (var poll = 0; poll < 10; poll++) Client(session, history.Repository, cache: cache).RecoverPendingOperations();
            Assert.That(history.PendingReads, Is.EqualTo(1));
            Client(session, history.Repository, cache: cache).MarkItemAsImported(Item());
            for (var poll = 0; poll < 10; poll++) Client(session, history.Repository, cache: cache).RecoverPendingOperations();
            Assert.That(history.PendingReads, Is.EqualTo(2));
            Client(session, history.Repository).RecoverPendingOperations();
            Assert.That(history.PendingReads, Is.EqualTo(3), "A fresh process must read durable history again");
        }

        [Test]
        public void failed_initial_history_read_should_not_disable_recovery()
        {
            var session = new Session();
            var history = new History { FailRead = true };
            var cache = new CacheManager();
            Assert.Throws<InvalidOperationException>(() => Client(session, history.Repository, cache: cache).RecoverPendingOperations());
            history.FailRead = false;
            Client(session, history.Repository, cache: cache).RecoverPendingOperations();
            Client(session, history.Repository, cache: cache).RecoverPendingOperations();
            Assert.That(history.PendingReads, Is.EqualTo(2));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void failed_marker_write_should_reenable_recovery_even_if_the_write_reached_the_database(bool lostReply)
        {
            var session = new Session();
            var history = new History();
            var cache = new CacheManager();
            Client(session, history.Repository, cache: cache).RecoverPendingOperations();
            history.FailWrite = !lostReply;
            history.LoseWriteReply = lostReply;
            Assert.Throws<InvalidOperationException>(() => Client(session, history.Repository, cache: cache).MarkItemAsImported(Item()));
            history.FailWrite = history.LoseWriteReply = false;
            Client(session, history.Repository, cache: cache).RecoverPendingOperations();
            Client(session, history.Repository, cache: cache).RecoverPendingOperations();
            Assert.That(history.PendingReads, Is.EqualTo(2));
            Assert.That(history.Saved.Data.ContainsKey(DownloadHistory.PendingLabelChangeKey), Is.False);
            Assert.That(session.Label, Is.EqualTo(lostReply ? "audio-imported" : "audio"));
        }

        [Test]
        public void unsuccessful_recovery_should_keep_retrying_until_finished_then_stop_reading_history()
        {
            var session = new Session { IgnoreSeedSettings = true };
            var history = new History();
            var cache = new CacheManager();
            Assert.Throws<DownloadClientException>(() => Client(session, history.Repository, cache: cache).MarkItemAsImported(Item()));
            Client(session, history.Repository, cache: cache).RecoverPendingOperations();
            Assert.That(history.Saved.Data.ContainsKey(DownloadHistory.PendingLabelChangeKey), Is.True);
            session.IgnoreSeedSettings = false;
            Client(session, history.Repository, cache: cache).RecoverPendingOperations();
            Client(session, history.Repository, cache: cache).RecoverPendingOperations();
            Assert.That(history.PendingReads, Is.EqualTo(2));
            Assert.That(session.Stop || session.Remove || session.Paused, Is.False);
        }

        [TestCase("Seeding", false)]
        [TestCase("Paused", true)]
        [TestCase("Queued", true)]
        [TestCase("Moving", false)]
        [TestCase("Checking", false)]
        [TestCase("Allocating", false)]
        public void guarded_swap_should_restore_prior_pause_and_queue_policy(string state, bool paused)
        {
            var session = new Session { State = state, Paused = paused };
            var history = new History();
            var item = Item();
            Client(session, history.Repository).MarkItemAsImported(item);
            Assert.Multiple(() =>
            {
                Assert.That(item.Category, Is.EqualTo("audio-imported"));
                Assert.That(session.Label, Is.EqualTo("audio-imported"));
                Assert.That(session.Stop || session.Remove, Is.False);
                Assert.That(session.AutoManaged, Is.True);
                Assert.That(session.Calls.Count(x => x == "core.resume_torrent"), Is.EqualTo(state == "Paused" ? 0 : 1));
                Assert.That(history.Saved.Data.ContainsKey(DownloadHistory.PendingLabelChangeKey), Is.False);
            });
            session.State = "Paused";
            session.Paused = true;
            session.Stop = true;
            session.Calls.Clear();
            Client(session, history.Repository).RecoverPendingOperations();
            Assert.That(session.Calls, Is.Empty);
            Assert.That(session.Stop && session.Paused, Is.True, "A later change is outside the finished operation");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void leaving_queue_label_must_also_be_guarded(bool autoManaged)
        {
            var session = new Session { OldQueue = true, NewQueue = false, AutoManaged = autoManaged };
            Client(session, new History().Repository).MarkItemAsImported(Item());
            Assert.That(session.Stop || session.Remove, Is.False);
            Assert.That(session.AutoManaged, Is.EqualTo(autoManaged));
            Assert.That(session.Calls, Does.Contain("core.pause_torrent"));
        }

        [Test]
        public void queue_off_labels_should_not_pause_or_write_history()
        {
            var session = new Session { NewQueue = false };
            Client(session, new History { FailWrite = true }.Repository).MarkItemAsImported(Item());
            Assert.That(session.Label, Is.EqualTo("audio-imported"));
            Assert.That(session.Calls, Does.Not.Contain("core.pause_torrent"));
            Assert.That(session.Calls, Does.Not.Contain("core.set_torrent_options"));
        }

        [Test]
        public void failed_history_write_must_not_pause_or_change_label()
        {
            var session = new Session();
            Assert.Throws<InvalidOperationException>(() => Client(session, new History { FailWrite = true }.Repository).MarkItemAsImported(Item()));
            Assert.That(session.Calls, Does.Not.Contain("core.pause_torrent"));
            Assert.That(session.Label, Is.EqualTo("audio"));
        }

        [Test]
        public void delayed_pause_status_must_be_confirmed_before_swapping_label()
        {
            var session = new Session { PauseStatusLag = 2 };
            Client(session, new History().Repository).MarkItemAsImported(Item());
            Assert.That(session.Label, Is.EqualTo("audio-imported"));
            Assert.That(session.Calls.Count(x => x == "core.pause_torrent"), Is.EqualTo(1));
            Assert.That(session.Calls.Count(x => x == "core.resume_torrent"), Is.EqualTo(1));
        }

        [Test]
        public void unconfirmed_pause_must_not_change_label()
        {
            var session = new Session { IgnorePause = true };
            Assert.Throws<DownloadClientException>(() => Client(session, new History().Repository).MarkItemAsImported(Item()));
            Assert.That(session.Label, Is.EqualTo("audio"));
            Assert.That(session.Calls, Does.Not.Contain("core.resume_torrent"));
        }

        [Test]
        public void reading_items_must_not_resume_an_unfinished_label_change()
        {
            var session = new Session { IgnoreSeedSettings = true };
            var history = new History();
            Assert.Throws<DownloadClientException>(() => Client(session, history.Repository).MarkItemAsImported(Item()));
            session.IgnoreSeedSettings = false;
            session.Calls.Clear();
            Client(session, history.Repository).GetItems().ToList();
            Assert.That(session.Calls, Is.EqualTo(new[] { "auth.login", "web.connected", "web.update_ui" }));
            Assert.That(session.Paused, Is.True);
            Assert.That(history.Saved.Data.ContainsKey(DownloadHistory.PendingLabelChangeKey), Is.True);
            Client(session, history.Repository).RecoverPendingOperations();
            Assert.That(session.Paused, Is.False);
        }

        [Test]
        public void unverified_seed_settings_must_not_resume()
        {
            var session = new Session { IgnoreSeedSettings = true };
            var history = new History();
            Assert.Throws<DownloadClientException>(() => Client(session, history.Repository).MarkItemAsImported(Item()));
            Assert.That(session.Paused, Is.True);
            session.IgnoreSeedSettings = false;
            Client(session, history.Repository).RecoverPendingOperations();
            Assert.That(session.Stop || session.Remove, Is.False);
            Assert.That(session.Calls.Count(x => x == "core.resume_torrent"), Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void interrupted_swap_should_recover_from_history_after_restart_and_author_deletion(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var events = Proxy<IEventAggregator>((method, args) => null);
                var repository = new DownloadHistoryRepository(database, events);
                var grab = Grab();
                grab.Id = 0;
                repository.Insert(grab);
                var session = new Session { FailBefore = "core.set_torrent_options" };
                Client(session, repository).MarkItemAsImported(Item());
                Assert.That(session.Label, Is.EqualTo("audio-imported"));
                Assert.That(session.Paused, Is.True);
                repository.DeleteByAuthorId(42);
                Assert.That(repository.GetPendingLabelChanges(99), Is.Empty);
                var restarted = new DownloadHistoryRepository(database, events);
                Client(session, restarted).RecoverPendingOperations();
                Assert.That(session.Stop || session.Remove, Is.False);
                Assert.That(session.Calls.Count(x => x == "core.resume_torrent"), Is.EqualTo(1));
                Assert.That(restarted.GetPendingLabelChanges(5), Is.Empty);
                Assert.That(restarted.FindByDownloadId("ABCDEF").Single().ShouldKeepSeeding(), Is.True);
            });
        }

        [TestCase("core.pause_torrent")]
        [TestCase("core.resume_torrent")]
        public void lost_rpc_reply_should_finish_the_recorded_running_operation(string rpc)
        {
            var session = new Session { FailAfter = rpc };
            var history = new History();
            Client(session, history.Repository).MarkItemAsImported(Item());
            session.State = "Paused";
            session.Paused = true;
            Client(session, history.Repository).RecoverPendingOperations();
            Assert.That(session.Paused, Is.False);
            Assert.That(session.Calls.Count(x => x == "core.resume_torrent"), Is.EqualTo(rpc == "core.resume_torrent" ? 2 : 1));
            Assert.That(history.Saved.Data.ContainsKey(DownloadHistory.PendingLabelChangeKey), Is.False);

            session.State = "Paused";
            session.Paused = true;
            session.Calls.Clear();
            Client(session, history.Repository).RecoverPendingOperations();
            Assert.That(session.Calls, Is.Empty, "A completed operation must not undo a later manual pause");
        }

        [Test]
        public void late_pause_confirmation_should_finish_on_the_next_poll()
        {
            var session = new Session { PauseStatusLag = int.MaxValue };
            var history = new History();
            Assert.Throws<DownloadClientException>(() => Client(session, history.Repository).MarkItemAsImported(Item()));
            Assert.That(session.Label, Is.EqualTo("audio"));
            session.PauseStatusLag = 0;
            Client(session, history.Repository).RecoverPendingOperations();
            Assert.That(session.Label, Is.EqualTo("audio-imported"));
            Assert.That(session.Stop || session.Remove || session.Paused, Is.False);
            Assert.That(history.Saved.Data.ContainsKey(DownloadHistory.PendingLabelChangeKey), Is.False);
        }

        [TestCase("Moving")]
        [TestCase("Seeding")]
        public void native_pause_with_unsettled_display_must_wait_before_applying_ratio_rules(string state)
        {
            var session = new Session { State = state, UnsettledState = true };
            var history = new History();
            Assert.Throws<DownloadClientException>(() => Client(session, history.Repository).MarkItemAsImported(Item()));
            Assert.That(session.Paused, Is.True);
            Assert.That(session.Label, Is.EqualTo("audio"));
            Assert.That(session.Calls, Does.Not.Contain("label.set_torrent"));
            session.UnsettledState = false;
            Client(session, history.Repository).RecoverPendingOperations();
            Assert.That(session.Paused, Is.False);
            Assert.That(session.Stop || session.Remove, Is.False);
            Assert.That(session.Label, Is.EqualTo("audio-imported"));
            Assert.That(history.Saved.Data.ContainsKey(DownloadHistory.PendingLabelChangeKey), Is.False);
        }

        [Test]
        public void lost_resume_reply_should_not_resume_again_if_already_running()
        {
            var session = new Session { FailAfter = "core.resume_torrent" };
            var history = new History();
            Client(session, history.Repository).MarkItemAsImported(Item());
            Client(session, history.Repository).RecoverPendingOperations();
            Assert.That(session.Calls.Count(x => x == "core.resume_torrent"), Is.EqualTo(1));
            Assert.That(history.Saved.Data.ContainsKey(DownloadHistory.PendingLabelChangeKey), Is.False);
        }

        [Test]
        public void failed_resume_request_should_retry_without_repeating_the_label_change()
        {
            var session = new Session { FailBefore = "core.resume_torrent" };
            var history = new History();
            Client(session, history.Repository).MarkItemAsImported(Item());
            Assert.That(session.Paused, Is.True);
            Client(session, history.Repository).RecoverPendingOperations();
            Assert.That(session.Paused, Is.False);
            Assert.That(session.Calls.Count(x => x == "label.set_torrent"), Is.EqualTo(1));
            Assert.That(session.Calls.Count(x => x == "core.pause_torrent"), Is.EqualTo(1));
            Assert.That(history.Saved.Data.ContainsKey(DownloadHistory.PendingLabelChangeKey), Is.False);
        }

        [TestCase("label.set_torrent", 1)]
        [TestCase("core.set_torrent_options", 0)]
        public void manual_resume_during_pending_swap_should_finish_without_a_permanent_error(string failedCall, int extraPauses)
        {
            var session = new Session { FailBefore = failedCall };
            var history = new History();
            Client(session, history.Repository).MarkItemAsImported(Item());
            session.Proxy().SetTorrentPaused("abcdef", false, new DelugeSettings());
            session.Calls.Clear();
            Client(session, history.Repository).RecoverPendingOperations();
            Assert.That(session.Calls.Count(x => x == "core.pause_torrent"), Is.EqualTo(extraPauses));
            Assert.That(session.Label, Is.EqualTo("audio-imported"));
            Assert.That(session.Stop || session.Remove || session.Paused, Is.False);
            Assert.That(history.Saved.Data.ContainsKey(DownloadHistory.PendingLabelChangeKey), Is.False);
        }

        [Test]
        public void removed_torrent_should_end_pending_action_without_readding_or_resuming()
        {
            var session = new Session { FailBefore = "core.set_torrent_options" };
            var history = new History();
            Client(session, history.Repository).MarkItemAsImported(Item());
            session.Missing = true;
            Client(session, history.Repository).RecoverPendingOperations();
            Assert.That(history.Saved.Data.ContainsKey(DownloadHistory.PendingLabelChangeKey), Is.False);
            Assert.That(session.Calls, Does.Not.Contain("core.resume_torrent"));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" ")]
        public void empty_hash_must_never_reach_pause_or_resume_rpc(string hash)
        {
            var session = new Session();
            Assert.Throws<ArgumentException>(() => session.Proxy().SetTorrentPaused(hash, true, new DelugeSettings()));
            Assert.Throws<ArgumentException>(() => session.Proxy().SetTorrentPaused(hash, false, new DelugeSettings()));
            Assert.That(session.Calls, Is.Empty);
        }
    }
}
