using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(108)]
    public class add_ignored_genres_to_metadata_profile : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            const string tableName = "MetadataProfiles";
            if (Schema.Table(tableName).Exists() &&
                !Schema.Table(tableName).Column("IgnoredGenres").Exists())
            {
                Alter.Table(tableName)
                    .AddColumn("IgnoredGenres")
                    .AsString()
                    .NotNullable()
                    .WithDefaultValue("[]");
            }
        }
    }
}
