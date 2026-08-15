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
        BookAggregate GetAggregateStatistics(List<int> authorIds, string mediaType);
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
            return Query(Builder(_database.DatabaseType).Where<Author>(author => author.Id == authorId));
        }

        public List<BookStatistics> AuthorStatistics(string mediaType)
        {
            var builder = Builder(_database.DatabaseType);
            ApplyMediaTypeFilter(builder, mediaType);
            return Query(builder);
        }

        public List<BookStatistics> AuthorStatistics(int authorId, string mediaType)
        {
            var builder = Builder(_database.DatabaseType).Where<Author>(author => author.Id == authorId);
            ApplyMediaTypeFilter(builder, mediaType);
            return Query(builder);
        }

        public BookAggregate GetAggregateStatistics(List<int> authorIds, string mediaType)
        {
            if (authorIds == null || authorIds.Count == 0)
            {
                return new BookAggregate { BookCount = 0, BookFileCount = 0, SizeOnDisk = 0 };
            }

            var distinctAuthorIds = authorIds.Distinct().ToArray();
            var authorIdBatches = _database.DatabaseType == DatabaseType.SQLite &&
                                  distinctAuthorIds.Length > SqliteVariableLimit.MaxParameters
                ? distinctAuthorIds.Chunk(SqliteVariableLimit.MaxParameters)
                : new[] { distinctAuthorIds };

            var results = new List<BookStatistics>();
            foreach (var authorIdBatch in authorIdBatches)
            {
                var builder = BuildFilteredAggregate(_database.DatabaseType, authorIdBatch);
                ApplyMediaTypeFilter(builder, mediaType);
                results.AddRange(Query(builder));
            }

            return new BookAggregate
            {
                BookCount = results.Sum(result => result.BookCount),
                BookFileCount = results.Sum(result => result.BookFileCount),
                SizeOnDisk = results.Sum(result => result.SizeOnDisk)
            };
        }

        internal static string BuildBaseSql(DatabaseType databaseType, bool aggregate)
        {
            var builder = aggregate ? AggregateBuilder(databaseType) : Builder(databaseType);
            return builder.AddTemplate(_selectTemplate).RawSql;
        }

        internal static string BuildFilteredAggregateSql(DatabaseType databaseType)
        {
            return BuildFilteredAggregate(databaseType, new[] { 1 })
                .AddTemplate(_selectTemplate)
                .RawSql;
        }

        private static string BuildAuthorIdsPredicate(DatabaseType databaseType)
        {
            return databaseType == DatabaseType.PostgreSQL
                ? @"""Books"".""AuthorId"" = ANY(@AuthorIds)"
                : @"""Books"".""AuthorId"" IN @AuthorIds";
        }

        private static SqlBuilder BuildFilteredAggregate(DatabaseType databaseType, int[] authorIds)
        {
            return AggregateBuilder(databaseType)
                .Where(BuildAuthorIdsPredicate(databaseType), new { AuthorIds = authorIds });
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

        private static SqlBuilder Builder(DatabaseType databaseType)
        {
            var trueIndicator = databaseType == DatabaseType.PostgreSQL ? "true" : "1";

            return new SqlBuilder(databaseType)
                .Select($@"""Authors"".""Id"" AS ""AuthorId"",
                         ""Books"".""Id"" AS ""BookId"",
                         COALESCE(""FileStatistics"".""SizeOnDisk"", 0) AS ""SizeOnDisk"",
                         CASE
                             WHEN ""Books"".""MediaType"" = 0 AND ""Books"".""AudiobookMonitored"" = {trueIndicator} THEN 1
                             WHEN ""Books"".""MediaType"" = 1 AND ""Books"".""EbookMonitored"" = {trueIndicator} THEN 1
                             ELSE 0
                         END AS ""TotalBookCount"",
                         CASE
                             WHEN ""Books"".""MediaType"" = 0 AND ""Books"".""AudiobookMonitored"" = {trueIndicator} AND COALESCE(""FileStatistics"".""BookFileCount"", 0) > 0 THEN 1
                             WHEN ""Books"".""MediaType"" = 1 AND ""Books"".""EbookMonitored"" = {trueIndicator} AND COALESCE(""FileStatistics"".""BookFileCount"", 0) > 0 THEN 1
                             ELSE 0
                         END AS ""AvailableBookCount"",
                         CASE
                             WHEN ""Books"".""MediaType"" = 0 AND ""Books"".""AudiobookMonitored"" = {trueIndicator} THEN 1
                             WHEN ""Books"".""MediaType"" = 1 AND ""Books"".""EbookMonitored"" = {trueIndicator} THEN 1
                             ELSE 0
                         END AS ""BookCount"",
                         COALESCE(""FileStatistics"".""BookFileCount"", 0) AS ""BookFileCount""")
                .Join<Book, Author>((book, author) => book.AuthorId == author.Id)
                .LeftJoin(_fileStatisticsJoin);
        }

        private static SqlBuilder AggregateBuilder(DatabaseType databaseType)
        {
            var trueIndicator = databaseType == DatabaseType.PostgreSQL ? "true" : "1";

            return new SqlBuilder(databaseType)
                .Select($@"""Books"".""Id"" AS ""BookId"",
                         COALESCE(""FileStatistics"".""SizeOnDisk"", 0) AS ""SizeOnDisk"",
                         CASE
                             WHEN ""Books"".""MediaType"" = 0 AND ""Books"".""AudiobookMonitored"" = {trueIndicator} THEN 1
                             WHEN ""Books"".""MediaType"" = 1 AND ""Books"".""EbookMonitored"" = {trueIndicator} THEN 1
                             ELSE 0
                         END AS ""TotalBookCount"",
                         CASE
                             WHEN ""Books"".""MediaType"" = 0 AND ""Books"".""AudiobookMonitored"" = {trueIndicator} AND COALESCE(""FileStatistics"".""BookFileCount"", 0) > 0 THEN 1
                             WHEN ""Books"".""MediaType"" = 1 AND ""Books"".""EbookMonitored"" = {trueIndicator} AND COALESCE(""FileStatistics"".""BookFileCount"", 0) > 0 THEN 1
                             ELSE 0
                         END AS ""AvailableBookCount"",
                         CASE WHEN COALESCE(""FileStatistics"".""BookFileCount"", 0) > 0 THEN 1 ELSE 0 END AS ""BookCount"",
                         COALESCE(""FileStatistics"".""BookFileCount"", 0) AS ""BookFileCount""")
                .LeftJoin(_fileStatisticsJoin);
        }
    }
}
