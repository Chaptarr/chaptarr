using System;
using System.Collections.Generic;
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
        // Templates belong to this test process only; each callback owns an independent clone.
        private static readonly Dictionary<(bool Postgres, MigrationType Type, string Admin), string> Templates = new();

        public static void Run(bool postgres, Action<IMainDatabase> test, MigrationType migrationType = MigrationType.Main, long? desiredVersion = null)
        {
            var adminString = Environment.GetEnvironmentVariable("CHAPTARR_TEST_POSTGRES_CONNECTION_STRING");
            if (postgres && string.IsNullOrEmpty(adminString))
            {
                if (string.Equals(Environment.GetEnvironmentVariable("CHAPTARR_REQUIRE_POSTGRES"), "true", StringComparison.OrdinalIgnoreCase))
                {
                    Assert.Fail("CHAPTARR_REQUIRE_POSTGRES is true, but CHAPTARR_TEST_POSTGRES_CONNECTION_STRING is missing.");
                }
                Assert.Ignore("Set CHAPTARR_TEST_POSTGRES_CONNECTION_STRING to exercise PostgreSQL persistence");
            }

            var name = DatabaseName(postgres, "api_identity_");
            var connectionString = ConnectionString(postgres, name, adminString, pooled: !postgres);

            try
            {
                if (desiredVersion.HasValue)
                {
                    CreateDatabase(postgres, name, adminString);
                    Migrate(postgres, connectionString, migrationType, desiredVersion);
                }
                else
                {
                    CreateDatabase(postgres, name, adminString, GetTemplate(postgres, migrationType, adminString));
                }
                var connectionInfo = new DatabaseConnectionInfo(postgres ? DatabaseType.PostgreSQL : DatabaseType.SQLite, connectionString);
                // The clone is already migrated; use the application's connection initialization.
                var factory = new DbFactory(
                    InterfaceProxy.Proxy<IMigrationController>((_, _) => null),
                    InterfaceProxy.Proxy<IConnectionStringFactory>((_, _) => connectionInfo),
                    null);
                var database = new MainDatabase(factory.Create(migrationType));
                test(database);
            }
            finally
            {
                DeleteDatabase(postgres, name, adminString);
            }
        }

        private static string DatabaseName(bool postgres, string prefix)
        {
            var name = prefix + Guid.NewGuid().ToString("N");
            return postgres ? name : Path.Combine(TestContext.CurrentContext.WorkDirectory, name + ".db");
        }

        private static string ConnectionString(bool postgres, string name, string adminString, bool pooled = false) => postgres
            // Test clones must support reopening their connections for migrations and restart checks.
            ? new NpgsqlConnectionStringBuilder(adminString) { Database = name, Pooling = false, PersistSecurityInfo = true }.ConnectionString
            : new SqliteConnectionStringBuilder { DataSource = name, Pooling = pooled, Cache = SqliteCacheMode.Private }.ToString();

        private static void Migrate(bool postgres, string connectionString, MigrationType migrationType, long? desiredVersion = null)
        {
            new MigrationController(LogManager.GetCurrentClassLogger(), null).Migrate(connectionString,
                new MigrationContext(migrationType, desiredVersion), postgres ? DatabaseType.PostgreSQL : DatabaseType.SQLite);
        }

        private static string GetTemplate(bool postgres, MigrationType migrationType, string adminString)
        {
            lock (Templates)
            {
                var key = (postgres, migrationType, postgres ? adminString : null);
                if (Templates.TryGetValue(key, out var existing))
                {
                    return existing;
                }

                var name = DatabaseName(postgres, "chaptarr_test_template_");
                try
                {
                    CreateDatabase(postgres, name, adminString);
                    Migrate(postgres, ConnectionString(postgres, name, adminString), migrationType);
                    Templates.Add(key, name);
                    return name;
                }
                catch
                {
                    DeleteDatabase(postgres, name, adminString);
                    throw;
                }
            }
        }

        private static void CreateDatabase(bool postgres, string name, string adminString, string template = null)
        {
            if (postgres)
            {
                using var admin = new NpgsqlConnection(adminString);
                admin.Open();
                admin.Execute($"CREATE DATABASE \"{name}\"" + (template == null ? "" : $" TEMPLATE \"{template}\""));
            }
            else if (template != null)
            {
                File.Copy(template, name);
            }
        }

        private static void DeleteDatabase(bool postgres, string name, string adminString)
        {
            if (postgres)
            {
                using var admin = new NpgsqlConnection(adminString);
                admin.Open();
                admin.Execute($"DROP DATABASE IF EXISTS \"{name}\"");
            }
            else
            {
                using var connection = new SqliteConnection(ConnectionString(false, name, null, pooled: true));
                SqliteConnection.ClearPool(connection);
                File.Delete(name);
            }
        }

        internal static void CleanupTemplates()
        {
            lock (Templates)
            {
                var failures = new List<Exception>();
                foreach (var template in Templates)
                {
                    try
                    {
                        DeleteDatabase(template.Key.Postgres, template.Value, template.Key.Admin);
                    }
                    catch (Exception error)
                    {
                        failures.Add(error);
                    }
                }

                Templates.Clear();
                if (failures.Count > 0)
                {
                    throw new AggregateException("Unable to remove test database templates", failures);
                }
            }
        }
    }
}
