using System;
using Dapper;
using Npgsql;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    public class PostgresCiContractFixture
    {
        [Test]
        public void required_postgres_job_must_execute_a_database_query()
        {
            if (Environment.GetEnvironmentVariable("CHAPTARR_REQUIRE_POSTGRES") != "true")
            {
                Assert.Ignore("PostgreSQL is optional outside the PostgreSQL CI job.");
            }

            var connectionString = Environment.GetEnvironmentVariable("CHAPTARR_TEST_POSTGRES_CONNECTION_STRING");
            Assert.That(connectionString, Is.Not.Null.And.Not.Empty,
                "The PostgreSQL CI job requires CHAPTARR_TEST_POSTGRES_CONNECTION_STRING; skipping PostgreSQL is not a pass.");
            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();
            Assert.That(connection.ExecuteScalar<int>("SELECT 1"), Is.EqualTo(1));
            TestContext.Progress.WriteLine("PostgreSQL exercised: server " + connection.PostgreSqlVersion);
        }
    }
}
