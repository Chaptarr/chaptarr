using System;
using System.IO;
using Dapper;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using NzbDrone.Core.Datastore.Migration;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    public class AuthorMediaSettingsBackfillRepairFixture
    {
        [Test]
        public void should_repair_root_folder_metadata_and_backfill_audiobook_author_settings()
        {
            WithDatabase(connection =>
            {
                SeedCommonProfiles(connection);

                connection.Execute(@"
                    INSERT INTO ""RootFolders"" (""Id"", ""Path"", ""FolderType"", ""AudiobookSettings"", ""EbookSettings"")
                    VALUES (1, '/audio', 1, '{""QualityProfileId"":21,""MetadataProfileId"":0,""MonitorExisting"":2,""MonitorFuture"":true}', NULL);

                    INSERT INTO ""Authors"" (
                        ""Id"", ""AudiobookRootFolderPath"", ""AudiobookQualityProfileId"", ""AudiobookMetadataProfileId"",
                        ""AudiobookMonitorExisting"", ""AudiobookMonitorFuture""
                    )
                    VALUES (1, '/audio/', 0, 0, NULL, NULL);
                ");

                ApplyRepair(connection);

                var rootFolderSettings = JObject.Parse(connection.QuerySingle<string>(
                    @"SELECT ""AudiobookSettings"" FROM ""RootFolders"" WHERE ""Id"" = 1;"));

                Assert.That(rootFolderSettings["MetadataProfileId"]?.Value<int>(), Is.EqualTo(11));

                var author = connection.QuerySingle<AuthorProjection>(@"
                    SELECT ""AudiobookQualityProfileId"",
                           ""AudiobookMetadataProfileId"",
                           ""AudiobookMonitorExisting"",
                           ""AudiobookMonitorFuture""
                    FROM ""Authors""
                    WHERE ""Id"" = 1;");

                Assert.That(author.AudiobookQualityProfileId, Is.EqualTo(21));
                Assert.That(author.AudiobookMetadataProfileId, Is.EqualTo(11));
                Assert.That(author.AudiobookMonitorExisting, Is.EqualTo(2));
                Assert.That(author.AudiobookMonitorFuture, Is.True);
            });
        }

        [Test]
        public void should_not_backfill_ebook_settings_from_audiobook_only_root_folder()
        {
            WithDatabase(connection =>
            {
                SeedCommonProfiles(connection);

                connection.Execute(@"
                    INSERT INTO ""RootFolders"" (""Id"", ""Path"", ""FolderType"", ""AudiobookSettings"", ""EbookSettings"")
                    VALUES (
                        1,
                        '/audio',
                        1,
                        '{""QualityProfileId"":21,""MetadataProfileId"":11,""MonitorExisting"":2,""MonitorFuture"":true}',
                        '{""QualityProfileId"":22,""MetadataProfileId"":12,""MonitorExisting"":1,""MonitorFuture"":false}'
                    );

                    INSERT INTO ""Authors"" (
                        ""Id"", ""EbookRootFolderPath"", ""EbookQualityProfileId"", ""EbookMetadataProfileId"",
                        ""EbookMonitorExisting"", ""EbookMonitorFuture""
                    )
                    VALUES (1, '/audio', 0, 0, NULL, NULL);
                ");

                ApplyRepair(connection);

                var author = connection.QuerySingle<AuthorProjection>(@"
                    SELECT ""EbookQualityProfileId"",
                           ""EbookMetadataProfileId"",
                           ""EbookMonitorExisting"",
                           ""EbookMonitorFuture""
                    FROM ""Authors""
                    WHERE ""Id"" = 1;");

                Assert.That(author.EbookQualityProfileId, Is.EqualTo(0));
                Assert.That(author.EbookMetadataProfileId, Is.EqualTo(0));
                Assert.That(author.EbookMonitorExisting, Is.Null);
                Assert.That(author.EbookMonitorFuture, Is.Null);

                var ebookSettings = JObject.Parse(connection.QuerySingle<string>(
                    @"SELECT ""EbookSettings"" FROM ""RootFolders"" WHERE ""Id"" = 1;"));

                Assert.That(ebookSettings["MetadataProfileId"]?.Value<int>(), Is.EqualTo(12));
            });
        }

        [Test]
        public void should_repair_invalid_ebook_profile_ids_from_compatible_root_folder()
        {
            WithDatabase(connection =>
            {
                SeedCommonProfiles(connection);

                connection.Execute(@"
                    INSERT INTO ""RootFolders"" (""Id"", ""Path"", ""FolderType"", ""AudiobookSettings"", ""EbookSettings"")
                    VALUES (1, '/ebooks', 2, NULL, '{""QualityProfileId"":22,""MetadataProfileId"":12,""MonitorExisting"":1,""MonitorFuture"":false}');

                    INSERT INTO ""Authors"" (
                        ""Id"", ""EbookRootFolderPath"", ""EbookQualityProfileId"", ""EbookMetadataProfileId"",
                        ""EbookMonitorExisting"", ""EbookMonitorFuture""
                    )
                    VALUES (1, '/ebooks/', 999, 11, NULL, NULL);
                ");

                ApplyRepair(connection);

                var author = connection.QuerySingle<AuthorProjection>(@"
                    SELECT ""EbookQualityProfileId"",
                           ""EbookMetadataProfileId"",
                           ""EbookMonitorExisting"",
                           ""EbookMonitorFuture""
                    FROM ""Authors""
                    WHERE ""Id"" = 1;");

                Assert.That(author.EbookQualityProfileId, Is.EqualTo(22));
                Assert.That(author.EbookMetadataProfileId, Is.EqualTo(12));
                Assert.That(author.EbookMonitorExisting, Is.EqualTo(1));
                Assert.That(author.EbookMonitorFuture, Is.False);
            });
        }

        private static void ApplyRepair(SqliteConnection connection)
        {
            using var transaction = connection.BeginTransaction();
            AuthorMediaSettingsBackfillRepair.Apply(connection, transaction);
            transaction.Commit();
        }

        private static void SeedCommonProfiles(SqliteConnection connection)
        {
            connection.Execute(@"
                INSERT INTO ""MetadataProfiles"" (""Id"", ""Name"", ""ProfileType"")
                VALUES
                    (10, 'None', 0),
                    (11, 'Audiobook Default', 1),
                    (12, 'Ebook Default', 2);

                INSERT INTO ""QualityProfiles"" (""Id"", ""ProfileType"")
                VALUES
                    (21, 1),
                    (22, 2);
            ");
        }

        private static void WithDatabase(Action<SqliteConnection> test)
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"author_media_settings_backfill_{Guid.NewGuid():N}.db");
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
                CREATE TABLE ""MetadataProfiles"" (
                    ""Id"" INTEGER PRIMARY KEY,
                    ""Name"" TEXT NOT NULL,
                    ""ProfileType"" INTEGER NOT NULL
                );

                CREATE TABLE ""QualityProfiles"" (
                    ""Id"" INTEGER PRIMARY KEY,
                    ""ProfileType"" INTEGER NOT NULL
                );

                CREATE TABLE ""RootFolders"" (
                    ""Id"" INTEGER PRIMARY KEY,
                    ""Path"" TEXT NOT NULL,
                    ""FolderType"" INTEGER NOT NULL,
                    ""AudiobookSettings"" TEXT NULL,
                    ""EbookSettings"" TEXT NULL
                );

                CREATE TABLE ""Authors"" (
                    ""Id"" INTEGER PRIMARY KEY,
                    ""AudiobookRootFolderPath"" TEXT NULL,
                    ""EbookRootFolderPath"" TEXT NULL,
                    ""AudiobookQualityProfileId"" INTEGER NULL,
                    ""EbookQualityProfileId"" INTEGER NULL,
                    ""AudiobookMetadataProfileId"" INTEGER NULL,
                    ""EbookMetadataProfileId"" INTEGER NULL,
                    ""AudiobookMonitorExisting"" INTEGER NULL,
                    ""EbookMonitorExisting"" INTEGER NULL,
                    ""AudiobookMonitorFuture"" INTEGER NULL,
                    ""EbookMonitorFuture"" INTEGER NULL
                );
            ");
        }

        private class AuthorProjection
        {
            public int? AudiobookQualityProfileId { get; set; }
            public int? AudiobookMetadataProfileId { get; set; }
            public int? AudiobookMonitorExisting { get; set; }
            public bool? AudiobookMonitorFuture { get; set; }
            public int? EbookQualityProfileId { get; set; }
            public int? EbookMetadataProfileId { get; set; }
            public int? EbookMonitorExisting { get; set; }
            public bool? EbookMonitorFuture { get; set; }
        }
    }
}
