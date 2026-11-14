using System;
using System.Linq;
using Dapper;
using NLog;
using NUnit.Framework;
using Chaptarr.Core.Test.Common;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration.Framework;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    public class IdentityCleanupFixture
    {
        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void trigram_migration_handles_present_and_absent_tables_and_keeps_search_index(bool postgres, bool absent)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var connection = database.OpenConnection();
                if (absent) connection.Execute("DROP TABLE book_trigrams; DROP TABLE author_trigrams;");
                var controller = new MigrationController(LogManager.GetCurrentClassLogger(), null);
                var migrationConnection = connection.ConnectionString;
                controller.Migrate(migrationConnection, new MigrationContext(MigrationType.Main, 128), database.DatabaseType);
                var tables = connection.Query<string>(postgres
                    ? "SELECT tablename FROM pg_tables WHERE schemaname=current_schema() AND tablename IN ('book_trigrams','author_trigrams')"
                    : "SELECT name FROM sqlite_master WHERE type='table' AND name IN ('book_trigrams','author_trigrams')");
                Assert.That(tables, Is.Empty);
                if (postgres)
                {
                    var indexes = connection.Query<string>("SELECT indexdef FROM pg_indexes WHERE tablename='Editions'");
                    Assert.That(indexes.Count(def => def.Contains("USING gin (to_tsvector('simple'::regconfig, COALESCE(\"MatchingTitle\", ''::text)))")), Is.EqualTo(1));
                }
            }, desiredVersion: 120);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void goodreads_work_lookup_does_not_return_a_colliding_edition_id(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var connection = database.OpenConnection();
                connection.Execute(@"INSERT INTO ""Authors"" (""Name"",""Path"",""Added"",""Monitored"",""CleanName"") VALUES ('Identity Author', '/identity', CURRENT_TIMESTAMP, @No, 'identity author');
                    INSERT INTO ""Books"" (""AuthorId"",""Title"",""CleanTitle"",""AnyEditionOk"",""Added"",""GoodreadsWorkId"",""GoodreadsBookId"")
                    VALUES (1, 'Work', 'work', @No, CURRENT_TIMESTAMP, '123', '456'),
                           (1, 'Other work', 'other work', @No, CURRENT_TIMESTAMP, '456', '123');", new { No = false });
                var events = InterfaceProxy.Proxy<IEventAggregator>((method, args) => null);
                var repository = new BookRepository(database, events);
                var found = repository.FindIdsByProviderIds("gr", new[] { "123", "456" });
                Assert.That(found["123"], Is.EqualTo(new[] { 1 }));
                Assert.That(found["456"], Is.EqualTo(new[] { 2 }));
            });
        }
    }
}
