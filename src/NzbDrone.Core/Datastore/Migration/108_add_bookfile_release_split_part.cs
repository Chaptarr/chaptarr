using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    // Records which part of a split release ("Part 2 of 2") a file came from, so importing
    // one part no longer replaces the files of the other parts as an upgrade.
    [Migration(108)]
    public class add_bookfile_release_split_part : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            const string tableName = "BookFiles";

            foreach (var column in new[] { "ReleasePart", "ReleasePartCount" })
            {
                if (Schema.Table(tableName).Exists() &&
                    !Schema.Table(tableName).Column(column).Exists())
                {
                    Alter.Table(tableName)
                        .AddColumn(column)
                        .AsInt32()
                        .NotNullable()
                        .WithDefaultValue(0);
                }
            }
        }
    }
}
