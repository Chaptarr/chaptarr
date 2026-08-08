using Dapper;
using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(39)]
    public class fix_monitored_editions_invariant : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Execute.WithConnection((connection, transaction) =>
            {
                // Enforce invariant: for each BookId with any editions, exactly 1 Edition must be monitored.
                // Preference order:
                //  1) ManualAdd (user selection)
                //  2) Edition with files on disk
                //  3) ReadingFormatId preference (audio for audiobooks, ebook/print for ebooks)
                //  4) Stable fallback by lowest Edition.Id
                //
                // Uses a boolean expression assignment for cross-DB compatibility:
                // - PostgreSQL: boolean
                // - SQLite: 0/1
                connection.Execute(
                    @"WITH ""BadBooks"" AS (
                          SELECT ""BookId""
                          FROM ""Editions""
                          WHERE ""BookId"" > 0
                          GROUP BY ""BookId""
                          HAVING SUM(CASE WHEN ""Monitored"" THEN 1 ELSE 0 END) != 1
                      ),
                      ""FileCounts"" AS (
                          SELECT ""EditionId"", COUNT(1) AS ""FileCount""
                          FROM ""BookFiles""
                          GROUP BY ""EditionId""
                      ),
                      ""Ranked"" AS (
                          SELECT e.""Id"",
                                 e.""BookId"",
                                 ROW_NUMBER() OVER (
                                     PARTITION BY e.""BookId""
                                     ORDER BY
                                         CASE WHEN e.""ManualAdd"" THEN 0 ELSE 1 END,
                                         CASE WHEN COALESCE(fc.""FileCount"", 0) > 0 THEN 0 ELSE 1 END,
                                         CASE
                                             WHEN b.""MediaType"" = 0 AND e.""ReadingFormatId"" = 2 THEN 0
                                             WHEN b.""MediaType"" = 1 AND e.""ReadingFormatId"" = 3 THEN 0
                                             WHEN b.""MediaType"" = 1 AND e.""ReadingFormatId"" = 1 THEN 1
                                             ELSE 2
                                         END,
                                         e.""Id""
                                 ) AS ""Rank""
                          FROM ""Editions"" e
                          JOIN ""BadBooks"" bb ON bb.""BookId"" = e.""BookId""
                          JOIN ""Books"" b ON b.""Id"" = e.""BookId""
                          LEFT JOIN ""FileCounts"" fc ON fc.""EditionId"" = e.""Id""
                      )
                      UPDATE ""Editions""
                      SET ""Monitored"" = (""Id"" IN (SELECT ""Id"" FROM ""Ranked"" WHERE ""Rank"" = 1))
                      WHERE ""BookId"" IN (SELECT ""BookId"" FROM ""BadBooks"");",
                    transaction: transaction);
            });
        }
    }
}

