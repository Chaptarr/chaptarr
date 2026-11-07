using System;
using System.IO;
using Dapper;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class AuthorMergeUniqueProviderIdFixture
    {
        private string _databasePath;
        private string _connectionString;

        [SetUp]
        public void Setup()
        {
            _databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"author_merge_unique_{Guid.NewGuid():N}.db");
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString();

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            connection.Execute(@"
                CREATE TABLE ""Authors"" (
                    ""Id"" INTEGER PRIMARY KEY,
                    ""HardcoverAuthorId"" TEXT NULL,
                    ""GoodreadsAuthorId"" TEXT NULL,
                    ""AudnexusAuthorId"" TEXT NULL,
                    ""OpenLibraryAuthorId"" TEXT NULL,
                    ""GoogleBooksAuthorId"" TEXT NULL,
                    ""RanobeDbAuthorId"" TEXT NULL
                );
                CREATE UNIQUE INDEX ""UX_Authors_HardcoverAuthorId"" ON ""Authors"" (""HardcoverAuthorId"");
                CREATE UNIQUE INDEX ""UX_Authors_GoodreadsAuthorId"" ON ""Authors"" (""GoodreadsAuthorId"");
                CREATE UNIQUE INDEX ""UX_Authors_AudnexusAuthorId"" ON ""Authors"" (""AudnexusAuthorId"");
                CREATE UNIQUE INDEX ""UX_Authors_OpenLibraryAuthorId"" ON ""Authors"" (""OpenLibraryAuthorId"");
                CREATE UNIQUE INDEX ""UX_Authors_GoogleBooksAuthorId"" ON ""Authors"" (""GoogleBooksAuthorId"");
                CREATE UNIQUE INDEX ""UX_Authors_RanobeDbAuthorId"" ON ""Authors"" (""RanobeDbAuthorId"");

                INSERT INTO ""Authors"" (
                    ""Id"", ""HardcoverAuthorId"", ""GoodreadsAuthorId"", ""AudnexusAuthorId"",
                    ""OpenLibraryAuthorId"", ""GoogleBooksAuthorId"", ""RanobeDbAuthorId"")
                VALUES (416, 'hc:123', 'gr:12345', 'az:B000APO0PQ', 'ol:123', 'gb:123', 'rdb:123');
                INSERT INTO ""Authors"" (""Id"") VALUES (1577);
            ");
        }

        [TearDown]
        public void TearDown()
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(_databasePath))
            {
                File.Delete(_databasePath);
            }
        }

        [Test]
        public void saving_the_survivor_while_the_source_still_owns_the_id_reproduces_issue_49()
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            var ex = Assert.Throws<SqliteException>(() => connection.Execute(
                @"UPDATE ""Authors"" SET ""AudnexusAuthorId"" = 'az:B000APO0PQ' WHERE ""Id"" = 1577;"));

            Assert.That(ex.Message, Does.Contain("UNIQUE constraint failed: Authors.AudnexusAuthorId"));
        }

        [Test]
        public void handoff_should_transfer_all_unique_provider_ids_without_violating_the_indexes()
        {
            var target = new Author
            {
                Id = 1577,
                HardcoverAuthorId = "hc:123",
                GoodreadsAuthorId = "gr:12345",
                AudnexusAuthorId = "az:B000APO0PQ",
                OpenLibraryAuthorId = "ol:123",
                GoogleBooksAuthorId = "gb:123",
                RanobeDbAuthorId = "rdb:123"
            };

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using (var transaction = connection.BeginTransaction())
            {
                RefreshAuthorService.ReleaseAndTransferUniqueProviderIds(connection, transaction, sourceId: 416, target);
                connection.Execute(@"DELETE FROM ""Authors"" WHERE ""Id"" = 416;", transaction: transaction);
                transaction.Commit();
            }

            var survivor = connection.QuerySingle(@"SELECT ""HardcoverAuthorId"", ""GoodreadsAuthorId"", ""AudnexusAuthorId"",
                ""OpenLibraryAuthorId"", ""GoogleBooksAuthorId"", ""RanobeDbAuthorId"" FROM ""Authors"" WHERE ""Id"" = 1577;");
            Assert.That((string)survivor.HardcoverAuthorId, Is.EqualTo("hc:123"));
            Assert.That((string)survivor.AudnexusAuthorId, Is.EqualTo("az:B000APO0PQ"));
            Assert.That((string)survivor.GoodreadsAuthorId, Is.EqualTo("gr:12345"));
            Assert.That((string)survivor.OpenLibraryAuthorId, Is.EqualTo("ol:123"));
            Assert.That((string)survivor.GoogleBooksAuthorId, Is.EqualTo("gb:123"));
            Assert.That((string)survivor.RanobeDbAuthorId, Is.EqualTo("rdb:123"));
            Assert.That(connection.ExecuteScalar<long>(@"SELECT COUNT(*) FROM ""Authors"" WHERE ""Id"" = 416;"), Is.EqualTo(0));
        }

        [Test]
        public void rollback_should_leave_the_source_untouched()
        {
            var target = new Author
            {
                Id = 1577,
                HardcoverAuthorId = "hc:123",
                GoodreadsAuthorId = "gr:12345",
                AudnexusAuthorId = "az:B000APO0PQ",
                OpenLibraryAuthorId = "ol:123",
                GoogleBooksAuthorId = "gb:123",
                RanobeDbAuthorId = "rdb:123"
            };

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using (var transaction = connection.BeginTransaction())
            {
                RefreshAuthorService.ReleaseAndTransferUniqueProviderIds(connection, transaction, sourceId: 416, target);
                transaction.Rollback();
            }

            var source = connection.QuerySingle(@"SELECT ""HardcoverAuthorId"", ""GoodreadsAuthorId"", ""AudnexusAuthorId"",
                ""OpenLibraryAuthorId"", ""GoogleBooksAuthorId"", ""RanobeDbAuthorId"" FROM ""Authors"" WHERE ""Id"" = 416;");
            Assert.That((string)source.HardcoverAuthorId, Is.EqualTo("hc:123"));
            Assert.That((string)source.GoodreadsAuthorId, Is.EqualTo("gr:12345"));
            Assert.That((string)source.AudnexusAuthorId, Is.EqualTo("az:B000APO0PQ"));
            Assert.That((string)source.OpenLibraryAuthorId, Is.EqualTo("ol:123"));
            Assert.That((string)source.GoogleBooksAuthorId, Is.EqualTo("gb:123"));
            Assert.That((string)source.RanobeDbAuthorId, Is.EqualTo("rdb:123"));

            var survivor = connection.QuerySingle(@"SELECT ""HardcoverAuthorId"", ""GoodreadsAuthorId"", ""AudnexusAuthorId"",
                ""OpenLibraryAuthorId"", ""GoogleBooksAuthorId"", ""RanobeDbAuthorId"" FROM ""Authors"" WHERE ""Id"" = 1577;");
            Assert.That((string)survivor.HardcoverAuthorId, Is.Null);
            Assert.That((string)survivor.GoodreadsAuthorId, Is.Null);
            Assert.That((string)survivor.AudnexusAuthorId, Is.Null);
            Assert.That((string)survivor.OpenLibraryAuthorId, Is.Null);
            Assert.That((string)survivor.GoogleBooksAuthorId, Is.Null);
            Assert.That((string)survivor.RanobeDbAuthorId, Is.Null);
        }
    }
}
