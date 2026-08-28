using System;
using System.IO;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    public class AuthorStatisticsEditionLookupIndexMigrationFixture
    {
        [Test]
        public void should_add_the_monitored_edition_lookup_index_in_query_order()
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"author_statistics_index_{Guid.NewGuid():N}.db");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString();

            try
            {
                using var connection = new SqliteConnection(connectionString);
                connection.Open();
                connection.Execute(@"
                    CREATE TABLE ""VersionInfo"" (
                        ""Version"" INTEGER PRIMARY KEY,
                        ""AppliedOn"" TEXT NULL,
                        ""Description"" TEXT NULL
                    );
                    WITH RECURSIVE versions(version) AS
                    (
                        SELECT 1
                        UNION ALL
                        SELECT version + 1 FROM versions WHERE version < 104
                    )
                    INSERT INTO ""VersionInfo"" (""Version"", ""AppliedOn"", ""Description"")
                    SELECT version, CURRENT_TIMESTAMP, 'test baseline' FROM versions;

                    CREATE TABLE ""Editions"" (
                        ""Id"" INTEGER PRIMARY KEY,
                        ""BookId"" INTEGER NOT NULL,
                        ""Monitored"" INTEGER NOT NULL
                    );
                ");

                var migrationController = new MigrationController(LogManager.GetLogger("AuthorStatisticsEditionLookupIndexMigrationFixture"), null);
                migrationController.Migrate(
                    connectionString,
                    new MigrationContext(MigrationType.Main, 105),
                    DatabaseType.SQLite);

                var indexCount = connection.QuerySingle<int>(@"
                    SELECT COUNT(*)
                    FROM pragma_index_list('Editions')
                    WHERE name = 'IX_Editions_BookId_Monitored_Id';");
                var columns = connection.Query<string>(@"
                    SELECT name
                    FROM pragma_index_info('IX_Editions_BookId_Monitored_Id')
                    ORDER BY seqno;").ToArray();

                Assert.Multiple(() =>
                {
                    Assert.That(indexCount, Is.EqualTo(1));
                    Assert.That(columns, Is.EqualTo(new[] { "BookId", "Monitored", "Id" }));
                });
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
