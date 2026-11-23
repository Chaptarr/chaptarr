using System;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NUnit.Framework;
using NzbDrone.Core.Datastore.Migration.Framework;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    public class MamUnsatisfiedSlotReservationRepositoryFixture
    {
        // The shipped table still has the unused ConfirmedUtc column.
        [TestCase(false)]
        [TestCase(true)]
        public void reservations_should_upsert_read_and_delete_on_the_shipped_table(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var repository = new MamUnsatisfiedSlotReservationRepository(database, InterfaceProxy.Proxy<IEventAggregator>((_, _) => null));
                var reservedUtc = new DateTime(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);
                var kept = repository.Upsert(new MamUnsatisfiedSlotReservation { IndexerId = 9, TorrentId = "100", ReservedUtc = reservedUtc });
                var expired = repository.Upsert(new MamUnsatisfiedSlotReservation { IndexerId = 10, TorrentId = "101", ReservedUtc = reservedUtc.AddHours(-3) });

                kept.ReservedUtc = reservedUtc.AddMinutes(5);
                repository.Upsert(kept);
                repository.DeleteMany(new[] { expired.Id });

                var row = repository.All().Single();

                Assert.Multiple(() =>
                {
                    Assert.That(row.Id, Is.EqualTo(kept.Id));
                    Assert.That(row.IndexerId, Is.EqualTo(9));
                    Assert.That(row.TorrentId, Is.EqualTo("100"));
                    Assert.That(row.ReservedUtc, Is.EqualTo(reservedUtc.AddMinutes(5)));
                });
            }, MigrationType.Main, desiredVersion: 100);
        }
    }
}
