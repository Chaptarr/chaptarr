using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Chaptarr.Core.Test.Common;
using Dapper;
using NLog;
using Npgsql;
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
                var (editions, book, edition) = Seed(database);
                using var connection = database.OpenConnection();
                string[] IndexDefinitions() => connection.Query<string>(postgres
                    ? "SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' ORDER BY indexname"
                    : "SELECT sql FROM sqlite_master WHERE name IN ('edition_fts', 'author_fts') ORDER BY name").ToArray();
                var before = IndexDefinitions();
                var original = editions.GetEdition(edition.Id);

                var connectionString = postgres
                    ? new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CHAPTARR_TEST_POSTGRES_CONNECTION_STRING"))
                        { Database = connection.Database, Pooling = false }.ConnectionString
                    : connection.ConnectionString;
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

        private static (EditionService Editions, Book Book, Edition Edition) Seed(IMainDatabase database)
        {
            var events = InterfaceProxy.Proxy<IEventAggregator>((_, _) => null);
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

        private static void CheckIntegrity(IDbConnection connection)
        {
            connection.Execute("INSERT INTO edition_header_fts(edition_header_fts, rank) VALUES ('integrity-check', 1)");
        }
    }
}
