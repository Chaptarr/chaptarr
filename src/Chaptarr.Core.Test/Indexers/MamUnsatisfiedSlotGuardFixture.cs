using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.Indexers.Torznab;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    public class MamUnsatisfiedSlotGuardFixture
    {
        private class ReservationRepositoryProxy : DispatchProxy
        {
            public List<MamUnsatisfiedSlotReservation> Rows { get; } = new();
            private int _nextId = 1;

            // Hand out copies so a change the guard does not save is lost, as with a real database.
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IMamUnsatisfiedSlotReservationRepository.All):
                        return Rows.Select(Copy).ToList();

                    case nameof(IMamUnsatisfiedSlotReservationRepository.Upsert):
                        var saved = (MamUnsatisfiedSlotReservation)args[0];
                        if (saved.Id == 0)
                        {
                            saved.Id = _nextId++;
                            Rows.Add(Copy(saved));
                        }
                        else
                        {
                            Rows[Rows.FindIndex(r => r.Id == saved.Id)] = Copy(saved);
                        }

                        return saved;

                    case nameof(IMamUnsatisfiedSlotReservationRepository.DeleteMany) when args[0] is IEnumerable<int> ids:
                        var deleted = ids.ToHashSet();
                        Rows.RemoveAll(r => deleted.Contains(r.Id));
                        return null;

                    default:
                        throw new NotImplementedException(targetMethod?.Name);
                }
            }

            public void Add(int indexerId, string torrentId, DateTime reservedUtc)
            {
                Rows.Add(new MamUnsatisfiedSlotReservation { Id = _nextId++, IndexerId = indexerId, TorrentId = torrentId, ReservedUtc = reservedUtc });
            }

            private static MamUnsatisfiedSlotReservation Copy(MamUnsatisfiedSlotReservation row)
            {
                return new MamUnsatisfiedSlotReservation { Id = row.Id, IndexerId = row.IndexerId, TorrentId = row.TorrentId, ReservedUtc = row.ReservedUtc };
            }
        }

        private class IndexerFactoryProxy : DispatchProxy
        {
            public List<IndexerDefinition> Definitions { get; } = new();
            public int AllCalls { get; private set; }
            public List<int> Finds { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IIndexerFactory.Find):
                        var id = (int)args[0];
                        Finds.Add(id);
                        return Definitions.SingleOrDefault(d => d.Id == id);

                    case nameof(IIndexerFactory.All):
                        AllCalls++;
                        return Definitions.ToList();

                    default:
                        throw new NotImplementedException(targetMethod?.Name);
                }
            }
        }

        [Test]
        public void a_slot_check_only_reads_indexers_for_counted_reservations()
        {
            var settings = FreshSettings(count: 12, limit: 50, safetyReserve: 5);
            settings.UserId = "account";
            var repository = CreateRepository(out var state);
            var snapshot = settings.UnsatisfiedSnapshotUtc.Value;
            state.Add(9, "100", snapshot);
            state.Add(10, "101", snapshot - MamUnsatisfiedSlotGuard.SummaryAccountingLag);
            state.Add(11, "102", snapshot);
            var factory = CreateFactory(Definition(settings));
            var queries = (IndexerFactoryProxy)(object)factory;
            var guard = CreateGuard(factory, repository);

            Assert.That(guard.Check(Release("102")).Accepted, Is.True);
            Assert.That(queries.AllCalls, Is.Zero);
            Assert.That(queries.Finds, Is.EqualTo(new[] { 9 }));

            state.Add(12, "103", snapshot);
            state.Add(12, "104", snapshot);
            queries.Finds.Clear();

            Assert.That(guard.Check(Release("102")).Accepted, Is.True);
            Assert.That(queries.AllCalls, Is.Zero);
            Assert.That(queries.Finds, Is.EqualTo(new[] { 9, 12 }));
        }

        [Test]
        public void a_grab_should_count_after_a_restart_and_block_the_next_slot()
        {
            var settings = FreshSettings(count: 44, limit: 50, safetyReserve: 5);
            var repository = CreateRepository(out var state);
            var factory = CreateFactory(Definition(settings));

            Assert.That(CreateGuard(factory, repository).TryReserve(Release("100")).Accepted, Is.True);

            var next = CreateGuard(factory, repository).Check(Release("101"));

            Assert.Multiple(() =>
            {
                Assert.That(state.Rows.Select(r => r.TorrentId), Is.EqualTo(new[] { "100" }));
                Assert.That(next.Accepted, Is.False);
                Assert.That(next.Reason, Does.Contain("45 of 50"));
            });
        }

        [Test]
        public void grabs_made_shortly_before_the_last_check_should_still_count()
        {
            // MAM said 30 at the last check, but 15 grabs made just before it may not be in that count yet.
            var settings = FreshSettings(count: 30, limit: 50, safetyReserve: 5);
            var repository = CreateRepository(out var state);
            for (var torrentId = 1; torrentId <= 15; torrentId++)
            {
                state.Add(9, torrentId.ToString(), settings.UnsatisfiedSnapshotUtc.Value - MamUnsatisfiedSlotGuard.SummaryAccountingLag + TimeSpan.FromMinutes(1));
            }

            var result = CreateGuard(CreateFactory(Definition(settings)), repository).Check(Release("100"));

            Assert.That(result.Accepted, Is.False);
            Assert.That(result.Reason, Does.Contain("45 of 50"));
        }

        [TestCase(1, false)]
        [TestCase(0, true)]
        public void a_grab_should_stop_counting_once_the_last_check_is_a_full_window_newer(int minutesInsideWindow, bool accepted)
        {
            var settings = FreshSettings(count: 44, limit: 50, safetyReserve: 5);
            var repository = CreateRepository(out var state);
            state.Add(9, "99", settings.UnsatisfiedSnapshotUtc.Value - MamUnsatisfiedSlotGuard.SummaryAccountingLag + TimeSpan.FromMinutes(minutesInsideWindow));

            Assert.That(CreateGuard(CreateFactory(Definition(settings)), repository).Check(Release("100")).Accepted, Is.EqualTo(accepted));
        }

        [Test]
        public void a_grab_should_count_when_mams_clock_runs_ahead_of_ours()
        {
            var settings = FreshSettings(count: 44, limit: 50, safetyReserve: 5);
            var now = DateTime.UtcNow;
            settings.UnsatisfiedSnapshotUtc = now.AddMinutes(4.5);
            var repository = CreateRepository(out var state);
            state.Add(9, "99", now.AddSeconds(-90));

            var result = CreateGuard(CreateFactory(Definition(settings)), repository).Check(Release("100"));

            Assert.That(result.Accepted, Is.False);
            Assert.That(result.Reason, Does.Contain("45 of 50"));
        }

        [Test]
        public void stale_status_should_pause_every_grab_including_a_retry()
        {
            var settings = FreshSettings(count: 0, limit: 50, safetyReserve: 5);
            var guard = CreateGuard(CreateFactory(Definition(settings)), CreateRepository(out _));

            Assert.That(guard.TryReserve(Release("100")).Accepted, Is.True);
            settings.UnsatisfiedStatusRefreshedUtc = DateTime.UtcNow.AddHours(-3);

            var retry = guard.TryReserve(Release("100"));

            Assert.That(retry.Accepted, Is.False);
            Assert.That(retry.Reason, Does.Contain("stale"));
        }

        [Test]
        public void a_retry_after_a_check_counts_until_the_next_check()
        {
            var settings = FreshSettings(count: 18, limit: 20, safetyReserve: 0);
            var repository = CreateRepository(out var state);
            state.Add(9, "100", DateTime.UtcNow.AddMinutes(-30));
            var guard = CreateGuard(CreateFactory(Definition(settings)), repository);

            Assert.That(guard.TryReserve(Release("100")).Accepted, Is.True);
            Assert.That(state.Rows.Select(row => row.TorrentId), Is.EqualTo(new[] { "100" }));
            Assert.That(guard.TryReserve(Release("101")).Accepted, Is.True);

            var next = guard.TryReserve(Release("102"));
            Assert.That(next.Accepted, Is.False);
            Assert.That(next.Reason, Does.Contain("20 of 20"));
        }

        [Test]
        public void a_torrent_grabbed_through_two_indexers_should_count_once()
        {
            var first = FreshSettings(count: 3, limit: 50, safetyReserve: 46);
            var second = FreshSettings(count: 3, limit: 50, safetyReserve: 46);
            var repository = CreateRepository(out var state);
            var guard = CreateGuard(CreateFactory(Definition(first), Definition(second, 10, "MAM ebooks")), repository);

            Assert.That(guard.TryReserve(Release("99", indexerId: 10)).Accepted, Is.True);
            Assert.That(guard.TryReserve(Release("99")).Accepted, Is.True);
            Assert.That(state.Rows, Has.Count.EqualTo(2));

            var next = guard.Check(Release("100"));

            Assert.Multiple(() =>
            {
                Assert.That(next.Accepted, Is.False);
                Assert.That(next.Reason, Does.Contain("4 of 50"));
            });
        }

        [Test]
        public void indexers_on_one_user_id_should_count_each_others_grabs_from_their_own_last_check()
        {
            // Audio last checked at 10:00 (30), ebooks at 10:40 (40, which includes 10 audio grabs at 10:10).
            var now = DateTime.UtcNow;
            var audio = FreshSettings(count: 30, limit: 50, safetyReserve: 9, userId: "42");
            var ebook = FreshSettings(count: 40, limit: 50, safetyReserve: 9, userId: "42");
            audio.UnsatisfiedSnapshotUtc = now.AddMinutes(-45);
            ebook.UnsatisfiedSnapshotUtc = now.AddMinutes(-5);
            var repository = CreateRepository(out var state);
            for (var torrentId = 1; torrentId <= 10; torrentId++)
            {
                state.Add(9, torrentId.ToString(), now.AddMinutes(-35));
            }

            var guard = CreateGuard(CreateFactory(Definition(audio), Definition(ebook, 10, "MAM ebooks")), repository);
            Assert.That(guard.TryReserve(Release("50", indexerId: 10)).Accepted, Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(guard.Check(Release("100")).Reason, Does.Contain("41 of 50"));
                Assert.That(guard.Check(Release("100", indexerId: 10)).Reason, Does.Contain("41 of 50"));
            });
        }

        [Test]
        public async Task indexers_should_keep_sharing_the_count_after_one_renews_its_cookie()
        {
            var audio = FreshSettings(count: 3, limit: 50, safetyReserve: 46);
            var ebook = FreshSettings(count: 3, limit: 50, safetyReserve: 46);
            await MyAnonaMouseWedgeFixture.CreateIndexerWithAccountSummary(audio).RefreshAccountStatus();
            await MyAnonaMouseWedgeFixture.CreateIndexerWithAccountSummary(ebook, "mam_id=renewed; path=/").RefreshAccountStatus();

            var guard = CreateGuard(CreateFactory(Definition(audio), Definition(ebook, 10, "MAM ebooks")), CreateRepository(out _));
            Assert.That(guard.TryReserve(Release("99", indexerId: 10)).Accepted, Is.True);

            var next = guard.Check(Release("100"));

            Assert.Multiple(() =>
            {
                Assert.That(ebook.MamId, Is.EqualTo("renewed"));
                Assert.That(ebook.UserId, Is.EqualTo("42"));
                Assert.That(next.Accepted, Is.False);
                Assert.That(next.Reason, Does.Contain("4 of 50"));
            });
        }

        [TestCase("42", null)]
        [TestCase(null, "42")]
        [TestCase(null, null)]
        public void grabs_should_count_when_either_user_id_is_unknown(string userId, string otherUserId)
        {
            var settings = FreshSettings(count: 44, limit: 50, safetyReserve: 5, userId: userId);
            var other = FreshSettings(count: 44, limit: 50, safetyReserve: 5, userId: otherUserId);
            var repository = CreateRepository(out var state);
            state.Add(10, "99", DateTime.UtcNow);

            var guard = CreateGuard(CreateFactory(Definition(settings), Definition(other, 10, "MAM ebooks")), repository);

            Assert.That(guard.Check(Release("100")).Accepted, Is.False);
        }

        [Test]
        public void grabs_on_a_different_user_id_should_not_count()
        {
            var settings = FreshSettings(count: 44, limit: 50, safetyReserve: 5, userId: "42");
            var other = FreshSettings(count: 44, limit: 50, safetyReserve: 5, userId: "43");
            var repository = CreateRepository(out var state);
            state.Add(10, "99", DateTime.UtcNow);

            var guard = CreateGuard(CreateFactory(Definition(settings), Definition(other, 10, "Other MAM account")), repository);

            Assert.That(guard.Check(Release("100")).Accepted, Is.True);
        }

        [Test]
        public void grabs_of_a_removed_indexer_should_count()
        {
            var settings = FreshSettings(count: 44, limit: 50, safetyReserve: 5, userId: "42");
            var repository = CreateRepository(out var state);
            state.Add(10, "99", DateTime.UtcNow);

            var result = CreateGuard(CreateFactory(Definition(settings)), repository).Check(Release("100"));

            Assert.That(result.Accepted, Is.False);
            Assert.That(result.Reason, Does.Contain("45 of 50"));
        }

        [Test]
        public void a_grab_should_remove_records_too_old_for_any_usable_check()
        {
            var settings = FreshSettings(count: 0, limit: 50, safetyReserve: 5);
            var repository = CreateRepository(out var state);
            var expiredBefore = DateTime.UtcNow.AddHours(-2) - MamUnsatisfiedSlotGuard.SummaryAccountingLag;
            state.Add(9, "98", expiredBefore.AddMinutes(-1));
            state.Add(9, "99", expiredBefore.AddMinutes(1));

            Assert.That(CreateGuard(CreateFactory(Definition(settings)), repository).TryReserve(Release("100")).Accepted, Is.True);

            Assert.That(state.Rows.Select(r => r.TorrentId), Is.EquivalentTo(new[] { "99", "100" }));
        }

        [Test]
        public void a_check_should_not_record_a_grab()
        {
            var settings = FreshSettings(count: 0, limit: 50, safetyReserve: 5);
            var repository = CreateRepository(out var state);

            Assert.That(CreateGuard(CreateFactory(Definition(settings)), repository).Check(Release("100")).Accepted, Is.True);

            Assert.That(state.Rows, Is.Empty);
        }

        [Test]
        public void generic_torznab_named_mam_should_not_use_the_native_slot_guard()
        {
            var definition = new IndexerDefinition
            {
                Id = 12,
                Name = "MyAnonaMouse via Prowlarr",
                Enable = true,
                Implementation = "Torznab",
                Settings = new TorznabSettings()
            };
            var repository = CreateRepository(out var state);
            var guard = CreateGuard(CreateFactory(definition), repository);

            var result = guard.TryReserve(Release("100", ReleaseSourceType.Search, 12, definition.Name));

            Assert.That(result.Accepted, Is.True);
            Assert.That(state.Rows, Is.Empty);
        }

        [TestCase("no count")]
        [TestCase("no limit")]
        [TestCase("no snapshot")]
        [TestCase("snapshot from the future")]
        public void unusable_status_should_pause_grabs(string problem)
        {
            var settings = FreshSettings(count: 0, limit: 50, safetyReserve: 5);
            switch (problem)
            {
                case "no count":
                    settings.UnsatisfiedCount = null;
                    break;
                case "no limit":
                    settings.UnsatisfiedLimit = 0;
                    break;
                case "no snapshot":
                    settings.UnsatisfiedSnapshotUtc = null;
                    break;
                default:
                    settings.UnsatisfiedSnapshotUtc = DateTime.UtcNow.AddMinutes(10);
                    break;
            }

            var result = CreateGuard(CreateFactory(Definition(settings)), CreateRepository(out _)).Check(Release("100"));

            Assert.That(result.Reason, Does.Contain("unavailable or stale"));
        }

        [TestCase("MAM-abc")]
        [TestCase("MAM-0")]
        [TestCase("12345")]
        public void a_release_without_a_mam_torrent_id_should_pause(string guid)
        {
            var release = Release("100");
            release.Release.Guid = guid;

            var result = CreateGuard(CreateFactory(Definition(FreshSettings(count: 0, limit: 50, safetyReserve: 5))), CreateRepository(out _)).Check(release);

            Assert.That(result.Reason, Does.Contain("no valid MAM torrent identity"));
        }

        [Test]
        public void a_book_without_a_release_should_not_use_the_slot_guard()
        {
            var guard = CreateGuard(CreateFactory(), CreateRepository(out _));

            Assert.That(guard.Check(new RemoteBook()).Accepted, Is.True);
        }

        [Test]
        public void explicit_advanced_opt_out_should_leave_mam_unchanged()
        {
            var settings = FreshSettings(count: 50, limit: 50, safetyReserve: 5);
            settings.ProtectUnsatisfiedSlots = false;
            var guard = CreateGuard(CreateFactory(Definition(settings)), CreateRepository(out _));

            Assert.That(guard.Check(Release("100")).Accepted, Is.True);
        }

        [TestCase(ReleaseSourceType.Rss)]
        [TestCase(ReleaseSourceType.Search)]
        [TestCase(ReleaseSourceType.UserInvokedSearch)]
        [TestCase(ReleaseSourceType.ReleasePush)]
        public void non_interactive_grabs_should_stop_at_the_safety_reserve_plus_user_buffer(ReleaseSourceType source)
        {
            var settings = FreshSettings(count: 40, limit: 50, safetyReserve: 5, manualGrabBuffer: 5);
            var guard = CreateGuard(CreateFactory(Definition(settings)), CreateRepository(out _));

            var result = guard.Check(Release("100", source));

            Assert.That(result.Accepted, Is.False);
            Assert.That(result.Reason, Does.Contain("5 safety slot(s) and 5 user slot(s) are being kept free"));
        }

        [Test]
        public void interactive_grab_should_use_the_user_buffer_but_never_use_the_safety_reserve()
        {
            var settings = FreshSettings(count: 40, limit: 50, safetyReserve: 5, manualGrabBuffer: 5);
            var guard = CreateGuard(CreateFactory(Definition(settings)), CreateRepository(out _));

            Assert.Multiple(() =>
            {
                Assert.That(guard.Check(Release("100")).Accepted, Is.False);
                Assert.That(guard.Check(Release("100", ReleaseSourceType.InteractiveSearch)).Accepted, Is.True);
            });

            settings.UnsatisfiedCount = 44;
            Assert.That(guard.TryReserve(Release("101", ReleaseSourceType.InteractiveSearch)).Accepted, Is.True);
            Assert.That(guard.Check(Release("102", ReleaseSourceType.InteractiveSearch)).Accepted, Is.False);
        }

        private static MyAnonaMouseSettings FreshSettings(int count, int limit, int safetyReserve, int manualGrabBuffer = 0, string userId = null)
        {
            return new MyAnonaMouseSettings
            {
                ProtectUnsatisfiedSlots = true,
                MamId = "shared-account-token",
                UserId = userId,
                UnsatisfiedSlotReserve = safetyReserve,
                ManualGrabBuffer = manualGrabBuffer,
                UnsatisfiedCount = count,
                UnsatisfiedLimit = limit,
                UnsatisfiedSnapshotUtc = DateTime.UtcNow.AddMinutes(-15),
                UnsatisfiedStatusRefreshedUtc = DateTime.UtcNow
            };
        }

        private static IndexerDefinition Definition(MyAnonaMouseSettings settings, int id = 9, string name = "MAM")
        {
            return new IndexerDefinition
            {
                Id = id,
                Name = name,
                Enable = true,
                Implementation = nameof(MyAnonaMouse),
                Settings = settings
            };
        }

        private static RemoteBook Release(string torrentId, ReleaseSourceType source = ReleaseSourceType.Search, int indexerId = 9, string indexerName = "MAM")
        {
            return new RemoteBook
            {
                Release = new ReleaseInfo
                {
                    Guid = "MAM-" + torrentId,
                    Indexer = indexerName,
                    IndexerId = indexerId,
                    DownloadProtocol = DownloadProtocol.Torrent
                },
                ReleaseSource = source
            };
        }

        private static IMamUnsatisfiedSlotReservationRepository CreateRepository(out ReservationRepositoryProxy state)
        {
            var repository = DispatchProxy.Create<IMamUnsatisfiedSlotReservationRepository, ReservationRepositoryProxy>();
            state = (ReservationRepositoryProxy)(object)repository;
            return repository;
        }

        private static IIndexerFactory CreateFactory(params IndexerDefinition[] definitions)
        {
            var factory = DispatchProxy.Create<IIndexerFactory, IndexerFactoryProxy>();
            ((IndexerFactoryProxy)(object)factory).Definitions.AddRange(definitions);
            return factory;
        }

        private static MamUnsatisfiedSlotGuard CreateGuard(IIndexerFactory factory, IMamUnsatisfiedSlotReservationRepository repository)
        {
            return new MamUnsatisfiedSlotGuard(factory, repository, LogManager.GetCurrentClassLogger());
        }
    }
}
