using Dapper;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Housekeeping.Housekeepers
{
    public class FixMultipleMonitoredEditions : IHousekeepingTask
    {
        private readonly IMainDatabase _database;

        public FixMultipleMonitoredEditions(IMainDatabase database)
        {
            _database = database;
        }

        public void Clean()
        {
            using var mapper = _database.OpenConnection();

            // Enforce invariant: for each BookId with any editions, exactly 1 Edition must be monitored.
            // Fixes both "0 monitored" and "2+ monitored" states.
            mapper.Execute(
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
                  WHERE ""BookId"" IN (SELECT ""BookId"" FROM ""BadBooks"");");
        }
    }
}
