using System;
using System.Collections.Generic;
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
        // Templates belong to this test process only; each callback owns an independent clone.
        private static readonly Dictionary<(bool Postgres, MigrationType Type, string Admin), string> Templates = new();

        public static void Run(bool postgres, Action<IMainDatabase> test, MigrationType migrationType = MigrationType.Main, long? desiredVersion = null)
        {
            var adminString = Environment.GetEnvironmentVariable("CHAPTARR_TEST_POSTGRES_CONNECTION_STRING");
            if (postgres && string.IsNullOrEmpty(adminString))
            {
                Assert.Ignore("Set CHAPTARR_TEST_POSTGRES_CONNECTION_STRING to exercise PostgreSQL persistence");
            }

            var name = DatabaseName(postgres, "api_identity_");
            var connectionString = ConnectionString(postgres, name, adminString);

            IDbConnection Open()
            {
                IDbConnection connection = postgres ? new NpgsqlConnection(connectionString) : new SqliteConnection(connectionString);
                connection.Open();
                return connection;
            }

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
                if (TableMapping.Mapper.TableMap.Count == 0)
                {
                    TableMapping.Map();
                }

                var database = new MainDatabase(new Database("dual-database-regression", Open));
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

        private static string ConnectionString(bool postgres, string name, string adminString) => postgres
            ? new NpgsqlConnectionStringBuilder(adminString) { Database = name, Pooling = false }.ConnectionString
            : new SqliteConnectionStringBuilder { DataSource = name, Pooling = false }.ToString();

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
