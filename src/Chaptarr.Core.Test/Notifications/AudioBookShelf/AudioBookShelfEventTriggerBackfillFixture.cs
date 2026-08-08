using System;
using System.IO;
using Dapper;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using NzbDrone.Core.Datastore.Migration;

namespace Chaptarr.Core.Test.Notifications.AudioBookShelf
{
    [TestFixture]
    public class AudioBookShelfEventTriggerBackfillFixture
    {
        [Test]
        public void should_enable_forced_event_triggers_for_audiobookshelf_rows()
        {
            WithDatabase(connection =>
            {
                connection.Execute(@"
                    INSERT INTO ""Notifications"" (""Id"", ""Name"", ""Implementation"", ""ConfigContract"", ""OnReleaseImport"", ""OnRename"", ""OnBookFileDelete"", ""OnHealthIssue"")
                    VALUES (1, 'ABS', 'AudioBookShelf', 'AudioBookShelfSettings', 0, 0, 0, 0);
                ");

                ApplyBackfill(connection);

                var row = connection.QuerySingle(@"SELECT ""OnReleaseImport"", ""OnRename"", ""OnBookFileDelete"", ""OnHealthIssue"" FROM ""Notifications"" WHERE ""Id"" = 1;");

                Assert.That((long)row.OnReleaseImport, Is.EqualTo(1));
                Assert.That((long)row.OnRename, Is.EqualTo(1));
                Assert.That((long)row.OnBookFileDelete, Is.EqualTo(1));
                Assert.That((long)row.OnHealthIssue, Is.EqualTo(0), "flags the factory never forced must stay untouched");
            });
        }

        [Test]
        public void should_not_touch_other_notification_implementations()
        {
            WithDatabase(connection =>
            {
                connection.Execute(@"
                    INSERT INTO ""Notifications"" (""Id"", ""Name"", ""Implementation"", ""ConfigContract"", ""OnReleaseImport"", ""OnRename"", ""OnBookFileDelete"", ""OnHealthIssue"")
                    VALUES (1, 'Discord', 'Discord', 'DiscordSettings', 0, 0, 0, 1);
                ");

                ApplyBackfill(connection);

                var row = connection.QuerySingle(@"SELECT ""OnReleaseImport"", ""OnRename"", ""OnBookFileDelete"" FROM ""Notifications"" WHERE ""Id"" = 1;");

                Assert.That((long)row.OnReleaseImport, Is.EqualTo(0));
                Assert.That((long)row.OnRename, Is.EqualTo(0));
                Assert.That((long)row.OnBookFileDelete, Is.EqualTo(0));
            });
        }

        private static void ApplyBackfill(SqliteConnection connection)
        {
            using var transaction = connection.BeginTransaction();
            AudioBookShelfEventTriggerBackfill.Apply(connection, transaction);
            transaction.Commit();
        }

        private static void WithDatabase(Action<SqliteConnection> test)
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"audiobookshelf_event_trigger_backfill_{Guid.NewGuid():N}.db");
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
                    CREATE TABLE ""Notifications"" (
                        ""Id"" INTEGER PRIMARY KEY,
                        ""Name"" TEXT,
                        ""Implementation"" TEXT,
                        ""ConfigContract"" TEXT,
                        ""OnReleaseImport"" INTEGER NOT NULL DEFAULT 0,
                        ""OnRename"" INTEGER NOT NULL DEFAULT 0,
                        ""OnBookFileDelete"" INTEGER NOT NULL DEFAULT 0,
                        ""OnHealthIssue"" INTEGER NOT NULL DEFAULT 0
                    );
                ");
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
                catch (IOException)
                {
                }
            }
        }
    }
}
