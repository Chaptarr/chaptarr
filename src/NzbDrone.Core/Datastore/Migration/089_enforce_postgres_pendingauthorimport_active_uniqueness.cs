using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(89)]
    public class enforce_postgres_pendingauthorimport_active_uniqueness : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            IfPostgres().Execute.Sql(@"
                UPDATE ""PendingAuthorImport"" AS p
                SET
                    ""AudiobookStatus"" =
                        CASE
                            WHEN EXISTS (
                                SELECT 1
                                FROM ""PendingAuthorImport"" p2
                                WHERE p2.""ProviderId"" = p.""ProviderId""
                                  AND p2.""OverallStatus"" IN (1, 2, 3)
                                  AND p2.""AudiobookStatus"" = 2
                            ) THEN 2
                            WHEN EXISTS (
                                SELECT 1
                                FROM ""PendingAuthorImport"" p2
                                WHERE p2.""ProviderId"" = p.""ProviderId""
                                  AND p2.""OverallStatus"" IN (1, 2, 3)
                                  AND p2.""AudiobookStatus"" = 1
                            ) THEN 1
                            WHEN EXISTS (
                                SELECT 1
                                FROM ""PendingAuthorImport"" p2
                                WHERE p2.""ProviderId"" = p.""ProviderId""
                                  AND p2.""OverallStatus"" IN (1, 2, 3)
                                  AND p2.""AudiobookStatus"" = 3
                            ) THEN 3
                            WHEN EXISTS (
                                SELECT 1
                                FROM ""PendingAuthorImport"" p2
                                WHERE p2.""ProviderId"" = p.""ProviderId""
                                  AND p2.""OverallStatus"" IN (1, 2, 3)
                                  AND p2.""AudiobookStatus"" = 4
                            ) THEN 4
                            WHEN EXISTS (
                                SELECT 1
                                FROM ""PendingAuthorImport"" p2
                                WHERE p2.""ProviderId"" = p.""ProviderId""
                                  AND p2.""OverallStatus"" IN (1, 2, 3)
                                  AND p2.""AudiobookStatus"" = 5
                            ) THEN 5
                            WHEN EXISTS (
                                SELECT 1
                                FROM ""PendingAuthorImport"" p2
                                WHERE p2.""ProviderId"" = p.""ProviderId""
                                  AND p2.""OverallStatus"" IN (1, 2, 3)
                                  AND p2.""AudiobookStatus"" = 6
                            ) THEN 6
                            ELSE 0
                        END,

                    ""EbookStatus"" =
                        CASE
                            WHEN EXISTS (
                                SELECT 1
                                FROM ""PendingAuthorImport"" p2
                                WHERE p2.""ProviderId"" = p.""ProviderId""
                                  AND p2.""OverallStatus"" IN (1, 2, 3)
                                  AND p2.""EbookStatus"" = 2
                            ) THEN 2
                            WHEN EXISTS (
                                SELECT 1
                                FROM ""PendingAuthorImport"" p2
                                WHERE p2.""ProviderId"" = p.""ProviderId""
                                  AND p2.""OverallStatus"" IN (1, 2, 3)
                                  AND p2.""EbookStatus"" = 1
                            ) THEN 1
                            WHEN EXISTS (
                                SELECT 1
                                FROM ""PendingAuthorImport"" p2
                                WHERE p2.""ProviderId"" = p.""ProviderId""
                                  AND p2.""OverallStatus"" IN (1, 2, 3)
                                  AND p2.""EbookStatus"" = 3
                            ) THEN 3
                            WHEN EXISTS (
                                SELECT 1
                                FROM ""PendingAuthorImport"" p2
                                WHERE p2.""ProviderId"" = p.""ProviderId""
                                  AND p2.""OverallStatus"" IN (1, 2, 3)
                                  AND p2.""EbookStatus"" = 4
                            ) THEN 4
                            WHEN EXISTS (
                                SELECT 1
                                FROM ""PendingAuthorImport"" p2
                                WHERE p2.""ProviderId"" = p.""ProviderId""
                                  AND p2.""OverallStatus"" IN (1, 2, 3)
                                  AND p2.""EbookStatus"" = 5
                            ) THEN 5
                            WHEN EXISTS (
                                SELECT 1
                                FROM ""PendingAuthorImport"" p2
                                WHERE p2.""ProviderId"" = p.""ProviderId""
                                  AND p2.""OverallStatus"" IN (1, 2, 3)
                                  AND p2.""EbookStatus"" = 6
                            ) THEN 6
                            ELSE 0
                        END,

                    ""AudiobookMonitorExisting"" = COALESCE(
                        p.""AudiobookMonitorExisting"",
                        (
                            SELECT p2.""AudiobookMonitorExisting""
                            FROM ""PendingAuthorImport"" p2
                            WHERE p2.""ProviderId"" = p.""ProviderId""
                              AND p2.""OverallStatus"" IN (1, 2, 3)
                              AND p2.""AudiobookStatus"" <> 0
                              AND p2.""AudiobookMonitorExisting"" IS NOT NULL
                            ORDER BY p2.""Id"" DESC
                            LIMIT 1
                        )
                    ),
                    ""AudiobookMonitorFuture"" = COALESCE(
                        p.""AudiobookMonitorFuture"",
                        (
                            SELECT p2.""AudiobookMonitorFuture""
                            FROM ""PendingAuthorImport"" p2
                            WHERE p2.""ProviderId"" = p.""ProviderId""
                              AND p2.""OverallStatus"" IN (1, 2, 3)
                              AND p2.""AudiobookStatus"" <> 0
                              AND p2.""AudiobookMonitorFuture"" IS NOT NULL
                            ORDER BY p2.""Id"" DESC
                            LIMIT 1
                        )
                    ),
                    ""AudiobookQualityProfileId"" = COALESCE(
                        p.""AudiobookQualityProfileId"",
                        (
                            SELECT p2.""AudiobookQualityProfileId""
                            FROM ""PendingAuthorImport"" p2
                            WHERE p2.""ProviderId"" = p.""ProviderId""
                              AND p2.""OverallStatus"" IN (1, 2, 3)
                              AND p2.""AudiobookStatus"" <> 0
                              AND p2.""AudiobookQualityProfileId"" IS NOT NULL
                            ORDER BY p2.""Id"" DESC
                            LIMIT 1
                        )
                    ),
                    ""AudiobookMetadataProfileId"" = COALESCE(
                        p.""AudiobookMetadataProfileId"",
                        (
                            SELECT p2.""AudiobookMetadataProfileId""
                            FROM ""PendingAuthorImport"" p2
                            WHERE p2.""ProviderId"" = p.""ProviderId""
                              AND p2.""OverallStatus"" IN (1, 2, 3)
                              AND p2.""AudiobookStatus"" <> 0
                              AND p2.""AudiobookMetadataProfileId"" IS NOT NULL
                            ORDER BY p2.""Id"" DESC
                            LIMIT 1
                        )
                    ),
                    ""AudiobookRootFolderPath"" = COALESCE(
                        NULLIF(p.""AudiobookRootFolderPath"", ''),
                        (
                            SELECT p2.""AudiobookRootFolderPath""
                            FROM ""PendingAuthorImport"" p2
                            WHERE p2.""ProviderId"" = p.""ProviderId""
                              AND p2.""OverallStatus"" IN (1, 2, 3)
                              AND p2.""AudiobookStatus"" <> 0
                              AND p2.""AudiobookRootFolderPath"" IS NOT NULL
                              AND p2.""AudiobookRootFolderPath"" <> ''
                            ORDER BY p2.""Id"" DESC
                            LIMIT 1
                        )
                    ),
                    ""AudiobookBooksToMonitor"" = COALESCE(
                        NULLIF(p.""AudiobookBooksToMonitor"", ''),
                        (
                            SELECT p2.""AudiobookBooksToMonitor""
                            FROM ""PendingAuthorImport"" p2
                            WHERE p2.""ProviderId"" = p.""ProviderId""
                              AND p2.""OverallStatus"" IN (1, 2, 3)
                              AND p2.""AudiobookStatus"" <> 0
                              AND p2.""AudiobookBooksToMonitor"" IS NOT NULL
                              AND p2.""AudiobookBooksToMonitor"" <> ''
                            ORDER BY p2.""Id"" DESC
                            LIMIT 1
                        )
                    ),

                    ""EbookMonitorExisting"" = COALESCE(
                        p.""EbookMonitorExisting"",
                        (
                            SELECT p2.""EbookMonitorExisting""
                            FROM ""PendingAuthorImport"" p2
                            WHERE p2.""ProviderId"" = p.""ProviderId""
                              AND p2.""OverallStatus"" IN (1, 2, 3)
                              AND p2.""EbookStatus"" <> 0
                              AND p2.""EbookMonitorExisting"" IS NOT NULL
                            ORDER BY p2.""Id"" DESC
                            LIMIT 1
                        )
                    ),
                    ""EbookMonitorFuture"" = COALESCE(
                        p.""EbookMonitorFuture"",
                        (
                            SELECT p2.""EbookMonitorFuture""
                            FROM ""PendingAuthorImport"" p2
                            WHERE p2.""ProviderId"" = p.""ProviderId""
                              AND p2.""OverallStatus"" IN (1, 2, 3)
                              AND p2.""EbookStatus"" <> 0
                              AND p2.""EbookMonitorFuture"" IS NOT NULL
                            ORDER BY p2.""Id"" DESC
                            LIMIT 1
                        )
                    ),
                    ""EbookQualityProfileId"" = COALESCE(
                        p.""EbookQualityProfileId"",
                        (
                            SELECT p2.""EbookQualityProfileId""
                            FROM ""PendingAuthorImport"" p2
                            WHERE p2.""ProviderId"" = p.""ProviderId""
                              AND p2.""OverallStatus"" IN (1, 2, 3)
                              AND p2.""EbookStatus"" <> 0
                              AND p2.""EbookQualityProfileId"" IS NOT NULL
                            ORDER BY p2.""Id"" DESC
                            LIMIT 1
                        )
                    ),
                    ""EbookMetadataProfileId"" = COALESCE(
                        p.""EbookMetadataProfileId"",
                        (
                            SELECT p2.""EbookMetadataProfileId""
                            FROM ""PendingAuthorImport"" p2
                            WHERE p2.""ProviderId"" = p.""ProviderId""
                              AND p2.""OverallStatus"" IN (1, 2, 3)
                              AND p2.""EbookStatus"" <> 0
                              AND p2.""EbookMetadataProfileId"" IS NOT NULL
                            ORDER BY p2.""Id"" DESC
                            LIMIT 1
                        )
                    ),
                    ""EbookRootFolderPath"" = COALESCE(
                        NULLIF(p.""EbookRootFolderPath"", ''),
                        (
                            SELECT p2.""EbookRootFolderPath""
                            FROM ""PendingAuthorImport"" p2
                            WHERE p2.""ProviderId"" = p.""ProviderId""
                              AND p2.""OverallStatus"" IN (1, 2, 3)
                              AND p2.""EbookStatus"" <> 0
                              AND p2.""EbookRootFolderPath"" IS NOT NULL
                              AND p2.""EbookRootFolderPath"" <> ''
                            ORDER BY p2.""Id"" DESC
                            LIMIT 1
                        )
                    ),
                    ""EbookBooksToMonitor"" = COALESCE(
                        NULLIF(p.""EbookBooksToMonitor"", ''),
                        (
                            SELECT p2.""EbookBooksToMonitor""
                            FROM ""PendingAuthorImport"" p2
                            WHERE p2.""ProviderId"" = p.""ProviderId""
                              AND p2.""OverallStatus"" IN (1, 2, 3)
                              AND p2.""EbookStatus"" <> 0
                              AND p2.""EbookBooksToMonitor"" IS NOT NULL
                              AND p2.""EbookBooksToMonitor"" <> ''
                            ORDER BY p2.""Id"" DESC
                            LIMIT 1
                        )
                    ),

                    ""Tags"" = COALESCE(
                        NULLIF(p.""Tags"", ''),
                        (
                            SELECT p2.""Tags""
                            FROM ""PendingAuthorImport"" p2
                            WHERE p2.""ProviderId"" = p.""ProviderId""
                              AND p2.""OverallStatus"" IN (1, 2, 3)
                              AND p2.""Tags"" IS NOT NULL
                              AND p2.""Tags"" <> ''
                            ORDER BY p2.""Id"" DESC
                            LIMIT 1
                        )
                    ),
                    ""SearchForMissingBooks"" = EXISTS (
                        SELECT 1
                        FROM ""PendingAuthorImport"" p2
                        WHERE p2.""ProviderId"" = p.""ProviderId""
                          AND p2.""OverallStatus"" IN (1, 2, 3)
                          AND p2.""SearchForMissingBooks"" = TRUE
                    ),
                    ""LockedBy"" = NULL,
                    ""LockedAt"" = NULL,
                    ""LeaseExpiresAt"" = NULL,
                    ""NextAttemptAt"" = (
                        SELECT MIN(p2.""NextAttemptAt"")
                        FROM ""PendingAuthorImport"" p2
                        WHERE p2.""ProviderId"" = p.""ProviderId""
                          AND p2.""OverallStatus"" IN (1, 2, 3)
                    ),
                    ""OverallStatus"" = 3
                WHERE p.""Id"" IN (
                    SELECT MAX(p2.""Id"")
                    FROM ""PendingAuthorImport"" p2
                    WHERE p2.""OverallStatus"" IN (1, 2, 3)
                    GROUP BY p2.""ProviderId""
                    HAVING COUNT(*) > 1
                );

                WITH active_groups AS (
                    SELECT ""ProviderId"", MAX(""Id"") AS keep_id
                    FROM ""PendingAuthorImport""
                    WHERE ""OverallStatus"" IN (1, 2, 3)
                    GROUP BY ""ProviderId""
                    HAVING COUNT(*) > 1
                ),
                rows_to_fail AS (
                    SELECT p.""Id""
                    FROM ""PendingAuthorImport"" p
                    INNER JOIN active_groups g ON g.""ProviderId"" = p.""ProviderId""
                    WHERE p.""OverallStatus"" IN (1, 2, 3)
                      AND p.""Id"" <> g.keep_id
                )
                UPDATE ""PendingAuthorImport""
                SET ""OverallStatus"" = 6,
                    ""AudiobookStatus"" = CASE WHEN ""AudiobookStatus"" <> 0 THEN 6 ELSE 0 END,
                    ""EbookStatus"" = CASE WHEN ""EbookStatus"" <> 0 THEN 6 ELSE 0 END,
                    ""LastError"" = COALESCE(NULLIF(""LastError"", ''), 'Deduped duplicate active PendingAuthorImport during DB migration'),
                    ""UpdatedAt"" = CURRENT_TIMESTAMP,
                    ""LockedBy"" = NULL,
                    ""LockedAt"" = NULL,
                    ""LeaseExpiresAt"" = NULL
                WHERE ""Id"" IN (SELECT ""Id"" FROM rows_to_fail);

                CREATE UNIQUE INDEX IF NOT EXISTS ""UX_PendingAuthorImport_Active""
                    ON ""PendingAuthorImport""(""ProviderId"")
                    WHERE ""OverallStatus"" IN (1, 2, 3);
            ");
        }
    }
}
