using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(125)]
    public class pending_import_format_origins : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("PendingAuthorImport").Column("ExplicitBookSearches").Exists())
            {
                Alter.Table("PendingAuthorImport").AddColumn("ExplicitBookSearches").AsString(int.MaxValue).Nullable();
            }

            foreach (var format in new[] { "Audiobook", "Ebook" })
            {
                var column = format + "RequestedBy";
                if (!Schema.Table("PendingAuthorImport").Column(column).Exists())
                {
                    Alter.Table("PendingAuthorImport").AddColumn(column).AsString().Nullable();
                    Execute.Sql($@"UPDATE ""PendingAuthorImport"" SET ""{column}"" = ""RequestedBy"" WHERE ""{format}Status"" <> 0");
                }
            }
        }
    }
}
