using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    // Data repair for legacy installs:
    // - Normalize unprefixed provider IDs on Books to canonical {prefix}:{id}
    // - Backfill BaseBookId to a stable provider-backed key
    // - Unmonitor phantom "missing" duplicates when a sibling copy already has files
    [Migration(69)]
    public class repair_book_provider_ids_and_prune_phantom_missing : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("Books").Exists() || !Schema.Table("Editions").Exists())
            {
                return;
            }

            // 1) Normalize obvious unprefixed provider IDs on Books.
            // Only add a prefix when the value has no ":" at all to avoid mangling already-prefixed IDs.
            IfDatabase("sqlite").Execute.Sql(@"
                UPDATE ""Books"" SET ""HardcoverBookId"" = 'hc:' || TRIM(""HardcoverBookId"")
                WHERE ""HardcoverBookId"" IS NOT NULL AND TRIM(""HardcoverBookId"") <> '' AND ""HardcoverBookId"" NOT LIKE '%:%';

                UPDATE ""Books"" SET ""GoodreadsBookId"" = 'gr:' || TRIM(""GoodreadsBookId"")
                WHERE ""GoodreadsBookId"" IS NOT NULL AND TRIM(""GoodreadsBookId"") <> '' AND ""GoodreadsBookId"" NOT LIKE '%:%';

                UPDATE ""Books"" SET ""GoodreadsWorkId"" = 'gr:' || TRIM(""GoodreadsWorkId"")
                WHERE ""GoodreadsWorkId"" IS NOT NULL AND TRIM(""GoodreadsWorkId"") <> '' AND ""GoodreadsWorkId"" NOT LIKE '%:%';

                UPDATE ""Books"" SET ""OpenLibraryEditionId"" = 'ol:' || TRIM(""OpenLibraryEditionId"")
                WHERE ""OpenLibraryEditionId"" IS NOT NULL AND TRIM(""OpenLibraryEditionId"") <> '' AND ""OpenLibraryEditionId"" NOT LIKE '%:%';

                UPDATE ""Books"" SET ""OpenLibraryWorkId"" = 'ol:' || TRIM(""OpenLibraryWorkId"")
                WHERE ""OpenLibraryWorkId"" IS NOT NULL AND TRIM(""OpenLibraryWorkId"") <> '' AND ""OpenLibraryWorkId"" NOT LIKE '%:%';

                UPDATE ""Books"" SET ""GoogleBooksId"" = 'gb:' || TRIM(""GoogleBooksId"")
                WHERE ""GoogleBooksId"" IS NOT NULL AND TRIM(""GoogleBooksId"") <> '' AND ""GoogleBooksId"" NOT LIKE '%:%';

                -- BaseBookId must be provider-backed. If it lacks a prefix, treat it as invalid so we can backfill it deterministically below.
                UPDATE ""Books"" SET ""BaseBookId"" = NULL
                WHERE ""BaseBookId"" IS NOT NULL AND TRIM(""BaseBookId"") <> '' AND ""BaseBookId"" NOT LIKE '%:%';
            ");

            IfPostgres().Execute.Sql(@"
                UPDATE ""Books"" SET ""HardcoverBookId"" = 'hc:' || btrim(""HardcoverBookId"")
                WHERE ""HardcoverBookId"" IS NOT NULL AND btrim(""HardcoverBookId"") <> '' AND ""HardcoverBookId"" NOT LIKE '%:%';

                UPDATE ""Books"" SET ""GoodreadsBookId"" = 'gr:' || btrim(""GoodreadsBookId"")
                WHERE ""GoodreadsBookId"" IS NOT NULL AND btrim(""GoodreadsBookId"") <> '' AND ""GoodreadsBookId"" NOT LIKE '%:%';

                UPDATE ""Books"" SET ""GoodreadsWorkId"" = 'gr:' || btrim(""GoodreadsWorkId"")
                WHERE ""GoodreadsWorkId"" IS NOT NULL AND btrim(""GoodreadsWorkId"") <> '' AND ""GoodreadsWorkId"" NOT LIKE '%:%';

                UPDATE ""Books"" SET ""OpenLibraryEditionId"" = 'ol:' || btrim(""OpenLibraryEditionId"")
                WHERE ""OpenLibraryEditionId"" IS NOT NULL AND btrim(""OpenLibraryEditionId"") <> '' AND ""OpenLibraryEditionId"" NOT LIKE '%:%';

                UPDATE ""Books"" SET ""OpenLibraryWorkId"" = 'ol:' || btrim(""OpenLibraryWorkId"")
                WHERE ""OpenLibraryWorkId"" IS NOT NULL AND btrim(""OpenLibraryWorkId"") <> '' AND ""OpenLibraryWorkId"" NOT LIKE '%:%';

                UPDATE ""Books"" SET ""GoogleBooksId"" = 'gb:' || btrim(""GoogleBooksId"")
                WHERE ""GoogleBooksId"" IS NOT NULL AND btrim(""GoogleBooksId"") <> '' AND ""GoogleBooksId"" NOT LIKE '%:%';

                -- BaseBookId must be provider-backed. If it lacks a prefix, treat it as invalid so we can backfill it deterministically below.
                UPDATE ""Books"" SET ""BaseBookId"" = NULL
                WHERE ""BaseBookId"" IS NOT NULL AND btrim(""BaseBookId"") <> '' AND ""BaseBookId"" NOT LIKE '%:%';
            ");

            // 2) Backfill BaseBookId for any book that doesn't have a provider-backed value.
            // Prefer work-level IDs first (Hardcover/Goodreads/OpenLibrary/GoogleBooks), then book-level IDs.
            IfDatabase("sqlite").Execute.Sql(@"
                UPDATE ""Books""
                SET ""BaseBookId"" = COALESCE(
                    NULLIF(TRIM(""HardcoverBookId""), ''),
                    NULLIF(TRIM(""GoodreadsWorkId""), ''),
                    NULLIF(TRIM(""OpenLibraryWorkId""), ''),
                    NULLIF(TRIM(""GoogleBooksId""), ''),
                    NULLIF(TRIM(""GoodreadsBookId""), ''),
                    NULLIF(TRIM(""OpenLibraryEditionId""), ''),
                    CASE
                        WHEN ""ASIN"" IS NOT NULL AND TRIM(""ASIN"") <> '' THEN 'az:' || UPPER(TRIM(""ASIN""))
                        WHEN ""AudibleASIN"" IS NOT NULL AND TRIM(""AudibleASIN"") <> '' THEN 'az:' || UPPER(TRIM(""AudibleASIN""))
                        ELSE NULL
                    END
                )
                WHERE ""BaseBookId"" IS NULL OR TRIM(""BaseBookId"") = '';
            ");

            IfPostgres().Execute.Sql(@"
                UPDATE ""Books""
                SET ""BaseBookId"" = COALESCE(
                    NULLIF(btrim(""HardcoverBookId""), ''),
                    NULLIF(btrim(""GoodreadsWorkId""), ''),
                    NULLIF(btrim(""OpenLibraryWorkId""), ''),
                    NULLIF(btrim(""GoogleBooksId""), ''),
                    NULLIF(btrim(""GoodreadsBookId""), ''),
                    NULLIF(btrim(""OpenLibraryEditionId""), ''),
                    CASE
                        WHEN ""ASIN"" IS NOT NULL AND btrim(""ASIN"") <> '' THEN 'az:' || upper(btrim(""ASIN""))
                        WHEN ""AudibleASIN"" IS NOT NULL AND btrim(""AudibleASIN"") <> '' THEN 'az:' || upper(btrim(""AudibleASIN""))
                        ELSE NULL
                    END
                )
                WHERE ""BaseBookId"" IS NULL OR btrim(""BaseBookId"") = '';
            ");

            // 2.5) Narrator-wanted copies are user intent and must not be pruned on refresh.
            // Historically this was protected via Books.InstanceType = 'wanted'; prefer Readarr semantics:
            // stamp AddOptions.addType = manual so refresh ShouldDelete() preserves it.
            if (Schema.Table("Books").Column("AddOptions").Exists())
            {
                IfDatabase("sqlite").Execute.Sql(@"
                    UPDATE ""Books""
                    SET ""AddOptions"" = json_set(
                        CASE
                            WHEN ""AddOptions"" IS NOT NULL AND json_valid(""AddOptions"") THEN ""AddOptions""
                            ELSE '{}'
                        END,
                        '$.addType', 'manual',
                        '$.searchForNewBook', json('false')
                    )
                    WHERE ""WantedNarratorId"" IS NOT NULL;
                ");

                IfPostgres().Execute.Sql(@"
                    UPDATE ""Books""
                    SET ""AddOptions"" = (
                        jsonb_set(
                            jsonb_set(
                                COALESCE(NULLIF(btrim(""AddOptions""), '')::jsonb, '{}'::jsonb),
                                '{addType}', '""manual""'::jsonb, true
                            ),
                            '{searchForNewBook}', 'false'::jsonb, true
                        )
                    )::text
                    WHERE ""WantedNarratorId"" IS NOT NULL;
                ");
            }

            // 3) Unmonitor phantom duplicates (no files) when another copy in the same identity group already has files.
            // This prevents perpetual "Missing" entries for already-downloaded books after provider ID rematching.
            if (!Schema.Table("BookFiles").Exists())
            {
                return;
            }

            IfDatabase("sqlite").Execute.Sql(@"
                WITH books_with_files AS (
                    SELECT DISTINCT b.""Id"" AS book_id,
                                    b.""AuthorId"" AS author_id,
                                    b.""MediaType"" AS media_type,
                                    b.""BaseBookId"" AS base_book_id
                    FROM ""Books"" b
                    JOIN ""Editions"" e ON e.""BookId"" = b.""Id""
                    JOIN ""BookFiles"" f ON f.""EditionId"" = e.""Id""
                    WHERE b.""BaseBookId"" IS NOT NULL AND TRIM(b.""BaseBookId"") <> ''
                      AND b.""WantedNarratorId"" IS NULL
                ),
                phantom AS (
                    SELECT b.""Id"" AS book_id
                    FROM ""Books"" b
                    JOIN books_with_files g
                      ON g.author_id = b.""AuthorId""
                     AND g.media_type = b.""MediaType""
                     AND g.base_book_id = b.""BaseBookId""
                    LEFT JOIN books_with_files self ON self.book_id = b.""Id""
                    WHERE self.book_id IS NULL
                      AND b.""BaseBookId"" IS NOT NULL AND TRIM(b.""BaseBookId"") <> ''
                      AND b.""WantedNarratorId"" IS NULL
                      AND NOT EXISTS (SELECT 1 FROM ""Editions"" e WHERE e.""BookId"" = b.""Id"" AND e.""ManualAdd"" = 1)
                )
                UPDATE ""Books""
                SET ""AudiobookMonitored"" = 0,
                    ""EbookMonitored"" = 0
                WHERE ""Id"" IN (SELECT book_id FROM phantom);
            ");

            IfPostgres().Execute.Sql(@"
                WITH books_with_files AS (
                    SELECT DISTINCT b.""Id"" AS book_id,
                                    b.""AuthorId"" AS author_id,
                                    b.""MediaType"" AS media_type,
                                    b.""BaseBookId"" AS base_book_id
                    FROM ""Books"" b
                    JOIN ""Editions"" e ON e.""BookId"" = b.""Id""
                    JOIN ""BookFiles"" f ON f.""EditionId"" = e.""Id""
                    WHERE b.""BaseBookId"" IS NOT NULL AND btrim(b.""BaseBookId"") <> ''
                      AND b.""WantedNarratorId"" IS NULL
                ),
                phantom AS (
                    SELECT b.""Id"" AS book_id
                    FROM ""Books"" b
                    JOIN books_with_files g
                      ON g.author_id = b.""AuthorId""
                     AND g.media_type = b.""MediaType""
                     AND g.base_book_id = b.""BaseBookId""
                    LEFT JOIN books_with_files self ON self.book_id = b.""Id""
                    WHERE self.book_id IS NULL
                      AND b.""BaseBookId"" IS NOT NULL AND btrim(b.""BaseBookId"") <> ''
                      AND b.""WantedNarratorId"" IS NULL
                      AND NOT EXISTS (SELECT 1 FROM ""Editions"" e WHERE e.""BookId"" = b.""Id"" AND e.""ManualAdd"" = true)
                )
                UPDATE ""Books"" b
                SET ""AudiobookMonitored"" = false,
                    ""EbookMonitored"" = false
                FROM phantom p
                WHERE b.""Id"" = p.book_id;
            ");

            // 4) Drop legacy Books.InstanceType column (superseded by WantedNarratorId and AddOptions.AddType).
            if (Schema.Table("Books").Column("InstanceType").Exists())
            {
                IfDatabase("sqlite").Execute.Sql(@"ALTER TABLE ""Books"" DROP COLUMN ""InstanceType"";");
                IfPostgres().Execute.Sql(@"ALTER TABLE ""Books"" DROP COLUMN IF EXISTS ""InstanceType"";");
            }
        }
    }
}
