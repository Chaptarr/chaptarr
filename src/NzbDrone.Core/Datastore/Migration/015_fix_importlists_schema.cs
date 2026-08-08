using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(15)]
    public class fix_importlists_schema : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("ImportLists").Exists())
            {
                return;
            }

            var needsRootFolderPath = !Schema.Table("ImportLists").Column("RootFolderPath").Exists();
            var hasEnabledColumn = Schema.Table("ImportLists").Column("Enabled").Exists();

            if (!needsRootFolderPath && !hasEnabledColumn)
            {
                return;
            }

            // SQLite: rebuild the table to add RootFolderPath and remove the stray Enabled column.
            // This keeps the schema aligned with ImportListDefinition, and avoids NOT NULL insert failures.
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

            // PostgreSQL: add RootFolderPath if missing and drop Enabled if present.
            // Also fix ShouldMonitor to be an integer when it was incorrectly created as a boolean.
            IfPostgres().Execute.Sql(@"
                ALTER TABLE ""ImportLists"" ADD COLUMN IF NOT EXISTS ""RootFolderPath"" TEXT;
                ALTER TABLE ""ImportLists"" DROP COLUMN IF EXISTS ""Enabled"";
                ALTER TABLE ""ImportLists"" DROP COLUMN IF EXISTS ""ShouldSearchMonitoredAuthors"";
                ALTER TABLE ""ImportLists"" DROP COLUMN IF EXISTS ""ListOrder"";

                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = current_schema()
                          AND table_name ILIKE 'importlists'
                          AND column_name ILIKE 'shouldmonitor'
                          AND data_type = 'boolean'
                    ) THEN
                        EXECUTE 'ALTER TABLE ""ImportLists"" ALTER COLUMN ""ShouldMonitor"" TYPE integer USING (CASE WHEN ""ShouldMonitor"" THEN 2 ELSE 0 END)';
                    END IF;
                END $$;
            ");
        }
    }
}
