using System;
using System.Linq;
using Chaptarr.Core.Test.Common;
using Dapper;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    public class HeaderAuthorIndexMigrationFixture
    {
        [TestCase(false)]
        [TestCase(true)]
        public void upgrade_keeps_author_rows_and_sqlite_index_tracks_insert_update_delete(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var connection = database.OpenConnection();
                var overrideIdentity = postgres ? "OVERRIDING SYSTEM VALUE" : string.Empty;
                void Add(int id, string name, string key) => connection.Execute($@"
                    INSERT INTO ""Authors"" (""Id"", ""Name"", ""SearchName"", ""CleanName"", ""TitleSlug"", ""Path"", ""Monitored"", ""Added"")
                    {overrideIdentity} VALUES (@Id, @Name, @Key, @Key, @Slug, @Path, @Monitored, @Added)",
                    new { Id = id, Name = name, Key = key, Slug = "author-" + id, Path = "/library/author-" + id, Monitored = true, Added = DateTime.UtcNow });
                Add(1, "J.K. Rowling", "j.k. rowling");
                var migrationConnection = connection.ConnectionString;
                void Upgrade() => new MigrationController(LogManager.GetCurrentClassLogger(), null).Migrate(
                    migrationConnection, new MigrationContext(MigrationType.Main, 120), database.DatabaseType);
                Upgrade();
                Assert.That(connection.QuerySingle<string>("SELECT \"Name\" FROM \"Authors\" WHERE \"Id\" = 1"), Is.EqualTo("J.K. Rowling"));
                Add(2, "C.N. Crawford", "c.n. crawford");
                if (!postgres)
                {
                    Assert.That(connection.Query<int>("SELECT rowid FROM author_fts WHERE author_fts MATCH 'jk AND rowling'"), Is.EqualTo(new[] { 1 }));
                    Assert.That(connection.Query<int>("SELECT rowid FROM author_fts WHERE author_fts MATCH 'cn AND crawford'"), Is.EqualTo(new[] { 2 }));
                    connection.Execute("UPDATE Authors SET Name = 'Jean-Paul Sartre', SearchName = 'jean paul sartre' WHERE Id = 1");
                    Assert.That(connection.Query<int>("SELECT rowid FROM author_fts WHERE author_fts MATCH 'jk'"), Is.Empty);
                    Assert.That(connection.Query<int>("SELECT rowid FROM author_fts WHERE author_fts MATCH 'jean AND paul AND sartre'"), Is.EqualTo(new[] { 1 }));
                    connection.Execute("DELETE FROM Authors WHERE Id = 2");
                    Assert.That(connection.Query<int>("SELECT rowid FROM author_fts WHERE author_fts MATCH 'cn'"), Is.Empty);
                }

                var remaining = connection.Query<int>("SELECT \"Id\" FROM \"Authors\" ORDER BY \"Id\"").ToArray();
                Upgrade();
                Assert.That(connection.Query<int>("SELECT \"Id\" FROM \"Authors\" ORDER BY \"Id\""), Is.EqualTo(remaining));
                if (!postgres)
                {
                    Assert.That(connection.Query<int>("SELECT rowid FROM author_fts ORDER BY rowid"), Is.EqualTo(remaining));
                }
            }, desiredVersion: 119);
        }
    }
}
