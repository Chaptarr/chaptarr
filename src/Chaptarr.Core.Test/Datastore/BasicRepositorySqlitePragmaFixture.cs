using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using Dapper;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    public class BasicRepositorySqlitePragmaFixture
    {
        private const int ExpectedTempStore = 1;
        private const long ExpectedCacheSize = -4096;

        private sealed class PooledPragmaModel : ModelBase
        {
            public string Name { get; set; }
        }

        private sealed class StubEventAggregator : IEventAggregator
        {
            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
            }
        }

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            if (!TableMapping.Mapper.TableMap.ContainsKey(typeof(PooledPragmaModel)))
            {
                TableMapping.Mapper.Entity<PooledPragmaModel>("PooledPragmaModels").RegisterModel();
            }
        }

        [Test]
        public void insert_many_should_restore_connection_pragmas()
        {
            WithRepository(false, repository =>
            {
                repository.InsertMany(new List<PooledPragmaModel>
                {
                    new PooledPragmaModel { Name = "First" },
                    new PooledPragmaModel { Name = "Second" }
                });
            });
        }

        [Test]
        public void update_many_should_restore_connection_pragmas()
        {
            WithRepository(true, repository =>
            {
                repository.UpdateMany(new List<PooledPragmaModel>
                {
                    new PooledPragmaModel { Id = 1, Name = "Updated first" },
                    new PooledPragmaModel { Id = 2, Name = "Updated second" }
                });
            });
        }

        [Test]
        public void set_fields_many_should_restore_connection_pragmas()
        {
            WithRepository(true, repository =>
            {
                repository.SetFields(new List<PooledPragmaModel>
                {
                    new PooledPragmaModel { Id = 1, Name = "Updated first" },
                    new PooledPragmaModel { Id = 2, Name = "Updated second" }
                }, model => model.Name);
            });
        }

        private static void WithRepository(bool seedRows, Action<BasicRepository<PooledPragmaModel>> action)
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"basic_repository_pragmas_{Guid.NewGuid():N}.db");
            var pooledConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                Pooling = true
            }.ToString();
            var setupConnectionString = new SqliteConnectionStringBuilder(pooledConnectionString)
            {
                Pooling = false
            }.ToString();

            try
            {
                using (var connection = new SqliteConnection(setupConnectionString))
                {
                    connection.Open();
                    connection.Execute(@"
                        CREATE TABLE ""PooledPragmaModels"" (
                            ""Id"" INTEGER PRIMARY KEY AUTOINCREMENT,
                            ""Name"" TEXT NULL
                        );
                    ");

                    if (seedRows)
                    {
                        connection.Execute(@"
                            INSERT INTO ""PooledPragmaModels"" (""Id"", ""Name"")
                            VALUES (1, 'First'), (2, 'Second');
                        ");
                    }
                }

                using (var connection = Open(pooledConnectionString))
                {
                    connection.Execute($"PRAGMA temp_store = {ExpectedTempStore}; PRAGMA cache_size = {ExpectedCacheSize};");
                    AssertPragmas(connection);
                }

                var database = new Database("pragma-test", () => Open(pooledConnectionString));
                action(new BasicRepository<PooledPragmaModel>(database, new StubEventAggregator()));

                using (var connection = Open(pooledConnectionString))
                {
                    AssertPragmas(connection);
                }
            }
            finally
            {
                using (var poolKey = new SqliteConnection(pooledConnectionString))
                {
                    SqliteConnection.ClearPool(poolKey);
                }

                if (File.Exists(databasePath))
                {
                    File.Delete(databasePath);
                }
            }
        }

        private static SqliteConnection Open(string connectionString)
        {
            var connection = new SqliteConnection(connectionString);
            connection.Open();
            return connection;
        }

        private static void AssertPragmas(IDbConnection connection)
        {
            Assert.That(connection.ExecuteScalar<int>("PRAGMA temp_store"), Is.EqualTo(ExpectedTempStore));
            Assert.That(connection.ExecuteScalar<long>("PRAGMA cache_size"), Is.EqualTo(ExpectedCacheSize));
        }
    }
}
