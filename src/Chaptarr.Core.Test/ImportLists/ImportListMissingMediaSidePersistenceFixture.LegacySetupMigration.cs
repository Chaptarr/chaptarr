using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Chaptarr.Core.Test.Common;
using Dapper;
using NLog;
using NLog.Config;
using NLog.Targets;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration;
using NzbDrone.Core.Datastore.Migration.Framework;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.MediaFiles.Events;

namespace Chaptarr.Core.Test.ImportLists
{
    public partial class ImportListMissingMediaSidePersistenceFixture
    {
        private const string LegacyInitialOptionsJson = "{\"monitor\":\"all\",\"searchForMissingBooks\":true,\"booksToMonitor\":[],\"monitored\":false}";

        [Test]
        public void legacy_initial_monitor_migration_should_preserve_listed_rows_and_complete_the_pending_scan(
            [Values(false, true)] bool postgres,
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType side,
            [Values(false, true)] bool explicitNullMediaPolicies)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var stored = new StoredListLibrary(database);
                stored.Author.AudiobookMonitored = side == BookMediaType.Audiobook;
                stored.Author.EbookMonitored = side == BookMediaType.Ebook;
                stored.Authors.Update(stored.Author);
                var listed = CatalogBook(side, "Listed Work", "123");
                listed.SetMonitored(true);
                listed.Editions.Single().Monitored = true;
                stored.Save(listed);
                var unlisted = CatalogBook(side, "Unlisted Work", "124");
                unlisted.Editions.Single().Monitored = true;
                stored.Save(unlisted);
                var opposite = CatalogBook(side == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook,
                    "Listed Work", "123");
                opposite.Editions.Single().Monitored = true;
                stored.Save(opposite);
                var original = JObject.Parse(LegacyInitialOptionsJson);
                if (explicitNullMediaPolicies)
                {
                    original["audiobookMonitor"] = JValue.CreateNull();
                    original["ebookMonitor"] = JValue.CreateNull();
                }
                using (var connection = database.OpenConnection())
                {
                    Assert.That(connection.ExecuteScalar<long>("SELECT MAX(\"Version\") FROM \"VersionInfo\""), Is.EqualTo(127));
                    connection.Execute("UPDATE \"Authors\" SET \"AddOptions\" = @options WHERE \"Id\" = @id",
                        new { options = explicitNullMediaPolicies ? original.ToString() : LegacyInitialOptionsJson, id = stored.Author.Id });
                }
                Assert.That(stored.Authors.Get(stored.Author.Id).AddOptions.Monitor, Is.EqualTo(MonitorTypes.All));

                UpgradeLegacySetupDatabase(database);

                using (var connection = database.OpenConnection())
                {
                    var expected = (JObject)original.DeepClone();
                    expected.Remove("monitor");
                    Assert.That(JToken.DeepEquals(JObject.Parse(ReadLegacySetupOptions(connection, stored.Author.Id)), expected), Is.True);
                    Assert.That(connection.ExecuteScalar<long>("SELECT MAX(\"Version\") FROM \"VersionInfo\""), Is.EqualTo(128));
                    using var transaction = connection.BeginTransaction();
                    Assert.That(new retain_book_level_extra_files().RemoveLegacyInitialMonitor(connection, transaction, database.DatabaseType), Is.Zero);
                    transaction.Commit();
                }
                var author = stored.Authors.Get(stored.Author.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(author.AddOptions.Monitor, Is.Null);
                    Assert.That(author.AddOptions.AudiobookMonitor, Is.Null);
                    Assert.That(author.AddOptions.EbookMonitor, Is.Null);
                    Assert.That(author.AddOptions.SearchForMissingBooks, Is.True);
                    Assert.That(author.AddOptions.BooksToMonitor, Is.Empty);
                });
                var added = new BookAddedService(new NzbDrone.Common.Cache.CacheManager(), stored.Queue, stored.Books, stored.Logger);
                var handler = new AuthorScannedHandler(new BookMonitoredService(stored.AuthorService, stored.Books, stored.Logger),
                    stored.AuthorService, stored.Queue, added, stored.Events, stored.Logger, stored.Books);

                handler.Handle(new AuthorScannedEvent(author));

                Assert.Multiple(() =>
                {
                    Assert.That(stored.Books.GetBooksByAuthor(author.Id).Where(book => book.IsMonitored()).Select(book => book.Id),
                        Is.EqualTo(new[] { listed.Id }));
                    Assert.That(stored.BookRows.GetMissingBookSearchTargets(null, author.Id).Select(target => target.BookId),
                        Is.EqualTo(new[] { listed.Id }));
                    Assert.That(stored.Commands.OfType<MissingBookSearchCommand>().Single().AuthorId, Is.EqualTo(author.Id));
                    Assert.That(stored.Commands.OfType<BookSearchCommand>(), Is.Empty);
                    Assert.That(stored.Authors.Get(author.Id).AddOptions, Is.Null);
                    Assert.That(stored.Authors.Get(author.Id).AudiobookMonitored, Is.EqualTo(side == BookMediaType.Audiobook));
                    Assert.That(stored.Authors.Get(author.Id).EbookMonitored, Is.EqualTo(side == BookMediaType.Ebook));
                });
                handler.Handle(new AuthorScannedEvent(stored.Authors.Get(author.Id)));
                Assert.That(stored.Commands.OfType<MissingBookSearchCommand>().Count(), Is.EqualTo(1));
            }, desiredVersion: 127);
        }

        [Test]
        public void legacy_initial_monitor_cleanup_should_preserve_explicit_policies_unknown_fields_and_unchanged_rows(
            [Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var connection = database.OpenConnection();
                var eligible = new[]
                {
                    LegacyInitialOptionsJson,
                    "{\"monitor\":\"all\",\"searchForMissingBooks\":true,\"booksToMonitor\":[],\"monitored\":false,\"audiobookMonitor\":null,\"ebookMonitor\":null,\"futureState\":{\"monitor\":\"keep nested\",\"values\":[1,null,{\"active\":false}]}}",
                    "{\"monitor\":null,\"audiobookMonitor\":null,\"ebookMonitor\":null,\"searchForMissingBooks\":false,\"futureState\":{\"monitor\":\"keep nested\"}}"
                };
                var unchanged = new string[]
                {
                    "{\"monitor\":\"all\",\"audiobookMonitor\":\"missing\",\"ebookMonitor\":null,\"searchForMissingBooks\":true}",
                    "{\"monitor\":\"all\",\"audiobookMonitor\":null,\"ebookMonitor\":\"none\",\"searchForMissingBooks\":true}",
                    "{\"monitor\":\"all\",\"audiobookMonitor\":0,\"ebookMonitor\":null}",
                    "{\"monitor\":\"all\",\"audiobookMonitor\":null,\"ebookMonitor\":0}",
                    "{\"searchForMissingBooks\":true,\"booksToMonitor\":[],\"monitored\":false,\"futureState\":{\"monitor\":\"keep nested\"}}",
                    "null",
                    null
                };
                var allOptions = eligible.Concat(unchanged).ToList();
                var ids = new List<int>();
                for (var i = 0; i < allOptions.Count; i++)
                {
                    ids.Add(connection.QuerySingle<int>(@"INSERT INTO ""Authors"" (""Name"", ""Path"", ""Added"", ""Monitored"", ""CleanName"", ""AddOptions"")
                        VALUES (@name, @path, CURRENT_TIMESTAMP, @monitored, @name, @options) RETURNING ""Id""",
                        new { name = "Migration control " + i, path = "/migration-controls/" + i, monitored = false, options = allOptions[i] }));
                }
                var migration = new retain_book_level_extra_files();
                var previousLogging = LogManager.Configuration;
                var messages = new MemoryTarget { Layout = "${message}" };
                var logging = new LoggingConfiguration();
                logging.AddRule(LogLevel.Info, LogLevel.Info, messages, nameof(retain_book_level_extra_files));
                try
                {
                    LogManager.Configuration = logging;
                    using var transaction = connection.BeginTransaction();
                    Assert.That(migration.RemoveLegacyInitialMonitor(connection, transaction, database.DatabaseType), Is.EqualTo(eligible.Length));
                    Assert.That(migration.RemoveLegacyInitialMonitor(connection, transaction, database.DatabaseType), Is.Zero);
                    transaction.Commit();
                    Assert.Multiple(() =>
                    {
                        Assert.That(messages.Logs, Has.Count.EqualTo(2));
                        Assert.That(messages.Logs[0], Does.Match(@"from 3 authors in \d+ ms$"));
                        Assert.That(messages.Logs[1], Does.Match(@"from 0 authors in \d+ ms$"));
                    });
                }
                finally
                {
                    LogManager.Configuration = previousLogging;
                }
                for (var i = 0; i < eligible.Length; i++)
                {
                    var expected = JObject.Parse(eligible[i]);
                    expected.Remove("monitor");
                    Assert.That(JToken.DeepEquals(JObject.Parse(ReadLegacySetupOptions(connection, ids[i])), expected), Is.True,
                        "Only the top-level legacy monitoring policy may be removed.");
                }
                for (var i = 0; i < unchanged.Length; i++)
                {
                    Assert.That(ReadLegacySetupOptions(connection, ids[eligible.Length + i]), Is.EqualTo(unchanged[i]));
                }
                UpgradeLegacySetupDatabase(database);
                using var finalTransaction = connection.BeginTransaction();
                Assert.That(migration.RemoveLegacyInitialMonitor(connection, finalTransaction, database.DatabaseType), Is.Zero);
                finalTransaction.Commit();
            }, desiredVersion: 127);
        }

        private static string ReadLegacySetupOptions(IDbConnection connection, int id) =>
            connection.QuerySingle<string>("SELECT \"AddOptions\" FROM \"Authors\" WHERE \"Id\" = @id", new { id });

        private static void UpgradeLegacySetupDatabase(IMainDatabase database)
        {
            using var connection = database.OpenConnection();
            var connectionString = connection.ConnectionString;
            new MigrationController(LogManager.GetCurrentClassLogger(), null)
                .Migrate(connectionString, new MigrationContext(MigrationType.Main, 128), database.DatabaseType);
        }
    }
}
