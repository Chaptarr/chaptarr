using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(30)]
    public class fix_pendingauthorimport_status_column_types : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            // PendingAuthorImport status columns were originally created as strings (e.g. "Pending") but are now
            // represented in code as PendingImportStatus enums (ints). PostgreSQL is strict about comparing text to
            // integers and will fail queries like OverallStatus = 1.

	            IfPostgres().Execute.Sql(@"
		                DO $$
		                BEGIN
		                    IF EXISTS (
	                        SELECT 1
	                        FROM information_schema.columns
	                        WHERE table_schema = current_schema()
	                          AND table_name ILIKE 'pendingauthorimport'
	                          AND column_name ILIKE 'audiobookstatus'
	                          AND data_type IN ('text', 'character varying')
	                    ) THEN
	                        -- Postgres attempts to cast the existing DEFAULT when changing the column type.
	                        -- Older schemas used string defaults like 'Pending', which are not castable to integer.
	                        -- Drop the default first, then re-apply an integer default after conversion.
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
                END $$;
            ");

            // SQLite is permissive about types but the values may be stored as strings ("Pending") which breaks
            // enum-based comparisons. Normalize values to integer enums and re-create the active unique index.
            IfDatabase("sqlite").Execute.Sql(@"
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

                -- Dedupe any duplicate active rows before recreating the unique partial index.
                -- Prior schemas used string OverallStatus values in the partial index predicate, but code can write
                -- integer enums, which can allow duplicates to accumulate and then cause index creation to fail.
                UPDATE PendingAuthorImport
                SET
                    AudiobookStatus =
                        CASE
                            WHEN EXISTS (
                                SELECT 1
                                FROM PendingAuthorImport p2
                                WHERE p2.ProviderId = PendingAuthorImport.ProviderId
                                  AND p2.OverallStatus IN (1, 2, 3)
                                  AND p2.AudiobookStatus = 2
                            ) THEN 2
                            WHEN EXISTS (
                                SELECT 1
                                FROM PendingAuthorImport p2
                                WHERE p2.ProviderId = PendingAuthorImport.ProviderId
                                  AND p2.OverallStatus IN (1, 2, 3)
                                  AND p2.AudiobookStatus = 1
                            ) THEN 1
                            WHEN EXISTS (
                                SELECT 1
                                FROM PendingAuthorImport p2
                                WHERE p2.ProviderId = PendingAuthorImport.ProviderId
                                  AND p2.OverallStatus IN (1, 2, 3)
                                  AND p2.AudiobookStatus = 3
                            ) THEN 3
                            WHEN EXISTS (
                                SELECT 1
                                FROM PendingAuthorImport p2
                                WHERE p2.ProviderId = PendingAuthorImport.ProviderId
                                  AND p2.OverallStatus IN (1, 2, 3)
                                  AND p2.AudiobookStatus = 4
                            ) THEN 4
                            WHEN EXISTS (
                                SELECT 1
                                FROM PendingAuthorImport p2
                                WHERE p2.ProviderId = PendingAuthorImport.ProviderId
                                  AND p2.OverallStatus IN (1, 2, 3)
                                  AND p2.AudiobookStatus = 5
                            ) THEN 5
                            WHEN EXISTS (
                                SELECT 1
                                FROM PendingAuthorImport p2
                                WHERE p2.ProviderId = PendingAuthorImport.ProviderId
                                  AND p2.OverallStatus IN (1, 2, 3)
                                  AND p2.AudiobookStatus = 6
                            ) THEN 6
                            ELSE 0
                        END,

                    EbookStatus =
                        CASE
                            WHEN EXISTS (
                                SELECT 1
                                FROM PendingAuthorImport p2
                                WHERE p2.ProviderId = PendingAuthorImport.ProviderId
                                  AND p2.OverallStatus IN (1, 2, 3)
                                  AND p2.EbookStatus = 2
                            ) THEN 2
                            WHEN EXISTS (
                                SELECT 1
                                FROM PendingAuthorImport p2
                                WHERE p2.ProviderId = PendingAuthorImport.ProviderId
                                  AND p2.OverallStatus IN (1, 2, 3)
                                  AND p2.EbookStatus = 1
                            ) THEN 1
                            WHEN EXISTS (
                                SELECT 1
                                FROM PendingAuthorImport p2
                                WHERE p2.ProviderId = PendingAuthorImport.ProviderId
                                  AND p2.OverallStatus IN (1, 2, 3)
                                  AND p2.EbookStatus = 3
                            ) THEN 3
                            WHEN EXISTS (
                                SELECT 1
                                FROM PendingAuthorImport p2
                                WHERE p2.ProviderId = PendingAuthorImport.ProviderId
                                  AND p2.OverallStatus IN (1, 2, 3)
                                  AND p2.EbookStatus = 4
                            ) THEN 4
                            WHEN EXISTS (
                                SELECT 1
                                FROM PendingAuthorImport p2
                                WHERE p2.ProviderId = PendingAuthorImport.ProviderId
                                  AND p2.OverallStatus IN (1, 2, 3)
                                  AND p2.EbookStatus = 5
                            ) THEN 5
                            WHEN EXISTS (
                                SELECT 1
                                FROM PendingAuthorImport p2
                                WHERE p2.ProviderId = PendingAuthorImport.ProviderId
                                  AND p2.OverallStatus IN (1, 2, 3)
                                  AND p2.EbookStatus = 6
                            ) THEN 6
                            ELSE 0
                        END,

                    AudiobookMonitorExisting = COALESCE(
                        AudiobookMonitorExisting,
                        (
                            SELECT p2.AudiobookMonitorExisting
                            FROM PendingAuthorImport p2
                            WHERE p2.ProviderId = PendingAuthorImport.ProviderId
                              AND p2.OverallStatus IN (1, 2, 3)
                              AND p2.AudiobookStatus != 0
                              AND p2.AudiobookMonitorExisting IS NOT NULL
                            ORDER BY p2.Id DESC
                            LIMIT 1
                        )
                    ),
                    AudiobookMonitorFuture = COALESCE(
                        AudiobookMonitorFuture,
                        (
                            SELECT p2.AudiobookMonitorFuture
                            FROM PendingAuthorImport p2
                            WHERE p2.ProviderId = PendingAuthorImport.ProviderId
                              AND p2.OverallStatus IN (1, 2, 3)
                              AND p2.AudiobookStatus != 0
                              AND p2.AudiobookMonitorFuture IS NOT NULL
                            ORDER BY p2.Id DESC
                            LIMIT 1
                        )
                    ),
                    AudiobookQualityProfileId = COALESCE(
                        AudiobookQualityProfileId,
                        (
                            SELECT p2.AudiobookQualityProfileId
                            FROM PendingAuthorImport p2
                            WHERE p2.ProviderId = PendingAuthorImport.ProviderId
                              AND p2.OverallStatus IN (1, 2, 3)
                              AND p2.AudiobookStatus != 0
                              AND p2.AudiobookQualityProfileId IS NOT NULL
                            ORDER BY p2.Id DESC
                            LIMIT 1
                        )
                    ),
                    AudiobookMetadataProfileId = COALESCE(
                        AudiobookMetadataProfileId,
                        (
                            SELECT p2.AudiobookMetadataProfileId
                            FROM PendingAuthorImport p2
                            WHERE p2.ProviderId = PendingAuthorImport.ProviderId
                              AND p2.OverallStatus IN (1, 2, 3)
                              AND p2.AudiobookStatus != 0
                              AND p2.AudiobookMetadataProfileId IS NOT NULL
                            ORDER BY p2.Id DESC
                            LIMIT 1
                        )
                    ),
                    AudiobookRootFolderPath = COALESCE(
                        NULLIF(AudiobookRootFolderPath, ''),
                        (
                            SELECT p2.AudiobookRootFolderPath
                            FROM PendingAuthorImport p2
                            WHERE p2.ProviderId = PendingAuthorImport.ProviderId
                              AND p2.OverallStatus IN (1, 2, 3)
                              AND p2.AudiobookStatus != 0
                              AND p2.AudiobookRootFolderPath IS NOT NULL
                              AND p2.AudiobookRootFolderPath != ''
                            ORDER BY p2.Id DESC
                            LIMIT 1
                        )
                    ),
                    AudiobookBooksToMonitor = COALESCE(
                        NULLIF(AudiobookBooksToMonitor, ''),
                        (
                            SELECT p2.AudiobookBooksToMonitor
                            FROM PendingAuthorImport p2
                            WHERE p2.ProviderId = PendingAuthorImport.ProviderId
                              AND p2.OverallStatus IN (1, 2, 3)
                              AND p2.AudiobookStatus != 0
                              AND p2.AudiobookBooksToMonitor IS NOT NULL
                              AND p2.AudiobookBooksToMonitor != ''
                            ORDER BY p2.Id DESC
                            LIMIT 1
                        )
                    ),

                    EbookMonitorExisting = COALESCE(
                        EbookMonitorExisting,
                        (
                            SELECT p2.EbookMonitorExisting
                            FROM PendingAuthorImport p2
                            WHERE p2.ProviderId = PendingAuthorImport.ProviderId
                              AND p2.OverallStatus IN (1, 2, 3)
                              AND p2.EbookStatus != 0
                              AND p2.EbookMonitorExisting IS NOT NULL
                            ORDER BY p2.Id DESC
                            LIMIT 1
                        )
                    ),
                    EbookMonitorFuture = COALESCE(
                        EbookMonitorFuture,
                        (
                            SELECT p2.EbookMonitorFuture
                            FROM PendingAuthorImport p2
                            WHERE p2.ProviderId = PendingAuthorImport.ProviderId
                              AND p2.OverallStatus IN (1, 2, 3)
                              AND p2.EbookStatus != 0
                              AND p2.EbookMonitorFuture IS NOT NULL
                            ORDER BY p2.Id DESC
                            LIMIT 1
                        )
                    ),
                    EbookQualityProfileId = COALESCE(
                        EbookQualityProfileId,
                        (
                            SELECT p2.EbookQualityProfileId
                            FROM PendingAuthorImport p2
                            WHERE p2.ProviderId = PendingAuthorImport.ProviderId
                              AND p2.OverallStatus IN (1, 2, 3)
                              AND p2.EbookStatus != 0
                              AND p2.EbookQualityProfileId IS NOT NULL
                            ORDER BY p2.Id DESC
                            LIMIT 1
                        )
                    ),
                    EbookMetadataProfileId = COALESCE(
                        EbookMetadataProfileId,
                        (
                            SELECT p2.EbookMetadataProfileId
                            FROM PendingAuthorImport p2
                            WHERE p2.ProviderId = PendingAuthorImport.ProviderId
                              AND p2.OverallStatus IN (1, 2, 3)
                              AND p2.EbookStatus != 0
                              AND p2.EbookMetadataProfileId IS NOT NULL
                            ORDER BY p2.Id DESC
                            LIMIT 1
                        )
                    ),
                    EbookRootFolderPath = COALESCE(
                        NULLIF(EbookRootFolderPath, ''),
                        (
                            SELECT p2.EbookRootFolderPath
                            FROM PendingAuthorImport p2
                            WHERE p2.ProviderId = PendingAuthorImport.ProviderId
                              AND p2.OverallStatus IN (1, 2, 3)
                              AND p2.EbookStatus != 0
                              AND p2.EbookRootFolderPath IS NOT NULL
                              AND p2.EbookRootFolderPath != ''
                            ORDER BY p2.Id DESC
                            LIMIT 1
                        )
                    ),
                    EbookBooksToMonitor = COALESCE(
                        NULLIF(EbookBooksToMonitor, ''),
                        (
                            SELECT p2.EbookBooksToMonitor
                            FROM PendingAuthorImport p2
                            WHERE p2.ProviderId = PendingAuthorImport.ProviderId
                              AND p2.OverallStatus IN (1, 2, 3)
                              AND p2.EbookStatus != 0
                              AND p2.EbookBooksToMonitor IS NOT NULL
                              AND p2.EbookBooksToMonitor != ''
                            ORDER BY p2.Id DESC
                            LIMIT 1
                        )
                    ),

                    Tags = COALESCE(
                        NULLIF(Tags, ''),
                        (
                            SELECT p2.Tags
                            FROM PendingAuthorImport p2
                            WHERE p2.ProviderId = PendingAuthorImport.ProviderId
                              AND p2.OverallStatus IN (1, 2, 3)
                              AND p2.Tags IS NOT NULL
                              AND p2.Tags != ''
                            ORDER BY p2.Id DESC
                            LIMIT 1
                        )
                    ),

                    SearchForMissingBooks = CASE
                        WHEN EXISTS (
                            SELECT 1
                            FROM PendingAuthorImport p2
                            WHERE p2.ProviderId = PendingAuthorImport.ProviderId
                              AND p2.OverallStatus IN (1, 2, 3)
                              AND p2.SearchForMissingBooks = 1
                        ) THEN 1
                        ELSE 0
                    END,

                    LockedBy = NULL,
                    LockedAt = NULL,
                    LeaseExpiresAt = NULL,

                    NextAttemptAt = (
                        SELECT MIN(p2.NextAttemptAt)
                        FROM PendingAuthorImport p2
                        WHERE p2.ProviderId = PendingAuthorImport.ProviderId
                          AND p2.OverallStatus IN (1, 2, 3)
                    ),

                    OverallStatus = 3
                WHERE Id IN (
                    SELECT MAX(Id)
                    FROM PendingAuthorImport
                    WHERE OverallStatus IN (1, 2, 3)
                    GROUP BY ProviderId
                    HAVING COUNT(*) > 1
                );

                UPDATE PendingAuthorImport
                SET
                    OverallStatus = 6,
                    AudiobookStatus = CASE WHEN AudiobookStatus != 0 THEN 6 ELSE 0 END,
                    EbookStatus = CASE WHEN EbookStatus != 0 THEN 6 ELSE 0 END,
                    LastError = COALESCE(NULLIF(LastError, ''), 'Deduped duplicate active PendingAuthorImport during DB migration'),
                    UpdatedAt = datetime('now'),
                    LockedBy = NULL,
                    LockedAt = NULL,
                    LeaseExpiresAt = NULL
                WHERE OverallStatus IN (1, 2, 3)
                  AND ProviderId IN (
                      SELECT ProviderId
                      FROM PendingAuthorImport
                      WHERE OverallStatus IN (1, 2, 3)
                      GROUP BY ProviderId
                      HAVING COUNT(*) > 1
                  )
                  AND Id NOT IN (
                      SELECT MAX(Id)
                      FROM PendingAuthorImport
                      WHERE OverallStatus IN (1, 2, 3)
                      GROUP BY ProviderId
                      HAVING COUNT(*) > 1
                  );

                DROP INDEX IF EXISTS UX_PendingAuthorImport_Active;
                CREATE UNIQUE INDEX IF NOT EXISTS UX_PendingAuthorImport_Active
                    ON PendingAuthorImport(ProviderId)
                    WHERE OverallStatus IN (1, 2, 3);
            ");
        }
    }
}
