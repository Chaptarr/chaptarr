using System;
using System.Data;
using System.IO;
using Dapper;
using Microsoft.Data.Sqlite;
using NLog;
using NLog.Config;
using NLog.Targets;
using Npgsql;
using NUnit.Framework;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace Chaptarr.Core.Test.Common
{
    [TestFixture]
    [NonParallelizable]
    public class DualDatabaseFixtureFixture
    {
        [TestCase(false)]
        [TestCase(true)]
        public void opened_fixture_connection_can_reopen_the_same_isolated_database(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var opened = database.OpenConnection();
                if (postgres)
                {
                    var wrongPassword = new NpgsqlConnectionStringBuilder(opened.ConnectionString)
                    {
                        Password = Guid.NewGuid().ToString("N"),
                        Pooling = false
                    }.ConnectionString;
                    var rejection = Assert.Throws<PostgresException>(() =>
                    {
                        using var rejected = new NpgsqlConnection(wrongPassword);
                        rejected.Open();
                    }, "Use password-authenticated PostgreSQL; trust authentication cannot prove credential retention");
                    Assert.That(rejection.SqlState, Is.EqualTo(PostgresErrorCodes.InvalidPassword));
                }

                using IDbConnection reopened = postgres
                    ? new NpgsqlConnection(opened.ConnectionString)
                    : new SqliteConnection(opened.ConnectionString);
                reopened.Open();
                Assert.That(reopened.Database, Is.EqualTo(opened.Database));
                Assert.That(reopened.ExecuteScalar<int>("SELECT COUNT(*) FROM \"VersionInfo\""), Is.GreaterThan(0));
            });
        }

        [Test]
        public void required_postgres_cannot_be_silently_skipped()
        {
            const string connectionKey = "CHAPTARR_TEST_POSTGRES_CONNECTION_STRING";
            const string requiredKey = "CHAPTARR_REQUIRE_POSTGRES";
            var connection = Environment.GetEnvironmentVariable(connectionKey);
            var required = Environment.GetEnvironmentVariable(requiredKey);
            try
            {
                Environment.SetEnvironmentVariable(connectionKey, null);
                Environment.SetEnvironmentVariable(requiredKey, "true");
                Assert.Throws<AssertionException>(() => DualDatabaseFixture.Run(true, _ => Assert.Fail("Must not open a database")));
            }
            finally
            {
                Environment.SetEnvironmentVariable(connectionKey, connection);
                Environment.SetEnvironmentVariable(requiredKey, required);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void cloned_databases_must_isolate_schema_data_and_migration_history(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, first =>
            {
                using var original = first.OpenConnection();
                var versions = original.Query<long>("SELECT \"Version\" FROM \"VersionInfo\" ORDER BY \"Version\"").AsList();
                original.Execute("CREATE TABLE \"CloneProbe\" (\"Value\" INTEGER); INSERT INTO \"CloneProbe\" VALUES (42); DELETE FROM \"VersionInfo\";");
                DualDatabaseFixture.Run(postgres, second =>
                {
                    using var copy = second.OpenConnection();
                    Assert.That(HasTable(copy, postgres, "CloneProbe"), Is.False);
                    Assert.That(copy.Query<long>("SELECT \"Version\" FROM \"VersionInfo\" ORDER BY \"Version\""), Is.EqualTo(versions));
                    copy.Execute("CREATE TABLE \"CloneProbe\" (\"Value\" INTEGER); INSERT INTO \"CloneProbe\" VALUES (99);");
                });
                Assert.That(original.ExecuteScalar<int>("SELECT \"Value\" FROM \"CloneProbe\""), Is.EqualTo(42));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ordinary_tests_must_not_repeat_migrations_after_template_creation(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, _ => { });
            var previous = LogManager.Configuration;
            var messages = new MemoryTarget { Layout = "${message}" };
            var config = new LoggingConfiguration();
            config.AddRule(LogLevel.Info, LogLevel.Fatal, messages);
            try
            {
                LogManager.Configuration = config;
                DualDatabaseFixture.Run(postgres, database =>
                {
                    using var connection = database.OpenConnection();
                    Assert.That(HasTable(connection, postgres, "Authors"), Is.True);
                });
                Assert.That(messages.Logs, Has.None.Contains("*** Migrating"));
            }
            finally
            {
                LogManager.Configuration = previous;
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void requested_migration_version_must_not_use_the_latest_template(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, _ => { });
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var connection = database.OpenConnection();
                Assert.That(connection.Query<long>("SELECT \"Version\" FROM \"VersionInfo\""), Is.EqualTo(new long[] { 1 }));
            }, desiredVersion: 1);
        }

        [TestCase(false, MigrationType.Log, "Logs")]
        [TestCase(true, MigrationType.Log, "Logs")]
        [TestCase(false, MigrationType.Cache, "HttpResponse")]
        [TestCase(true, MigrationType.Cache, "HttpResponse")]
        public void component_templates_must_not_clone_the_main_schema(bool postgres, MigrationType migrationType, string table)
        {
            DualDatabaseFixture.Run(postgres, _ => { });
            for (var i = 0; i < 2; i++)
            {
                DualDatabaseFixture.Run(postgres, database =>
                {
                    using var connection = database.OpenConnection();
                    Assert.That(HasTable(connection, postgres, table), Is.True);
                    Assert.That(HasTable(connection, postgres, "Authors"), Is.False);
                }, migrationType);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void failed_test_must_remove_only_its_own_database(bool postgres)
        {
            string name = null;
            DualDatabaseFixture.Run(postgres, survivor =>
            {
                Assert.That(() => DualDatabaseFixture.Run(postgres, database =>
                {
                    using var connection = database.OpenConnection();
                    name = postgres ? ((NpgsqlConnection)connection).Database : ((SqliteConnection)connection).DataSource;
                    throw new InvalidOperationException("fixture failure");
                }), Throws.TypeOf<InvalidOperationException>().With.Message.EqualTo("fixture failure"));

                using var remaining = survivor.OpenConnection();
                Assert.That(HasTable(remaining, postgres, "Authors"), Is.True);
                if (postgres)
                {
                    Assert.That(remaining.ExecuteScalar<int>("SELECT COUNT(*) FROM pg_database WHERE datname = @name", new { name }), Is.Zero);
                }
                else
                {
                    Assert.That(File.Exists(name), Is.False);
                    Assert.That(File.Exists(name + "-wal"), Is.False);
                    Assert.That(File.Exists(name + "-shm"), Is.False);
                }
            });
        }

        private static bool HasTable(IDbConnection connection, bool postgres, string name) => connection.ExecuteScalar<int>(postgres
            ? "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = @name"
            : "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name", new { name }) == 1;
    }
}
