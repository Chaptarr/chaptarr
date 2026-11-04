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
        List<BookStatistics> AuthorStatistics(string mediaType);
        List<BookStatistics> AuthorStatistics(int authorId, string mediaType);
        List<BookStatistics> AuthorStatistics(IEnumerable<int> authorIds, string mediaType);
        List<BookStatistics> BookshelfStatistics();
        List<BookStatistics> BookshelfStatistics(int authorId);
    }

    public class AuthorStatisticsRepository : IAuthorStatisticsRepository
    {
        private const string _selectTemplate = "SELECT /**select**/ FROM \"Books\" /**join**/ /**innerjoin**/ /**leftjoin**/ /**where**/ /**groupby**/ /**having**/ /**orderby**/";

        private static readonly string _fileStatisticsJoin = "(" +
            BookFileStatisticsSql.GroupedByBookWithFormatStatistics +
            @") AS ""FileStatistics"" ON ""FileStatistics"".""BookId"" = ""Books"".""Id""";

        private readonly IMainDatabase _database;
        private const string _bookshelfSelect = @"
SELECT
    ""Authors"".""Id"" AS ""AuthorId"",
    COALESCE(
        MIN(CASE WHEN ""Books"".""MediaType"" = 0 THEN ""Books"".""Id"" END),
        MIN(CASE WHEN ""Books"".""MediaType"" = 1 THEN ""Books"".""Id"" END),
        MIN(""Books"".""Id"")
    ) AS ""BookId"",
    COUNT(""BookFiles"".""Id"") AS ""BookFileCount"",
    SUM(CASE WHEN {ebookExtension} THEN 1 ELSE 0 END) AS ""EbookFileCount"",
    SUM(CASE WHEN {audioExtension} THEN 1 ELSE 0 END) AS ""AudiobookFileCount"",
    COALESCE(SUM(COALESCE(""BookFiles"".""Size"", 0)), 0) AS ""SizeOnDisk"",
    COALESCE(SUM(CASE WHEN {ebookExtension} THEN COALESCE(""BookFiles"".""Size"", 0) ELSE 0 END), 0) AS ""EbookSizeOnDisk"",
    COALESCE(SUM(CASE WHEN {audioExtension} THEN COALESCE(""BookFiles"".""Size"", 0) ELSE 0 END), 0) AS ""AudiobookSizeOnDisk"",
    1 AS ""TotalBookCount"",
    MAX(CASE WHEN ""BookFiles"".""Id"" IS NULL THEN 0 ELSE 1 END) AS ""AvailableBookCount"",
    CASE WHEN
        (MAX(CASE WHEN {monitoredPredicate} THEN 1 ELSE 0 END) = 1 AND
         (MIN(""Books"".""ReleaseDate"") < @currentDate OR MIN(""Books"".""ReleaseDate"") IS NULL)) OR
        MIN(""Books"".""ReleaseDate"") IS NULL OR
        MAX(CASE WHEN ""BookFiles"".""Id"" IS NULL THEN 0 ELSE 1 END) = 1
        THEN 1 ELSE 0 END AS ""BookCount""
FROM ""Editions""
JOIN ""Books"" ON ""Books"".""Id"" = ""Editions"".""BookId""
JOIN ""Authors"" ON ""Authors"".""Id"" = ""Books"".""AuthorId""
LEFT JOIN ""BookFiles"" ON ""BookFiles"".""EditionId"" = ""Editions"".""Id""
LEFT JOIN ""BookshelfLegacyIdentityMap"" AS ""LegacyBook""
       ON ""LegacyBook"".""Source"" = 'BookshelfNG'
      AND ""LegacyBook"".""EntityType"" = 'Book'
      AND ""LegacyBook"".""TargetId"" = ""Books"".""Id""
WHERE ""Editions"".""Monitored"" = {trueIndicator}
  {authorFilter}
GROUP BY ""Authors"".""Id"", COALESCE(""LegacyBook"".""SourceId"", ""Books"".""Id"")
ORDER BY ""Authors"".""Id"", ""BookId""";

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
            return Query(Builder(_database.DatabaseType, authorId).Where<Author>(author => author.Id == authorId));
        }

        public List<BookStatistics> AuthorStatistics(string mediaType)
        {
            var builder = Builder(_database.DatabaseType);
            ApplyMediaTypeFilter(builder, mediaType);
            return Query(builder);
        }

        public List<BookStatistics> AuthorStatistics(int authorId, string mediaType)
        {
            var builder = Builder(_database.DatabaseType, authorId).Where<Author>(author => author.Id == authorId);
            ApplyMediaTypeFilter(builder, mediaType);
            return Query(builder);
        }

        public List<BookStatistics> AuthorStatistics(IEnumerable<int> authorIds, string mediaType)
        {
            var idsArray = authorIds?.Distinct().ToArray() ?? Array.Empty<int>();
            if (idsArray.Length == 0)
            {
                return new List<BookStatistics>();
            }

            var builder = Builder(_database.DatabaseType).Where<Author>(author => Enumerable.Contains(idsArray, author.Id));
            ApplyMediaTypeFilter(builder, mediaType);
            return Query(builder);
        }

        public List<BookStatistics> BookshelfStatistics()
        {
            return QueryBookshelfStatistics(null);
        }

        public List<BookStatistics> BookshelfStatistics(int authorId)
        {
            return QueryBookshelfStatistics(authorId);
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

        private List<BookStatistics> QueryBookshelfStatistics(int? authorId)
        {
            var trueIndicator = _database.DatabaseType == DatabaseType.PostgreSQL ? "true" : "1";
            var authorFilter = authorId.HasValue ? "AND \"Authors\".\"Id\" = @authorId" : string.Empty;
            var sql = _bookshelfSelect
                .Replace("{ebookExtension}", BookFileStatisticsSql.EbookExtensionCondition, StringComparison.Ordinal)
                .Replace("{audioExtension}", BookFileStatisticsSql.AudiobookExtensionCondition, StringComparison.Ordinal)
                .Replace("{monitoredPredicate}", BuildMonitoredPredicate(trueIndicator), StringComparison.Ordinal)
                .Replace("{trueIndicator}", trueIndicator, StringComparison.Ordinal)
                .Replace("{authorFilter}", authorFilter, StringComparison.Ordinal);

            using var connection = _database.OpenConnection();
            return connection.Query<BookStatistics>(sql, new { currentDate = DateTime.UtcNow, authorId }).ToList();
        }

        private static SqlBuilder Builder(DatabaseType databaseType, int? authorId = null)
        {
            var fileStatisticsJoin = authorId.HasValue
                ? "(" + BookFileStatisticsSql.GroupedByBookWithFormatStatisticsForAuthor(authorId.Value) + @") AS ""FileStatistics"" ON ""FileStatistics"".""BookId"" = ""Books"".""Id"""
                : _fileStatisticsJoin;

            return new SqlBuilder(databaseType)
                .Select(BuildStatisticsSelect(databaseType, includeAuthorId: true))
                .Join<Book, Author>((book, author) => book.AuthorId == author.Id)
                .LeftJoin(fileStatisticsJoin)
                .AddParameters(new { currentDate = DateTime.UtcNow });
        }

        private static string BuildStatisticsSelect(DatabaseType databaseType, bool includeAuthorId)
        {
            var trueIndicator = databaseType == DatabaseType.PostgreSQL ? "true" : "1";
            var monitoredPredicate = BuildMonitoredPredicate(trueIndicator);
            var effectiveReleaseDate = BuildEffectiveReleaseDate(trueIndicator);
            var authorColumn = includeAuthorId ? @"""Authors"".""Id"" AS ""AuthorId""," : string.Empty;

            return $@"{authorColumn}
                     ""Books"".""Id"" AS ""BookId"",
                     COALESCE(""FileStatistics"".""SizeOnDisk"", 0) AS ""SizeOnDisk"",
                     COALESCE(""FileStatistics"".""EbookFileCount"", 0) AS ""EbookFileCount"",
                     COALESCE(""FileStatistics"".""AudiobookFileCount"", 0) AS ""AudiobookFileCount"",
                     COALESCE(""FileStatistics"".""EbookSizeOnDisk"", 0) AS ""EbookSizeOnDisk"",
                     COALESCE(""FileStatistics"".""AudiobookSizeOnDisk"", 0) AS ""AudiobookSizeOnDisk"",
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

        private static string BuildEffectiveReleaseDate(string trueIndicator)
        {
            return $@"COALESCE(
                        (SELECT ""Editions"".""ReleaseDate""
                         FROM ""Editions""
                         WHERE ""Editions"".""BookId"" = ""Books"".""Id""
                           AND ""Editions"".""Monitored"" = {trueIndicator}
                         ORDER BY ""Editions"".""Id""
                         LIMIT 1),
                        ""Books"".""ReleaseDate"")";
        }
    }
}
