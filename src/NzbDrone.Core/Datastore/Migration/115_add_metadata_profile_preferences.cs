using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(115)]
    public class add_metadata_profile_preferences : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            const string tableName = "MetadataProfiles";

            if (Schema.Table(tableName).Exists() && !Schema.Table(tableName).Column("PreferredEditionTerms").Exists())
            {
                Alter.Table(tableName).AddColumn("PreferredEditionTerms").AsString().Nullable();
            }

            if (Schema.Table(tableName).Exists() && !Schema.Table(tableName).Column("PreferredSeries").Exists())
            {
                Alter.Table(tableName).AddColumn("PreferredSeries").AsString().Nullable();
            }
        }
    }
}
