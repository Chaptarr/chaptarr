using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(68)]
    public class remove_series_instance_type : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("Series").Exists())
            {
                return;
            }

            // BaseSeriesId is the canonical local identity key for grouping series variants (like BaseBookId for books).
            // Ensure it is populated for original series rows so variant lookups do not require legacy heuristics.
            IfDatabase("sqlite").Execute.Sql(@"
                UPDATE ""Series""
                SET ""BaseSeriesId"" = ""GoodreadsSeriesId""
                WHERE (""BaseSeriesId"" IS NULL OR TRIM(""BaseSeriesId"") = '')
                  AND ""GoodreadsSeriesId"" IS NOT NULL
                  AND TRIM(""GoodreadsSeriesId"") <> '';
            ");

            IfPostgres().Execute.Sql(@"
                UPDATE ""Series""
                SET ""BaseSeriesId"" = ""GoodreadsSeriesId""
                WHERE (""BaseSeriesId"" IS NULL OR btrim(""BaseSeriesId"") = '')
                  AND ""GoodreadsSeriesId"" IS NOT NULL
                  AND btrim(""GoodreadsSeriesId"") <> '';
            ");

            if (!Schema.Table("Series").Column("InstanceType").Exists())
            {
                return;
            }

            // PostgreSQL can drop columns directly.
            IfPostgres().Execute.Sql(@"
                DROP INDEX IF EXISTS ""IX_Series_InstanceType"";
                ALTER TABLE ""Series"" DROP COLUMN IF EXISTS ""InstanceType"";
            ");

            // SQLite: rebuild the table without the dropped column.
            IfDatabase("sqlite").Execute.Sql(@"
                DROP TABLE IF EXISTS ""Series_new"";

                CREATE TABLE ""Series_new"" (
                    ""Id"" INTEGER PRIMARY KEY AUTOINCREMENT,
                    ""Title"" TEXT NOT NULL,
                    ""TitleSlug"" TEXT NULL,
                    ""Description"" TEXT NULL,
                    ""Numbered"" INTEGER NOT NULL DEFAULT 1,
                    ""WorkCount"" INTEGER NOT NULL DEFAULT 0,
                    ""PrimaryWorkCount"" INTEGER NOT NULL DEFAULT 0,
                    ""SeriesType"" TEXT NULL,
                    ""ParentSeriesId"" INTEGER NULL,
                    ""TotalBooks"" INTEGER NOT NULL DEFAULT 0,
                    ""PrimaryBooks"" INTEGER NOT NULL DEFAULT 0,
                    ""BaseSeriesId"" TEXT NULL,
                    ""InstanceNumber"" INTEGER NOT NULL DEFAULT 0,
                    ""PreferredNarratorId"" INTEGER NULL,
                    ""Narrator"" TEXT NULL,
                    ""GoodreadsSeriesId"" TEXT NULL,
                    ""HardcoverSeriesId"" TEXT NULL,
                    ""OpenLibrarySeriesId"" TEXT NULL,
                    ""AmazonSeriesAsin"" TEXT NULL,
                    ""Links"" TEXT NULL DEFAULT '{}',
                    ""ProviderUrls"" TEXT NULL DEFAULT '{}',
                    ""LastUpdated"" DATETIME NULL,
                    ""MediaType"" INTEGER NOT NULL DEFAULT 0
                );

                INSERT INTO ""Series_new"" (
                    ""Id"", ""Title"", ""TitleSlug"", ""Description"", ""Numbered"", ""WorkCount"", ""PrimaryWorkCount"",
                    ""SeriesType"", ""ParentSeriesId"", ""TotalBooks"", ""PrimaryBooks"",
                    ""BaseSeriesId"", ""InstanceNumber"", ""PreferredNarratorId"", ""Narrator"",
                    ""GoodreadsSeriesId"", ""HardcoverSeriesId"", ""OpenLibrarySeriesId"", ""AmazonSeriesAsin"",
                    ""Links"", ""ProviderUrls"", ""LastUpdated"", ""MediaType""
                )
                SELECT
                    ""Id"", ""Title"", ""TitleSlug"", ""Description"", ""Numbered"", ""WorkCount"", ""PrimaryWorkCount"",
                    ""SeriesType"", ""ParentSeriesId"", ""TotalBooks"", ""PrimaryBooks"",
                    ""BaseSeriesId"", ""InstanceNumber"", ""PreferredNarratorId"", ""Narrator"",
                    ""GoodreadsSeriesId"", ""HardcoverSeriesId"", ""OpenLibrarySeriesId"", ""AmazonSeriesAsin"",
                    ""Links"", ""ProviderUrls"", ""LastUpdated"", ""MediaType""
                FROM ""Series"";

                DROP TABLE ""Series"";
                ALTER TABLE ""Series_new"" RENAME TO ""Series"";

                CREATE INDEX IF NOT EXISTS IX_Series_Title_Narrator ON Series(Title, Narrator);
                CREATE INDEX IF NOT EXISTS IX_Series_BaseSeriesId ON Series(BaseSeriesId);
                CREATE INDEX IF NOT EXISTS IX_Series_HardcoverSeriesId ON Series(HardcoverSeriesId);
                CREATE INDEX IF NOT EXISTS IX_Series_AmazonSeriesAsin ON Series(AmazonSeriesAsin);
            ");
        }
    }
}

