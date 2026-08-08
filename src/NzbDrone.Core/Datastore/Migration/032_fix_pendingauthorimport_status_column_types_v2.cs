using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(32)]
    public class fix_pendingauthorimport_status_column_types_v2 : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            // Migration 30 attempted to convert PendingAuthorImport status columns from string to int enums,
            // but some Postgres installs ended up with the columns still as TEXT/VARCHAR (e.g. "Pending").
            // This causes runtime errors like: operator does not exist: text = integer.
            //
            // This migration is intentionally idempotent and safe to run after migration 30.

            IfPostgres().Execute.Sql(@"
                DO $$
                DECLARE
                    had_active_unique_index boolean := false;
                    idx record;
                BEGIN
                    -- Some installs may have created the SQLite-only partial unique index in PostgreSQL
                    -- (or created it manually). If present, it may depend on string literals and can block
                    -- ALTER COLUMN TYPE, so drop and recreate it (only if it existed) with integer predicates.
                    FOR idx IN
                        SELECT n.nspname, c.relname
                        FROM pg_class c
                        INNER JOIN pg_namespace n ON n.oid = c.relnamespace
                        WHERE c.relkind = 'i'
                          AND n.nspname = current_schema()
                          AND lower(c.relname) = lower('UX_PendingAuthorImport_Active')
                    LOOP
                        had_active_unique_index := true;
                        EXECUTE format('DROP INDEX IF EXISTS %I.%I', idx.nspname, idx.relname);
                    END LOOP;

                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = current_schema()
                          AND table_name ILIKE 'pendingauthorimport'
                          AND column_name ILIKE 'audiobookstatus'
                          AND data_type IN ('text', 'character varying')
                    ) THEN
                        EXECUTE 'ALTER TABLE ""PendingAuthorImport"" ALTER COLUMN ""AudiobookStatus"" DROP DEFAULT';
                        EXECUTE 'ALTER TABLE ""PendingAuthorImport"" ALTER COLUMN ""AudiobookStatus"" TYPE integer USING (
                            CASE ""AudiobookStatus""
                                WHEN ''NotRequested'' THEN 0
                                WHEN ''Pending'' THEN 1
                                WHEN ''InProgress'' THEN 2
                                WHEN ''Retrying'' THEN 3
                                WHEN ''PartialSuccess'' THEN 4
                                WHEN ''Succeeded'' THEN 5
                                WHEN ''Failed'' THEN 6
                                WHEN ''0'' THEN 0
                                WHEN ''1'' THEN 1
                                WHEN ''2'' THEN 2
                                WHEN ''3'' THEN 3
                                WHEN ''4'' THEN 4
                                WHEN ''5'' THEN 5
                                WHEN ''6'' THEN 6
                                ELSE 0
                            END
                        )';
                        EXECUTE 'ALTER TABLE ""PendingAuthorImport"" ALTER COLUMN ""AudiobookStatus"" SET DEFAULT 0';
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = current_schema()
                          AND table_name ILIKE 'pendingauthorimport'
                          AND column_name ILIKE 'ebookstatus'
                          AND data_type IN ('text', 'character varying')
                    ) THEN
                        EXECUTE 'ALTER TABLE ""PendingAuthorImport"" ALTER COLUMN ""EbookStatus"" DROP DEFAULT';
                        EXECUTE 'ALTER TABLE ""PendingAuthorImport"" ALTER COLUMN ""EbookStatus"" TYPE integer USING (
                            CASE ""EbookStatus""
                                WHEN ''NotRequested'' THEN 0
                                WHEN ''Pending'' THEN 1
                                WHEN ''InProgress'' THEN 2
                                WHEN ''Retrying'' THEN 3
                                WHEN ''PartialSuccess'' THEN 4
                                WHEN ''Succeeded'' THEN 5
                                WHEN ''Failed'' THEN 6
                                WHEN ''0'' THEN 0
                                WHEN ''1'' THEN 1
                                WHEN ''2'' THEN 2
                                WHEN ''3'' THEN 3
                                WHEN ''4'' THEN 4
                                WHEN ''5'' THEN 5
                                WHEN ''6'' THEN 6
                                ELSE 0
                            END
                        )';
                        EXECUTE 'ALTER TABLE ""PendingAuthorImport"" ALTER COLUMN ""EbookStatus"" SET DEFAULT 0';
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = current_schema()
                          AND table_name ILIKE 'pendingauthorimport'
                          AND column_name ILIKE 'overallstatus'
                          AND data_type IN ('text', 'character varying')
                    ) THEN
                        EXECUTE 'ALTER TABLE ""PendingAuthorImport"" ALTER COLUMN ""OverallStatus"" DROP DEFAULT';
                        EXECUTE 'ALTER TABLE ""PendingAuthorImport"" ALTER COLUMN ""OverallStatus"" TYPE integer USING (
                            CASE ""OverallStatus""
                                WHEN ''NotRequested'' THEN 0
                                WHEN ''Pending'' THEN 1
                                WHEN ''InProgress'' THEN 2
                                WHEN ''Retrying'' THEN 3
                                WHEN ''PartialSuccess'' THEN 4
                                WHEN ''Succeeded'' THEN 5
                                WHEN ''Failed'' THEN 6
                                WHEN ''0'' THEN 0
                                WHEN ''1'' THEN 1
                                WHEN ''2'' THEN 2
                                WHEN ''3'' THEN 3
                                WHEN ''4'' THEN 4
                                WHEN ''5'' THEN 5
                                WHEN ''6'' THEN 6
                                ELSE 1
                            END
                        )';
                        EXECUTE 'ALTER TABLE ""PendingAuthorImport"" ALTER COLUMN ""OverallStatus"" SET DEFAULT 1';
                    END IF;

                    IF had_active_unique_index THEN
                        EXECUTE 'CREATE UNIQUE INDEX IF NOT EXISTS UX_PendingAuthorImport_Active
                            ON ""PendingAuthorImport""(""ProviderId"")
                            WHERE ""OverallStatus"" IN (1, 2, 3)';
                    END IF;
                END $$;
            ");

            // Keep SQLite installs consistent even if migration 30 was skipped or partial.
            IfDatabase("SQLite", "sqlite").Execute.Sql(@"
                UPDATE PendingAuthorImport
                SET AudiobookStatus =
                    CASE CAST(AudiobookStatus AS TEXT)
                        WHEN 'NotRequested' THEN 0
                        WHEN 'Pending' THEN 1
                        WHEN 'InProgress' THEN 2
                        WHEN 'Retrying' THEN 3
                        WHEN 'PartialSuccess' THEN 4
                        WHEN 'Succeeded' THEN 5
                        WHEN 'Failed' THEN 6
                        WHEN '0' THEN 0
                        WHEN '1' THEN 1
                        WHEN '2' THEN 2
                        WHEN '3' THEN 3
                        WHEN '4' THEN 4
                        WHEN '5' THEN 5
                        WHEN '6' THEN 6
                        ELSE 0
                    END;

                UPDATE PendingAuthorImport
                SET EbookStatus =
                    CASE CAST(EbookStatus AS TEXT)
                        WHEN 'NotRequested' THEN 0
                        WHEN 'Pending' THEN 1
                        WHEN 'InProgress' THEN 2
                        WHEN 'Retrying' THEN 3
                        WHEN 'PartialSuccess' THEN 4
                        WHEN 'Succeeded' THEN 5
                        WHEN 'Failed' THEN 6
                        WHEN '0' THEN 0
                        WHEN '1' THEN 1
                        WHEN '2' THEN 2
                        WHEN '3' THEN 3
                        WHEN '4' THEN 4
                        WHEN '5' THEN 5
                        WHEN '6' THEN 6
                        ELSE 0
                    END;

                UPDATE PendingAuthorImport
                SET OverallStatus =
                    CASE CAST(OverallStatus AS TEXT)
                        WHEN 'NotRequested' THEN 0
                        WHEN 'Pending' THEN 1
                        WHEN 'InProgress' THEN 2
                        WHEN 'Retrying' THEN 3
                        WHEN 'PartialSuccess' THEN 4
                        WHEN 'Succeeded' THEN 5
                        WHEN 'Failed' THEN 6
                        WHEN '0' THEN 0
                        WHEN '1' THEN 1
                        WHEN '2' THEN 2
                        WHEN '3' THEN 3
                        WHEN '4' THEN 4
                        WHEN '5' THEN 5
                        WHEN '6' THEN 6
                        ELSE 1
                    END;

                DROP INDEX IF EXISTS UX_PendingAuthorImport_Active;
                CREATE UNIQUE INDEX IF NOT EXISTS UX_PendingAuthorImport_Active
                    ON PendingAuthorImport(ProviderId)
                    WHERE OverallStatus IN (1, 2, 3);
            ");
        }
    }
}
