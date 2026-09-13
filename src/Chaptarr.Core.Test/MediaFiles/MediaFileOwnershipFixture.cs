using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using Npgsql;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class MediaFileOwnershipFixture
    {
        [TestCase(false)]
        [TestCase(true)]
        public void inventory_and_unmapped_record_cleanup_preserve_other_owners_on_both_engines(bool postgres)
        {
            var pgConnection = Environment.GetEnvironmentVariable("CHAPTARR_TEST_POSTGRES_CONNECTION_STRING");
            if (postgres && string.IsNullOrWhiteSpace(pgConnection))
            {
                Assert.Ignore("Set CHAPTARR_TEST_POSTGRES_CONNECTION_STRING to exercise PostgreSQL temporary tables");
            }

            if (TableMapping.Mapper.TableMap.Count == 0)
            {
                TableMapping.Map();
            }

            IDbConnection Open()
            {
                IDbConnection connection = postgres
                    ? new NpgsqlConnection(pgConnection)
                    : new SqliteConnection("Data Source=:memory:;Pooling=False");
                connection.Open();
                try
                {
                    // Session-local tables shadow application names; no library data is modified.
                    connection.Execute(@"CREATE TEMP TABLE ""Books"" (""Id"" INTEGER PRIMARY KEY, ""AuthorId"" INTEGER);
                        CREATE TEMP TABLE ""Editions"" (""Id"" INTEGER PRIMARY KEY, ""BookId"" INTEGER);
                        CREATE TEMP TABLE ""BookFiles"" (""Id"" INTEGER PRIMARY KEY, ""EditionId"" INTEGER, ""Path"" TEXT, ""ReplicaPaths"" TEXT);
                        INSERT INTO ""Books"" VALUES (1, 10), (2, 20);
                        INSERT INTO ""Editions"" VALUES (11, 1), (12, 1), (21, 2);
                        INSERT INTO ""BookFiles"" VALUES
                            (1, 11, '/audio/A/book.m4b', '[]'),
                            (2, 12, '/ebooks/A/book.epub', '[""/audio/A/copy.epub""]'),
                            (3, 21, '/audio/A/other-author.epub', NULL),
                            (4, 0, '/audio/A/unmapped.m4b', '[]');");
                    return connection;
                }
                catch
                {
                    connection.Dispose();
                    throw;
                }
            }

            using var connection = Open();
            var lease = AuthorDeletionSafetyFixture.Proxy<IDbConnection>((method, args) =>
                method.Name == nameof(IDisposable.Dispose) ? null : method.Invoke(connection, args));
            var database = AuthorDeletionSafetyFixture.Proxy<IMainDatabase>((method, args) => method.Name switch
            {
                nameof(IMainDatabase.OpenConnection) => lease,
                "get_DatabaseType" => postgres ? DatabaseType.PostgreSQL : DatabaseType.SQLite,
                _ => throw new NotImplementedException(method.Name)
            });
            var repository = new MediaFileRepository(database, null);
            var files = repository.GetFilePathOwnership();
            Assert.That(files, Has.Count.EqualTo(4));
            Assert.That(files.Single(file => file.Id == 1).Author.Id, Is.EqualTo(10));
            Assert.That(files.Single(file => file.Id == 2).Author.Id, Is.EqualTo(10));
            Assert.That(files.Single(file => file.Id == 2).ReplicaPaths, Is.EqualTo(new[] { "/audio/A/copy.epub" }));
            Assert.That(files.Single(file => file.Id == 3).Author.Id, Is.EqualTo(20));
            Assert.That(files.Single(file => file.Id == 4).Author, Is.Null);

            var purgedPaths = new List<string>();
            var ingest = AuthorDeletionSafetyFixture.Proxy<IIngestQueueRepository>((method, args) =>
            {
                if (method.Name != nameof(IIngestQueueRepository.PurgeUnderPath)) throw new NotImplementedException(method.Name);
                purgedPaths.Add((string)args[0]);
                return method.ReturnType == typeof(void) ? null : 1;
            });
            var events = AuthorDeletionSafetyFixture.Proxy<IEventAggregator>((method, args) =>
                throw new InvalidOperationException("Record-only cleanup must not publish physical deletion events"));
            var service = new MediaFileService(repository, events, ingest, null, LogManager.GetCurrentClassLogger());
            service.DeleteRecordsForAuthorDeletion(files.Where(file => file.Author == null).ToList());
            Assert.That(connection.Query<int>(@"SELECT ""Id"" FROM ""BookFiles"" ORDER BY ""Id"""), Is.EqualTo(new[] { 1, 2, 3 }));
            Assert.That(purgedPaths, Is.EqualTo(new[] { "/audio/A/unmapped.m4b" }));
        }
    }
}
