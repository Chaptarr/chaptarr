using System;
using System.IO;
using Dapper;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using NzbDrone.Core.Datastore.Migration;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    public class StringifiedProviderIdRepairFixture
    {
        private const string Poison = "System.Collections.Generic.List`1[System.String]";

        [Test]
        public void should_clear_stringified_provider_ids_without_deleting_user_data()
        {
            WithDatabase(connection =>
            {
                connection.Execute($@"
                    INSERT INTO ""Authors"" (""Id"", ""LastInfoSync"")
                    VALUES
                        (10, '2026-05-19 01:00:00'),
                        (11, '2026-05-19 01:00:00');

                    INSERT INTO ""AuthorSyncMetadata"" (""Id"", ""AuthorId"", ""NextSyncNotBefore"")
                    VALUES
                        (1, 10, '2026-05-20 01:00:00'),
                        (2, 11, '2026-05-20 01:00:00');

                    INSERT INTO ""Books"" (
                        ""Id"", ""AuthorId"", ""ForeignEditionId"", ""GoodreadsBookId"", ""GoodreadsWorkId"",
                        ""HardcoverBookId"", ""OpenLibraryEditionId"", ""OpenLibraryWorkId"", ""GoogleBooksId"",
                        ""ASIN"", ""AudibleASIN"", ""BaseBookId"", ""LastInfoSync""
                    )
                    VALUES
                        (20, 10, '{Poison}', NULL, 'gr:123', NULL, NULL, NULL, NULL, NULL, NULL, '{Poison}', '2026-05-19 02:00:00'),
                        (21, 11, NULL, NULL, 'gr:999', NULL, NULL, NULL, NULL, NULL, NULL, 'gr:999', '2026-05-19 02:00:00');

                    INSERT INTO ""Editions"" (
                        ""Id"", ""BookId"", ""ForeignEditionId"", ""HardcoverEditionId"",
                        ""OpenLibraryEditionId"", ""GoogleBooksEditionId"", ""Asin"", ""AudibleASIN""
                    )
                    VALUES
                        (30, 20, 'hc:edition:{Poison}-audiobook', '{Poison}', '{Poison}', '{Poison}', 'B01MTQMK2A', NULL);

                    INSERT INTO ""BookFiles"" (""Id"", ""EditionId"")
                    VALUES (40, 30);

                    INSERT INTO ""ProviderAliasIndex"" (""Id"", ""EntityType"", ""EntityId"", ""Scope"", ""Provider"", ""NormalizedProviderId"")
                    VALUES
                        (1, 'Book', 20, 'edition', 'hc', '{Poison}'),
                        (2, 'Edition', 30, 'edition', 'hc', 'edition:{Poison}'),
                        (3, 'Book', 20, 'work', 'gr', '123');
                ");

                ApplyRepair(connection);

                var poisonCount = connection.QuerySingle<int>($@"
                    SELECT
                        (SELECT COUNT(*) FROM ""Books""
                         WHERE ""ForeignEditionId"" LIKE '%System.Collections%'
                            OR ""BaseBookId"" LIKE '%System.Collections%')
                      + (SELECT COUNT(*) FROM ""Editions""
                         WHERE ""ForeignEditionId"" LIKE '%System.Collections%'
                            OR ""HardcoverEditionId"" LIKE '%System.Collections%'
                            OR ""OpenLibraryEditionId"" LIKE '%System.Collections%'
                            OR ""GoogleBooksEditionId"" LIKE '%System.Collections%')
                      + (SELECT COUNT(*) FROM ""ProviderAliasIndex""
                         WHERE ""NormalizedProviderId"" LIKE '%System.Collections%');");

                Assert.That(poisonCount, Is.EqualTo(0));
                Assert.That(connection.QuerySingle<int>(@"SELECT COUNT(*) FROM ""BookFiles"" WHERE ""EditionId"" = 30;"), Is.EqualTo(1));
                Assert.That(connection.QuerySingle<string>(@"SELECT ""Asin"" FROM ""Editions"" WHERE ""Id"" = 30;"), Is.EqualTo("B01MTQMK2A"));
                Assert.That(connection.QuerySingle<string>(@"SELECT ""GoodreadsWorkId"" FROM ""Books"" WHERE ""Id"" = 20;"), Is.EqualTo("gr:123"));
                Assert.That(connection.QuerySingle<int>(@"SELECT COUNT(*) FROM ""ProviderAliasIndex"" WHERE ""NormalizedProviderId"" = '123';"), Is.EqualTo(1));
                Assert.That(connection.QuerySingle<DateTime?>(@"SELECT ""LastInfoSync"" FROM ""Books"" WHERE ""Id"" = 20;"), Is.Null);
                Assert.That(connection.QuerySingle<DateTime?>(@"SELECT ""LastInfoSync"" FROM ""Authors"" WHERE ""Id"" = 10;"), Is.Null);
                Assert.That(connection.QuerySingle<DateTime?>(@"SELECT ""NextSyncNotBefore"" FROM ""AuthorSyncMetadata"" WHERE ""AuthorId"" = 10;"), Is.Null);
                Assert.That(connection.QuerySingle<DateTime?>(@"SELECT ""LastInfoSync"" FROM ""Books"" WHERE ""Id"" = 21;"), Is.Not.Null);
                Assert.That(connection.QuerySingle<DateTime?>(@"SELECT ""NextSyncNotBefore"" FROM ""AuthorSyncMetadata"" WHERE ""AuthorId"" = 11;"), Is.Not.Null);
            });
        }

        private static void ApplyRepair(SqliteConnection connection)
        {
            using var transaction = connection.BeginTransaction();
            StringifiedProviderIdRepair.Apply(connection, transaction, isPostgres: false);
            transaction.Commit();
        }

        private static void WithDatabase(Action<SqliteConnection> test)
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"stringified_provider_id_repair_{Guid.NewGuid():N}.db");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString();

            try
            {
                using var connection = new SqliteConnection(connectionString);
                connection.Open();
                CreateSchema(connection);
                test(connection);
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

        private static void CreateSchema(SqliteConnection connection)
        {
            connection.Execute(@"
                CREATE TABLE ""Authors"" (
                    ""Id"" INTEGER PRIMARY KEY,
                    ""LastInfoSync"" DATETIME NULL
                );

                CREATE TABLE ""AuthorSyncMetadata"" (
                    ""Id"" INTEGER PRIMARY KEY,
                    ""AuthorId"" INTEGER NOT NULL,
                    ""NextSyncNotBefore"" DATETIME NULL
                );

                CREATE TABLE ""Books"" (
                    ""Id"" INTEGER PRIMARY KEY,
                    ""AuthorId"" INTEGER NOT NULL,
                    ""ForeignEditionId"" TEXT NULL,
                    ""GoodreadsBookId"" TEXT NULL,
                    ""GoodreadsWorkId"" TEXT NULL,
                    ""HardcoverBookId"" TEXT NULL,
                    ""OpenLibraryEditionId"" TEXT NULL,
                    ""OpenLibraryWorkId"" TEXT NULL,
                    ""GoogleBooksId"" TEXT NULL,
                    ""ASIN"" TEXT NULL,
                    ""AudibleASIN"" TEXT NULL,
                    ""BaseBookId"" TEXT NULL,
                    ""LastInfoSync"" DATETIME NULL
                );

                CREATE TABLE ""Editions"" (
                    ""Id"" INTEGER PRIMARY KEY,
                    ""BookId"" INTEGER NOT NULL,
                    ""ForeignEditionId"" TEXT NULL,
                    ""HardcoverEditionId"" TEXT NULL,
                    ""OpenLibraryEditionId"" TEXT NULL,
                    ""GoogleBooksEditionId"" TEXT NULL,
                    ""Asin"" TEXT NULL,
                    ""AudibleASIN"" TEXT NULL
                );

                CREATE TABLE ""BookFiles"" (
                    ""Id"" INTEGER PRIMARY KEY,
                    ""EditionId"" INTEGER NULL
                );

                CREATE TABLE ""ProviderAliasIndex"" (
                    ""Id"" INTEGER PRIMARY KEY,
                    ""EntityType"" TEXT NOT NULL,
                    ""EntityId"" INTEGER NOT NULL,
                    ""Scope"" TEXT NOT NULL,
                    ""Provider"" TEXT NOT NULL,
                    ""NormalizedProviderId"" TEXT NOT NULL
                );
            ");
        }
    }
}
