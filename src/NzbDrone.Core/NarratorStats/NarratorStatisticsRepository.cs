using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.NarratorStats
{
    public class NarratorStatisticsRepository : INarratorStatisticsRepository
    {
        private readonly IMainDatabase _database;

        public NarratorStatisticsRepository(IMainDatabase database)
        {
            _database = database;
        }

        public List<BookStatistics> NarratorStatistics()
        {
            using (var conn = _database.OpenConnection())
            {
                return conn.Query<BookStatistics>(BuildQuery(), new { currentDate = DateTime.UtcNow }).ToList();
            }
        }

        public List<BookStatistics> NarratorStatistics(int narratorId)
        {
            using (var conn = _database.OpenConnection())
            {
                return conn.Query<BookStatistics>($"{BuildQuery()} AND \"Narrators\".\"Id\" = @narratorId", new { narratorId, currentDate = DateTime.UtcNow }).ToList();
            }
        }

        private string BuildQuery()
        {
            return @"
                SELECT
                    ""Narrators"".""Id"" AS NarratorId,
                    ""Books"".""Id"" AS BookId,
                    SUM(COALESCE(""BookFiles"".""Size"", 0)) AS SizeOnDisk,
                    1 AS TotalBookCount,
                    CASE WHEN MIN(""BookFiles"".""Id"") IS NULL THEN 0 ELSE 1 END AS AvailableBookCount,
                    CASE WHEN (((""Books"".""AudiobookMonitored"" OR ""Books"".""EbookMonitored"") AND (""Books"".""ReleaseDate"" < @currentDate OR ""Books"".""ReleaseDate"" IS NULL)) OR MIN(""BookFiles"".""Id"") IS NOT NULL) THEN 1 ELSE 0 END AS BookCount,
                    CASE WHEN MIN(""BookFiles"".""Id"") IS NULL THEN 0 ELSE COUNT(""BookFiles"".""Id"") END AS BookFileCount
                FROM ""BookNarratorLink""
                INNER JOIN ""Narrators"" ON ""BookNarratorLink"".""NarratorId"" = ""Narrators"".""Id""
                INNER JOIN ""Books"" ON ""BookNarratorLink"".""BookId"" = ""Books"".""Id""
                INNER JOIN ""Editions"" ON ""Books"".""Id"" = ""Editions"".""BookId""
                LEFT JOIN ""BookFiles"" ON ""Editions"".""Id"" = ""BookFiles"".""EditionId""
                WHERE ""Editions"".""Monitored""
                  AND ""BookNarratorLink"".""IsPrimary""
                GROUP BY ""Narrators"".""Id"", ""Books"".""Id""";
        }
    }
}
