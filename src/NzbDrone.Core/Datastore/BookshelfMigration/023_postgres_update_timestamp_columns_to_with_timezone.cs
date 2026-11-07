// Source: BookshelfNG main-v0.4.21.71 (3fad9427d167ed69d30ca6ed55f40262c2846487), GPL-3.0.
// This isolated copy upgrades a disposable BookshelfNG source snapshot before import.
using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.BookshelfMigration
{
    [Migration(23)]
    public class postgres_update_timestamp_columns_to_with_timezone : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Delete.FromTable("Commands").AllRows();
            // This migration only changes PostgreSQL timestamp types. SQLite stores
            // these values dynamically and the BookshelfNG SQLite schema already
            // uses DATETIME affinity, so rebuilding every table is unnecessary.
        }

        protected override void LogDbUpgrade()
        {
            Alter.Table("Logs").AlterColumn("Time").AsDateTimeOffset().NotNullable();
            Alter.Table("VersionInfo").AlterColumn("AppliedOn").AsDateTimeOffset().Nullable();
        }

        protected override void CacheDbUpgrade()
        {
            Alter.Table("HttpResponse").AlterColumn("LastRefresh").AsDateTimeOffset().Nullable();
            Alter.Table("HttpResponse").AlterColumn("Expiry").AsDateTimeOffset().Nullable();
        }
    }
}
