using System;
using System.Data;
using System.IO;
using Dapper;
using Microsoft.Data.Sqlite;
using NLog;
using Npgsql;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace Chaptarr.Core.Test.Common
{
    public static class DualDatabaseFixture
    {
        public static void Run(bool postgres, Action<IMainDatabase> test, MigrationType migrationType = MigrationType.Main, long? desiredVersion = null)
        {
            var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"api_identity_{Guid.NewGuid():N}.db");
            var databaseName = "api_identity_" + Guid.NewGuid().ToString("N");
            var adminString = Environment.GetEnvironmentVariable("CHAPTARR_TEST_POSTGRES_CONNECTION_STRING");
            if (postgres && string.IsNullOrEmpty(adminString))
            {
                Assert.Ignore("Set CHAPTARR_TEST_POSTGRES_CONNECTION_STRING to exercise PostgreSQL persistence");
            }

            var connectionString = postgres
                ? new NpgsqlConnectionStringBuilder(adminString) { Database = databaseName, Pooling = false }.ConnectionString
                : new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
            if (postgres)
            {
                using var admin = new NpgsqlConnection(adminString);
                admin.Open();
                admin.Execute($"CREATE DATABASE \"{databaseName}\"");
            }

            IDbConnection Open()
            {
                IDbConnection connection = postgres ? new NpgsqlConnection(connectionString) : new SqliteConnection(connectionString);
                connection.Open();
                return connection;
            }

            try
            {
                var logger = LogManager.GetCurrentClassLogger();
                new MigrationController(logger, null).Migrate(connectionString, new MigrationContext(migrationType, desiredVersion),
                    postgres ? DatabaseType.PostgreSQL : DatabaseType.SQLite);
                if (TableMapping.Mapper.TableMap.Count == 0)
                {
                    TableMapping.Map();
                }

                var database = new MainDatabase(new Database("dual-database-regression", Open));
                test(database);
            }
            finally
            {
                if (postgres)
                {
                    using var admin = new NpgsqlConnection(adminString);
                    admin.Open();
                    admin.Execute($"DROP DATABASE \"{databaseName}\"");
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }
}
