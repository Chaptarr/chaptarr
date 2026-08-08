using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(20)]
    public class fix_monitor_existing_column_types : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            // SQLite is permissive with column types (BOOLEAN is stored as integer affinity), but PostgreSQL is strict.
            // These columns were originally created as boolean and later repurposed to store tri-state monitoring:
            // 0=None, 1=All, 2=Selected, NULL=inherit.
            IfPostgres().Execute.Sql(@"
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = current_schema()
                          AND table_name ILIKE 'authors'
                          AND column_name ILIKE 'audiobookmonitorexisting'
                          AND data_type = 'boolean'
                    ) THEN
                        EXECUTE 'ALTER TABLE ""Authors"" ALTER COLUMN ""AudiobookMonitorExisting"" TYPE integer USING (CASE WHEN ""AudiobookMonitorExisting"" IS NULL THEN NULL WHEN ""AudiobookMonitorExisting"" THEN 1 ELSE 0 END)';
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = current_schema()
                          AND table_name ILIKE 'authors'
                          AND column_name ILIKE 'ebookmonitorexisting'
                          AND data_type = 'boolean'
                    ) THEN
                        EXECUTE 'ALTER TABLE ""Authors"" ALTER COLUMN ""EbookMonitorExisting"" TYPE integer USING (CASE WHEN ""EbookMonitorExisting"" IS NULL THEN NULL WHEN ""EbookMonitorExisting"" THEN 1 ELSE 0 END)';
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = current_schema()
                          AND table_name ILIKE 'pendingauthorimport'
                          AND column_name ILIKE 'audiobookmonitorexisting'
                          AND data_type = 'boolean'
                    ) THEN
                        EXECUTE 'ALTER TABLE ""PendingAuthorImport"" ALTER COLUMN ""AudiobookMonitorExisting"" TYPE integer USING (CASE WHEN ""AudiobookMonitorExisting"" IS NULL THEN NULL WHEN ""AudiobookMonitorExisting"" THEN 1 ELSE 0 END)';
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = current_schema()
                          AND table_name ILIKE 'pendingauthorimport'
                          AND column_name ILIKE 'ebookmonitorexisting'
                          AND data_type = 'boolean'
                    ) THEN
                        EXECUTE 'ALTER TABLE ""PendingAuthorImport"" ALTER COLUMN ""EbookMonitorExisting"" TYPE integer USING (CASE WHEN ""EbookMonitorExisting"" IS NULL THEN NULL WHEN ""EbookMonitorExisting"" THEN 1 ELSE 0 END)';
                    END IF;
                END $$;
            ");
        }
    }
}
