using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(100)]
    public class add_mam_unsatisfied_slot_reservations : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("MamUnsatisfiedSlotReservations").Exists())
            {
                Create.Table("MamUnsatisfiedSlotReservations")
                    .WithColumn("Id").AsInt32().PrimaryKey().Identity()
                    .WithColumn("IndexerId").AsInt32().NotNullable()
                    .WithColumn("TorrentId").AsString(64).NotNullable()
                    .WithColumn("ReservedUtc").AsDateTime().NotNullable()
                    .WithColumn("ConfirmedUtc").AsDateTime().Nullable();
            }

            if (!Schema.Table("MamUnsatisfiedSlotReservations").Index("UX_MamUnsatisfiedSlotReservations_Indexer_Torrent").Exists())
            {
                Create.Index("UX_MamUnsatisfiedSlotReservations_Indexer_Torrent")
                    .OnTable("MamUnsatisfiedSlotReservations")
                    .OnColumn("IndexerId").Ascending()
                    .OnColumn("TorrentId").Ascending()
                    .WithOptions().Unique();
            }
        }
    }
}
