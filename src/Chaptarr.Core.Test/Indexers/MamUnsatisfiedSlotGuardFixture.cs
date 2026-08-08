using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IMamUnsatisfiedSlotReservationRepository.AllForIndexer):
                        return Rows.Where(r => r.IndexerId == (int)args[0]).ToList();

                    case nameof(IMamUnsatisfiedSlotReservationRepository.Find) when args.Length == 2:
                        return Rows.SingleOrDefault(r => r.IndexerId == (int)args[0] && r.TorrentId == (string)args[1]);

                    case nameof(IMamUnsatisfiedSlotReservationRepository.Insert):
                        var inserted = (MamUnsatisfiedSlotReservation)args[0];
                        inserted.Id = _nextId++;
                        Rows.Add(inserted);
                        return inserted;

                    case nameof(IMamUnsatisfiedSlotReservationRepository.Update):
                        return args[0];

                    case nameof(IMamUnsatisfiedSlotReservationRepository.Delete) when args[0] is int id:
                        Rows.RemoveAll(r => r.Id == id);
                        return null;

                    case nameof(IMamUnsatisfiedSlotReservationRepository.All):
                        return Rows.ToList();

                    default:
                        throw new NotImplementedException(targetMethod?.Name);
                }
            }
        }

        private class IndexerFactoryProxy : DispatchProxy
        {
            public List<IndexerDefinition> Definitions { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IIndexerFactory.Find):
                    case nameof(IIndexerFactory.Get):
                        return Definitions.SingleOrDefault(d => d.Id == (int)args[0]);

                    case nameof(IIndexerFactory.All):
                        return Definitions.ToList();

                    default:
                        throw new NotImplementedException(targetMethod?.Name);
                }
            }
        }

        [Test]
        public void reservation_should_survive_a_new_guard_instance_and_block_the_next_slot()
        {
            var settings = FreshSettings(count: 44, limit: 50, safetyReserve: 5);
            var repository = CreateRepository(out var repositoryState);
            var factory = CreateFactory(Definition(settings));

            var firstGuard = CreateGuard(factory, repository);
            Assert.That(firstGuard.TryReserve(Release("100")).Accepted, Is.True);

            var afterRestart = CreateGuard(factory, repository);
            var next = afterRestart.Check(Release("101"));

            Assert.Multiple(() =>
            {
                Assert.That(repositoryState.Rows.Select(r => r.TorrentId), Is.EqualTo(new[] { "100" }));
                Assert.That(next.Accepted, Is.False);
                Assert.That(next.Reason, Does.Contain("45 of 50"));
            });
        }

        [Test]
        public void existing_reservation_should_remain_retryable_when_status_becomes_stale()
        {
            var settings = FreshSettings(count: 0, limit: 50, safetyReserve: 5);
            var repository = CreateRepository(out _);
            var factory = CreateFactory(Definition(settings));
            var guard = CreateGuard(factory, repository);

            Assert.That(guard.TryReserve(Release("100")).Accepted, Is.True);
            settings.UnsatisfiedStatusRefreshedUtc = DateTime.UtcNow.AddHours(-3);

            Assert.Multiple(() =>
            {
                Assert.That(guard.Check(Release("100")).Accepted, Is.True);
                Assert.That(guard.Check(Release("101")).Accepted, Is.False);
            });
        }

        [Test]
        public void reservations_should_not_expire_without_a_provider_snapshot()
        {
            var settings = FreshSettings(count: 44, limit: 50, safetyReserve: 5);
            var repository = CreateRepository(out var repositoryState);
            repositoryState.Rows.Add(new MamUnsatisfiedSlotReservation
            {
                Id = 1,
                IndexerId = 9,
                TorrentId = "99",
                ReservedUtc = DateTime.UtcNow.AddYears(-1)
            });

            var guard = CreateGuard(CreateFactory(Definition(settings)), repository);

            Assert.That(guard.Check(Release("100")).Accepted, Is.False);
        }

        [Test]
        public void reservations_from_another_native_indexer_on_the_same_account_should_count_toward_the_limit()
        {
            var firstSettings = FreshSettings(count: 44, limit: 50, safetyReserve: 5);
            var secondSettings = FreshSettings(count: 44, limit: 50, safetyReserve: 5);
            var repository = CreateRepository(out var repositoryState);
            repositoryState.Rows.Add(new MamUnsatisfiedSlotReservation
            {
                Id = 1,
                IndexerId = 10,
                TorrentId = "99",
                ReservedUtc = DateTime.UtcNow
            });

            var guard = CreateGuard(
                CreateFactory(Definition(firstSettings), Definition(secondSettings, 10, "MAM ebooks")),
                repository);

            var result = guard.Check(Release("100"));

            Assert.That(result.Accepted, Is.False);
            Assert.That(result.Reason, Does.Contain("45 of 50"));
        }

        [Test]
        public void reservations_from_a_different_mam_account_should_not_count_toward_the_limit()
        {
            var firstSettings = FreshSettings(count: 44, limit: 50, safetyReserve: 5);
            var secondSettings = FreshSettings(count: 44, limit: 50, safetyReserve: 5);
            secondSettings.MamId = "different-account-token";
            var repository = CreateRepository(out var repositoryState);
            repositoryState.Rows.Add(new MamUnsatisfiedSlotReservation
            {
                Id = 1,
                IndexerId = 10,
                TorrentId = "99",
                ReservedUtc = DateTime.UtcNow
            });

            var guard = CreateGuard(
                CreateFactory(Definition(firstSettings), Definition(secondSettings, 10, "Other MAM account")),
                repository);

            Assert.That(guard.Check(Release("100")).Accepted, Is.True);
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
            var repository = CreateRepository(out var repositoryState);
            var guard = CreateGuard(CreateFactory(definition), repository);

            var result = guard.Check(Release("100", ReleaseSourceType.Search, 12, definition.Name));

            Assert.That(result.Accepted, Is.True);
            Assert.That(repositoryState.Rows, Is.Empty);
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
            var repository = CreateRepository(out _);
            var guard = CreateGuard(CreateFactory(Definition(settings)), repository);

            Assert.Multiple(() =>
            {
                Assert.That(guard.Check(Release("100")).Accepted, Is.False);
                Assert.That(guard.Check(Release("100", ReleaseSourceType.InteractiveSearch)).Accepted, Is.True);
            });

            settings.UnsatisfiedCount = 44;
            Assert.That(guard.TryReserve(Release("101", ReleaseSourceType.InteractiveSearch)).Accepted, Is.True);
            Assert.That(guard.Check(Release("102", ReleaseSourceType.InteractiveSearch)).Accepted, Is.False);
        }

        [Test]
        public void retry_should_refresh_an_unconfirmed_reservations_accounting_window()
        {
            var settings = FreshSettings(count: 0, limit: 50, safetyReserve: 5);
            var repository = CreateRepository(out var repositoryState);
            repositoryState.Rows.Add(new MamUnsatisfiedSlotReservation
            {
                Id = 1,
                IndexerId = 9,
                TorrentId = "100",
                ReservedUtc = DateTime.UtcNow.AddHours(-1)
            });

            var previousAttempt = repositoryState.Rows.Single().ReservedUtc;
            var guard = CreateGuard(CreateFactory(Definition(settings)), repository);

            Assert.That(guard.TryReserve(Release("100")).Accepted, Is.True);
            Assert.That(repositoryState.Rows.Single().ReservedUtc, Is.GreaterThan(previousAttempt));
            Assert.That(repositoryState.Rows.Single().ConfirmedUtc, Is.Null);
        }

        [Test]
        public void provider_snapshot_should_retire_reservations_from_another_indexer_on_the_same_account()
        {
            var firstSettings = FreshSettings(count: 1, limit: 50, safetyReserve: 5);
            var secondSettings = FreshSettings(count: 1, limit: 50, safetyReserve: 5);
            var repository = CreateRepository(out var repositoryState);
            var anchor = DateTime.UtcNow.AddMinutes(-30);
            repositoryState.Rows.Add(new MamUnsatisfiedSlotReservation
            {
                Id = 1,
                IndexerId = 10,
                TorrentId = "659145",
                ReservedUtc = anchor
            });

            var indexer = CreateMamIndexer(firstSettings, repository);
            var guard = CreateGuard(
                CreateFactory((IndexerDefinition)indexer.Definition, Definition(secondSettings, 10, "MAM ebooks")),
                repository);

            guard.Reconcile(indexer, new MyAnonaMouseAccountStatus { SnapshotCreatedUtc = anchor.AddMinutes(20) });

            Assert.That(repositoryState.Rows, Is.Empty);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void reservation_should_retire_only_after_the_provider_snapshot_covers_the_accounting_lag(bool confirmed)
        {
            var settings = FreshSettings(count: 1, limit: 50, safetyReserve: 5);
            var repository = CreateRepository(out var repositoryState);
            var anchor = DateTime.UtcNow.AddMinutes(-30);
            repositoryState.Rows.Add(new MamUnsatisfiedSlotReservation
            {
                Id = 1,
                IndexerId = 9,
                TorrentId = "659145",
                ReservedUtc = confirmed ? anchor.AddMinutes(-30) : anchor,
                ConfirmedUtc = confirmed ? anchor : null
            });

            var indexer = CreateMamIndexer(settings, repository);
            var guard = CreateGuard(CreateFactory((IndexerDefinition)indexer.Definition), repository);

            guard.Reconcile(indexer, new MyAnonaMouseAccountStatus { SnapshotCreatedUtc = anchor.AddMinutes(19) });
            Assert.That(repositoryState.Rows, Has.Count.EqualTo(1));

            guard.Reconcile(indexer, new MyAnonaMouseAccountStatus { SnapshotCreatedUtc = anchor.AddMinutes(20) });
            Assert.That(repositoryState.Rows, Is.Empty);
        }

        private static MyAnonaMouseSettings FreshSettings(int count, int limit, int safetyReserve, int manualGrabBuffer = 0)
        {
            return new MyAnonaMouseSettings
            {
                ProtectUnsatisfiedSlots = true,
                MamId = "shared-account-token",
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

        private static MyAnonaMouse CreateMamIndexer(MyAnonaMouseSettings settings, IMamUnsatisfiedSlotReservationRepository repository)
        {
            return new MyAnonaMouse(null, null, null, null, repository, LogManager.GetCurrentClassLogger())
            {
                Definition = Definition(settings)
            };
        }
    }
}
