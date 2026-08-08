using System;
using System.IO;
using Dapper;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using NzbDrone.Core.Datastore.Migration;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    public class MyAnonaMouseWedgeOptInResetFixture
    {
        [Test]
        public void should_reset_existing_wedge_preferences_without_touching_other_indexers()
        {
            WithDatabase(connection =>
            {
                connection.Execute(@"
                    INSERT INTO ""Indexers"" (""Id"", ""Implementation"", ""ConfigContract"", ""Settings"")
                    VALUES
                        (1, 'MyAnonaMouse', 'MyAnonaMouseSettings', '{""useFreeleechWedge"":1,""mamSsl"":true}'),
                        (2, 'MyAnonaMouse', 'MyAnonaMouseSettings', '{""useFreeleechWedge"":""2"",""mamSsl"":true}'),
                        (3, 'MyAnonaMouse', 'MyAnonaMouseSettings', '{""useFreeleechWedge"":0,""mamSsl"":true}'),
                        (4, 'Newznab', 'NewznabSettings', '{""useFreeleechWedge"":1}'),
                        (5, 'MyAnonaMouse', 'MyAnonaMouseSettings', 'not-json');
                ");

                using var transaction = connection.BeginTransaction();
                MyAnonaMouseWedgeOptInReset.Apply(connection, transaction);
                transaction.Commit();

                Assert.That(ReadWedgeValue(connection, 1), Is.Zero);
                Assert.That(ReadWedgeValue(connection, 2), Is.Zero);
                Assert.That(ReadWedgeValue(connection, 3), Is.Zero);
                Assert.That(ReadWedgeValue(connection, 4), Is.EqualTo(1));
                Assert.That(connection.QuerySingle<string>(@"SELECT ""Settings"" FROM ""Indexers"" WHERE ""Id"" = 5;"), Is.EqualTo("not-json"));
                Assert.That(ReadSettings(connection, 1)["mamSsl"]?.Value<bool>(), Is.True);
            });
        }

        private static int ReadWedgeValue(SqliteConnection connection, int id)
        {
            return ReadSettings(connection, id)["useFreeleechWedge"]?.Value<int>() ?? -1;
        }

        private static JObject ReadSettings(SqliteConnection connection, int id)
        {
            var settings = connection.QuerySingle<string>(
                @"SELECT ""Settings"" FROM ""Indexers"" WHERE ""Id"" = @Id;",
                new { Id = id });

            return JObject.Parse(settings);
        }

        private static void WithDatabase(Action<SqliteConnection> test)
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"mam_wedge_opt_in_reset_{Guid.NewGuid():N}.db");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,

                // Windows holds pooled handles open past connection disposal, which blocks the temp-db delete below.
                Pooling = false
            }.ToString();

            try
            {
                using var connection = new SqliteConnection(connectionString);
                connection.Open();
                connection.Execute(@"
                    CREATE TABLE ""Indexers"" (
                        ""Id"" INTEGER PRIMARY KEY,
                        ""Implementation"" TEXT NOT NULL,
                        ""ConfigContract"" TEXT NOT NULL,
                        ""Settings"" TEXT NULL
                    );
                ");
                test(connection);
            }
            finally
            {
                if (File.Exists(databasePath))
                {
                    File.Delete(databasePath);
                }
            }
        }
    }
}
