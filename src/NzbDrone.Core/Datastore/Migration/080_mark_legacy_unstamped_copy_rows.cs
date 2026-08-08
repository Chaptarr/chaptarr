using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(80)]
    public class mark_legacy_unstamped_copy_rows : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("Books").Exists() || !Schema.Table("Books").Column("UnitKeyHash").Exists())
            {
                return;
            }

            IfDatabase("sqlite").Execute.Sql(SqliteSql);
            IfPostgres().Execute.Sql(PostgresSql);
        }

        private const string SqliteSql = @"
                WITH ranked AS (
                    SELECT b.""Id"",
                           COUNT(*) OVER (
                               PARTITION BY b.""AuthorId"", b.""MediaType"", b.""BaseBookId""
                           ) AS group_count,
                           ROW_NUMBER() OVER (
                               PARTITION BY b.""AuthorId"", b.""MediaType"", b.""BaseBookId""
                               ORDER BY
                                   CASE
                                       WHEN (b.""MediaType"" = 0 AND b.""AudiobookMonitored"" = 1)
                                         OR (b.""MediaType"" = 1 AND b.""EbookMonitored"" = 1)
                                       THEN 0 ELSE 1
                                   END,
                                   CASE
                                       WHEN EXISTS (
                                           SELECT 1
                                           FROM ""Editions"" e
                                           JOIN ""BookFiles"" f ON f.""EditionId"" = e.""Id""
                                           WHERE e.""BookId"" = b.""Id""
                                       )
                                       THEN 0 ELSE 1
                                   END,
                                   CASE WHEN b.""TitleSlug"" LIKE '%_copy_%' THEN 1 ELSE 0 END,
                                   b.""Id""
                           ) AS rn
                    FROM ""Books"" b
                    WHERE b.""BaseBookId"" IS NOT NULL
                      AND TRIM(b.""BaseBookId"") <> ''
                      AND (b.""UnitKeyHash"" IS NULL OR TRIM(b.""UnitKeyHash"") = '')
                      AND b.""WantedNarratorId"" IS NULL
                )
                UPDATE ""Books""
                SET ""UnitKeyHash"" = 'legacy-copy:' || ""Id""
                WHERE ""Id"" IN (
                    SELECT ""Id""
                    FROM ranked
                    WHERE group_count > 1 AND rn > 1
                );";

        private const string PostgresSql = @"
                WITH ranked AS (
                    SELECT b.""Id"",
                           COUNT(*) OVER (
                               PARTITION BY b.""AuthorId"", b.""MediaType"", b.""BaseBookId""
                           ) AS group_count,
                           ROW_NUMBER() OVER (
                               PARTITION BY b.""AuthorId"", b.""MediaType"", b.""BaseBookId""
                               ORDER BY
                                   CASE
                                       WHEN (b.""MediaType"" = 0 AND b.""AudiobookMonitored"" = TRUE)
                                         OR (b.""MediaType"" = 1 AND b.""EbookMonitored"" = TRUE)
                                       THEN 0 ELSE 1
                                   END,
                                   CASE
                                       WHEN EXISTS (
                                           SELECT 1
                                           FROM ""Editions"" e
                                           JOIN ""BookFiles"" f ON f.""EditionId"" = e.""Id""
                                           WHERE e.""BookId"" = b.""Id""
                                       )
                                       THEN 0 ELSE 1
                                   END,
                                   CASE WHEN b.""TitleSlug"" LIKE '%_copy_%' THEN 1 ELSE 0 END,
                                   b.""Id""
                           ) AS rn
                    FROM ""Books"" b
                    WHERE b.""BaseBookId"" IS NOT NULL
                      AND btrim(b.""BaseBookId"") <> ''
                      AND (b.""UnitKeyHash"" IS NULL OR btrim(b.""UnitKeyHash"") = '')
                      AND b.""WantedNarratorId"" IS NULL
                )
                UPDATE ""Books"" b
                SET ""UnitKeyHash"" = 'legacy-copy:' || b.""Id""::text
                FROM ranked r
                WHERE b.""Id"" = r.""Id""
                  AND r.group_count > 1
                  AND r.rn > 1;";
    }
}
