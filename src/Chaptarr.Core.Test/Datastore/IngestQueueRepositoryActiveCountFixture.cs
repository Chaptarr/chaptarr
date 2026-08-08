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
    public class IngestQueueRepositoryActiveCountFixture
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
        public void get_active_count_should_include_queued_pending_author_import_items()
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"staging_active_{Guid.NewGuid():N}.db");
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
                    ");

                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                    connection.Execute(@"
                        INSERT INTO ingest_queue (id, path, mtime_ns, size_bytes, tags_json, status, err, attempts, created_at, updated_at)
                        VALUES
                            (1, '/scan/book1.m4b', 0, 0, '{}', 'queued', 'PENDING_AUTHOR_IMPORT', 0, @now, @now),
                            (2, '/scan/book2.m4b', 0, 0, '{}', 'queued', NULL, 0, @now, @now),
                            (3, '/scan/book3.m4b', 0, 0, '{}', 'in_progress', NULL, 0, @now, @now),
                            (4, '/other/book4.m4b', 0, 0, '{}', 'queued', NULL, 0, @now, @now);
                    ", new { now });
                }

                var sut = new IngestQueueRepository(new TestStagingDbContext(connectionString), LogManager.GetLogger("test"));

                var count = sut.GetActiveCountUnderPath("/scan");

                Assert.That(count, Is.EqualTo(3), "Queued pending-author-import items now stay visible in the active count until they are terminalized.");
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
