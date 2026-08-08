using System;
using System.IO;
using Dapper;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Housekeeping.Housekeepers;

namespace Chaptarr.Core.Test.Housekeeping
{
    [TestFixture]
    public class CleanupOrphanedBlocklistFixture
    {
        [Test]
        public void should_delete_orphaned_blocklist_entries_in_sqlite()
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"cleanup_orphaned_blocklist_{Guid.NewGuid():N}.db");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString();

            try
            {
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();

                    connection.Execute(@"
                        CREATE TABLE ""Authors"" (
                            ""GoodreadsAuthorId"" TEXT NULL,
                            ""HardcoverAuthorId"" TEXT NULL,
                            ""OpenLibraryAuthorId"" TEXT NULL,
                            ""AudnexusAuthorId"" TEXT NULL,
                            ""GoogleBooksAuthorId"" TEXT NULL
                        );

                        CREATE TABLE ""Blocklist"" (
                            ""Id"" INTEGER PRIMARY KEY AUTOINCREMENT,
                            ""AuthorProviderIds"" TEXT NULL
                        );
                    ");

                    connection.Execute(@"INSERT INTO ""Authors"" (""GoodreadsAuthorId"") VALUES ('gr:123');");
                    connection.Execute(@"INSERT INTO ""Blocklist"" (""AuthorProviderIds"") VALUES (NULL);");
                    connection.Execute(@"INSERT INTO ""Blocklist"" (""AuthorProviderIds"") VALUES (NULL);");
                }

                var database = new Database("main", () =>
                {
                    var conn = new SqliteConnection(connectionString);
                    conn.Open();
                    return conn;
                });

                var mainDatabase = new MainDatabase(database);
                var sut = new CleanupOrphanedBlocklist(mainDatabase);

                Assert.DoesNotThrow(() => sut.Clean());

                using (var verifyConnection = new SqliteConnection(connectionString))
                {
                    verifyConnection.Open();
                    var remaining = verifyConnection.QuerySingle<int>(@"SELECT COUNT(*) FROM ""Blocklist"";");
                    Assert.That(remaining, Is.EqualTo(0));
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
