using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(71)]
    public class repair_book_provider_ids_canonical_contract : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("Books").Exists())
            {
                return;
            }

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

                UPDATE ""Books"" SET ""BaseBookId"" = NULL
                WHERE ""BaseBookId"" IS NOT NULL AND TRIM(""BaseBookId"") <> '' AND ""BaseBookId"" NOT LIKE '%:%';

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

                UPDATE ""Books"" SET ""BaseBookId"" = NULL
                WHERE ""BaseBookId"" IS NOT NULL AND btrim(""BaseBookId"") <> '' AND ""BaseBookId"" NOT LIKE '%:%';

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
        }
    }
}
