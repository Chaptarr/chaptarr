using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Chaptarr.Core.Test.Common;
using Dapper;
using NLog;
using NLog.Config;
using NLog.Targets;
using NzbDrone.Core.Books.Commands;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration.Framework;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    [NonParallelizable]
    public class LibraryTitleSearchIndexFixture
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        [TestCase(false)]
        [TestCase(true)]
        public void upgrade_indexes_existing_edition_titles_without_changing_shared_indexes(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var (editions, book, edition) = Seed(database, historicalSchema: true);
                using var connection = database.OpenConnection();
                string[] IndexDefinitions() => connection.Query<string>(postgres
                    ? "SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' ORDER BY indexname"
                    : "SELECT sql FROM sqlite_master WHERE name IN ('edition_fts', 'author_fts') ORDER BY name").ToArray();
                var before = IndexDefinitions();
                var original = editions.GetEdition(edition.Id);

                var connectionString = connection.ConnectionString;
                new MigrationController(Logger, null).Migrate(connectionString,
                    new MigrationContext(MigrationType.Main, 118), database.DatabaseType);

                Assert.Multiple(() =>
                {
                    Assert.That(IndexDefinitions(), Is.EqualTo(before));
                    Assert.That(editions.GetEdition(edition.Id).Title, Is.EqualTo(original.Title));
                    Assert.That(editions.GetEdition(edition.Id).MatchingTitle, Is.EqualTo(original.MatchingTitle));
                    Assert.That(new LibrarySearchService(database, Logger).Search("man spider", 10).Books.Single().Id, Is.EqualTo(book.Id));
                    Assert.That(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"VersionInfo\" WHERE \"Version\"=118"), Is.EqualTo(1));
                });
                if (!postgres)
                {
                    Assert.That(connection.Query<int>("SELECT rowid FROM edition_fts WHERE edition_fts MATCH 'MatchingTitle:\"spider-man\"'"), Is.EqualTo(new[] { edition.Id }));
                    Assert.That(connection.Query<int>("SELECT rowid FROM edition_fts WHERE edition_fts MATCH 'MatchingTitle:(spider AND man)'"), Is.Empty);
                    CheckIntegrity(connection);
                }
            }, desiredVersion: 117);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void real_edition_writes_and_transaction_rollback_keep_header_results_current(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var (editions, book, edition) = Seed(database);
                var service = new LibrarySearchService(database, Logger);
                Assert.That(service.Search("spider man", 10).Books.Single().Id, Is.EqualTo(book.Id));
                Assert.That(service.Search("edition display", 10).Books, Is.Empty, "The work display title is not edition-title evidence");

                edition.Title = "High-Opp";
                editions.UpdateMany(new List<Edition> { edition });
                Assert.That(service.Search("spider", 10).Books, Is.Empty);
                Assert.That(service.Search("opp", 10).Books.Single().Id, Is.EqualTo(book.Id));

                using (var connection = database.OpenConnection())
                {
                    using (var transaction = connection.BeginTransaction())
                    {
                        connection.Execute("DELETE FROM \"Editions\" WHERE \"Id\"=@Id", new { edition.Id }, transaction);
                        transaction.Rollback();
                    }
                    if (!postgres) CheckIntegrity(connection);
                }
                Assert.That(service.Search("opp", 10).Books.Single().Id, Is.EqualTo(book.Id));

                var added = new List<Edition>
                {
                    new Edition { BookId = book.Id, Title = "Søren's Catch-22" },
                    new Edition { BookId = book.Id, Title = "Straße Spider Man" }
                };
                editions.InsertMany(added);
                Assert.That(service.Search("22 soren", 10).Books.Single().Id, Is.EqualTo(book.Id));
                Assert.That(service.Search("strasse man", 10).Books.Single().Id, Is.EqualTo(book.Id));
                Assert.That(service.Search("catch strasse", 10).Books, Is.Empty, "Every word must match the same edition");
                added[0].Title = "Revised Catalog";
                added[1].Title = "Second Catalog";
                editions.UpdateMany(added);
                Assert.That(service.Search("22", 10).Books, Is.Empty);
                Assert.That(service.Search("strasse", 10).Books, Is.Empty);
                Assert.That(service.Search("revised", 10).Books.Single().Id, Is.EqualTo(book.Id));
                editions.DeleteMany(added.Append(edition).ToList());
                Assert.That(service.Search("catalog", 10).Books, Is.Empty);
                Assert.That(service.Search("opp", 10).Books, Is.Empty);
                if (!postgres)
                {
                    using var connection = database.OpenConnection();
                    CheckIntegrity(connection);
                }
            });
        }

        [Test]
        public void existing_rebuild_restores_header_index_and_its_triggers()
        {
            DualDatabaseFixture.Run(false, database =>
            {
                var (editions, book, edition) = Seed(database);
                using (var connection = database.OpenConnection())
                {
                    connection.Execute("DROP TABLE edition_header_fts");
                }

                var repository = new EditionFtsRepository(database, Logger);
                repository.RebuildIndex();
                repository.RebuildIndex();
                var service = new LibrarySearchService(database, Logger);
                Assert.That(service.Search("spid man", 10).Books.Single().Id, Is.EqualTo(book.Id));
                edition.Title = "Restored-Title";
                editions.UpdateMany(new List<Edition> { edition });
                Assert.That(service.Search("spider", 10).Books, Is.Empty);
                Assert.That(service.Search("title restored", 10).Books.Single().Id, Is.EqualTo(book.Id));
                using var verify = database.OpenConnection();
                CheckIntegrity(verify);
                Assert.That(verify.QuerySingle<int>("SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name LIKE 'edition_header_fts_%'"), Is.EqualTo(3));
            });
        }

        [TestCase("missing")]
        [TestCase("empty")]
        [TestCase("extra")]
        public void startup_repairs_actual_header_index_rows(string damage)
        {
            DualDatabaseFixture.Run(false, database =>
            {
                var (editions, book, edition) = Seed(database);
                using var connection = database.OpenConnection();
                if (damage == "missing") connection.Execute("DROP TABLE edition_header_fts");
                else if (damage == "empty")
                {
                    connection.Execute("INSERT INTO edition_header_fts(edition_header_fts) VALUES ('delete-all')");
                    Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM edition_header_fts"), Is.EqualTo(1), "External content hides the missing index rows");
                }
                else connection.Execute("INSERT INTO edition_header_fts(rowid, MatchingTitle) VALUES (999999, 'phantom')");

                var repository = new EditionFtsRepository(database, Logger);
                repository.EnsureIndexPopulated();
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM edition_header_fts_docsize"), Is.EqualTo(1));
                var service = new LibrarySearchService(database, Logger);
                Assert.That(service.Search("spid man", 10).Books.Single().Id, Is.EqualTo(book.Id));
                edition.Title = "Repaired-Title";
                editions.UpdateMany(new List<Edition> { edition });
                Assert.That(service.Search("repaired title", 10).Books.Single().Id, Is.EqualTo(book.Id));
                CheckIntegrity(connection);
                var schemaVersion = connection.QuerySingle<int>("PRAGMA schema_version");
                repository.EnsureIndexPopulated();
                Assert.That(connection.QuerySingle<int>("PRAGMA schema_version"), Is.EqualTo(schemaVersion), "Healthy startup must not rebuild");
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void dropdown_uses_book_page_title_and_exposes_the_matching_edition(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var (editions, book, edition) = Seed(database);
                edition.Title = "Pocket Potters: Harry (Pocket Potters, #1)";
                edition.Monitored = true;
                editions.UpdateMany(new List<Edition> { edition });
                var alternate = new Edition { BookId = book.Id, Title = "Pocket Potters: Hermione" };
                editions.InsertMany(new List<Edition> { alternate });
                var events = InterfaceProxy.Proxy<IEventAggregator>((_, _) => null);
                var series = new BasicRepository<Series>(database, events).Insert(new Series { Title = "Pocket Potters", GoodreadsSeriesId = "123", MediaType = book.MediaType });
                var link = new BasicRepository<SeriesBookLink>(database, events).Insert(new SeriesBookLink { BookId = book.Id, SeriesId = series.Id, Position = "1" });
                link.Series = series;
                book.SeriesLinks = new List<SeriesBookLink> { link };
                book.Editions = new List<Edition> { edition, alternate };
                var expected = Chaptarr.Api.V1.Books.BookResourceMapper.ToResource(book).Title;
                Assert.That(expected, Is.EqualTo("Pocket Potters: Harry"));
                var service = new LibrarySearchService(database, Logger);
                var result = service.Search("Pocket Potters Hermione", 10).Books.Single();
                Assert.That(result.Title, Is.EqualTo(expected));
                Assert.That(result.LocalEbookBooks.Single().Title, Is.EqualTo(expected));
                Assert.That(result.MatchedEditionTitle, Is.EqualTo(alternate.Title));
                Assert.That(service.Search("Pocket Potters Harry", 10).Books.Single().MatchedEditionTitle, Is.Null);
            });
        }

        [Test]
        public void author_index_upgrade_and_maintenance_reuse_the_folded_key()
        {
            DualDatabaseFixture.Run(false, database =>
            {
                var events = InterfaceProxy.Proxy<IEventAggregator>((_, _) => null);
                using var connection = database.OpenConnection();
                // Seed the historical schema, not today's model (which includes later columns).
                var author = new Author { Id = InsertHistoricalAuthor(connection, "J.K. Rowling", "jkrowling") };
                Assert.That(connection.Query<int>("SELECT rowid FROM author_fts WHERE author_fts MATCH 'jk AND rowling'"), Is.Empty);
                new MigrationController(Logger, null).Migrate(connection.ConnectionString, new MigrationContext(MigrationType.Main, 120), DatabaseType.SQLite);
                var service = new LibrarySearchService(database, Logger);
                Assert.That(service.Search("jk rowling", 10).Authors.Single().Id, Is.EqualTo(author.Id));
                connection.Execute("DROP TABLE author_fts;");
                var authorService = InterfaceProxy.Proxy<IAuthorService>((method, _) => throw new InvalidOperationException("Rebuild must not load authors individually: " + method.Name));
                new FtsMaintenanceService(database, authorService, Logger).RebuildAllFts();
                Assert.That(service.Search("jk rowling", 10).Authors.Single().Id, Is.EqualTo(author.Id));
            }, desiredVersion: 119);
        }

        [Test]
        public void author_rebuild_failure_logs_an_error_and_does_not_skip_book_header_repair()
        {
            DualDatabaseFixture.Run(false, database =>
            {
                var (_, book, _) = Seed(database);
                using var connection = database.OpenConnection();
                connection.Execute("DROP TABLE author_fts; CREATE VIEW author_fts AS SELECT Id AS rowid FROM Authors; DROP TABLE edition_header_fts;");
                var previous = LogManager.Configuration;
                var errors = new MemoryTarget { Layout = "${level}|${message}|${exception:message}" };
                var config = new LoggingConfiguration();
                config.AddRule(LogLevel.Error, LogLevel.Fatal, errors);
                try
                {
                    LogManager.Configuration = config;
                    new UpdateFtsCommandHandler(new FtsMaintenanceService(database, null, Logger),
                        new EditionFtsRepository(database, Logger), Logger).Execute(new UpdateFtsCommand());
                    Assert.That(errors.Logs, Has.Some.Contains("Failed to rebuild the author search index"));
                    Assert.That(new LibrarySearchService(database, Logger).Search("spid man", 10).Books.Single().Id, Is.EqualTo(book.Id));
                    Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM edition_header_fts_docsize"), Is.EqualTo(1));
                }
                finally { LogManager.Configuration = previous; }
            });
        }

        private static (EditionService Editions, Book Book, Edition Edition) Seed(IMainDatabase database, bool historicalSchema = false)
        {
            var events = InterfaceProxy.Proxy<IEventAggregator>((_, _) => null);
            if (historicalSchema)
            {
                using var connection = database.OpenConnection();
                var authorId = InsertHistoricalAuthor(connection, "Library Writer", "librarywriter");
                var bookId = connection.ExecuteScalar<int>(@"INSERT INTO ""Books"" (""AuthorId"", ""Title"", ""CleanTitle"", ""MediaType"", ""Added"", ""AnyEditionOk"")
                    VALUES (@authorId, 'Edition Display', 'editiondisplay', @mediaType, @now, @anyEditionOk) RETURNING ""Id"";",
                    new { authorId, mediaType = (int)BookMediaType.Ebook, now = DateTime.UtcNow, anyEditionOk = true });
                var editionId = connection.ExecuteScalar<int>(@"INSERT INTO ""Editions"" (""BookId"", ""Title"", ""MatchingTitle"", ""Monitored"")
                    VALUES (@bookId, 'Spider-Man', @matchingTitle, @monitored) RETURNING ""Id"";", new { bookId, matchingTitle = NzbDrone.Core.Books.Extensions.StringSuperNormalizer.ComputeMatchingTitle("Spider-Man"), monitored = false });
                return (new EditionService(new EditionRepository(database, events), events, Logger),
                    new Book { Id = bookId, AuthorId = authorId, Title = "Edition Display", MediaType = BookMediaType.Ebook },
                    new Edition { Id = editionId, BookId = bookId, Title = "Spider-Man" });
            }
            var author = new AuthorRepository(database, events, Logger).Insert(new Author
            {
                Name = "Library Writer", CleanName = "librarywriter", Path = "/library-search", Added = DateTime.UtcNow
            });
            var book = new BasicRepository<Book>(database, events).Insert(new Book
            {
                AuthorId = author.Id, Title = "Edition Display", CleanTitle = "editiondisplay", MediaType = BookMediaType.Ebook, Added = DateTime.UtcNow
            });
            var editions = new EditionService(new EditionRepository(database, events), events, Logger);
            var edition = new Edition { BookId = book.Id, Title = "Spider-Man" };
            editions.InsertMany(new List<Edition> { edition });
            return (editions, book, edition);
        }

        private static int InsertHistoricalAuthor(IDbConnection connection, string name, string cleanName) =>
            connection.ExecuteScalar<int>(@"INSERT INTO ""Authors"" (""Name"", ""CleanName"", ""SearchName"", ""Path"", ""Monitored"", ""Added"")
                VALUES (@name, @cleanName, @searchName, '/library-search', @monitored, @now) RETURNING ""Id"";",
                new { name, cleanName, searchName = NzbDrone.Core.Books.Extensions.StringSuperNormalizer.NormalizeForFts(name), monitored = false, now = DateTime.UtcNow });

        private static void CheckIntegrity(IDbConnection connection)
        {
            connection.Execute("INSERT INTO edition_header_fts(edition_header_fts, rank) VALUES ('integrity-check', 1)");
        }
    }
}
