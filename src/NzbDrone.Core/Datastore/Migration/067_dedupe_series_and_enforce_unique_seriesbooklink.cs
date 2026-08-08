using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(67)]
    public class dedupe_series_and_enforce_unique_seriesbooklink : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("Series").Exists() || !Schema.Table("SeriesBookLink").Exists())
            {
                return;
            }

            // 1) Remove invalid Series rows (original instance type only) that lack a Goodreads series ID.
            //    Goodreads is the canonical series identity; Amazon-only series are not supported.
            IfDatabase("sqlite").Execute.Sql(@"
                DELETE FROM ""SeriesBookLink""
                WHERE ""SeriesId"" IN (
                    SELECT ""Id"" FROM ""Series""
                    WHERE LOWER(COALESCE(""InstanceType"", 'original')) = 'original'
                      AND (""GoodreadsSeriesId"" IS NULL OR TRIM(""GoodreadsSeriesId"") = '')
                );

                DELETE FROM ""Series""
                WHERE LOWER(COALESCE(""InstanceType"", 'original')) = 'original'
                  AND (""GoodreadsSeriesId"" IS NULL OR TRIM(""GoodreadsSeriesId"") = '');
            ");

            IfPostgres().Execute.Sql(@"
                DELETE FROM ""SeriesBookLink"" sbl
                USING ""Series"" s
                WHERE sbl.""SeriesId"" = s.""Id""
                  AND LOWER(COALESCE(s.""InstanceType"", 'original')) = 'original'
                  AND (s.""GoodreadsSeriesId"" IS NULL OR btrim(s.""GoodreadsSeriesId"") = '');

                DELETE FROM ""Series""
                WHERE LOWER(COALESCE(""InstanceType"", 'original')) = 'original'
                  AND (""GoodreadsSeriesId"" IS NULL OR btrim(""GoodreadsSeriesId"") = '');
            ");

            // 2) Deduplicate duplicate Series rows (original instance type only) by GoodreadsSeriesId + media type.
            //    Keep the lowest Id row as canonical, re-point SeriesBookLink rows, then delete duplicates.
            IfDatabase("sqlite").Execute.Sql(@"
                DROP TABLE IF EXISTS series_dedupe_map;

                CREATE TEMP TABLE series_dedupe_map AS
                SELECT s.""Id"" AS dup_id,
                       g.canonical_id AS canonical_id
                FROM ""Series"" s
                JOIN (
                    SELECT ""MediaType"" AS media_type,
                           ""GoodreadsSeriesId"" AS goodreads_id,
                           MIN(""Id"") AS canonical_id,
                           COUNT(*) AS cnt
                    FROM ""Series""
                    WHERE LOWER(COALESCE(""InstanceType"", 'original')) = 'original'
                      AND ""GoodreadsSeriesId"" IS NOT NULL
                      AND TRIM(""GoodreadsSeriesId"") <> ''
                    GROUP BY ""MediaType"", ""GoodreadsSeriesId""
                    HAVING COUNT(*) > 1
                ) g
                ON s.""MediaType"" = g.media_type
               AND s.""GoodreadsSeriesId"" = g.goodreads_id
                WHERE s.""Id"" <> g.canonical_id;

                -- If a unique constraint already exists on (BookId, SeriesId, SeriesInstanceType),
                -- repointing duplicate series links can violate it. Remove would-be duplicate links first.
                DELETE FROM ""SeriesBookLink""
                WHERE ""Id"" IN (
                    SELECT sbl.""Id""
                    FROM ""SeriesBookLink"" sbl
                    JOIN series_dedupe_map m ON sbl.""SeriesId"" = m.dup_id
                    JOIN ""SeriesBookLink"" existing
                      ON existing.""SeriesId"" = m.canonical_id
                     AND existing.""BookId"" = sbl.""BookId""
                     AND existing.""SeriesInstanceType"" IS sbl.""SeriesInstanceType""
                );

                DELETE FROM ""SeriesBookLink""
                WHERE ""Id"" IN (
                    SELECT sbl1.""Id""
                    FROM ""SeriesBookLink"" sbl1
                    JOIN series_dedupe_map m1 ON sbl1.""SeriesId"" = m1.dup_id
                    JOIN ""SeriesBookLink"" sbl2
                      ON sbl2.""BookId"" = sbl1.""BookId""
                     AND sbl2.""SeriesInstanceType"" IS sbl1.""SeriesInstanceType""
                    JOIN series_dedupe_map m2 ON sbl2.""SeriesId"" = m2.dup_id
                    WHERE m1.canonical_id = m2.canonical_id
                      AND sbl1.""Id"" > sbl2.""Id""
                );

                UPDATE ""SeriesBookLink""
                SET ""SeriesId"" = (SELECT canonical_id FROM series_dedupe_map WHERE dup_id = ""SeriesId"")
                WHERE ""SeriesId"" IN (SELECT dup_id FROM series_dedupe_map);

                DELETE FROM ""Series"" WHERE ""Id"" IN (SELECT dup_id FROM series_dedupe_map);

                DROP TABLE IF EXISTS series_dedupe_map;
            ");

            IfPostgres().Execute.Sql(@"
                DROP TABLE IF EXISTS series_dedupe_map;

                CREATE TEMP TABLE series_dedupe_map AS
                SELECT s.""Id"" AS dup_id,
                       g.canonical_id AS canonical_id
                FROM ""Series"" s
                JOIN (
                    SELECT ""MediaType"" AS media_type,
                           ""GoodreadsSeriesId"" AS goodreads_id,
                           MIN(""Id"") AS canonical_id,
                           COUNT(*) AS cnt
                    FROM ""Series""
                    WHERE LOWER(COALESCE(""InstanceType"", 'original')) = 'original'
                      AND ""GoodreadsSeriesId"" IS NOT NULL
                      AND btrim(""GoodreadsSeriesId"") <> ''
                    GROUP BY ""MediaType"", ""GoodreadsSeriesId""
                    HAVING COUNT(*) > 1
                ) g
                ON s.""MediaType"" = g.media_type
               AND s.""GoodreadsSeriesId"" = g.goodreads_id
                WHERE s.""Id"" <> g.canonical_id;

                -- If a unique constraint already exists on (BookId, SeriesId, SeriesInstanceType),
                -- repointing duplicate series links can violate it. Remove would-be duplicate links first.
                DELETE FROM ""SeriesBookLink"" sbl
                USING series_dedupe_map m, ""SeriesBookLink"" existing
                WHERE sbl.""SeriesId"" = m.dup_id
                  AND existing.""SeriesId"" = m.canonical_id
                  AND existing.""BookId"" = sbl.""BookId""
                  AND existing.""SeriesInstanceType"" IS NOT DISTINCT FROM sbl.""SeriesInstanceType"";

                WITH repoint AS (
                    SELECT sbl.""Id"" AS id,
                           m.canonical_id AS canonical_id,
                           sbl.""BookId"" AS book_id,
                           sbl.""SeriesInstanceType"" AS instance_type,
                           ROW_NUMBER() OVER (PARTITION BY m.canonical_id, sbl.""BookId"", sbl.""SeriesInstanceType"" ORDER BY sbl.""Id"") AS rn
                    FROM ""SeriesBookLink"" sbl
                    JOIN series_dedupe_map m ON sbl.""SeriesId"" = m.dup_id
                )
                DELETE FROM ""SeriesBookLink"" sbl
                USING repoint r
                WHERE sbl.""Id"" = r.id
                  AND r.rn > 1;

                UPDATE ""SeriesBookLink"" sbl
                SET ""SeriesId"" = m.canonical_id
                FROM series_dedupe_map m
                WHERE sbl.""SeriesId"" = m.dup_id;

                DELETE FROM ""Series"" WHERE ""Id"" IN (SELECT dup_id FROM series_dedupe_map);

                DROP TABLE IF EXISTS series_dedupe_map;
            ");

            // 3) Deduplicate SeriesBookLink rows and enforce uniqueness going forward.
            //    Keep the lowest Id row per (BookId, SeriesId, SeriesInstanceType).
            IfDatabase("sqlite").Execute.Sql(@"
                DROP TABLE IF EXISTS seriesbooklink_dedupe_map;

                CREATE TEMP TABLE seriesbooklink_dedupe_map AS
                SELECT sbl.""Id"" AS dup_id,
                       g.canonical_id AS canonical_id
                FROM ""SeriesBookLink"" sbl
                JOIN (
                    SELECT ""BookId"" AS book_id,
                           ""SeriesId"" AS series_id,
                           ""SeriesInstanceType"" AS instance_type,
                           MIN(""Id"") AS canonical_id,
                           COUNT(*) AS cnt
                    FROM ""SeriesBookLink""
                    GROUP BY ""BookId"", ""SeriesId"", ""SeriesInstanceType""
                    HAVING COUNT(*) > 1
                ) g
                ON sbl.""BookId"" = g.book_id
               AND sbl.""SeriesId"" = g.series_id
               AND sbl.""SeriesInstanceType"" IS g.instance_type
                WHERE sbl.""Id"" <> g.canonical_id;

                DELETE FROM ""SeriesBookLink"" WHERE ""Id"" IN (SELECT dup_id FROM seriesbooklink_dedupe_map);

                DROP TABLE IF EXISTS seriesbooklink_dedupe_map;

                DROP INDEX IF EXISTS IX_SeriesBookLink_BookId_SeriesId_InstanceType;
                CREATE UNIQUE INDEX IF NOT EXISTS IX_SeriesBookLink_BookId_SeriesId_InstanceType
                    ON SeriesBookLink(BookId, SeriesId, SeriesInstanceType);
            ");

            IfPostgres().Execute.Sql(@"
                DROP TABLE IF EXISTS seriesbooklink_dedupe_map;

                CREATE TEMP TABLE seriesbooklink_dedupe_map AS
                SELECT sbl.""Id"" AS dup_id,
                       g.canonical_id AS canonical_id
                FROM ""SeriesBookLink"" sbl
                JOIN (
                    SELECT ""BookId"" AS book_id,
                           ""SeriesId"" AS series_id,
                           ""SeriesInstanceType"" AS instance_type,
                           MIN(""Id"") AS canonical_id,
                           COUNT(*) AS cnt
                    FROM ""SeriesBookLink""
                    GROUP BY ""BookId"", ""SeriesId"", ""SeriesInstanceType""
                    HAVING COUNT(*) > 1
                ) g
                ON sbl.""BookId"" = g.book_id
               AND sbl.""SeriesId"" = g.series_id
               AND sbl.""SeriesInstanceType"" IS NOT DISTINCT FROM g.instance_type
                WHERE sbl.""Id"" <> g.canonical_id;

                DELETE FROM ""SeriesBookLink"" WHERE ""Id"" IN (SELECT dup_id FROM seriesbooklink_dedupe_map);

                DROP TABLE IF EXISTS seriesbooklink_dedupe_map;

                DROP INDEX IF EXISTS ""IX_SeriesBookLink_BookId_SeriesId_InstanceType"";
                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_SeriesBookLink_BookId_SeriesId_InstanceType""
                    ON ""SeriesBookLink"" (""BookId"", ""SeriesId"", ""SeriesInstanceType"");
            ");
        }
    }
}
