using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(51)]
    public class postgres_catchup_indexes : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            // Catch-up migration for PostgreSQL installs that previously skipped Postgres-only blocks guarded by
            // IfDatabase("postgres") due to FluentMigrator processor id/alias changes (e.g. Postgres15_0Processor).
            //
            // This migration is intentionally idempotent: it ensures the critical indexes exist so imports and
            // matching behave the same as SQLite.

            // BookFiles.Path must be unique for ON CONFLICT("Path") DO NOTHING (used by staging/rescans).
            IfPostgres().Execute.Sql(@"
                -- Merge any duplicate BookFiles.Path rows by re-pointing dependent references (MetadataFiles/ExtraFiles)
                -- to a single canonical row, then deleting the duplicates before creating the unique index.
                DROP TABLE IF EXISTS bookfiles_dedupe_map;

                CREATE TEMP TABLE bookfiles_dedupe_map AS
                WITH ranked AS (
                    SELECT ""Id"",
                           FIRST_VALUE(""Id"") OVER (PARTITION BY ""Path"" ORDER BY ""EditionId"" DESC, ""Id"" ASC) AS canonical_id,
                           ROW_NUMBER() OVER (PARTITION BY ""Path"" ORDER BY ""EditionId"" DESC, ""Id"" ASC) AS rn
                    FROM ""BookFiles""
                )
                SELECT ""Id"" AS dup_id, canonical_id
                FROM ranked
                WHERE rn > 1;

                UPDATE ""MetadataFiles""
                SET ""BookFileId"" = (SELECT canonical_id FROM bookfiles_dedupe_map WHERE dup_id = ""BookFileId"")
                WHERE ""BookFileId"" IN (SELECT dup_id FROM bookfiles_dedupe_map);

                UPDATE ""ExtraFiles""
                SET ""BookFileId"" = (SELECT canonical_id FROM bookfiles_dedupe_map WHERE dup_id = ""BookFileId"")
                WHERE ""BookFileId"" IN (SELECT dup_id FROM bookfiles_dedupe_map);

                DELETE FROM ""BookFiles""
                WHERE ""Id"" IN (SELECT dup_id FROM bookfiles_dedupe_map);

                DROP TABLE IF EXISTS bookfiles_dedupe_map;

                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_BookFiles_Path_Unique"" ON ""BookFiles"" (""Path"");
            ");

            // Core FTS indexes used by search/matching on PostgreSQL.
            IfPostgres().Execute.Sql(@"
                CREATE INDEX IF NOT EXISTS idx_authors_fts
                ON ""Authors""
                USING GIN (
                    to_tsvector('simple', COALESCE(""Name"", '') || ' ' || COALESCE(""CleanName"", '') || ' ' || COALESCE(""TitleSlug"", ''))
                );

                CREATE INDEX IF NOT EXISTS idx_editions_fts
                ON ""Editions""
                USING GIN (
                    to_tsvector('simple', COALESCE(""Title"", '') || ' ' || COALESCE(""TitleSlug"", ''))
                );

                CREATE INDEX IF NOT EXISTS idx_books_series_fts
                ON ""Books""
                USING GIN (
                    to_tsvector('simple', COALESCE(""SeriesName"", ''))
                );

                CREATE INDEX IF NOT EXISTS idx_editions_matching_title_fts
                ON ""Editions""
                USING GIN (
                    to_tsvector('simple', COALESCE(""MatchingTitle"", ''))
                );

                -- Keep GIN index aligned with matching query (include NarratorNames for multi-narrator audiobooks).
                DROP INDEX IF EXISTS idx_editions_matching_fts;
                CREATE INDEX IF NOT EXISTS idx_editions_matching_fts
                ON ""Editions""
                USING GIN (
                    to_tsvector('simple',
                        COALESCE(""MatchingTitle"", '') || ' ' ||
                        COALESCE(""Subtitle"", '') || ' ' ||
                        COALESCE(""NarratorNames"", '') || ' ' ||
                        COALESCE(""Narrator"", '')
                    )
                );

                CREATE INDEX IF NOT EXISTS idx_editions_ebook_fts
                ON ""Editions""
                USING GIN (
                    to_tsvector('simple',
                        COALESCE(""MatchingTitle"", '') || ' ' ||
                        COALESCE(""Publisher"", '')
                    )
                );
            ");

            // Case-insensitive identifier lookups.
            IfPostgres().Execute.Sql(@"
                CREATE INDEX IF NOT EXISTS IX_Editions_Asin_Upper
                ON ""Editions"" (UPPER(""Asin""))
                WHERE ""Asin"" IS NOT NULL AND LENGTH(""Asin"") > 0;

                CREATE INDEX IF NOT EXISTS IX_Editions_AudibleASIN_Upper
                ON ""Editions"" (UPPER(""AudibleASIN""))
                WHERE ""AudibleASIN"" IS NOT NULL AND LENGTH(""AudibleASIN"") > 0;
            ");
        }
    }
}
