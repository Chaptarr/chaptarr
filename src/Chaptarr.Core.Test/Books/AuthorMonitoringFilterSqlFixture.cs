using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class AuthorMonitoringFilterSqlFixture
    {
        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            if (TableMapping.Mapper.TableMap.Count == 0)
            {
                TableMapping.Map();
            }
        }

        [TestCase(DatabaseType.SQLite)]
        [TestCase(DatabaseType.PostgreSQL)]
        public void unmonitored_filter_must_emit_a_null_safe_negation_for_nullable_author_gate(DatabaseType databaseType)
        {
            var filter = AuthorExtensions.GetBookMonitoringFilter(BookMediaType.Audiobook, monitored: false);
            var builder = new SqlBuilder(databaseType)
                .Join<Book, Author>((book, author) => book.AuthorId == author.Id)
                .Where<Book>(filter);

            var template = builder.AddTemplate(@"
                SELECT ""Books"".""Id""
                FROM ""Books""
                /**join**/
                /**where**/");
            var sql = template.RawSql;

            Assert.That(sql, Does.Contain("\"Authors\".\"AudiobookMonitored\" IS NULL"));
            Assert.That(sql, Does.Not.Contain("\"Authors\".\"AudiobookMonitored\" <>"));

            if (databaseType == DatabaseType.SQLite)
            {
                ExecuteUnmonitoredIds(sql, template.Parameters);
            }
        }

        private static void ExecuteUnmonitoredIds(string sql, object parameters)
        {
            using var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            connection.Execute(@"
                CREATE TABLE ""Authors"" (""Id"" INTEGER PRIMARY KEY, ""AudiobookMonitored"" INTEGER NULL);
                CREATE TABLE ""Books"" (""Id"" INTEGER PRIMARY KEY, ""AuthorId"" INTEGER NOT NULL, ""MediaType"" INTEGER NOT NULL, ""AudiobookMonitored"" INTEGER NOT NULL);
                INSERT INTO ""Authors"" VALUES (1, 1), (2, 0), (3, NULL);
                INSERT INTO ""Books"" VALUES (11, 1, 0, 1), (12, 2, 0, 1), (13, 3, 0, 1), (14, 1, 0, 0), (15, 3, 1, 1);");

            var ids = connection.Query<int>(sql, parameters).OrderBy(id => id).ToArray();
            Assert.That(ids, Is.EqualTo(new[] { 12, 13, 14 }));
        }
    }
}
