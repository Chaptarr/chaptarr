using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(60)]
    public class remove_unused_importlist_fields : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("ImportLists").Exists())
            {
                return;
            }

            var hasShouldSearchMonitoredAuthors = Schema.Table("ImportLists").Column("ShouldSearchMonitoredAuthors").Exists();
            var hasListOrder = Schema.Table("ImportLists").Column("ListOrder").Exists();

            if (!hasShouldSearchMonitoredAuthors && !hasListOrder)
            {
                return;
            }

            IfDatabase("sqlite").Execute.Sql($@"
                DROP TABLE IF EXISTS ""ImportLists_new"";

                CREATE TABLE IF NOT EXISTS ""ImportLists_new"" (
                    ""Id"" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    ""EnableAutomaticAdd"" INTEGER NOT NULL,
                    ""ShouldMonitor"" INTEGER NOT NULL,
                    ""ShouldSearch"" INTEGER NOT NULL,
                    ""ShouldMonitorExisting"" INTEGER NOT NULL,
                    ""MonitorNewItems"" INTEGER NOT NULL,
                    ""Name"" TEXT NOT NULL,
                    ""Implementation"" TEXT NOT NULL,
                    ""Settings"" TEXT,
                    ""ConfigContract"" TEXT NOT NULL,
                    ""Tags"" TEXT,
                    ""QualityProfileId"" INTEGER NOT NULL DEFAULT 1,
                    ""MetadataProfileId"" INTEGER NOT NULL DEFAULT 1,
                    ""RootFolderPath"" TEXT
                );

                INSERT INTO ""ImportLists_new"" (
                    ""Id"",
                    ""EnableAutomaticAdd"",
                    ""ShouldMonitor"",
                    ""ShouldSearch"",
                    ""ShouldMonitorExisting"",
                    ""MonitorNewItems"",
                    ""Name"",
                    ""Implementation"",
                    ""Settings"",
                    ""ConfigContract"",
                    ""Tags"",
                    ""QualityProfileId"",
                    ""MetadataProfileId"",
                    ""RootFolderPath""
                )
                SELECT
                    ""Id"",
                    ""EnableAutomaticAdd"",
                    ""ShouldMonitor"",
                    ""ShouldSearch"",
                    ""ShouldMonitorExisting"",
                    ""MonitorNewItems"",
                    ""Name"",
                    ""Implementation"",
                    ""Settings"",
                    ""ConfigContract"",
                    ""Tags"",
                    ""QualityProfileId"",
                    ""MetadataProfileId"",
                    {(Schema.Table("ImportLists").Column("RootFolderPath").Exists() ? @"""RootFolderPath""" : "NULL")}
                FROM ""ImportLists"";

                DROP TABLE ""ImportLists"";
                ALTER TABLE ""ImportLists_new"" RENAME TO ""ImportLists"";
            ");

            IfPostgres().Execute.Sql(@"
                ALTER TABLE ""ImportLists"" DROP COLUMN IF EXISTS ""ShouldSearchMonitoredAuthors"";
                ALTER TABLE ""ImportLists"" DROP COLUMN IF EXISTS ""ListOrder"";
            ");
        }
    }
}
