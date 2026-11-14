using System;
using System.Linq;
using Chaptarr.Core.Test.Common;
using Dapper;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration.Framework;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Others;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    public class RetainedExtraFileMigrationFixture
    {
        [Test]
        public void exclusion_migration_keeps_broader_scope_and_enforces_format_identity([Values] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var connection = database.OpenConnection();
                void Add(string id, int? type) => connection.Execute("INSERT INTO \"ImportListExclusions\" (\"ForeignId\", \"Name\", \"MediaType\") VALUES (@id, 'Author', @type)", new { id, type });
                Add("gr:58", null); Add(" GR:58 ", null); Add("gr:58", 0); Add("gr:58", 1);
                Add("gr:99", 0); Add("gr:99", 0); Add("gr:99", 1);
                var cs = connection.ConnectionString;
                var migration = new MigrationController(LogManager.GetCurrentClassLogger(), null);
                migration.Migrate(cs, new MigrationContext(MigrationType.Main, 128), postgres ? DatabaseType.PostgreSQL : DatabaseType.SQLite);
                Assert.That(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"ImportListExclusions\""), Is.EqualTo(3));
                Assert.That(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"ImportListExclusions\" WHERE \"ForeignId\"='gr:58' AND \"MediaType\" IS NULL"), Is.EqualTo(1));
                Assert.Catch(() => Add(" GR:58 ", null));
                Assert.Catch(() => Add("gr:99", 0));
            }, desiredVersion: 127);
        }

        [Test]
        public void fresh_database_uses_an_unqualified_unknown_title([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var connection = database.OpenConnection();
                Assert.That(connection.QuerySingle<string>("SELECT \"Title\" FROM \"QualityDefinitions\" WHERE \"Quality\" = 0"), Is.EqualTo("Unknown"));
            });
        }

        [Test]
        public void notification_batch_table_is_created_without_erasing_a_previous_snapshot(
            [Values] bool postgres, [Values] bool existing)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var connection = database.OpenConnection();
                if (existing)
                {
                    connection.Execute(@"CREATE TABLE ""PendingNotificationItems"" (
                        ""Id"" INTEGER PRIMARY KEY, ""NotificationId"" INTEGER NOT NULL,
                        ""Path"" TEXT NOT NULL, ""PathKey"" TEXT NOT NULL, ""IsFolder"" BOOLEAN NOT NULL)");
                    connection.Execute(@"INSERT INTO ""PendingNotificationItems"" (""Id"", ""NotificationId"", ""Path"", ""PathKey"", ""IsFolder"")
                        VALUES (77, 8, '/books/Dune', '/books/Dune', @folder)", new { folder = true });
                }
                var connectionString = connection.ConnectionString;
                new MigrationController(LogManager.GetCurrentClassLogger(), null).Migrate(connectionString,
                    new MigrationContext(MigrationType.Main, 128), postgres ? DatabaseType.PostgreSQL : DatabaseType.SQLite);
                Assert.That(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"PendingNotificationItems\""), Is.EqualTo(existing ? 1 : 0));
                var indexes = connection.Query<string>(postgres
                    ? "SELECT indexname FROM pg_indexes WHERE tablename = 'PendingNotificationItems'"
                    : "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'PendingNotificationItems'");
                Assert.That(indexes, Does.Contain("IX_PendingNotificationItems_Identity"));
                if (existing)
                {
                    Assert.That(connection.QuerySingle<string>("SELECT \"Path\" FROM \"PendingNotificationItems\" WHERE \"Id\" = 77"), Is.EqualTo("/books/Dune"));
                    Assert.That(connection.QuerySingle<string>("SELECT \"PathKey\" FROM \"PendingNotificationItems\" WHERE \"Id\" = 77"),
                        Is.EqualTo(NzbDrone.Core.Notifications.PendingNotificationItem.GetPathKey("/books/Dune")));
                }
            }, desiredVersion: 127);
        }

        [Test]
        public void upgrade_renames_only_the_unchanged_unknown_title(
            [Values(false, true)] bool postgres, [Values("Unknown Text", "Unclassified", "Unknown")] string title)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var connection = database.OpenConnection();
                connection.Execute("UPDATE \"QualityDefinitions\" SET \"Title\" = @title WHERE \"Quality\" = 0", new { title });
                var connectionString = connection.ConnectionString;
                new MigrationController(LogManager.GetCurrentClassLogger(), null).Migrate(connectionString,
                    new MigrationContext(MigrationType.Main, 128), postgres ? DatabaseType.PostgreSQL : DatabaseType.SQLite);
                Assert.That(connection.QuerySingle<string>("SELECT \"Title\" FROM \"QualityDefinitions\" WHERE \"Quality\" = 0"),
                    Is.EqualTo(title == "Unknown Text" ? "Unknown" : title));
            }, desiredVersion: 127);
        }

        [Test]
        public void migration_preserves_rows_indexes_and_identity_and_allows_book_only_extras(
            [Values(false, true)] bool postgres, [Values(false, true)] bool empty)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var repository = new ExtraFileRepository<OtherExtraFile>(database, InterfaceProxy.Proxy<IEventAggregator>((_, _) => null));
                OtherExtraFile NewExtra() => new OtherExtraFile
                {
                    AuthorId = 7, BookId = 11, BookFileId = 19, RelativePath = "Book/封面.jpg", Extension = ".jpg",
                    Added = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                    LastUpdated = new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc)
                };
                var original = repository.Insert(NewExtra());
                var lastId = repository.Insert(NewExtra()).Id;
                repository.Delete(lastId);
                if (empty) repository.Delete(original.Id);
                using var connection = database.OpenConnection();
                string[] Indexes() => connection.Query<string>(postgres
                    ? "SELECT indexname FROM pg_indexes WHERE tablename = 'ExtraFiles' ORDER BY indexname"
                    : "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'ExtraFiles' ORDER BY name").ToArray();
                var beforeIndexes = Indexes();
                Assert.That(beforeIndexes, Does.Contain("IX_ExtraFiles_BookId"));
                Assert.That(beforeIndexes, Does.Contain("IX_ExtraFiles_BookFileId"));
                var migrationConnection = connection.ConnectionString;
                void Upgrade() => new MigrationController(LogManager.GetCurrentClassLogger(), null).Migrate(migrationConnection,
                    new MigrationContext(MigrationType.Main, 128), postgres ? DatabaseType.PostgreSQL : DatabaseType.SQLite);
                Upgrade();
                Assert.That(Indexes(), Is.EqualTo(beforeIndexes));
                Assert.That(repository.All(), Has.Count.EqualTo(empty ? 0 : 1));
                if (!empty)
                {
                    var saved = repository.Get(original.Id);
                    Assert.Multiple(() =>
                    {
                        Assert.That(saved.AuthorId, Is.EqualTo(original.AuthorId));
                        Assert.That(saved.BookId, Is.EqualTo(original.BookId));
                        Assert.That(saved.BookFileId, Is.EqualTo(original.BookFileId));
                        Assert.That(saved.RelativePath, Is.EqualTo(original.RelativePath));
                        Assert.That(saved.Extension, Is.EqualTo(original.Extension));
                        Assert.That(saved.Added, Is.EqualTo(original.Added));
                        Assert.That(saved.LastUpdated, Is.EqualTo(original.LastUpdated));
                    });
                    saved.BookFileId = null;
                    repository.Update(saved);
                }
                var extra = NewExtra(); extra.BookFileId = null;
                var inserted = repository.Insert(extra);
                Assert.That(inserted.Id, Is.GreaterThan(lastId), "deleted IDs must not be reused by the rebuild");
                Upgrade();
                Assert.That(repository.Get(inserted.Id).BookFileId, Is.Null);
                Assert.That(repository.GetFilesByBook(7, 11).Count, Is.EqualTo(empty ? 1 : 2));
            }, desiredVersion: 127);
        }
    }
}
