using System;
using System.IO;
using Dapper;
using Microsoft.Data.Sqlite;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    public class MetadataProfilePreferencesMigrationFixture
    {
        [Test]
        public void should_add_preference_columns_to_existing_metadata_profiles()
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"metadata_profile_preferences_{Guid.NewGuid():N}.db");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();

            try
            {
                using (var connection = new SqliteConnection(connectionString))
                {
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
                            SELECT version + 1 FROM versions WHERE version < 114
                        )
                        INSERT INTO ""VersionInfo"" (""Version"", ""AppliedOn"", ""Description"")
                        SELECT version, CURRENT_TIMESTAMP, 'test baseline' FROM versions;
                        CREATE TABLE ""Authors"" (
                            ""Id"" INTEGER PRIMARY KEY,
                            ""AudiobookRootFolderPath"" TEXT NULL,
                            ""EbookRootFolderPath"" TEXT NULL
                        );
                        CREATE TABLE ""RootFolders"" (
                            ""Id"" INTEGER PRIMARY KEY,
                            ""Path"" TEXT NOT NULL
                        );
                        CREATE TABLE ""MetadataProfiles"" (
                            ""Id"" INTEGER PRIMARY KEY,
                            ""Name"" TEXT NOT NULL
                        );
                        INSERT INTO ""MetadataProfiles"" (""Id"", ""Name"") VALUES (1, 'Existing profile');");
                }

                var migrationController = new MigrationController(
                    LogManager.GetLogger(nameof(MetadataProfilePreferencesMigrationFixture)),
                    null);
                migrationController.Migrate(
                    connectionString,
                    new MigrationContext(MigrationType.Main, 118),
                    DatabaseType.SQLite);

                using var resultConnection = new SqliteConnection(connectionString);
                resultConnection.Open();
                var columns = resultConnection.Query<string>(
                    "SELECT name FROM pragma_table_info('MetadataProfiles');").AsList();

                Assert.Multiple(() =>
                {
                    Assert.That(columns, Does.Contain("PreferredEditionTerms"));
                    Assert.That(columns, Does.Contain("PreferredSeries"));
                    Assert.That(resultConnection.QuerySingle<string>("SELECT Name FROM MetadataProfiles WHERE Id = 1;"), Is.EqualTo("Existing profile"));
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
