using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.AuthorStats
{
    public interface IAuthorStatisticsRepository
    {
        List<BookStatistics> AuthorStatistics();
        List<BookStatistics> AuthorStatistics(int authorId);
        List<BookStatistics> AuthorStatistics(IEnumerable<int> authorIds);
        List<BookStatistics> AuthorStatistics(string mediaType);
        List<BookStatistics> AuthorStatistics(int authorId, string mediaType);
    }

    public class AuthorStatisticsRepository : IAuthorStatisticsRepository
    {
        private const string _selectTemplate = "SELECT /**select**/ FROM \"Books\" /**join**/ /**innerjoin**/ /**leftjoin**/ /**where**/ /**groupby**/ /**having**/ /**orderby**/";

        private const string _fileStatisticsJoin = "(" + BookFileStatisticsSql.GroupedByBook +
                                                   @") AS ""FileStatistics"" ON ""FileStatistics"".""BookId"" = ""Books"".""Id""";

        private readonly IMainDatabase _database;

        public AuthorStatisticsRepository(IMainDatabase database)
        {
            _database = database;
        }

        public List<BookStatistics> AuthorStatistics()
        {
            return Query(Builder(_database.DatabaseType));
        }

        public List<BookStatistics> AuthorStatistics(int authorId)
        {
            return AuthorStatistics(new[] { authorId });
        }

        public List<BookStatistics> AuthorStatistics(IEnumerable<int> authorIds)
        {
            return AuthorStatistics(authorIds, null);
        }

        public List<BookStatistics> AuthorStatistics(string mediaType)
        {
            var builder = Builder(_database.DatabaseType);
            ApplyMediaTypeFilter(builder, mediaType);
            return Query(builder);
        }

        public List<BookStatistics> AuthorStatistics(int authorId, string mediaType)
        {
            return AuthorStatistics(new[] { authorId }, mediaType);
        }

        private List<BookStatistics> AuthorStatistics(IEnumerable<int> authorIds, string mediaType)
        {
            var ids = authorIds?.Distinct().ToArray() ?? Array.Empty<int>();
            if (ids.Length == 0)
            {
                return new List<BookStatistics>();
            }

            var databaseType = _database.DatabaseType;
            var predicate = databaseType == DatabaseType.PostgreSQL ? "= ANY(@authorIds)" : "IN @authorIds";
            var statistics = new List<BookStatistics>();
            foreach (var batch in ids.Chunk(databaseType == DatabaseType.SQLite ? SqliteVariableLimit.MaxParameters : ids.Length))
            {
                var builder = Builder(databaseType, predicate)
                    .Where($@"""Authors"".""Id"" {predicate}")
                    .AddParameters(new { authorIds = batch });
                ApplyMediaTypeFilter(builder, mediaType);
                statistics.AddRange(Query(builder));
            }

            return statistics;
        }

        internal static string BuildBaseSql(DatabaseType databaseType)
        {
            return Builder(databaseType).AddTemplate(_selectTemplate).RawSql;
        }

        private static void ApplyMediaTypeFilter(SqlBuilder builder, string mediaType)
        {
            if (mediaType == "audiobook")
            {
                builder.Where<Book>(book => book.MediaType == BookMediaType.Audiobook);
            }
            else if (mediaType == "ebook")
            {
                builder.Where<Book>(book => book.MediaType == BookMediaType.Ebook);
            }
        }

        private List<BookStatistics> Query(SqlBuilder builder)
        {
            var sql = builder.AddTemplate(_selectTemplate).LogQuery();

            using (var connection = _database.OpenConnection())
            {
                return connection.Query<BookStatistics>(sql.RawSql, sql.Parameters).ToList();
            }
        }

        private static SqlBuilder Builder(DatabaseType databaseType, string authorIdPredicate = null)
        {
            var fileStatisticsJoin = authorIdPredicate == null
                ? _fileStatisticsJoin
                : "(" + BookFileStatisticsSql.GroupedByBookForAuthors(authorIdPredicate) +
                  @") AS ""FileStatistics"" ON ""FileStatistics"".""BookId"" = ""Books"".""Id""";

            return new SqlBuilder(databaseType)
                .Select(BuildStatisticsSelect(databaseType, includeAuthorId: true))
                .Join<Book, Author>((book, author) => book.AuthorId == author.Id)
                .LeftJoin(fileStatisticsJoin)
                .LeftJoin(BuildMonitoredEditionJoin(databaseType))
                .AddParameters(new { currentDate = DateTime.UtcNow });
        }

        private static string BuildStatisticsSelect(DatabaseType databaseType, bool includeAuthorId)
        {
            var trueIndicator = databaseType == DatabaseType.PostgreSQL ? "true" : "1";
            var monitoredPredicate = BuildMonitoredPredicate(trueIndicator);
            var effectiveReleaseDate = @"COALESCE(""MonitoredEdition"".""ReleaseDate"", ""Books"".""ReleaseDate"")";
            var authorColumn = includeAuthorId ? @"""Authors"".""Id"" AS ""AuthorId""," : string.Empty;

            return $@"{authorColumn}
                     ""Books"".""Id"" AS ""BookId"",
                     COALESCE(""FileStatistics"".""SizeOnDisk"", 0) AS ""SizeOnDisk"",
                     CASE WHEN {monitoredPredicate} THEN 1 ELSE 0 END AS ""TotalBookCount"",
                     CASE WHEN ({monitoredPredicate}) AND COALESCE(""FileStatistics"".""BookFileCount"", 0) > 0 THEN 1 ELSE 0 END AS ""AvailableBookCount"",
                     CASE WHEN ({monitoredPredicate}) AND
                               ({effectiveReleaseDate} IS NULL OR {effectiveReleaseDate} <= @currentDate OR COALESCE(""FileStatistics"".""BookFileCount"", 0) > 0)
                          THEN 1 ELSE 0 END AS ""BookCount"",
                     COALESCE(""FileStatistics"".""BookFileCount"", 0) AS ""BookFileCount""";
        }

        private static string BuildMonitoredPredicate(string trueIndicator)
        {
            return $@"(""Books"".""MediaType"" = 0 AND ""Books"".""AudiobookMonitored"" = {trueIndicator}) OR
                    (""Books"".""MediaType"" = 1 AND ""Books"".""EbookMonitored"" = {trueIndicator})";
        }

        private static string BuildMonitoredEditionJoin(DatabaseType databaseType)
        {
            var trueIndicator = databaseType == DatabaseType.PostgreSQL ? "true" : "1";

            return $@"""Editions"" AS ""MonitoredEdition""
                      ON ""MonitoredEdition"".""BookId"" = ""Books"".""Id""
                         AND ""MonitoredEdition"".""Monitored"" = {trueIndicator}";
        }
    }
}
