using System;
using System.IO;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Housekeeping.Housekeepers;

namespace Chaptarr.Core.Test.Housekeeping
{
    [TestFixture]
    public class CleanupUnusedTagsFixture
    {
        [OneTimeSetUp]
        public void SetupTypeMappings()
        {
            if (TableMapping.Mapper.TableMap.Count == 0)
            {
                TableMapping.Map();
            }
        }

        [Test]
        public void should_keep_tags_referenced_only_by_pending_media_sides()
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"cleanup_pending_media_tags_{Guid.NewGuid():N}.db");
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
                        CREATE TABLE ""Tags"" (""Id"" INTEGER PRIMARY KEY);
                        CREATE TABLE ""Authors"" (""Tags"" TEXT NULL, ""AudiobookTags"" TEXT NULL, ""EbookTags"" TEXT NULL);
                        CREATE TABLE ""Notifications"" (""Tags"" TEXT NULL);
                        CREATE TABLE ""DelayProfiles"" (""Tags"" TEXT NULL);
                        CREATE TABLE ""ReleaseProfiles"" (""Tags"" TEXT NULL);
                        CREATE TABLE ""ImportLists"" (""Tags"" TEXT NULL);
                        CREATE TABLE ""Indexers"" (""Tags"" TEXT NULL);
                        CREATE TABLE ""DownloadClients"" (""Tags"" TEXT NULL, ""AudiobookTags"" TEXT NULL, ""EbookTags"" TEXT NULL);
                        CREATE TABLE ""Restrictions"" (""Tags"" TEXT NULL);
                        CREATE TABLE ""Narrators"" (""Tags"" TEXT NULL);
                        CREATE TABLE ""PendingAuthorImport"" (""Tags"" TEXT NULL, ""AudiobookTags"" TEXT NULL, ""EbookTags"" TEXT NULL);
                        CREATE TABLE ""RootFolders"" (""DefaultTags"" TEXT NULL, ""AudiobookSettings"" TEXT NULL, ""EbookSettings"" TEXT NULL);

                        INSERT INTO ""Tags"" (""Id"") VALUES (1), (2), (3);
                        INSERT INTO ""PendingAuthorImport"" (""AudiobookTags"", ""EbookTags"") VALUES ('[1]', '[2]');");
                }

                var database = new Database("main", () =>
                {
                    var connection = new SqliteConnection(connectionString);
                    connection.Open();
                    return connection;
                });

                new CleanupUnusedTags(new MainDatabase(database)).Clean();

                using var verify = new SqliteConnection(connectionString);
                verify.Open();
                Assert.That(verify.Query<int>(@"SELECT ""Id"" FROM ""Tags"" ORDER BY ""Id"";").ToArray(), Is.EqualTo(new[] { 1, 2 }));
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
