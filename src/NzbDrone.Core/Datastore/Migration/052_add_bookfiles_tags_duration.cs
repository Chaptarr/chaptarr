using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(52)]
    public class add_bookfiles_tags_duration : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("BookFiles").Column("AllTags").Exists())
            {
                Alter.Table("BookFiles")
                    .AddColumn("AllTags")
                    .AsString()
                    .Nullable();
            }

            if (!Schema.Table("BookFiles").Column("DurationSeconds").Exists())
            {
                Alter.Table("BookFiles")
                    .AddColumn("DurationSeconds")
                    .AsInt32()
                    .Nullable();
            }
        }
    }
}
