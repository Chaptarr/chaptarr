using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(53)]
    public class catchup_books_is_omnibus : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            // Catch-up migration: earlier histories reused version 004 for different migrations.
            // Ensure the Books.IsOmnibus column exists even if migration 004 was skipped due to VersionInfo drift.
            if (!Schema.Table("Books").Column("IsOmnibus").Exists())
            {
                Alter.Table("Books")
                    .AddColumn("IsOmnibus").AsBoolean().WithDefaultValue(false);
            }
        }
    }
}

