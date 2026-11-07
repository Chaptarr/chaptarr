using System.Linq;
using System.Runtime.CompilerServices;
using Dapper;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    public class SqliteDecimalHandlerFixture
    {
        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            RuntimeHelpers.RunClassConstructor(typeof(DbFactory).TypeHandle);
        }

        [Test]
        public void nullable_decimal_should_map_integer_real_and_null_rows_in_either_order()
        {
            using var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            connection.Execute(@"
                CREATE TABLE ""ConversionJobs"" (
                    ""Id"" INTEGER PRIMARY KEY,
                    ""Progress"" NUMERIC NULL
                );

                INSERT INTO ""ConversionJobs"" (""Id"", ""Progress"")
                VALUES (1, 0), (2, 0.125), (3, NULL);
            ");

            var storageClasses = connection.Query<string>(@"
                SELECT typeof(""Progress"")
                FROM ""ConversionJobs""
                ORDER BY ""Id"" ASC");

            Assert.That(storageClasses, Is.EqualTo(new[] { "integer", "real", "null" }));

            var ascending = connection.Query<ConversionJob>(@"
                SELECT ""Id"", ""Progress""
                FROM ""ConversionJobs""
                ORDER BY ""Id"" ASC").ToList();

            Assert.That(ascending.Select(job => job.Progress), Is.EqualTo(new decimal?[] { 0m, 0.125m, null }));

            var descending = connection.Query<ConversionJob>(@"
                SELECT ""Id"", ""Progress""
                FROM ""ConversionJobs""
                ORDER BY ""Id"" DESC").ToList();

            Assert.That(descending.Select(job => job.Progress), Is.EqualTo(new decimal?[] { null, 0.125m, 0m }));
        }
    }
}
