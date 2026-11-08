using Chaptarr.Core.Test.Common;
using Dapper;
using NUnit.Framework;
using NzbDrone.Core.Datastore;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    public class DatabaseStatisticsFixture
    {
        [Test]
        public void housekeeping_vacuum_should_preserve_identity_lookup_plans_without_creating_statistics()
        {
            DualDatabaseFixture.Run(false, database =>
            {
                using (var connection = database.OpenConnection())
                {
                    connection.Execute(@"WITH RECURSIVE ids(id) AS (SELECT 1 UNION ALL SELECT id + 1 FROM ids WHERE id < 2000)
                        INSERT INTO ""Books"" (""Id"", ""AuthorId"", ""Title"", ""CleanTitle"", ""MediaType"", ""AnyEditionOk"", ""Overview"", ""Added"")
                        SELECT id, 1, 'Book ' || id, 'book ' || id, 0, 1, '', '2026-01-01 00:00:00Z' FROM ids;");
                    Assert.That(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_schema WHERE name = 'sqlite_stat1'"), Is.Zero);
                }

                const string lookup = @"SELECT ""Id"" FROM ""Books"" WHERE ""ISBN13"" = '9780000000000' OR ""ISBN10"" = '0000000000'";
                using (var connection = database.OpenConnection())
                {
                    Assert.That(connection.Query<int>(lookup), Is.Empty);
                }
                database.Vacuum();
                database.Vacuum();
                using (var connection = database.OpenConnection())
                {
                    Assert.That(connection.Query<int>(lookup), Is.Empty);
                    Assert.That(connection.ExecuteScalar<int>(@"SELECT COUNT(*) FROM ""Books"""), Is.EqualTo(2000));
                    Assert.That(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_schema WHERE name = 'sqlite_stat1'"), Is.Zero);
                    var plan = connection.Query("EXPLAIN QUERY PLAN " + lookup);
                    foreach (var row in plan)
                    {
                        Assert.That((string)row.detail, Does.Not.Contain("SCAN Books"));
                    }
                }
            });
        }

        [Test]
        public void housekeeping_vacuum_should_preserve_statistics_written_by_explicit_maintenance()
        {
            DualDatabaseFixture.Run(false, database =>
            {
                string original;
                using (var connection = database.OpenConnection())
                {
                    connection.Execute(@"CREATE TABLE statistics_control (id INTEGER PRIMARY KEY, value TEXT);
                        CREATE INDEX statistics_control_value ON statistics_control(value);
                        INSERT INTO statistics_control VALUES (1, 'first'); ANALYZE statistics_control;");
                    original = connection.ExecuteScalar<string>("SELECT stat FROM sqlite_stat1 WHERE idx = 'statistics_control_value'");
                    connection.Execute("INSERT INTO statistics_control VALUES (2, 'second'), (3, 'third')");
                }

                database.Vacuum();
                using (var connection = database.OpenConnection())
                {
                    Assert.That(connection.ExecuteScalar<string>("SELECT stat FROM sqlite_stat1 WHERE idx = 'statistics_control_value'"), Is.EqualTo(original));
                    Assert.That(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM statistics_control"), Is.EqualTo(3));
                }
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void housekeeping_vacuum_should_preserve_an_empty_database(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                database.Vacuum();
                database.Vacuum();
                using var connection = database.OpenConnection();
                Assert.That(connection.ExecuteScalar<int>(@"SELECT COUNT(*) FROM ""Books"""), Is.Zero);
                if (!postgres)
                {
                    Assert.That(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_schema WHERE name = 'sqlite_stat1'"), Is.Zero);
                }
            });
        }
    }
}
