using NUnit.Framework;
using NzbDrone.Core.AuthorStats;
using NzbDrone.Core.Datastore;

namespace Chaptarr.Core.Test.AuthorStats
{
    [TestFixture]
    public class AuthorStatisticsSqlParityFixture
    {
        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            if (TableMapping.Mapper.TableMap.Count == 0)
            {
                TableMapping.Map();
            }
        }

        [TestCase(DatabaseType.SQLite, " = 1")]
        [TestCase(DatabaseType.PostgreSQL, " = true")]
        public void should_build_the_same_books_and_file_aggregate_shape_for_both_databases(DatabaseType databaseType, string booleanComparison)
        {
            var authorSql = AuthorStatisticsRepository.BuildBaseSql(databaseType, aggregate: false);
            var aggregateSql = AuthorStatisticsRepository.BuildBaseSql(databaseType, aggregate: true);
            var filteredAggregateSql = AuthorStatisticsRepository.BuildFilteredAggregateSql(databaseType);

            Assert.Multiple(() =>
            {
                StringAssert.Contains("FROM \"Books\"", authorSql);
                StringAssert.Contains("FROM \"BookFiles\"", authorSql);
                StringAssert.Contains("CROSS JOIN \"Editions\"", authorSql);
                StringAssert.Contains("GROUP BY \"Editions\".\"BookId\"", authorSql);
                StringAssert.Contains(booleanComparison, authorSql);
                StringAssert.DoesNotContain("MIN(\"BookFiles\"", authorSql);
                StringAssert.Contains(@"""Editions"".""BookId"" = ""Books"".""Id""", authorSql);
                StringAssert.Contains(@"ORDER BY ""Editions"".""Id""", authorSql);

                StringAssert.Contains("FROM \"Books\"", aggregateSql);
                StringAssert.Contains("FROM \"BookFiles\"", aggregateSql);
                StringAssert.Contains(booleanComparison, aggregateSql);

                if (databaseType == DatabaseType.PostgreSQL)
                {
                    StringAssert.Contains(@"""Books"".""AuthorId"" = ANY(@AuthorIds)", filteredAggregateSql);
                    StringAssert.DoesNotContain(@"""Books"".""AuthorId"" IN @AuthorIds", filteredAggregateSql);
                }
                else
                {
                    StringAssert.Contains(@"""Books"".""AuthorId"" IN @AuthorIds", filteredAggregateSql);
                }
            });
        }
    }
}
