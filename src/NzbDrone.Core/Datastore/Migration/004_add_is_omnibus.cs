using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(4)]
    public class add_is_omnibus : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            // Add IsOmnibus column to Books table
            // This flag identifies omnibus/anthology/collection books (box sets, complete series, etc.)
            // Populated from metadata server's golden.works.is_multi_work field
            if (!Schema.Table("Books").Column("IsOmnibus").Exists())
            {
                Alter.Table("Books")
                    .AddColumn("IsOmnibus").AsBoolean().WithDefaultValue(false);
            }
        }
    }
}
