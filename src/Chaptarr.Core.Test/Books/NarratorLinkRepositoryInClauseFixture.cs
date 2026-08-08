using System;
using System.IO;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books.Repositories;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class NarratorLinkRepositoryInClauseFixture
    {
        private sealed class StubEventAggregator : IEventAggregator
        {
            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
            }
        }

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            if (TableMapping.Mapper.TableMap.Count == 0)
            {
                TableMapping.Map();
            }
        }

        [Test]
        public void should_delete_book_narrator_links_by_book_ids()
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"book_narrator_link_in_{Guid.NewGuid():N}.db");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString();

            try
            {
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    connection.Execute("PRAGMA journal_mode = MEMORY;");
                    connection.Execute("PRAGMA synchronous = OFF;");

                    connection.Execute(@"
                        CREATE TABLE ""BookNarratorLink"" (
                            ""Id"" INTEGER PRIMARY KEY,
                            ""BookId"" INTEGER NOT NULL,
                            ""NarratorId"" INTEGER NOT NULL,
                            ""IsPrimary"" INTEGER NOT NULL DEFAULT 0,
                            ""Role"" TEXT NOT NULL DEFAULT 'Narrator'
                        );
                    ");

                    connection.Execute(@"
                        INSERT INTO ""BookNarratorLink"" (""BookId"", ""NarratorId"", ""IsPrimary"", ""Role"")
                        VALUES
                            (1, 10, 1, 'Narrator'),
                            (2, 11, 1, 'Narrator'),
                            (3, 12, 1, 'Narrator');
                    ");
                }

                var database = new Database("main", () =>
                {
                    var conn = new SqliteConnection(connectionString);
                    conn.Open();
                    return conn;
                });

                var mainDatabase = new MainDatabase(database);
                var sut = new BookNarratorLinkRepository(mainDatabase, new StubEventAggregator());

                Assert.DoesNotThrow(() => sut.DeleteByBookIds(new[] { 1, 3 }.ToList()));

                using (var verify = new SqliteConnection(connectionString))
                {
                    verify.Open();
                    var remaining = verify.QuerySingle<int>(@"SELECT COUNT(*) FROM ""BookNarratorLink"";");
                    Assert.That(remaining, Is.EqualTo(1));

                    var remainingBookIds = verify.Query<int>(@"SELECT ""BookId"" FROM ""BookNarratorLink"";").ToList();
                    Assert.That(remainingBookIds, Is.EquivalentTo(new[] { 2 }));
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(databasePath))
                    {
                        File.Delete(databasePath);
                    }
                }
                catch
                {
                }
            }
        }

        [Test]
        public void should_delete_edition_narrator_links_by_edition_ids()
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"edition_narrator_link_in_{Guid.NewGuid():N}.db");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString();

            try
            {
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    connection.Execute("PRAGMA journal_mode = MEMORY;");
                    connection.Execute("PRAGMA synchronous = OFF;");

                    connection.Execute(@"
                        CREATE TABLE ""EditionNarratorLink"" (
                            ""Id"" INTEGER PRIMARY KEY,
                            ""EditionId"" INTEGER NOT NULL,
                            ""NarratorId"" INTEGER NOT NULL,
                            ""IsPrimary"" INTEGER NOT NULL DEFAULT 0,
                            ""Role"" TEXT NOT NULL DEFAULT 'Narrator'
                        );
                    ");

                    connection.Execute(@"
                        INSERT INTO ""EditionNarratorLink"" (""EditionId"", ""NarratorId"", ""IsPrimary"", ""Role"")
                        VALUES
                            (10, 100, 1, 'Narrator'),
                            (11, 101, 1, 'Narrator'),
                            (12, 102, 1, 'Narrator');
                    ");
                }

                var database = new Database("main", () =>
                {
                    var conn = new SqliteConnection(connectionString);
                    conn.Open();
                    return conn;
                });

                var mainDatabase = new MainDatabase(database);
                var sut = new EditionNarratorLinkRepository(mainDatabase, new StubEventAggregator());

                Assert.DoesNotThrow(() => sut.DeleteByEditionIds(new[] { 10, 12 }.ToList()));

                using (var verify = new SqliteConnection(connectionString))
                {
                    verify.Open();
                    var remaining = verify.QuerySingle<int>(@"SELECT COUNT(*) FROM ""EditionNarratorLink"";");
                    Assert.That(remaining, Is.EqualTo(1));

                    var remainingEditionIds = verify.Query<int>(@"SELECT ""EditionId"" FROM ""EditionNarratorLink"";").ToList();
                    Assert.That(remainingEditionIds, Is.EquivalentTo(new[] { 11 }));
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(databasePath))
                    {
                        File.Delete(databasePath);
                    }
                }
                catch
                {
                }
            }
        }

        [Test]
        public void should_get_edition_narrator_links_with_monitored_by_book_ids()
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"edition_narrator_link_query_{Guid.NewGuid():N}.db");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString();

            try
            {
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    connection.Execute("PRAGMA journal_mode = MEMORY;");
                    connection.Execute("PRAGMA synchronous = OFF;");

                    connection.Execute(@"
                        CREATE TABLE ""Editions"" (
                            ""Id"" INTEGER PRIMARY KEY,
                            ""BookId"" INTEGER NOT NULL,
                            ""Monitored"" INTEGER NOT NULL DEFAULT 0
                        );

                        CREATE TABLE ""EditionNarratorLink"" (
                            ""Id"" INTEGER PRIMARY KEY,
                            ""EditionId"" INTEGER NOT NULL,
                            ""NarratorId"" INTEGER NOT NULL,
                            ""IsPrimary"" INTEGER NOT NULL DEFAULT 0,
                            ""Role"" TEXT NOT NULL DEFAULT 'Narrator'
                        );
                    ");

                    connection.Execute(@"
                        INSERT INTO ""Editions"" (""Id"", ""BookId"", ""Monitored"") VALUES
                            (10, 1, 1),
                            (11, 1, 0),
                            (12, 2, 1);

                        INSERT INTO ""EditionNarratorLink"" (""EditionId"", ""NarratorId"", ""IsPrimary"", ""Role"") VALUES
                            (10, 100, 1, 'Narrator'),
                            (11, 101, 0, 'Narrator'),
                            (12, 102, 1, 'Narrator');
                    ");
                }

                var database = new Database("main", () =>
                {
                    var conn = new SqliteConnection(connectionString);
                    conn.Open();
                    return conn;
                });

                var mainDatabase = new MainDatabase(database);
                var sut = new EditionNarratorLinkRepository(mainDatabase, new StubEventAggregator());

                var results = sut.GetByBookIds(new[] { 1 }.ToList());

                Assert.That(results, Has.Count.EqualTo(2));
                Assert.That(results.Select(r => r.BookId), Is.All.EqualTo(1));

                var byNarrator = results.ToDictionary(r => r.NarratorId, r => r);
                Assert.That(byNarrator[100].IsPrimary, Is.True);
                Assert.That(byNarrator[100].Monitored, Is.True);
                Assert.That(byNarrator[101].IsPrimary, Is.False);
                Assert.That(byNarrator[101].Monitored, Is.False);
            }
            finally
            {
                try
                {
                    if (File.Exists(databasePath))
                    {
                        File.Delete(databasePath);
                    }
                }
                catch
                {
                }
            }
        }
    }
}

