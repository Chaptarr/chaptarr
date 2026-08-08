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
    public class CleanupOrphanedNarratorReferencesFixture
    {
        [Test]
        public void should_remove_links_with_missing_owner_or_narrator()
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"cleanup_orphaned_narrators_{Guid.NewGuid():N}.db");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,

                // Windows holds pooled handles open past connection disposal, which blocks the temp-db delete below.
                Pooling = false
            }.ToString();

            try
            {
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    connection.Execute(@"
                        CREATE TABLE ""Narrators"" (""Id"" INTEGER PRIMARY KEY);
                        CREATE TABLE ""Books"" (
                            ""Id"" INTEGER PRIMARY KEY,
                            ""NarratorId"" INTEGER NULL,
                            ""WantedNarratorId"" INTEGER NULL
                        );
                        CREATE TABLE ""Editions"" (""Id"" INTEGER PRIMARY KEY);
                        CREATE TABLE ""Series"" (
                            ""Id"" INTEGER PRIMARY KEY,
                            ""PreferredNarratorId"" INTEGER NULL
                        );
                        CREATE TABLE ""BookNarratorLink"" (
                            ""Id"" INTEGER PRIMARY KEY,
                            ""BookId"" INTEGER NOT NULL,
                            ""NarratorId"" INTEGER NOT NULL
                        );
                        CREATE TABLE ""EditionNarratorLink"" (
                            ""Id"" INTEGER PRIMARY KEY,
                            ""EditionId"" INTEGER NOT NULL,
                            ""NarratorId"" INTEGER NOT NULL
                        );

                        INSERT INTO ""Narrators"" (""Id"") VALUES (1);
                        INSERT INTO ""Books"" (""Id"", ""NarratorId"", ""WantedNarratorId"") VALUES (10, 1, 1);
                        INSERT INTO ""Editions"" (""Id"") VALUES (20);
                        INSERT INTO ""Series"" (""Id"", ""PreferredNarratorId"") VALUES (30, 1);

                        INSERT INTO ""BookNarratorLink"" VALUES (1, 10, 1);
                        INSERT INTO ""BookNarratorLink"" VALUES (2, 999, 1);
                        INSERT INTO ""BookNarratorLink"" VALUES (3, 10, 999);
                        INSERT INTO ""EditionNarratorLink"" VALUES (1, 20, 1);
                        INSERT INTO ""EditionNarratorLink"" VALUES (2, 999, 1);
                        INSERT INTO ""EditionNarratorLink"" VALUES (3, 20, 999);
                    ");
                }

                var database = new Database("main", () =>
                {
                    var connection = new SqliteConnection(connectionString);
                    connection.Open();
                    return connection;
                });

                new CleanupOrphanedNarratorReferences(new MainDatabase(database)).Clean();

                using var verify = new SqliteConnection(connectionString);
                verify.Open();
                Assert.That(verify.ExecuteScalar<int>(@"SELECT COUNT(*) FROM ""BookNarratorLink"";"), Is.EqualTo(1));
                Assert.That(verify.ExecuteScalar<int>(@"SELECT COUNT(*) FROM ""EditionNarratorLink"";"), Is.EqualTo(1));
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
