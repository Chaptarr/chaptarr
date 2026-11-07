using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(120)]
    public class add_book_file_release_part : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("BookFiles").Exists())
            {
                return;
            }

            // Which piece of a book published as several releases a file came from. Zero means the
            // file is not known to be one piece of a split release.
            if (!Schema.Table("BookFiles").Column("ReleasePart").Exists())
            {
                Alter.Table("BookFiles")
                    .AddColumn("ReleasePart").AsInt32().NotNullable().WithDefaultValue(0);
            }

            if (!Schema.Table("BookFiles").Column("ReleasePartCount").Exists())
            {
                Alter.Table("BookFiles")
                    .AddColumn("ReleasePartCount").AsInt32().NotNullable().WithDefaultValue(0);
            }
        }
    }
}
