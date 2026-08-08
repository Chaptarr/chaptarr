using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(90)]
    public class enforce_unique_original_series_identity : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("Series").Exists() || !Schema.Table("SeriesBookLink").Exists())
            {
                return;
            }

            // Chaptarr stores one original series row per media type. Narrator variants are allowed
            // to share the same provider id, but duplicate originals are never valid.
            IfDatabase("sqlite").Execute.Sql(@"
                DROP TABLE IF EXISTS series_original_dedupe_map;

                CREATE TEMP TABLE series_original_dedupe_map AS
                SELECT s.""Id"" AS dup_id,
                       g.canonical_id AS canonical_id
                FROM ""Series"" s
                JOIN (
                    SELECT ""MediaType"" AS media_type,
                           ""GoodreadsSeriesId"" AS goodreads_id,
                           MIN(""Id"") AS canonical_id,
                           COUNT(*) AS cnt
                    FROM ""Series""
                    WHERE ""GoodreadsSeriesId"" IS NOT NULL
                      AND TRIM(""GoodreadsSeriesId"") <> ''
                      AND ""PreferredNarratorId"" IS NULL
                      AND (""Narrator"" IS NULL OR TRIM(""Narrator"") = '')
                    GROUP BY ""MediaType"", ""GoodreadsSeriesId""
                    HAVING COUNT(*) > 1
                ) g
                ON s.""MediaType"" = g.media_type
               AND s.""GoodreadsSeriesId"" = g.goodreads_id
                WHERE s.""Id"" <> g.canonical_id
                  AND s.""PreferredNarratorId"" IS NULL
                  AND (s.""Narrator"" IS NULL OR TRIM(s.""Narrator"") = '');

                DELETE FROM ""SeriesBookLink""
                WHERE ""Id"" IN (
                    SELECT sbl.""Id""
                    FROM ""SeriesBookLink"" sbl
                    JOIN series_original_dedupe_map m ON sbl.""SeriesId"" = m.dup_id
                    JOIN ""SeriesBookLink"" existing
                      ON existing.""SeriesId"" = m.canonical_id
                     AND existing.""BookId"" = sbl.""BookId""
                     AND existing.""SeriesInstanceType"" IS sbl.""SeriesInstanceType""
                );

                DELETE FROM ""SeriesBookLink""
                WHERE ""Id"" IN (
                    SELECT sbl1.""Id""
                    FROM ""SeriesBookLink"" sbl1
                    JOIN series_original_dedupe_map m1 ON sbl1.""SeriesId"" = m1.dup_id
                    JOIN ""SeriesBookLink"" sbl2
                      ON sbl2.""BookId"" = sbl1.""BookId""
                     AND sbl2.""SeriesInstanceType"" IS sbl1.""SeriesInstanceType""
                    JOIN series_original_dedupe_map m2 ON sbl2.""SeriesId"" = m2.dup_id
                    WHERE m1.canonical_id = m2.canonical_id
                      AND sbl1.""Id"" > sbl2.""Id""
                );

                UPDATE ""SeriesBookLink""
                SET ""SeriesId"" = (SELECT canonical_id FROM series_original_dedupe_map WHERE dup_id = ""SeriesId"")
                WHERE ""SeriesId"" IN (SELECT dup_id FROM series_original_dedupe_map);

                UPDATE ""Books""
                SET ""SeriesId"" = (SELECT canonical_id FROM series_original_dedupe_map WHERE dup_id = ""SeriesId"")
                WHERE ""SeriesId"" IN (SELECT dup_id FROM series_original_dedupe_map);

                UPDATE ""Series""
                SET ""ParentSeriesId"" = (SELECT canonical_id FROM series_original_dedupe_map WHERE dup_id = ""ParentSeriesId"")
                WHERE ""ParentSeriesId"" IN (SELECT dup_id FROM series_original_dedupe_map);

                DELETE FROM ""Series"" WHERE ""Id"" IN (SELECT dup_id FROM series_original_dedupe_map);

                DROP TABLE IF EXISTS series_original_dedupe_map;

                CREATE UNIQUE INDEX IF NOT EXISTS UX_Series_MediaType_GoodreadsSeriesId_Original
                    ON ""Series"" (""MediaType"", ""GoodreadsSeriesId"")
                    WHERE ""GoodreadsSeriesId"" IS NOT NULL
                      AND TRIM(""GoodreadsSeriesId"") <> ''
                      AND ""PreferredNarratorId"" IS NULL
                      AND (""Narrator"" IS NULL OR TRIM(""Narrator"") = '');
            ");

            IfPostgres().Execute.Sql(@"
                DROP TABLE IF EXISTS series_original_dedupe_map;

                CREATE TEMP TABLE series_original_dedupe_map AS
                SELECT s.""Id"" AS dup_id,
                       g.canonical_id AS canonical_id
                FROM ""Series"" s
                JOIN (
                    SELECT ""MediaType"" AS media_type,
                           ""GoodreadsSeriesId"" AS goodreads_id,
                           MIN(""Id"") AS canonical_id,
                           COUNT(*) AS cnt
                    FROM ""Series""
                    WHERE ""GoodreadsSeriesId"" IS NOT NULL
                      AND btrim(""GoodreadsSeriesId"") <> ''
                      AND ""PreferredNarratorId"" IS NULL
                      AND (""Narrator"" IS NULL OR btrim(""Narrator"") = '')
                    GROUP BY ""MediaType"", ""GoodreadsSeriesId""
                    HAVING COUNT(*) > 1
                ) g
                ON s.""MediaType"" = g.media_type
               AND s.""GoodreadsSeriesId"" = g.goodreads_id
                WHERE s.""Id"" <> g.canonical_id
                  AND s.""PreferredNarratorId"" IS NULL
                  AND (s.""Narrator"" IS NULL OR btrim(s.""Narrator"") = '');

                DELETE FROM ""SeriesBookLink"" sbl
                USING series_original_dedupe_map m, ""SeriesBookLink"" existing
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
                    JOIN series_original_dedupe_map m ON sbl.""SeriesId"" = m.dup_id
                )
                DELETE FROM ""SeriesBookLink"" sbl
                USING repoint r
                WHERE sbl.""Id"" = r.id
                  AND r.rn > 1;

                UPDATE ""SeriesBookLink"" sbl
                SET ""SeriesId"" = m.canonical_id
                FROM series_original_dedupe_map m
                WHERE sbl.""SeriesId"" = m.dup_id;

                UPDATE ""Books"" b
                SET ""SeriesId"" = m.canonical_id
                FROM series_original_dedupe_map m
                WHERE b.""SeriesId"" = m.dup_id;

                UPDATE ""Series"" s
                SET ""ParentSeriesId"" = m.canonical_id
                FROM series_original_dedupe_map m
                WHERE s.""ParentSeriesId"" = m.dup_id;

                DELETE FROM ""Series"" WHERE ""Id"" IN (SELECT dup_id FROM series_original_dedupe_map);

                DROP TABLE IF EXISTS series_original_dedupe_map;

                CREATE UNIQUE INDEX IF NOT EXISTS ""UX_Series_MediaType_GoodreadsSeriesId_Original""
                    ON ""Series"" (""MediaType"", ""GoodreadsSeriesId"")
                    WHERE ""GoodreadsSeriesId"" IS NOT NULL
                      AND btrim(""GoodreadsSeriesId"") <> ''
                      AND ""PreferredNarratorId"" IS NULL
                      AND (""Narrator"" IS NULL OR btrim(""Narrator"") = '');
            ");
        }
    }
}
