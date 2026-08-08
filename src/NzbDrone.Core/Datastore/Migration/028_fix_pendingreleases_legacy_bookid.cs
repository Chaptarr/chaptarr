using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(28)]
    public class fix_pendingreleases_legacy_bookid : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("PendingReleases").Exists())
            {
                return;
            }

            // Some legacy databases still have a NOT NULL BookId column on PendingReleases.
            // The current PendingRelease model does not persist BookId, causing inserts to fail on SQLite.
            if (!Schema.Table("PendingReleases").Column("BookId").Exists())
            {
                return;
            }

            var hasAdditionalInfo = Schema.Table("PendingReleases").Column("AdditionalInfo").Exists();
            var additionalInfoSelect = hasAdditionalInfo ? @"""AdditionalInfo""" : "NULL";

            IfDatabase("sqlite").Execute.Sql($@"
                DROP TABLE IF EXISTS ""PendingReleases_new"";

                CREATE TABLE IF NOT EXISTS ""PendingReleases_new"" (
                    ""Id"" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    ""AuthorId"" INTEGER NOT NULL,
                    ""Title"" TEXT NOT NULL,
                    ""Added"" TEXT NOT NULL,
                    ""Release"" TEXT NOT NULL,
                    ""ParsedBookInfo"" TEXT,
                    ""Reason"" INTEGER NOT NULL,
                    ""AdditionalInfo"" TEXT
                );

                INSERT INTO ""PendingReleases_new"" (
                    ""Id"",
                    ""AuthorId"",
                    ""Title"",
                    ""Added"",
                    ""Release"",
                    ""ParsedBookInfo"",
                    ""Reason"",
                    ""AdditionalInfo""
                )
                SELECT
                    ""Id"",
                    CASE
                        WHEN COALESCE(""AuthorId"", 0) > 0 THEN ""AuthorId""
                        ELSE COALESCE(
                            (SELECT ""Books"".""AuthorId"" FROM ""Books"" WHERE ""Books"".""Id"" = ""PendingReleases"".""BookId""),
                            0
                        )
                    END AS ""AuthorId"",
                    ""Title"",
                    ""Added"",
                    ""Release"",
                    ""ParsedBookInfo"",
                    ""Reason"",
                    {additionalInfoSelect} AS ""AdditionalInfo""
                FROM ""PendingReleases"";

                DROP TABLE ""PendingReleases"";
                ALTER TABLE ""PendingReleases_new"" RENAME TO ""PendingReleases"";
            ");

            IfPostgres().Execute.Sql(@"
                ALTER TABLE ""PendingReleases""
                DROP COLUMN IF EXISTS ""BookId"";
            ");
        }
    }
}
