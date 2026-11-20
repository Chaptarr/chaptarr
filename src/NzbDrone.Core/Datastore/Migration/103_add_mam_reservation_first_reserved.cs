using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(103)]
    public class add_mam_reservation_first_reserved : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (Schema.Table("MamUnsatisfiedSlotReservations").Exists() &&
                !Schema.Table("MamUnsatisfiedSlotReservations").Column("FirstReservedUtc").Exists())
            {
                Alter.Table("MamUnsatisfiedSlotReservations")
                    .AddColumn("FirstReservedUtc").AsDateTime().Nullable();

                Execute.Sql("UPDATE \"MamUnsatisfiedSlotReservations\" SET \"FirstReservedUtc\" = \"ReservedUtc\" WHERE \"FirstReservedUtc\" IS NULL");
            }
        }
    }
}
