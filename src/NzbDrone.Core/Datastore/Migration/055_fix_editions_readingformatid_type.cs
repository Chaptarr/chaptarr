using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(55)]
    public class fix_editions_readingformatid_type : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("Editions").Exists() || !Schema.Table("Editions").Column("ReadingFormatId").Exists())
            {
                return;
            }

            // SQLite: normalize legacy string values to ints. SQLite is permissive with column types, so focus on data.
            IfDatabase("sqlite").Execute.Sql(@"
                UPDATE ""Editions""
                SET ""ReadingFormatId"" = 2
                WHERE LOWER(COALESCE(""ReadingFormatId"", '')) IN ('audio', 'audiobook');

                UPDATE ""Editions""
                SET ""ReadingFormatId"" = 3
                WHERE LOWER(COALESCE(""ReadingFormatId"", '')) IN ('ebook', 'e-book', 'kindle', 'digital');

                UPDATE ""Editions""
                SET ""ReadingFormatId"" = 1
                WHERE LOWER(COALESCE(""ReadingFormatId"", '')) IN ('physical', 'print', 'hardcover', 'paperback');

                -- Convert numeric strings ('1','2','3','4',...) to integers
                UPDATE ""Editions""
                SET ""ReadingFormatId"" = CAST(""ReadingFormatId"" AS INTEGER)
                WHERE typeof(""ReadingFormatId"") = 'text'
                  AND ""ReadingFormatId"" GLOB '[0-9]*'
                  AND ""ReadingFormatId"" != '';
            ");

            // PostgreSQL: convert TEXT/VARCHAR ReadingFormatId columns to INTEGER with safe normalization.
            IfPostgres().Execute.Sql(@"
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = current_schema()
                          AND table_name ILIKE 'editions'
                          AND column_name ILIKE 'readingformatid'
                          AND data_type IN ('text', 'character varying')
                    ) THEN
                        ALTER TABLE ""Editions"" ADD COLUMN ""ReadingFormatId_new"" INTEGER;

                        UPDATE ""Editions""
                        SET ""ReadingFormatId_new"" = CASE
                            WHEN ""ReadingFormatId"" ~ '^[0-9]+$' THEN ""ReadingFormatId""::INTEGER
                            WHEN LOWER(COALESCE(""ReadingFormatId"", '')) IN ('audio', 'audiobook') THEN 2
                            WHEN LOWER(COALESCE(""ReadingFormatId"", '')) IN ('ebook', 'e-book', 'kindle', 'digital') THEN 3
                            WHEN LOWER(COALESCE(""ReadingFormatId"", '')) IN ('physical', 'print', 'hardcover', 'paperback') THEN 1
                            ELSE NULL
                        END;

                        ALTER TABLE ""Editions"" DROP COLUMN ""ReadingFormatId"";
                        ALTER TABLE ""Editions"" RENAME COLUMN ""ReadingFormatId_new"" TO ""ReadingFormatId"";
                    END IF;
                END $$;
            ");
        }
    }
}

