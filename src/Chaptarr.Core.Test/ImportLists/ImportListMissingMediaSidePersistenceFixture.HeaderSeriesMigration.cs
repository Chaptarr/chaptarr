using System.Collections.Generic;
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

namespace Chaptarr.Core.Test.ImportLists
{
    public partial class ImportListMissingMediaSidePersistenceFixture
    {
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
