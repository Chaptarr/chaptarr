using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(124)]
    public class add_upgrade_confirmation_and_recording_override : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("BookFiles").Column("AudioProductionOverride").Exists())
            {
                Alter.Table("BookFiles").AddColumn("AudioProductionOverride").AsBoolean().Nullable();
            }

            if (!Schema.Table("QualityProfiles").Column("ConfirmAutomaticUpgrades").Exists())
            {
                Alter.Table("QualityProfiles").AddColumn("ConfirmAutomaticUpgrades")
                    .AsBoolean().NotNullable().WithDefaultValue(false);
            }
        }
    }
}
