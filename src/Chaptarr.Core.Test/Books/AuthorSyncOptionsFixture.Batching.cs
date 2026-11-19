using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Common;
using Dapper;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Datastore;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Books
{
    public partial class AuthorSyncOptionsFixture
    {
        private static IMainDatabase ObserveCommands(IMainDatabase database, Action<string, string> observe)
        {
            IDbConnection Open()
            {
                var connection = database.OpenConnection();
                return Proxy<IDbConnection>((method, args) =>
                {
                    var result = method.Invoke(connection, args);
                    if (result is not IDbCommand command) return result;
                    return Proxy<IDbCommand>((call, parameters) =>
                    {
                        if (call.Name is nameof(IDbCommand.ExecuteReader) or nameof(IDbCommand.ExecuteScalar) or nameof(IDbCommand.ExecuteNonQuery))
                            observe(call.Name, command.CommandText);
                        return call.Invoke(command, parameters);
                    });
                });
            }

            return Proxy<IMainDatabase>((method, args) => method.Name == nameof(IDatabase.OpenConnection) ? Open() : method.Invoke(database, args));
        }

        [Test]
        public void author_setup_should_check_catalog_presence_once_without_loading_books(
            [Values(false, true)] bool postgres, [Values(false, true)] bool nativeAdd,
            [Values(false, true)] bool missingSide)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var preflight = true;
                var queries = new List<string>();
                var h = new Harness(ObserveCommands(database, (_, sql) =>
                {
                    if (preflight) queries.Add(sql);
                })) { CatalogWorkCount = 250 };
                h.Import(h.Request("audiobook", false));
                h.Scan();
                var beforeFetches = h.MetadataFetches;
                var beforeMonitoring = h.BookRows.GetBooksByAuthorId(h.Author.Id).Select(book => book.IsMonitored()).ToArray();
                Set(h.Library, "_authorInfo", Proxy<NzbDrone.Core.MetadataSource.IProvideAuthorInfo>((method, args) =>
                {
                    preflight = false;
                    return method.Invoke(h.Info, args);
                }));
                async Task<Author> FinishSetup(Task<Author> setup)
                {
                    try { return await setup; }
                    finally { preflight = false; }
                }
                Set(h.Controller, "_authorLibraryService", Proxy<IAuthorLibraryService>((method, args) =>
                    FinishSetup((Task<Author>)method.Invoke(h.Library, args))));
                var request = h.Request(missingSide ? "ebook" : "audiobook", false);
                request.AudiobookMonitorExistingMode = missingSide ? null : MonitorTypes.None;
                queries.Clear();

                if (nativeAdd) h.Controller.AddAuthor(NativeAuthorRequest(request)).GetAwaiter().GetResult();
                else h.Import(request);

                preflight = false;
                Assert.Multiple(() =>
                {
                    Assert.That(h.MetadataFetches - beforeFetches, Is.EqualTo(missingSide ? 1 : 0));
                    Assert.That(h.BookRows.GetBooksByAuthorId(h.Author.Id).Where(book => book.MediaType == BookMediaType.Audiobook).Select(book => book.IsMonitored()), Is.EqualTo(beforeMonitoring));
                    Assert.That(queries.Count(sql => sql.Contains("SELECT DISTINCT \"MediaType\" FROM \"Books\"")), Is.EqualTo(1));
                    Assert.That(queries.Any(sql => sql.Contains("\"Books\".*") || sql.Contains("\"Editions\".*") || sql.Contains("\"SeriesBookLink\".*")), Is.False,
                        "Existence checks must not materialize books, editions or series links.");
                });
            });
        }

        [Test]
        public void initial_format_sync_should_batch_writes_and_reuse_author_without_losing_events(
            [Values(false, true)] bool postgres, [Values(1, 25, 250)] int works)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var queries = new List<string>();
                var h = new Harness(ObserveCommands(database, (_, sql) => queries.Add(sql))) { CatalogWorkCount = works };
                h.Import(h.Request("audiobook", false));
                h.Scan();
                var request = h.Request("ebook", true);
                request.EbookMonitorExistingMode = MonitorTypes.None;
                request.SearchForMissingBooks = true;
                h.Import(request);
                var author = h.Author;
                h.BookChanges.Clear();
                queries.Clear();

                h.Scan();

                Assert.Multiple(() =>
                {
                    Assert.That(queries.Count(sql => sql.StartsWith("UPDATE \"Books\"")), Is.EqualTo(2));
                    Assert.That(queries.Where(sql => sql.StartsWith("UPDATE \"Books\"")),
                        Has.All.Not.Contains("\"Title\""));
                    Assert.That(queries.Count(sql => sql.Contains("JOIN \"Books\"") && sql.Contains("SELECT \"Authors\".*")), Is.Zero);
                    Assert.That(queries.Count(sql => sql.StartsWith("UPDATE \"Authors\"") && !sql.Contains("SET \"AddOptions\"")), Is.Zero);
                    Assert.That(h.BookChanges, Has.Count.EqualTo(works * 2));
                    Assert.That(h.BookChanges.All(change => change.Book.Author.Id == author.Id && change.OldBook.Author.Id == author.Id && change.Book.Author.Name == author.Name && change.OldBook.Author.Name == author.Name), Is.True);
                    Assert.That(h.BookChanges.Count(change => change.Book.EbookMonitored != change.OldBook.EbookMonitored), Is.EqualTo(works * 2));
                    Assert.That(h.BookRows.GetBooksByAuthorId(author.Id).All(book => book.IsMonitored()), Is.True);
                    Assert.That(h.SearchTargets.Single(), Is.EquivalentTo(h.BookRows.GetBooksByAuthorId(author.Id)
                        .Where(book => book.MediaType == BookMediaType.Ebook).Select(book => book.Id)));
                });
            });
        }

        [Test]
        public void individual_monitoring_should_load_author_once_for_complete_event_details([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var queries = new List<string>();
                var h = new Harness(ObserveCommands(database, (_, sql) => queries.Add(sql)));
                h.Import(h.Request("audiobook", false));
                h.Scan();
                var author = h.Author;
                var book = h.BookRows.GetBooksByAuthorId(author.Id).Single();
                h.BookChanges.Clear();
                queries.Clear();

                h.Books.SetBookMonitored(book.Id, false);

                Assert.That(queries.Count(sql => sql.Contains("JOIN \"Books\"") && sql.Contains("SELECT \"Authors\".*")), Is.Zero);
                var change = h.BookChanges.Single();
                Assert.Multiple(() =>
                {
                    Assert.That(change.Book.Author.Name, Is.EqualTo(author.Name));
                    Assert.That(change.OldBook.Author.Name, Is.EqualTo(author.Name));
                    Assert.That(change.Book.AudiobookMonitored, Is.False);
                    Assert.That(change.OldBook.AudiobookMonitored, Is.True);
                });
            });
        }

        [Test]
        public void monitoring_batch_should_preserve_other_fields_and_all_flag_combinations([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database) { CatalogWorkCount = 550 };
                h.Import(h.Request());
                var rows = h.BookRows.GetBooksByAuthorId(h.Author.Id);
                var original = rows.ToDictionary(book => book.Id, book => (book.Title, book.AnyEditionOk, book.MediaType, book.AuthorId));
                foreach (var row in rows)
                {
                    row.AudiobookMonitored = false;
                    row.EbookMonitored = false;
                }

                h.BookRows.SetMonitoring(rows);
                Assert.That(h.BookRows.GetBooksByAuthorId(h.Author.Id).All(book => !book.AudiobookMonitored && !book.EbookMonitored), Is.True);
                foreach (var row in rows)
                {
                    row.AudiobookMonitored = row.Id % 2 == 0;
                    row.EbookMonitored = row.Id % 3 == 0;
                    row.Title = "Unpersisted title";
                    row.AnyEditionOk = !row.AnyEditionOk;
                }

                h.BookRows.SetMonitoring(rows);
                var saved = h.BookRows.GetBooksByAuthorId(h.Author.Id);
                Assert.That(saved.Select(book => (book.AudiobookMonitored, book.EbookMonitored)),
                    Is.EqualTo(saved.Select(book => (book.Id % 2 == 0, book.Id % 3 == 0))));
                Assert.That(saved.All(book => (book.Title, book.AnyEditionOk, book.MediaType, book.AuthorId) == original[book.Id]), Is.True);
            });
        }

        [Test]
        public void failed_monitoring_batch_should_roll_back_earlier_groups([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.Import(h.Request());
                var rows = h.BookRows.GetBooksByAuthorId(h.Author.Id).OrderBy(book => book.Id).ToList();
                var original = rows.Select(book => (book.Id, book.AudiobookMonitored, book.EbookMonitored)).ToArray();
                using var connection = database.OpenConnection();
                if (postgres)
                {
                    connection.Execute($"ALTER TABLE \"Books\" ADD CONSTRAINT reject_monitor_change CHECK (\"Id\" <> {rows[1].Id} OR NOT \"AudiobookMonitored\")");
                }
                else
                {
                    connection.Execute($"CREATE TRIGGER reject_monitor_change BEFORE UPDATE ON Books WHEN NEW.Id = {rows[1].Id} AND NEW.AudiobookMonitored = 1 BEGIN SELECT RAISE(ABORT, 'Test write failure'); END");
                }

                rows[0].AudiobookMonitored = false;
                rows[0].EbookMonitored = false;
                rows[1].AudiobookMonitored = true;
                rows[1].EbookMonitored = false;
                var error = Assert.Catch<System.Data.Common.DbException>(() => h.BookRows.SetMonitoring(rows));
                Assert.That(error.Message, Does.Contain(postgres ? "reject_monitor_change" : "Test write failure"));
                Assert.That(h.BookRows.GetBooksByAuthorId(h.Author.Id).OrderBy(book => book.Id)
                    .Select(book => (book.Id, book.AudiobookMonitored, book.EbookMonitored)), Is.EqualTo(original));
            });
        }
    }
}
