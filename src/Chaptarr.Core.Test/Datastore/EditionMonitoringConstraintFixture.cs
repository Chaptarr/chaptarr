using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Common;
using Chaptarr.Http.ErrorManagement;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Dapper;
using NzbDrone.Common.Messaging;
using NzbDrone.Common.Disk;
using NLog;
using Npgsql;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration.Framework;
using NzbDrone.Core.Housekeeping.Housekeepers;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.Discord;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Parser.Model;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    public class EditionMonitoringConstraintFixture
    {
        private sealed class SilentEvents : IEventAggregator
        {
            public void PublishEvent<TEvent>(TEvent value) where TEvent : class, IEvent { }
        }

        private sealed class CleanupEvents : IEventAggregator
        {
            public Action<IEvent> Handle { get; set; }
            public int CleanupFailures { get; private set; }

            public void PublishEvent<TEvent>(TEvent value) where TEvent : class, IEvent
            {
                // Production EventAggregator records synchronous handler failures and
                // continues. Parent integrity must also hold in that failure path.
                try { Handle?.Invoke(value); }
                catch (InvalidOperationException) { CleanupFailures++; }
            }
        }

        private static void SeedLegacyOrphanedBooks(IMainDatabase database, int authorId)
        {
            using var connection = database.OpenConnection();
            if (database.DatabaseType == DatabaseType.PostgreSQL)
            {
                // This isolated fixture deliberately reconstructs legacy corruption.
                // The new Editions parent guard remains installed throughout.
                connection.Execute("ALTER TABLE \"Books\" DROP CONSTRAINT \"FK_Books_Authors\"");
            }

            connection.Execute("DELETE FROM \"Authors\" WHERE \"Id\"=@authorId", new { authorId });
            if (database.DatabaseType == DatabaseType.PostgreSQL)
            {
                // Preserve the legacy row while restoring enforcement for new writes.
                connection.Execute(@"ALTER TABLE ""Books"" ADD CONSTRAINT ""FK_Books_Authors""
                    FOREIGN KEY (""AuthorId"") REFERENCES ""Authors"" (""Id"") NOT VALID");
            }
        }

        private static void AssertParentConflictResponse(Exception failure)
        {
            foreach (var verb in new[] { "DELETE", "PUT", "POST" })
            {
                var context = new DefaultHttpContext();
                context.Request.Method = verb;
                using var body = new MemoryStream();
                context.Response.Body = body;
                context.Features.Set<IExceptionHandlerPathFeature>(new ExceptionHandlerFeature { Error = failure });
                new ChaptarrErrorPipeline(LogManager.GetCurrentClassLogger()).HandleException(context).GetAwaiter().GetResult();
                Assert.That(context.Response.StatusCode, Is.EqualTo(409), verb);
            }
        }

        private static readonly IEventAggregator Events = new SilentEvents();

        private static Book AddBook(IMainDatabase db, BookMediaType mediaType = BookMediaType.Audiobook, bool anyEditionOk = true)
        {
            var author = new AuthorRepository(db, Events, LogManager.GetCurrentClassLogger()).Insert(new Author
            {
                Name = "Test Author", CleanName = "testauthor", SortName = "testauthor",
                TitleSlug = Guid.NewGuid().ToString(), Path = "/fixture/author", Added = DateTime.UtcNow
            });
            return new BookRepository(db, Events).Insert(new Book
            {
                Title = "Test Book", CleanTitle = "testbook", TitleSlug = Guid.NewGuid().ToString(),
                MediaType = mediaType, AuthorId = author.Id, Added = DateTime.UtcNow, AnyEditionOk = anyEditionOk
            });
        }

        // Historical migration fixtures use the historical columns, not today's INSERT shape.
        private static T InsertLegacy<T>(IMainDatabase db, T model) where T : ModelBase
        {
            using var connection = db.OpenConnection();
            var table = TableMapping.Mapper.TableNameMapping(typeof(T));
            var excluded = TableMapping.Mapper.ExcludeProperties(typeof(T)).Select(property => property.Name).ToHashSet();
            string[] columns;
            using (var reader = connection.ExecuteReader($"SELECT * FROM \"{table}\" WHERE 1 = 0"))
                columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName)
                    .Where(name => name != "Id" && !excluded.Contains(name) && typeof(T).GetProperty(name) != null).ToArray();
            var parameters = new DynamicParameters();
            foreach (var name in columns) parameters.Add(name, typeof(T).GetProperty(name).GetValue(model));
            model.Id = connection.ExecuteScalar<int>($"INSERT INTO \"{table}\" ({string.Join(",", columns.Select(name => $"\"{name}\""))}) VALUES ({string.Join(",", columns.Select(name => $"@{name}"))}) RETURNING \"Id\"", parameters);
            return model;
        }

        private static Book AddLegacyBook(IMainDatabase db, BookMediaType mediaType = BookMediaType.Audiobook, bool anyEditionOk = true)
        {
            var author = InsertLegacy(db, new Author { Name = "Test Author", CleanName = "testauthor", SortName = "testauthor",
                TitleSlug = Guid.NewGuid().ToString(), Path = "/fixture/author", Added = DateTime.UtcNow });
            return InsertLegacy(db, new Book { Title = "Test Book", CleanTitle = "testbook", TitleSlug = Guid.NewGuid().ToString(),
                AuthorId = author.Id, MediaType = mediaType, AnyEditionOk = anyEditionOk, Added = DateTime.UtcNow });
        }

        private sealed class LegacyEditions : BasicRepository<Edition>
        {
            private readonly IMainDatabase _legacyDatabase;
            public LegacyEditions(IMainDatabase db) : base(db, Events) { _legacyDatabase = db; }
            public override Edition Insert(Edition edition) => InsertLegacy(_legacyDatabase, edition);
            public override Edition Update(Edition edition)
            {
                using var connection = _database.OpenConnection();
                connection.Execute("UPDATE \"Editions\" SET \"ReadingFormatId\"=@ReadingFormatId WHERE \"Id\"=@Id", edition);
                return edition;
            }
        }

        private static Edition NewEdition(Book book, bool monitored = false, int format = 2, int votes = 1, bool manual = false)
        {
            return new Edition
            {
                BookId = book.Id, Title = "Test Edition", TitleSlug = Guid.NewGuid().ToString(),
                ReadingFormatId = format, Ratings = new Ratings { Votes = votes, Value = 4m },
                Monitored = monitored, ManualAdd = manual
            };
        }

        [TestCase(false)]
        [TestCase(true)]
        public void migration_repair_uses_the_migration_timeout_for_every_read_and_write(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                var book = AddLegacyBook(db);
                var editions = new LegacyEditions(db);
                editions.Insert(NewEdition(book, true));
                editions.Insert(NewEdition(book, true, votes: 100));
                using var connection = db.OpenConnection();
                using var transaction = connection.BeginTransaction();
                var timeouts = new List<int>();
                var observed = CommandObserver.Wrap<System.Data.IDbConnection>(connection, command => timeouts.Add(command.CommandTimeout), includeWrites: true);
                CleanupOrphanedEditions.Cleanup(observed, transaction, MigrationController.CommandTimeoutSeconds);
                Assert.That(FixMultipleMonitoredEditions.Repair(observed, transaction, db.DatabaseType,
                    LogManager.GetCurrentClassLogger(), true, MigrationController.CommandTimeoutSeconds), Is.EqualTo(1));
                Assert.That(timeouts.Count, Is.GreaterThan(4));
                Assert.That(timeouts, Is.All.EqualTo(300));
                transaction.Commit();
            }, desiredVersion: 116);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void database_rejects_two_selected_but_allows_zero_and_unlimited_unselected(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                var book = AddBook(db);
                var editions = new EditionRepository(db, Events);
                var rows = Enumerable.Range(0, 5).Select(_ => NewEdition(book)).ToList();
                editions.InsertMany(rows);
                using var connection = db.OpenConnection();
                connection.Execute("UPDATE \"Editions\" SET \"Monitored\"=true WHERE \"Id\"=@Id", rows[0]);
                var failure = Assert.Catch(() => connection.Execute("UPDATE \"Editions\" SET \"Monitored\"=true WHERE \"Id\"=@Id", rows[1]));
                if (postgres) Assert.That(((PostgresException)failure).SqlState, Is.EqualTo(PostgresErrorCodes.UniqueViolation));
                else Assert.That(((SqliteException)failure).SqliteExtendedErrorCode, Is.EqualTo(2067));
                Assert.That(editions.FindRowsByBook(new[] { book.Id }).Single(edition => edition.Monitored).Id, Is.EqualTo(rows[0].Id));
                Assert.That(editions.FindRowsByBook(new[] { book.Id }), Has.Count.EqualTo(5));
            });
        }

        [Test]
        public void writes_switch_atomically_in_both_id_orders_and_do_not_change_other_books(
            [Values(false, true)] bool postgres, [Values("update", "selection", "set")] string writer)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                var book = AddBook(db);
                var other = AddBook(db, BookMediaType.Ebook);
                var editions = new EditionRepository(db, Events);
                var first = editions.Insert(NewEdition(book, true, manual: true));
                var second = editions.Insert(NewEdition(book));
                var otherSelected = editions.Insert(NewEdition(other, true));
                foreach (var target in new[] { second, first })
                {
                    target.Monitored = true;
                    if (writer == "update") editions.Update(target);
                    else if (writer == "selection") editions.UpdateSelection(new List<Edition> { target });
                    else editions.SetMonitored(target, true);
                    Assert.That(editions.FindRowsByBook(new[] { book.Id }).Single(edition => edition.Monitored).Id, Is.EqualTo(target.Id));
                    Assert.That(editions.Get(otherSelected.Id).Monitored, Is.True);
                }
                var inserted = editions.Insert(NewEdition(book, true));
                Assert.Throws<InvalidOperationException>(() => editions.SetMonitored(new Edition { Id = int.MaxValue, BookId = book.Id }));
                Assert.That(editions.FindRowsByBook(new[] { book.Id }).Single(edition => edition.Monitored).Id, Is.EqualTo(inserted.Id));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void failed_update_restores_previous_selection_and_pin(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                var book = AddBook(db);
                var editions = new EditionRepository(db, Events);
                var first = editions.Insert(NewEdition(book, true, manual: true));
                var second = editions.Insert(NewEdition(book));
                second.Monitored = true;
                second.Title = null;
                Assert.Catch(() => editions.Update(second));
                Assert.That(editions.Get(first.Id).Monitored, Is.True);
                Assert.That(editions.Get(first.Id).ManualAdd, Is.True);
                Assert.That(editions.Get(second.Id).Monitored, Is.False);
            });
        }

        [Test]
        public void edition_removed_before_save_does_not_clear_the_previous_selection(
            [Values(false, true)] bool postgres, [Values(false, true)] bool selectionOnly)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                var book = AddBook(db);
                var editions = new EditionRepository(db, Events);
                var current = editions.Insert(NewEdition(book, true, manual: true));
                var removed = editions.Insert(NewEdition(book));
                editions.Delete(removed.Id);
                removed.Monitored = true;
                Assert.Throws<ModelConflictException>(() =>
                {
                    if (selectionOnly) editions.UpdateSelection(new List<Edition> { removed });
                    else editions.Update(removed);
                });
                Assert.That(editions.Get(current.Id).Monitored, Is.True);
                Assert.That(editions.Get(current.Id).ManualAdd, Is.True);
            });
        }

        [Test]
        public void failed_insert_or_ambiguous_batch_preserves_the_selection(
            [Values(false, true)] bool postgres, [Values(false, true)] bool ambiguous)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                var book = AddBook(db);
                var editions = new EditionRepository(db, Events);
                var old = editions.Insert(NewEdition(book, true, manual: true));
                var first = NewEdition(book, true);
                var second = NewEdition(book, ambiguous);
                if (!ambiguous) second.Title = null;
                Assert.Catch(() => editions.InsertMany(new List<Edition> { first, second }));
                Assert.That(editions.FindRowsByBook(new[] { book.Id }), Has.Count.EqualTo(1));
                Assert.That(editions.Get(old.Id).Monitored, Is.True);
                Assert.That(editions.Get(old.Id).ManualAdd, Is.True);
            });
        }

        [Test]
        public void concurrent_selections_leave_one_selected_without_losing_either_request(
            [Values(false, true)] bool postgres, [Values(false, true)] bool existingSelection)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                var book = AddBook(db);
                var editions = new EditionRepository(db, Events);
                var first = editions.Insert(NewEdition(book, existingSelection));
                var second = editions.Insert(NewEdition(book));
                using var start = new Barrier(2);
                var tasks = new[] { first, second }.Select(edition => Task.Run(() =>
                {
                    start.SignalAndWait();
                    edition.Monitored = true;
                    new EditionRepository(db, Events).UpdateSelection(new List<Edition> { edition });
                })).ToArray();
                Task.WaitAll(tasks);
                Assert.That(editions.FindRowsByBook(new[] { book.Id }).Count(edition => edition.Monitored), Is.EqualTo(1));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void migration_repairs_duplicates_using_files_then_pins_then_insertion_ranking_without_churn(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                var editions = new LegacyEditions(db);
                var expected = new List<int>();
                using var connection = db.OpenConnection();
                for (var scenario = 0; scenario < 8; scenario++)
                {
                    var book = AddLegacyBook(db, scenario == 4 ? BookMediaType.Ebook : BookMediaType.Audiobook, anyEditionOk: scenario != 5);
                    var first = editions.Insert(NewEdition(book, true, format: scenario == 3 ? 3 : 2, votes: 1000, manual: scenario == 0));
                    var second = editions.Insert(NewEdition(book, scenario != 6, format: scenario == 4 ? 3 : 2, votes: scenario is 6 or 7 ? 100000 : 10, manual: scenario == 1));
                    if (scenario is 0 or 6)
                    {
                        InsertLegacy(db, new BookFile
                        {
                            EditionId = second.Id, Path = $"/fixture/{book.Id}/book.m4b", Size = 1,
                            DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow,
                            Quality = new QualityModel(), MediaInfo = new MediaInfoModel()
                        });
                    }
                    if (scenario == 2) first.ReadingFormatId = 1;
                    if (scenario == 3) second.ReadingFormatId = 1;
                    if (scenario == 5)
                    {
                        editions.Insert(NewEdition(book, false, format: 2, votes: 100000));
                    }
                    editions.Update(first);
                    editions.Update(second);
                    expected.Add(scenario is 0 or 1 or 2 or 4 or 7 ? second.Id : first.Id);
                }
                var unselected = editions.Insert(NewEdition(AddLegacyBook(db), false));
                var filesBefore = connection.Query<(int Id, int EditionId)>("SELECT \"Id\",\"EditionId\" FROM \"BookFiles\"").ToArray();
                var migration = new MigrationController(LogManager.GetCurrentClassLogger(), null);
                var connectionString = connection.ConnectionString;
                migration.Migrate(connectionString, new MigrationContext(MigrationType.Main, 117), db.DatabaseType);
                Assert.That(editions.All().Where(edition => edition.Monitored).Select(edition => edition.Id), Is.EquivalentTo(expected));
                Assert.That(connection.Query<(int Id, int EditionId)>("SELECT \"Id\",\"EditionId\" FROM \"BookFiles\""), Is.EqualTo(filesBefore));
                Assert.That(editions.Get(unselected.Id).Monitored, Is.False);
                new FixMultipleMonitoredEditions(db).Clean();
                Assert.That(editions.All().Where(edition => edition.Monitored).Select(edition => edition.Id), Is.EquivalentTo(expected.Append(unselected.Id)));
                Assert.That(editions.All().Count(edition => edition.ManualAdd), Is.EqualTo(2),
                    "monitoring repair must preserve independent retention pins, including losing pins");
            }, desiredVersion: 116);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void migration_removes_invalid_orphan_editions_before_enforcing_selection(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                var validBook = AddLegacyBook(db);
                var missingBook = AddLegacyBook(db);
                var editions = new LegacyEditions(db);
                var retained = editions.Insert(NewEdition(validBook, true, manual: true));
                var invalid = new[]
                {
                    editions.Insert(NewEdition(missingBook, true, manual: true)),
                    editions.Insert(NewEdition(missingBook, true)),
                    editions.Insert(NewEdition(missingBook, false))
                };
                new BookRepository(db, Events).Delete(missingBook.Id);
                using var connection = db.OpenConnection();
                var migration = new MigrationController(LogManager.GetCurrentClassLogger(), null);
                var connectionString = connection.ConnectionString;

                migration.Migrate(connectionString, new MigrationContext(MigrationType.Main, 117), db.DatabaseType);

                Assert.That(editions.All().Select(edition => edition.Id), Is.EqualTo(new[] { retained.Id }));
                Assert.That(editions.Get(retained.Id).ManualAdd, Is.True);
                Assert.That(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"Editions\" WHERE \"BookId\"=@BookId", new { BookId = missingBook.Id }), Is.Zero);
                Assert.That(invalid, Has.Length.EqualTo(3));
            }, desiredVersion: 116);
        }

        [Test]
        public void automatic_transaction_displacement_preserves_independent_manual_retention(
            [Values(false, true)] bool postgres, [Values(false, true)] bool selectionOnly)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                var book = AddBook(db);
                var editions = new EditionRepository(db, Events);
                var retained = editions.Insert(NewEdition(book, true, manual: true));
                var selected = editions.Insert(NewEdition(book));
                selected.Monitored = true;

                if (selectionOnly) editions.UpdateSelection(new List<Edition> { selected });
                else editions.Update(selected);

                Assert.That(editions.Get(retained.Id).Monitored, Is.False);
                Assert.That(editions.Get(retained.Id).ManualAdd, Is.True);
                Assert.That(editions.Get(selected.Id).Monitored, Is.True);
            });
        }

        [Test]
        public void explicit_selection_releases_displaced_pin_and_preserves_independent_retention(
            [Values(false, true)] bool postgres, [Values(false, true)] bool manualSelection)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                var book = AddBook(db);
                var editions = new EditionRepository(db, Events);
                var previous = editions.Insert(NewEdition(book, true, manual: true));
                var retained = editions.Insert(NewEdition(book, false, manual: true));
                var chosen = editions.Insert(NewEdition(book));

                editions.SetMonitored(chosen, manualSelection);

                Assert.That(editions.Get(previous.Id).Monitored, Is.False);
                Assert.That(editions.Get(previous.Id).ManualAdd, Is.False,
                    "explicit displacement releases the prior selection pin");
                Assert.That(editions.Get(retained.Id).ManualAdd, Is.True,
                    "an independent unselected retention pin is not displaced");
                Assert.That(editions.Get(chosen.Id).ManualAdd, Is.EqualTo(manualSelection));

                Assert.That(editions.FindRowsByBook(new[] { book.Id }).Single(edition => edition.Monitored).Id, Is.EqualTo(chosen.Id));
                Assert.That(editions.Get(retained.Id).ManualAdd, Is.True);
            });
        }

        [Test]
        public void unchanged_selection_guard_rejects_removed_or_rehomed_edition_and_rolls_back_parent(
            [Values(false, true)] bool postgres, [Values(false, true)] bool rehome)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                var book = AddBook(db);
                var other = AddBook(db);
                var editions = new EditionRepository(db, Events);
                var selected = editions.Insert(NewEdition(book, true));
                var expected = new Edition { Id = selected.Id, BookId = book.Id };
                if (rehome)
                {
                    selected.BookId = other.Id;
                    editions.Update(selected);
                }
                else
                {
                    editions.DeleteMany(new[] { selected.Id });
                }

                Assert.Throws<ModelConflictException>(() =>
                {
                    using var connection = db.OpenConnection();
                    using var transaction = connection.BeginTransaction();
                    connection.Execute("UPDATE \"Books\" SET \"AnyEditionOk\"=false WHERE \"Id\"=@Id", book, transaction);
                    editions.UpdateMany(new List<Edition>(), connection, transaction, expected);
                    transaction.Commit();
                });

                Assert.That(new BookRepository(db, Events).Get(book.Id).AnyEditionOk, Is.True);
                if (rehome) Assert.That(editions.Get(selected.Id).BookId, Is.EqualTo(other.Id));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void unchanged_selection_guard_rejects_concurrent_selection_change_but_allows_written_choice(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                var book = AddBook(db);
                var editions = new EditionRepository(db, Events);
                var stale = editions.Insert(NewEdition(book, true));
                var current = editions.Insert(NewEdition(book));
                current.Monitored = true;
                editions.UpdateSelection(new List<Edition> { current });

                Assert.Throws<ModelConflictException>(() =>
                {
                    using var connection = db.OpenConnection();
                    using var transaction = connection.BeginTransaction();
                    editions.UpdateMany(new List<Edition>(), connection, transaction, stale);
                    transaction.Commit();
                });
                Assert.That(editions.Get(current.Id).Monitored, Is.True);

                using (var connection = db.OpenConnection())
                using (var transaction = connection.BeginTransaction())
                {
                    editions.UpdateMany(new List<Edition> { stale }, connection, transaction, stale);
                    transaction.Commit();
                }
                Assert.That(editions.Get(stale.Id).Monitored, Is.True);
                Assert.That(editions.Get(current.Id).Monitored, Is.False);
            });
        }

        [Test]
        public void parent_contract_rejects_missing_null_zero_and_stale_rehome(
            [Values(false, true)] bool postgres, [Values("missing", "null", "zero", "rehome")] string operation)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                var book = AddBook(db);
                var removed = AddBook(db);
                var editions = new EditionRepository(db, Events);
                var selected = editions.Insert(NewEdition(book, true, manual: true));
                new BookRepository(db, Events).Delete(removed.Id);
                using var connection = db.OpenConnection();

                var failure = Assert.Catch(() =>
                {
                    if (operation == "rehome")
                    {
                        selected.BookId = removed.Id;
                        editions.Update(selected);
                    }
                    else if (operation == "null")
                    {
                        connection.Execute("UPDATE \"Editions\" SET \"BookId\"=NULL WHERE \"Id\"=@Id", selected);
                    }
                    else
                    {
                        var invalid = NewEdition(book, true);
                        invalid.BookId = operation == "zero" ? 0 : removed.Id;
                        editions.Insert(invalid);
                    }
                });

                Assert.That(failure, Is.InstanceOf(postgres ? typeof(PostgresException) : typeof(SqliteException)));
                if (operation != "null")
                {
                    if (postgres) Assert.That(((PostgresException)failure).SqlState, Is.EqualTo(PostgresErrorCodes.ForeignKeyViolation));
                    AssertParentConflictResponse(failure);
                }
                var stored = editions.Get(selected.Id);
                Assert.That(stored.BookId, Is.EqualTo(book.Id));
                Assert.That(stored.Monitored, Is.True);
                Assert.That(stored.ManualAdd, Is.True);
                Assert.That(editions.All(), Has.Count.EqualTo(1));
            });
        }

        [Test]
        public void book_deletion_commits_ownership_before_events_or_rolls_back_every_row(
            [Values(false, true)] bool postgres, [Values(false, true)] bool failDelete,
            [Values(false, true)] bool deleteFiles, [Values(false, true)] bool bulk)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                var events = new CleanupEvents();
                var book = AddBook(db);
                var retainedBook = AddBook(db);
                var books = new BookRepository(db, events);
                var editions = new EditionRepository(db, events);
                var edition = NewEdition(book, true, manual: true);
                edition.ForeignEditionId = "gr:123456";
                editions.Insert(edition);
                var retainedEdition = editions.Insert(NewEdition(retainedBook, true));
                var files = new MediaFileRepository(db, events);
                var file = files.Insert(new BookFile
                {
                    EditionId = edition.Id, Path = "/fixture/kept.m4b", Size = 1,
                    DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow,
                    Quality = new QualityModel(), MediaInfo = new MediaInfoModel()
                });
                var logger = LogManager.GetCurrentClassLogger();
                var media = new MediaFileService(files, events, null, null, logger);
                var editionService = new EditionService(editions, events, logger);
                var notifications = new List<BookDeletedEvent>();
                events.Handle = message =>
                {
                    if (message is BookDeletedEvent deleted)
                    {
                        Assert.That(books.Find(book.Id), Is.Null);
                        Assert.That(editions.Find(edition.Id), Is.Null);
                        Assert.That(deleted.Book.BookFiles.Select(item => item.Id), Is.EqualTo(new[] { file.Id }));
                        notifications.Add(deleted);
                    }
                };
                using var connection = db.OpenConnection();
                var aliasesBefore = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"ProviderAliasIndex\" WHERE \"EntityType\"='Edition' AND \"EntityId\"=@Id", edition);
                Assert.That(aliasesBefore, Is.GreaterThan(0));
                if (failDelete)
                {
                    connection.Execute(postgres
                        ? @"CREATE FUNCTION reject_book_delete() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'constructed delete failure'; END $$;
                            CREATE TRIGGER reject_book_delete BEFORE DELETE ON ""Books"" FOR EACH ROW EXECUTE FUNCTION reject_book_delete();"
                        : @"CREATE TRIGGER reject_book_delete BEFORE DELETE ON Books BEGIN SELECT RAISE(ABORT, 'constructed delete failure'); END;");
                }

                var authors = Proxy<IAuthorService>((method, args) => method.Name == nameof(IAuthorService.GetAuthor)
                    ? new AuthorRepository(db, events, logger).Get((int)args[0]) : throw new NotImplementedException(method.Name));
                var service = new BookService(books, editionService, events, authors, media, null,
                    new SeriesBookLinkRepository(db, events), null, logger,
                    deleteMediaFiles: new Lazy<IDeleteMediaFiles>(() => Proxy<IDeleteMediaFiles>((method, _) =>
                        method.Name == nameof(IDeleteMediaFiles.DeleteBookFiles) ? new List<BookFile> { file } : throw new NotSupportedException(method.Name))));
                void Delete()
                {
                    if (bulk) service.DeleteMany(new List<Book> { book }, filesAlreadyDeleted: deleteFiles,
                        deletedBookFiles: deleteFiles ? new List<BookFile> { file } : null);
                    else service.DeleteBook(book.Id, deleteFiles);
                }

                if (failDelete)
                {
                    var error = Assert.Catch(Delete);
                    Assert.That(error.ToString(), Does.Contain("constructed delete failure"));
                    Assert.That(books.Find(book.Id), Is.Not.Null);
                    Assert.That(editions.Find(edition.Id), Is.Not.Null);
                    Assert.That(files.Get(file.Id).EditionId, Is.EqualTo(edition.Id));
                    Assert.That(notifications, Is.Empty);
                }
                else
                {
                    Delete();
                    Assert.That(books.Find(book.Id), Is.Null);
                    Assert.That(editions.Find(edition.Id), Is.Null);
                    Assert.That(notifications, Has.Count.EqualTo(1));
                    if (deleteFiles) Assert.That(files.Find(file.Id), Is.Null);
                    else Assert.That(files.Get(file.Id).EditionId, Is.Zero);
                }

                Assert.That(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"ProviderAliasIndex\" WHERE \"EntityType\"='Edition' AND \"EntityId\"=@Id", edition),
                    Is.EqualTo(failDelete ? aliasesBefore : 0));
                Assert.That(books.Find(retainedBook.Id), Is.Not.Null);
                Assert.That(editions.Find(retainedEdition.Id), Is.Not.Null);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void orphan_book_housekeeping_uses_normal_cleanup_and_keeps_physical_files(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                var events = new CleanupEvents();
                var logger = LogManager.GetCurrentClassLogger();
                var orphan = AddBook(db);
                var retained = AddBook(db);
                var authors = new AuthorRepository(db, events, logger);
                var books = new BookRepository(db, events);
                var editions = new EditionRepository(db, events);
                var orphanEditions = new[] { editions.Insert(NewEdition(orphan, true)), editions.Insert(NewEdition(orphan)) };
                var retainedEdition = editions.Insert(NewEdition(retained, true, manual: true));
                var files = new MediaFileRepository(db, events);
                var file = files.Insert(new BookFile
                {
                    EditionId = orphanEditions[0].Id, Path = "/fixture/keep-original.m4b", Size = 1,
                    DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow,
                    Quality = new QualityModel(), MediaInfo = new MediaInfoModel()
                });
                SeedLegacyOrphanedBooks(db, orphan.AuthorId);
                var editionService = new EditionService(editions, events, logger);
                var diskCalls = 0;
                var disk = Proxy<IDiskProvider>((method, args) =>
                {
                    diskCalls++;
                    throw new AssertionException("Orphan cleanup must not perform physical file operations: " + method.Name);
                });
                var queue = Proxy<IIngestQueueRepository>((method, args) => method.Name is nameof(IIngestQueueRepository.PurgePaths) or nameof(IIngestQueueRepository.PurgeUnderPath)
                    ? 0 : throw new NotImplementedException(method.Name));
                var media = new MediaFileService(files, events, queue, disk, logger);
                var diskDeletion = new MediaFileDeletionService(disk, null, media, authors, null, events, null, null, logger, metadataFiles: Chaptarr.Core.Test.Common.EmptyDeletionExtras.Repository<NzbDrone.Core.Extras.Metadata.Files.MetadataFile>(), otherFiles: Chaptarr.Core.Test.Common.EmptyDeletionExtras.Repository<NzbDrone.Core.Extras.Others.OtherExtraFile>(), metadataFileService: Chaptarr.Core.Test.Common.EmptyDeletionExtras.Metadata(), otherFileService: Chaptarr.Core.Test.Common.EmptyDeletionExtras.Other());
                var notificationSends = 0;
                var notificationFailures = 0;
                var discordProxy = Proxy<IDiscordProxy>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(IDiscordProxy.SendPayload)));
                    notificationSends++;
                    return null;
                });
                var definitions = new[] { false, true }.Select((tagged, index) => new NotificationDefinition
                {
                    Id = index + 1, Name = "Discord fixture", Implementation = nameof(Discord), OnBookDelete = true,
                    Tags = tagged ? new HashSet<int> { 7 } : new HashSet<int>(),
                    Settings = new DiscordSettings { WebHookUrl = "https://example.test/notification-fixture" }
                }).ToList();
                var notificationRepository = Proxy<INotificationRepository>((method, args) => method.Name == nameof(INotificationRepository.All)
                    ? definitions : throw new NotImplementedException(method.Name));
                var status = Proxy<INotificationStatusService>((method, args) =>
                {
                    if (method.Name == nameof(INotificationStatusService.GetBlockedProviders)) return new List<NotificationStatus>();
                    if (method.Name == nameof(INotificationStatusService.RecordFailure)) notificationFailures++;
                    else Assert.That(method.Name, Is.EqualTo(nameof(INotificationStatusService.RecordSuccess)));
                    return null;
                });
                var provider = Proxy<IServiceProvider>((method, args) => (Type)args[0] == typeof(Discord)
                    ? new Discord(discordProxy) : throw new NotImplementedException(method.Name));
                var factory = new NotificationFactory(status, notificationRepository,
                    new INotification[] { new Discord(discordProxy) }, provider, events, logger);
                var notification = new NotificationService(factory, status, editionService, logger);
                var deleted = new List<int>();
                events.Handle = message =>
                {
                    if (message is BookDeletedEvent removed)
                    {
                        Assert.That(removed.Book.Author, Is.Null);
                        Assert.That(removed.Book.AuthorId, Is.EqualTo(orphan.AuthorId), "missing Author context must retain the numeric ownership ID");
                        Assert.That(removed.DeleteFiles, Is.False);
                        notification.Handle(removed); // Missing-author maintenance skips provider addressing.
                        media.HandleAsync(removed);
                        deleted.Add(removed.Book.Id);
                    }
                    else if (message is EditionDeletedEvent removedEdition)
                    {
                        media.Handle(removedEdition);
                    }
                };
                var authorService = Proxy<IAuthorService>((method, args) => method.Name == nameof(IAuthorService.GetAuthor)
                    ? authors.Get((int)args[0]) : throw new NotImplementedException(method.Name));
                var service = new BookService(books, editionService, events, authorService, media, null,
                    new SeriesBookLinkRepository(db, events), null, logger,
                    deleteMediaFiles: new Lazy<IDeleteMediaFiles>(() => diskDeletion));
                var cleanup = new CleanupOrphanedBooks(db, service);

                cleanup.Clean();
                cleanup.Clean();

                Assert.That(deleted, Is.EqualTo(new[] { orphan.Id }));
                Assert.That(books.Find(orphan.Id), Is.Null);
                Assert.That(editions.FindRowsByBook(new[] { orphan.Id }), Is.Empty);
                Assert.That(files.Get(file.Id).EditionId, Is.Zero);
                Assert.That(files.Get(file.Id).Path, Is.EqualTo(file.Path));
                Assert.That(diskCalls, Is.Zero);
                Assert.That(books.Find(retained.Id), Is.Not.Null);
                Assert.That(editions.Get(retainedEdition.Id).Monitored, Is.True);
                Assert.That(events.CleanupFailures, Is.Zero);
                Assert.That(notificationSends, Is.Zero);
                Assert.That(notificationFailures, Is.Zero);

                var retainedSnapshot = new Book { Title = "Retained book", Author = new Author
                    { Id = retained.AuthorId, Name = "Retained Author", Tags = new HashSet<int> { 7 } } };
                notification.Handle(new BookDeletedEvent(retainedSnapshot, false, false));
                Assert.That(notificationSends, Is.EqualTo(2), "normal factory and tagged/untagged adapters still receive complete context");
                Assert.That(notificationFailures, Is.Zero);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void orphan_book_housekeeping_rechecks_and_bounds_each_delete_batch(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                var first = AddBook(db);
                var retained = AddBook(db);
                var books = new BookRepository(db, Events);
                var rows = Enumerable.Range(0, SqliteVariableLimit.MaxParameters).Select(_ => new Book
                {
                    AuthorId = first.AuthorId, Title = "Orphan", CleanTitle = "orphan", TitleSlug = Guid.NewGuid().ToString(),
                    MediaType = BookMediaType.Audiobook, Added = DateTime.UtcNow
                }).ToList();
                books.InsertMany(rows);
                SeedLegacyOrphanedBooks(db, first.AuthorId);
                var batches = new List<int>();
                var service = Proxy<IBookService>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(IBookService.DeleteMany)));
                    var batch = (List<Book>)args[0];
                    batches.Add(batch.Count);
                    Assert.That(batch.All(book => book.AuthorId == first.AuthorId), Is.True);
                    books.DeleteMany(batch);
                    return null;
                });

                new CleanupOrphanedBooks(db, service).Clean();

                Assert.That(batches, Is.EqualTo(new[] { SqliteVariableLimit.MaxParameters, 1 }));
                Assert.That(books.All().Select(book => book.Id), Is.EqualTo(new[] { retained.Id }));
            });
        }

        [Test]
        public void concurrent_parent_deletion_rejects_a_stale_insert_or_rehome(
            [Values(false, true)] bool postgres, [Values(false, true)] bool rehome)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                var removed = AddBook(db);
                var retained = AddBook(db);
                var editions = new EditionRepository(db, Events);
                var existing = rehome ? editions.Insert(NewEdition(retained, true, manual: true)) : NewEdition(removed);
                using var deleteReady = new ManualResetEventSlim();
                using var writeReady = new ManualResetEventSlim();
                var deleting = Task.Run(() =>
                {
                    using var connection = db.OpenConnection();
                    using var transaction = connection.BeginTransaction();
                    connection.Execute("DELETE FROM \"Books\" WHERE \"Id\"=@Id", removed, transaction);
                    deleteReady.Set();
                    Assert.That(writeReady.Wait(TimeSpan.FromSeconds(10)), Is.True);
                    transaction.Commit();
                });
                var writing = Task.Run(() =>
                {
                    Assert.That(deleteReady.Wait(TimeSpan.FromSeconds(10)), Is.True);
                    existing.BookId = removed.Id;
                    writeReady.Set();
                    return Assert.Catch(() =>
                    {
                        if (rehome) editions.Update(existing);
                        else editions.Insert(existing);
                    });
                });
                Task.WaitAll(deleting, writing);

                Assert.That(writing.Result, Is.InstanceOf(postgres ? typeof(PostgresException) : typeof(SqliteException)));
                using var verify = db.OpenConnection();
                Assert.That(verify.ExecuteScalar<int>("SELECT COUNT(*) FROM \"Editions\" e LEFT JOIN \"Books\" b ON b.\"Id\"=e.\"BookId\" WHERE b.\"Id\" IS NULL"), Is.Zero);
                if (rehome) Assert.That(editions.Get(existing.Id).BookId, Is.EqualTo(retained.Id));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void refresh_pocket_convergence_preserves_each_populated_copys_selection_and_file_links(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                var books = new BookRepository(db, Events);
                var target = AddBook(db);
                var sources = new[] { 1001, 1002 }.Select(id => books.Insert(new Book
                {
                    Title = "Source Book", CleanTitle = "sourcebook", TitleSlug = Guid.NewGuid().ToString(),
                    GoodreadsWorkId = $"gr:{id}", AuthorId = target.AuthorId,
                    MediaType = target.MediaType, AnyEditionOk = true, Added = DateTime.UtcNow
                })).ToArray();
                target.GoodreadsWorkId = "gr:2000";
                books.Update(target);
                var editions = new EditionRepository(db, Events);
                var current = editions.Insert(NewEdition(target, true, votes: 10000));
                var moved = sources.Select((book, i) =>
                {
                    var edition = NewEdition(book, true, votes: 10 + i);
                    edition.ForeignEditionId = $"gr:{3000 + i}-audiobook";
                    return editions.Insert(edition);
                }).ToList();
                var files = new BasicRepository<BookFile>(db, Events);
                foreach (var edition in moved)
                {
                    files.Insert(new BookFile
                    {
                        EditionId = edition.Id, Path = $"/fixture/{edition.Id}/recording.m4b", Size = 1,
                        DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow,
                        Quality = new QualityModel(), MediaInfo = new MediaInfoModel()
                    });
                }
                var filesBefore = files.All().Select(file => (file.Id, file.EditionId, file.Path)).ToArray();
                var bookService = Proxy<IBookService>((method, args) =>
                {
                    if (method.Name == nameof(IBookService.GetBooksByAuthorId)) return books.GetBooksByAuthorId((int)args[0]);
                    if (method.Name == nameof(IBookService.UpdateMany)) { books.UpdateMany((List<Book>)args[0]); return null; }
                    throw new NotImplementedException(method.Name);
                });
                var editionService = Proxy<IEditionService>((method, args) =>
                {
                    if (method.Name == nameof(IEditionService.GetEditionsByBook)) return editions.FindByBook((IEnumerable<int>)args[0]);
                    if (method.Name == nameof(IEditionService.UpdateMany)) { editions.UpdateMany((List<Edition>)args[0]); return null; }
                    throw new NotImplementedException(method.Name);
                });
                var mediaFiles = Proxy<IMediaFileService>((method, args) => method.Name == nameof(IMediaFileService.GetFilesByBooks)
                    ? files.All() : throw new NotImplementedException(method.Name));
                var service = new RefreshAuthorService(MetadataHealthGateStub.Ready(), authorInfo: null, authorService: null, bookService: bookService,
                    editionService: editionService, metadataProfileService: null, refreshBookService: null,
                    refreshSeriesService: null, eventAggregator: Events, commandQueueManager: null,
                    mediaFileService: mediaFiles, historyRepository: new global::NzbDrone.Core.History.HistoryRepository(db, Events), rootFolderService: null,
                    checkIfAuthorShouldBeRefreshed: null, monitorNewBookService: null, configService: null,
                    importListExclusionService: null, syncMetadataService: null, syncQueueService: null,
                    rootFolderSettingsResolver: null, logger: LogManager.GetCurrentClassLogger(), mainDatabase: db, bookRepository: new BookRepository(db, Events), authorRepository: new AuthorRepository(db, Events, LogManager.GetCurrentClassLogger()));
                service.RepairOverMergedBookEditions(target.AuthorId, new List<Book>
                {
                    new() { Title = target.Title, CleanTitle = target.CleanTitle, GoodreadsWorkId = target.GoodreadsWorkId, MediaType = target.MediaType, Editions = moved }
                });
                // The empty target is absorbed; both populated local copies and their selections survive.
                Assert.That(books.Find(target.Id), Is.Null);
                Assert.That(sources.Select(book => book.Id), Does.Contain(editions.Get(current.Id).BookId));
                foreach (var source in sources)
                {
                    Assert.That(books.Get(source.Id).GoodreadsWorkId, Is.EqualTo(target.GoodreadsWorkId));
                    Assert.That(editions.FindRowsByBook(new[] { source.Id }).Single(edition => edition.Monitored).Id,
                        Is.EqualTo(moved.Single(edition => edition.BookId == source.Id).Id));
                }
                Assert.That(files.All().Select(file => (file.Id, file.EditionId, file.Path)), Is.EqualTo(filesBefore));
            });
        }

        private static IMainDatabase ObserveCommands(IMainDatabase db, List<string> statements) =>
            Proxy<IMainDatabase>((method, args) => method.Name == nameof(IMainDatabase.OpenConnection)
                ? CommandObserver.Wrap(db.OpenConnection(), command => statements.Add(command.CommandText), includeWrites: true)
                : method.Invoke(db, args));

        [TestCase(false)]
        [TestCase(true)]
        public void selection_bulk_write_chunks_parameters_without_per_book_reads(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                const int count = 901;
                var seed = AddBook(db);
                var books = Enumerable.Range(1, count - 1).Select(_ => new Book
                {
                    Title = "Batch Book", CleanTitle = "batchbook", TitleSlug = Guid.NewGuid().ToString(),
                    AuthorId = seed.AuthorId, MediaType = BookMediaType.Audiobook, Added = DateTime.UtcNow
                }).ToList();
                new BookRepository(db, Events).InsertMany(books);
                books.Add(seed);
                var repository = new EditionRepository(db, Events);
                var old = books.Select(book => NewEdition(book, true)).ToList();
                var next = books.Select(book => NewEdition(book)).ToList();
                repository.InsertMany(old.Concat(next).ToList());
                next.ForEach(edition => edition.Monitored = true);
                var statements = new List<string>();
                var timer = Stopwatch.StartNew();
                new EditionRepository(ObserveCommands(db, statements), Events).UpdateSelection(next);
                timer.Stop();
                Assert.That(repository.All().Where(edition => edition.Monitored).Select(edition => edition.Id),
                    Is.EquivalentTo(next.Select(edition => edition.Id)));
                Assert.That(statements, Has.Count.EqualTo(postgres ? 3 : 5));
                TestContext.WriteLine($"{db.DatabaseType}: switching {count} books used {statements.Count} SQL commands in {timer.Elapsed.TotalMilliseconds:F2} ms");
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void selection_batch_handles_large_libraries_without_per_edition_reads(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                var editions = new EditionRepository(db, Events);
                var book = AddBook(db);
                var rows = Enumerable.Range(0, 1132).Select(_ => NewEdition(book)).ToList();
                rows[0].Monitored = true;
                editions.InsertMany(rows);
                var statements = new List<string>();
                var observed = ObserveCommands(db, statements);
                var writer = new EditionRepository(observed, Events);
                var watch = Stopwatch.StartNew();
                for (var i = 0; i < 10; i++)
                {
                    var selected = rows[i + 1];
                    selected.Monitored = true;
                    writer.UpdateSelection(new List<Edition> { selected });
                }
                watch.Stop();
                Assert.That(editions.FindRowsByBook(new[] { book.Id }).Single(edition => edition.Monitored).Id, Is.EqualTo(rows[10].Id));
                Assert.That(statements, Has.Count.EqualTo(postgres ? 30 : 20));
                Assert.That(statements.Any(sql => sql.StartsWith("SELECT", StringComparison.Ordinal) && sql.Contains("Editions", StringComparison.Ordinal)), Is.False);
                TestContext.WriteLine($"{db.DatabaseType}: {statements.Count} SQL commands; 10 selection writes across 1132 editions took {watch.Elapsed.TotalMilliseconds:F2} ms");
            });
        }
    }
}
