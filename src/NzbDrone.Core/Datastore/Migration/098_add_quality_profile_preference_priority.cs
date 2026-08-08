using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(98)]
    public class add_quality_profile_preference_priority : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("QualityProfiles").Column("PreferCustomFormatsOverQuality").Exists())
            {
                Alter.Table("QualityProfiles")
                    .AddColumn("PreferCustomFormatsOverQuality")
                    .AsBoolean()
                    .NotNullable()
                    .WithDefaultValue(false);
            }
        }
    }
}
