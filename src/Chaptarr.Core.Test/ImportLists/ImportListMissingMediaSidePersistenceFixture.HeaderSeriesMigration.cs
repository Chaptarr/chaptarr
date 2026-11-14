using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Common;
using Chaptarr.Core.Test.Datastore;
using Dapper;
using NLog;
using NLog.Config;
using NLog.Targets;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration;

namespace Chaptarr.Core.Test.ImportLists
{
    public partial class ImportListMissingMediaSidePersistenceFixture
    {
        [TestCase(false)]
        [TestCase(true)]
        public void paged_header_reset_matches_released_update_and_rolls_back_a_later_page(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var connection = database.OpenConnection();
                connection.Execute(@"INSERT INTO ""Authors"" (""Name"",""CleanName"",""Path"",""Monitored"",""Added"")
                    VALUES ('Header fixture','header fixture','fixture',@Yes,@Now)",
                    new { Yes = true, Now = System.DateTime.UtcNow });
                var identity = postgres ? "OVERRIDING SYSTEM VALUE" : "";
                connection.Execute($@"WITH RECURSIVE n(id) AS (SELECT 0 UNION ALL SELECT id+1 FROM n WHERE id<1500)
                    INSERT INTO ""Books"" (""Id"",""AuthorId"",""Title"",""CleanTitle"",""MediaType"",""Added"",""AnyEditionOk"",""SeriesId"") {identity}
                    SELECT id*3-6,1,'Header book','header book',id%2,@Now,@Yes,
                        CASE WHEN id%7=0 THEN NULL ELSE id%5 END FROM n",
                    new { Yes = true, Now = System.DateTime.UtcNow });
                string Snapshot(System.Data.IDbTransaction transaction = null) => new JArray(
                    connection.Query("SELECT * FROM \"Books\" ORDER BY \"Id\"", transaction: transaction)
                        .Select(row => JObject.FromObject(row))).ToString(Newtonsoft.Json.Formatting.None);
                var before = Snapshot();
                string expected;
                using (var transaction = connection.BeginTransaction())
                {
                    // Independent released SQL oracle: preserve every column except the reset field.
                    connection.Execute("UPDATE \"Books\" SET \"SeriesId\" = NULL WHERE \"SeriesId\" IS NOT NULL", transaction: transaction);
                    expected = Snapshot(transaction);
                    transaction.Rollback();
                }
                connection.Execute(postgres
                    ? @"CREATE FUNCTION fail_header_page() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
                        IF NEW.""Id"" >= 3500 THEN RAISE EXCEPTION 'fixture late header page'; END IF; RETURN NEW; END; $$;
                        CREATE TRIGGER fail_header_page BEFORE UPDATE OF ""SeriesId"" ON ""Books"" FOR EACH ROW EXECUTE FUNCTION fail_header_page();"
                    : @"CREATE TRIGGER fail_header_page BEFORE UPDATE OF ""SeriesId"" ON ""Books"" WHEN NEW.""Id"" >= 3500
                        BEGIN SELECT RAISE(ABORT, 'fixture late header page'); END;");
                var capture = new MigrationRebuildBudgetFixture.Capture();
                var failure = Assert.Catch<System.Exception>(() => MigrationRebuildBudgetFixture.Upgrade(database, postgres, 128, capture));
                Assert.That(failure.ToString(), Does.Contain("fixture late header page"));
                var reads = capture.Commands.Where(command => command.Operation == "ExecuteReader" && command.Sql.Contains("FROM \"Books\"")).ToList();
                Assert.Multiple(() =>
                {
                    Assert.That(reads, Has.Count.GreaterThanOrEqualTo(3), "Recovery must reach a third page before the injected fault.");
                    Assert.That(reads.Select(command => command.Sql), Has.All.Contains("ORDER BY \"Id\" LIMIT @batchSize"));
                    Assert.That(capture.Commands.Count(command => command.Operation == "ExecuteNonQuery" && command.Sql.Contains("UPDATE \"Books\" SET \"SeriesId\" = NULL")),
                        Is.GreaterThanOrEqualTo(2), "Two page writes must succeed before the fault; the released single statement cannot satisfy this.");
                });
                Assert.That(connection.ExecuteScalar<int>("SELECT MAX(\"Version\") FROM \"VersionInfo\""), Is.EqualTo(127));
                Assert.That(Snapshot(), Is.EqualTo(before), "An earlier page must roll back with the later failure.");
                connection.Execute(postgres
                    ? "DROP TRIGGER fail_header_page ON \"Books\"; DROP FUNCTION fail_header_page();"
                    : "DROP TRIGGER fail_header_page;");
                UpgradeLegacySetupDatabase(database);
                Assert.That(Snapshot(), Is.EqualTo(expected));
                Assert.That(connection.ExecuteScalar<int>("SELECT MAX(\"Version\") FROM \"VersionInfo\""), Is.EqualTo(128));
                connection.Execute("UPDATE \"Books\" SET \"SeriesId\"=42 WHERE \"Id\"=3");
                UpgradeLegacySetupDatabase(database);
                Assert.That(connection.ExecuteScalar<int>("SELECT \"SeriesId\" FROM \"Books\" WHERE \"Id\"=3"), Is.EqualTo(42),
                    "A completed 128 must not erase metadata refreshed after the upgrade.");
            }, desiredVersion: 127);
        }

        [Test]
        public void legacy_header_reset_should_preserve_memberships_and_allow_fresh_header_selection_after_upgrade(
            [Values(false, true)] bool postgres,
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType mediaType)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var stored = new StoredListLibrary(database);
                var series = new BasicRepository<Series>(database, stored.Events);
                var fallback = series.Insert(new Series { Title = "Fallback", GoodreadsSeriesId = "gr:49075", MediaType = mediaType });
                var header = series.Insert(new Series { Title = "Header", GoodreadsSeriesId = "gr:135117", MediaType = mediaType });
                var book = CatalogBook(mediaType, "Series Work", "123");
                book.SeriesId = 135117;
                book.SeriesName = "Header";
                book.SeriesPosition = "5";
                book.SetMonitored(true);
                stored.Save(book);
                var oldLocal = CatalogBook(mediaType, "Local-ID Work", "124");
                oldLocal.SeriesId = header.Id;
                stored.Save(oldLocal);
                stored.Save(CatalogBook(mediaType, "Without Header", "125"));
                var links = new SeriesBookLinkRepository(database, stored.Events);
                links.Insert(new SeriesBookLink { BookId = book.Id, SeriesId = fallback.Id, Position = "1", IsPrimary = true });
                links.Insert(new SeriesBookLink { BookId = book.Id, SeriesId = header.Id, Position = "5", IsPrimary = false });
                Assert.That(BookSeries.GetHeaderLink(stored.Books.GetBook(book.Id)).Series.Value.Title, Is.EqualTo("Header"));
                using var connection = database.OpenConnection();
                JArray Snapshot(string table, bool omitHeader = false) => new JArray(connection.Query("SELECT * FROM \"" + table + "\" ORDER BY \"Id\"")
                    .Select(row =>
                    {
                        var value = JObject.FromObject(row);
                        if (omitHeader) value.Remove("SeriesId");
                        return value;
                    }));
                var booksBefore = Snapshot("Books", omitHeader: true);
                var seriesBefore = Snapshot("Series");
                var linksBefore = Snapshot("SeriesBookLink");
                var previousLogging = LogManager.Configuration;
                var messages = new MemoryTarget { Layout = "${message}" };
                var logging = new LoggingConfiguration();
                logging.AddRule(LogLevel.Info, LogLevel.Info, messages, nameof(retain_book_level_extra_files));
                try
                {
                    LogManager.Configuration = logging;
                    UpgradeLegacySetupDatabase(database);
                    using var transaction = connection.BeginTransaction();
                    Assert.That(new retain_book_level_extra_files().ClearLegacyHeaderSeriesIds(connection, transaction), Is.Zero);
                    transaction.Commit();
                    var resets = messages.Logs.Where(message => message.StartsWith("Cleared ")).ToList();
                    Assert.Multiple(() =>
                    {
                        Assert.That(resets, Has.Count.EqualTo(2));
                        Assert.That(resets[0], Does.Match(@"^Cleared 2 legacy book header series identifiers in \d+ ms$"));
                        Assert.That(resets[1], Does.Match(@"^Cleared 0 legacy book header series identifiers in \d+ ms$"));
                    });
                }
                finally
                {
                    LogManager.Configuration = previousLogging;
                }
                Assert.Multiple(() =>
                {
                    Assert.That(connection.Query<int?>("SELECT \"SeriesId\" FROM \"Books\""), Is.All.Null);
                    Assert.That(JToken.DeepEquals(Snapshot("Books", omitHeader: true), booksBefore), Is.True);
                    Assert.That(JToken.DeepEquals(Snapshot("Series"), seriesBefore), Is.True);
                    Assert.That(JToken.DeepEquals(Snapshot("SeriesBookLink"), linksBefore), Is.True);
                });
                var saved = stored.Books.GetBook(book.Id);
                Assert.That(BookSeries.GetHeaderLink(saved), Is.Null);
                Assert.That(BookSeries.Select(saved).Series.Value.Title, Is.EqualTo("Fallback"));
                var fresh = RefreshEntityCopy.CloneBook(saved, includeEditions: true);
                fresh.SeriesId = 135117;
                saved.UseMetadataFrom(fresh);
                stored.Books.UpdateManyWithLifecycle(new List<Book> { saved });
                UpgradeLegacySetupDatabase(database);
                var restored = stored.Books.GetBook(book.Id);
                Assert.That(restored.SeriesId, Is.EqualTo(135117));
                Assert.That(BookSeries.Select(restored).Series.Value.Title, Is.EqualTo("Header"));
            }, desiredVersion: 127);
        }
    }
}
