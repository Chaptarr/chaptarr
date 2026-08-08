using System;
using System.Data;
using System.IO;
using Dapper;
using Microsoft.Data.Sqlite;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Datastore;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    public class IngestQueueRepositoryPurgeOldCompletedFixture
    {
        private sealed class TestStagingDbContext : IStagingDbContext
        {
            private readonly string _connectionString;

            public TestStagingDbContext(string connectionString)
            {
                _connectionString = connectionString;
            }

            public IDbConnection OpenConnection()
            {
                var connection = new SqliteConnection(_connectionString);
                connection.Open();
                connection.Execute("PRAGMA foreign_keys=ON;");
                return connection;
            }

            public void InitializeDatabase()
            {
            }
        }

        [Test]
        public void should_purge_completed_queue_items_and_results_without_foreign_key_errors()
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"staging_purge_{Guid.NewGuid():N}.db");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                ForeignKeys = true
            }.ToString();

            try
            {
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    connection.Execute("PRAGMA foreign_keys=ON;");

                    connection.Execute(@"
                        CREATE TABLE ingest_queue(
                            id INTEGER PRIMARY KEY,
                            path TEXT NOT NULL UNIQUE,
                            mtime_ns INTEGER NOT NULL,
                            size_bytes INTEGER NOT NULL,
                            tags_json TEXT NOT NULL,
                            status TEXT NOT NULL DEFAULT 'queued',
                            attempts INTEGER NOT NULL DEFAULT 0,
                            err TEXT,
                            created_at INTEGER NOT NULL,
                            updated_at INTEGER NOT NULL
                        );

                        CREATE TABLE import_results(
                            id INTEGER PRIMARY KEY,
                            queue_item_id INTEGER NOT NULL,
                            path TEXT NOT NULL,
                            outcome TEXT NOT NULL CHECK(outcome IN ('imported', 'unmapped', 'failed')),
                            book_id INTEGER,
                            author_id INTEGER,
                            quality TEXT,
                            error_message TEXT,
                            created_at INTEGER NOT NULL,
                            FOREIGN KEY(queue_item_id) REFERENCES ingest_queue(id)
                        );
                    ");

                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var old = now - (15 * 24 * 60 * 60);

                    connection.Execute(@"
                        INSERT INTO ingest_queue (id, path, mtime_ns, size_bytes, tags_json, status, attempts, created_at, updated_at)
                        VALUES (1, '/tmp/book', 0, 0, '{}', 'done', 0, @old, @old);
                    ", new { old });

                    connection.Execute(@"
                        INSERT INTO import_results (id, queue_item_id, path, outcome, created_at)
                        VALUES (1, 1, '/tmp/book', 'imported', @old);
                    ", new { old });
                }

                var sut = new IngestQueueRepository(new TestStagingDbContext(connectionString), LogManager.GetLogger("test"));

                Assert.DoesNotThrow(() => sut.PurgeOldCompleted(daysToKeep: 14));

                using (var verifyConnection = new SqliteConnection(connectionString))
                {
                    verifyConnection.Open();
                    verifyConnection.Execute("PRAGMA foreign_keys=ON;");

                    var remainingQueueItems = verifyConnection.QuerySingle<int>("SELECT COUNT(*) FROM ingest_queue;");
                    var remainingResults = verifyConnection.QuerySingle<int>("SELECT COUNT(*) FROM import_results;");

                    Assert.That(remainingQueueItems, Is.EqualTo(0));
                    Assert.That(remainingResults, Is.EqualTo(0));
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(databasePath))
                    {
                        File.Delete(databasePath);
                    }
                }
                catch
                {
                }
            }
        }
    }
}

