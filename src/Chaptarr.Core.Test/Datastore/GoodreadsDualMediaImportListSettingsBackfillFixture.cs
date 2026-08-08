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
    public class GoodreadsDualMediaImportListSettingsBackfillFixture
    {
        [Test]
        public void should_migrate_audiobook_legacy_list_without_enabling_ebooks()
        {
            WithDatabase(connection =>
            {
                SeedProfiles(connection);

                connection.Execute(@"
                    INSERT INTO ""RootFolders"" (""Id"", ""Path"", ""FolderType"")
                    VALUES (1, '/audiobooks', 1);

                    INSERT INTO ""ImportLists"" (""Id"", ""Implementation"", ""Settings"", ""RootFolderPath"", ""QualityProfileId"", ""MetadataProfileId"", ""Tags"")
                    VALUES (1, 'GoodreadsListImportList', '{""listId"":""123""}', '/audiobooks/', 21, 11, '[5,3,5]');
                ");

                ApplyBackfill(connection);

                var settings = ReadSettings(connection, 1);

                Assert.That(settings["monitorAudiobooks"]?.Value<bool>(), Is.True);
                Assert.That(settings["monitorEbooks"]?.Value<bool>(), Is.False);
                Assert.That(settings["audiobookRootFolderPath"]?.Value<string>(), Is.EqualTo("/audiobooks/"));
                Assert.That(settings["ebookRootFolderPath"], Is.Null);
                Assert.That(settings["audiobookQualityProfileId"]?.Value<int>(), Is.EqualTo(21));
                Assert.That(settings["ebookQualityProfileId"], Is.Null);
                Assert.That(settings["audiobookMetadataProfileId"]?.Value<int>(), Is.EqualTo(11));
                Assert.That(settings["ebookMetadataProfileId"], Is.Null);
                Assert.That(settings["audiobookTags"]?.Values<int>(), Is.EquivalentTo(new[] { 3, 5 }));
                Assert.That(settings["ebookTags"], Is.Null);
            });
        }

        [Test]
        public void should_migrate_ebook_legacy_list_without_enabling_audiobooks()
        {
            WithDatabase(connection =>
            {
                SeedProfiles(connection);

                connection.Execute(@"
                    INSERT INTO ""RootFolders"" (""Id"", ""Path"", ""FolderType"")
                    VALUES (1, '/ebooks', 2);

                    INSERT INTO ""ImportLists"" (""Id"", ""Implementation"", ""Settings"", ""RootFolderPath"", ""QualityProfileId"", ""MetadataProfileId"", ""Tags"")
                    VALUES (1, 'GoodreadsSeriesImportList', '{""seriesId"":""456""}', '/ebooks', 22, 12, '[7]');
                ");

                ApplyBackfill(connection);

                var settings = ReadSettings(connection, 1);

                Assert.That(settings["monitorAudiobooks"]?.Value<bool>(), Is.False);
                Assert.That(settings["monitorEbooks"]?.Value<bool>(), Is.True);
                Assert.That(settings["audiobookRootFolderPath"], Is.Null);
                Assert.That(settings["ebookRootFolderPath"]?.Value<string>(), Is.EqualTo("/ebooks"));
                Assert.That(settings["audiobookQualityProfileId"], Is.Null);
                Assert.That(settings["ebookQualityProfileId"]?.Value<int>(), Is.EqualTo(22));
                Assert.That(settings["audiobookMetadataProfileId"], Is.Null);
                Assert.That(settings["ebookMetadataProfileId"]?.Value<int>(), Is.EqualTo(12));
                Assert.That(settings["audiobookTags"], Is.Null);
                Assert.That(settings["ebookTags"]?.Values<int>(), Is.EquivalentTo(new[] { 7 }));
            });
        }

        [Test]
        public void should_keep_mixed_root_dual_media_but_not_copy_wrong_typed_quality_profile()
        {
            WithDatabase(connection =>
            {
                SeedProfiles(connection);

                connection.Execute(@"
                    INSERT INTO ""RootFolders"" (""Id"", ""Path"", ""FolderType"")
                    VALUES (1, '/books', 0);

                    INSERT INTO ""ImportLists"" (""Id"", ""Implementation"", ""Settings"", ""RootFolderPath"", ""QualityProfileId"", ""MetadataProfileId"", ""Tags"")
                    VALUES (1, 'GoodreadsListImportList', '{""listId"":""123""}', '/books', 21, 10, '[9]');
                ");

                ApplyBackfill(connection);

                var settings = ReadSettings(connection, 1);

                Assert.That(settings["monitorAudiobooks"]?.Value<bool>(), Is.True);
                Assert.That(settings["monitorEbooks"]?.Value<bool>(), Is.True);
                Assert.That(settings["audiobookRootFolderPath"]?.Value<string>(), Is.EqualTo("/books"));
                Assert.That(settings["ebookRootFolderPath"]?.Value<string>(), Is.EqualTo("/books"));
                Assert.That(settings["audiobookQualityProfileId"]?.Value<int>(), Is.EqualTo(21));
                Assert.That(settings["ebookQualityProfileId"], Is.Null);
                Assert.That(settings["audiobookMetadataProfileId"]?.Value<int>(), Is.EqualTo(10));
                Assert.That(settings["ebookMetadataProfileId"]?.Value<int>(), Is.EqualTo(10));
                Assert.That(settings["audiobookTags"]?.Values<int>(), Is.EquivalentTo(new[] { 9 }));
                Assert.That(settings["ebookTags"]?.Values<int>(), Is.EquivalentTo(new[] { 9 }));
            });
        }

        [Test]
        public void should_not_overwrite_explicit_dual_media_settings()
        {
            WithDatabase(connection =>
            {
                SeedProfiles(connection);

                connection.Execute(@"
                    INSERT INTO ""RootFolders"" (""Id"", ""Path"", ""FolderType"")
                    VALUES (1, '/ebooks', 2);

                    INSERT INTO ""ImportLists"" (""Id"", ""Implementation"", ""Settings"", ""RootFolderPath"", ""QualityProfileId"", ""MetadataProfileId"", ""Tags"")
                    VALUES (
                        1,
                        'GoodreadsSeriesImportList',
                        '{""seriesId"":""456"",""monitorAudiobooks"":true,""audiobookRootFolderPath"":""/custom-audio"",""audiobookQualityProfileId"":21,""ebookQualityProfileId"":22}',
                        '/ebooks',
                        22,
                        12,
                        '[7]'
                    );
                ");

                ApplyBackfill(connection);

                var settings = ReadSettings(connection, 1);

                Assert.That(settings["monitorAudiobooks"]?.Value<bool>(), Is.True);
                Assert.That(settings["monitorEbooks"]?.Value<bool>(), Is.True);
                Assert.That(settings["audiobookRootFolderPath"]?.Value<string>(), Is.EqualTo("/custom-audio"));
                Assert.That(settings["audiobookQualityProfileId"]?.Value<int>(), Is.EqualTo(21));
                Assert.That(settings["ebookQualityProfileId"]?.Value<int>(), Is.EqualTo(22));
            });
        }

        [Test]
        public void should_infer_media_type_from_profile_when_root_folder_is_missing()
        {
            WithDatabase(connection =>
            {
                SeedProfiles(connection);

                connection.Execute(@"
                    INSERT INTO ""ImportLists"" (""Id"", ""Implementation"", ""Settings"", ""RootFolderPath"", ""QualityProfileId"", ""MetadataProfileId"", ""Tags"")
                    VALUES (1, 'GoodreadsListImportList', '{""listId"":""123""}', NULL, 21, 11, '[5]');
                ");

                ApplyBackfill(connection);

                var settings = ReadSettings(connection, 1);

                Assert.That(settings["monitorAudiobooks"]?.Value<bool>(), Is.True);
                Assert.That(settings["monitorEbooks"]?.Value<bool>(), Is.False);
                Assert.That(settings["audiobookRootFolderPath"], Is.Null);
                Assert.That(settings["ebookRootFolderPath"], Is.Null);
                Assert.That(settings["audiobookQualityProfileId"]?.Value<int>(), Is.EqualTo(21));
                Assert.That(settings["ebookQualityProfileId"], Is.Null);
            });
        }

        private static void ApplyBackfill(SqliteConnection connection)
        {
            using var transaction = connection.BeginTransaction();
            GoodreadsDualMediaImportListSettingsBackfill.Apply(connection, transaction);
            transaction.Commit();
        }

        private static JObject ReadSettings(SqliteConnection connection, int id)
        {
            var settings = connection.QuerySingle<string>(
                @"SELECT ""Settings"" FROM ""ImportLists"" WHERE ""Id"" = @Id;",
                new { Id = id });

            return JObject.Parse(settings);
        }

        private static void SeedProfiles(SqliteConnection connection)
        {
            connection.Execute(@"
                INSERT INTO ""QualityProfiles"" (""Id"", ""ProfileType"")
                VALUES
                    (21, 1),
                    (22, 2);

                INSERT INTO ""MetadataProfiles"" (""Id"", ""ProfileType"")
                VALUES
                    (10, 0),
                    (11, 1),
                    (12, 2);
            ");
        }

        private static void WithDatabase(Action<SqliteConnection> test)
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"goodreads_dual_media_import_list_backfill_{Guid.NewGuid():N}.db");
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
                CREATE TABLE ""QualityProfiles"" (
                    ""Id"" INTEGER PRIMARY KEY,
                    ""ProfileType"" INTEGER NOT NULL
                );

                CREATE TABLE ""MetadataProfiles"" (
                    ""Id"" INTEGER PRIMARY KEY,
                    ""ProfileType"" INTEGER NOT NULL
                );

                CREATE TABLE ""RootFolders"" (
                    ""Id"" INTEGER PRIMARY KEY,
                    ""Path"" TEXT NOT NULL,
                    ""FolderType"" INTEGER NOT NULL
                );

                CREATE TABLE ""ImportLists"" (
                    ""Id"" INTEGER PRIMARY KEY,
                    ""Implementation"" TEXT NOT NULL,
                    ""Settings"" TEXT NULL,
                    ""RootFolderPath"" TEXT NULL,
                    ""QualityProfileId"" INTEGER NOT NULL,
                    ""MetadataProfileId"" INTEGER NOT NULL,
                    ""Tags"" TEXT NULL
                );
            ");
        }
    }
}
