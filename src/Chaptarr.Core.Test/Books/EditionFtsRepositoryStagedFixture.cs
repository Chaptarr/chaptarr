using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class EditionFtsRepositoryStagedFixture
    {
        [OneTimeSetUp]
        public void SetUpFixture()
        {
            if (TableMapping.Mapper.TableMap.Count == 0)
            {
                TableMapping.Map();
            }
        }

        [Test]
        public void stage2_should_report_per_field_hits_but_keep_bm25_out_of_the_book_decision()
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"staged_fts_{Guid.NewGuid():N}.db");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,

                // Windows holds pooled handles open past connection disposal, which blocks the temp-db delete below.
                Pooling = false
            }.ToString();

            try
            {
                SeedDatabase(connectionString);
                var database = new Database("main", () =>
                {
                    var connection = new SqliteConnection(connectionString);
                    connection.Open();
                    return connection;
                });
                var repository = new EditionFtsRepository(
                    new MainDatabase(database),
                    LogManager.GetCurrentClassLogger());
                var recalls = new[]
                {
                    new BookFtsMatch { BookId = 1, AuthorId = 1, AuthorName = "Freida McFadden", BookTitle = "The Boyfriend", MatchScore = 10 },
                    new BookFtsMatch { BookId = 2, AuthorId = 1, AuthorName = "Freida McFadden", BookTitle = "The Wife Upstairs", MatchScore = 9 }
                };
                var fields = new[]
                {
                    new EditionFtsFieldQuery
                    {
                        Key = "TITLE[0]",
                        ResidualValue = "The Boyfriend",
                        Terms = new[] { "the", "boyfriend" },
                        SourceFields = new[] { "TITLE[0]" }
                    },
                    new EditionFtsFieldQuery
                    {
                        Key = "PUBLISHER[0]",
                        ResidualValue = "Hollywood Upstairs Press",
                        Terms = new[] { "hollywood", "upstairs", "press" },
                        SourceFields = new[] { "PUBLISHER[0]" }
                    }
                };

                var ranked = repository.RankEditions(recalls, fields, BookMediaType.Ebook);

                Assert.That(ranked, Has.Count.EqualTo(2));
                Assert.That(ranked[0].BookId, Is.EqualTo(1), "the clean title field must beat a publisher field that happens to contain another Book title token");
                Assert.That(ranked[0].Stage2TitleSourceFields, Is.EqualTo("TITLE[0]"));
                Assert.That(ranked[1].Stage2DetailScore, Is.GreaterThan(ranked[0].Stage2DetailScore), "publisher may discriminate Editions only after Book/title ranking");
                Assert.That(ranked[0].MatchScore, Is.EqualTo(ranked[0].BroadRecallScore), "BM25 is recall/diagnostic data, not a Stage 2 decision sum");
                Assert.That(ranked[1].MatchScore, Is.EqualTo(ranked[1].BroadRecallScore));
                Assert.That(ranked[0].Stage2FieldHits.Single(hit => hit.FieldKey == "TITLE[0]").TitleHit, Is.True);
                var noisyField = ranked[1].Stage2FieldHits.Single(hit => hit.FieldKey == "PUBLISHER[0]");
                Assert.That(noisyField.TitleHit, Is.True, "the DB may broadly recall a title token from this field");
                Assert.That(noisyField.DetailHit, Is.True, "the same DB hit is preserved so the matcher can enforce one-field/one-use");
            }
            finally
            {
                if (File.Exists(databasePath))
                {
                    File.Delete(databasePath);
                }
            }
        }

        [Test]
        public void recall_should_apply_the_shared_book_and_author_monitoring_contract_before_limit()
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"staged_fts_monitoring_{Guid.NewGuid():N}.db");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();

            try
            {
                SeedDatabase(connectionString);
                var database = new Database("main", () =>
                {
                    var connection = new SqliteConnection(connectionString);
                    connection.Open();
                    return connection;
                });
                var repository = new EditionFtsRepository(
                    new MainDatabase(database),
                    LogManager.GetCurrentClassLogger());

                var unfilteredTopResult = repository.RecallBooks(
                    null,
                    new[] { "freida" },
                    BookMediaType.Ebook,
                    limit: 1);

                Assert.That(unfilteredTopResult.Select(candidate => candidate.BookId), Is.EqualTo(new[] { 3 }));

                var monitoredTopResult = repository.RecallBooks(
                    null,
                    new[] { "freida" },
                    BookMediaType.Ebook,
                    limit: 1,
                    monitoredOnly: true);

                Assert.Multiple(() =>
                {
                    Assert.That(monitoredTopResult, Has.Count.EqualTo(1));
                    Assert.That(monitoredTopResult[0].BookId, Is.Not.EqualTo(3),
                        "monitoring must be applied before the recall limit excludes the higher-ranked unmonitored row");
                });

                var monitored = repository.RecallBooks(
                    null,
                    new[] { "freida" },
                    BookMediaType.Ebook,
                    limit: 20,
                    monitoredOnly: true);

                Assert.That(monitored.Select(candidate => candidate.BookId), Is.EquivalentTo(new[] { 1, 2 }));

                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    connection.Execute("UPDATE Books SET EbookMonitored = 0 WHERE Id = 2");
                }

                monitored = repository.RecallBooks(
                    null,
                    new[] { "freida" },
                    BookMediaType.Ebook,
                    limit: 20,
                    monitoredOnly: true);

                Assert.That(monitored.Select(candidate => candidate.BookId), Is.EqualTo(new[] { 1 }));

                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    connection.Execute("UPDATE Authors SET EbookMonitorExisting = 0, EbookMonitorFuture = 0 WHERE Id = 1");
                }

                monitored = repository.RecallBooks(
                    null,
                    new[] { "freida" },
                    BookMediaType.Ebook,
                    limit: 20,
                    monitoredOnly: true);

                Assert.That(monitored, Is.Empty);

                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    connection.Execute("UPDATE Authors SET EbookMonitorExisting = 1 WHERE Id = 1");
                    connection.Execute(@"
                        INSERT INTO Editions (Id, ForeignEditionId, BookId, Monitored, Language, Title, MatchingTitle, NarratorNames, Narrator, Publisher, Images, DurationSeconds, ReleaseDate, ReadingFormatId)
                        VALUES (12, 'hc:edition:poison', 1, 0, 'eng', 'Dune Messiah', 'dune messiah', '[]', '', '', '[]', 0, '2026-01-01', 3);
                        INSERT INTO edition_fts (rowid, MatchingTitle, SeriesName, AuthorName, Narrator, Subtitle, Publisher)
                        VALUES (12, 'dune messiah', '', 'Freida McFadden', '', '', '');");
                }

                var unfilteredPoisonedSibling = repository.RecallBooks(
                    null,
                    new[] { "messiah" },
                    BookMediaType.Ebook,
                    limit: 20);

                Assert.That(unfilteredPoisonedSibling.Select(candidate => candidate.BookId), Does.Contain(1));

                var poisonedSibling = repository.RecallBooks(
                    null,
                    new[] { "messiah" },
                    BookMediaType.Ebook,
                    limit: 20,
                    monitoredOnly: true);

                Assert.That(poisonedSibling, Is.Empty,
                    "an unmonitored sibling edition title must not establish RSS identity for its monitored book");
            }
            finally
            {
                if (File.Exists(databasePath))
                {
                    File.Delete(databasePath);
                }
            }
        }

        private static void SeedDatabase(string connectionString)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            connection.Execute(@"
                CREATE TABLE Authors (
                    Id INTEGER PRIMARY KEY,
                    Name TEXT NOT NULL,
                    AudiobookMonitorExisting INTEGER,
                    AudiobookMonitorFuture INTEGER,
                    EbookMonitorExisting INTEGER,
                    EbookMonitorFuture INTEGER
                );
                CREATE TABLE Books (
                    Id INTEGER PRIMARY KEY,
                    AuthorId INTEGER NOT NULL,
                    Title TEXT NOT NULL,
                    SeriesName TEXT,
                    SeriesPosition TEXT,
                    MediaType INTEGER NOT NULL,
                    AudiobookMonitored INTEGER NOT NULL DEFAULT 0,
                    EbookMonitored INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE Editions (
                    Id INTEGER PRIMARY KEY,
                    ForeignEditionId TEXT,
                    BookId INTEGER NOT NULL,
                    Monitored INTEGER NOT NULL DEFAULT 0,
                    Language TEXT,
                    Title TEXT,
                    Subtitle TEXT,
                    MatchingTitle TEXT,
                    NarratorNames TEXT,
                    Narrator TEXT,
                    Publisher TEXT,
                    Images TEXT,
                    DurationSeconds INTEGER,
                    ReleaseDate TEXT,
                    ReadingFormatId INTEGER
                );
                CREATE VIRTUAL TABLE edition_fts USING fts5(
                    MatchingTitle,
                    SeriesName,
                    AuthorName,
                    Narrator,
                    Subtitle,
                    Publisher
                );

                INSERT INTO Authors (Id, Name, AudiobookMonitorExisting, AudiobookMonitorFuture, EbookMonitorExisting, EbookMonitorFuture)
                VALUES (1, 'Freida McFadden', 0, 0, 1, 0);
                INSERT INTO Books (Id, AuthorId, Title, MediaType, AudiobookMonitored, EbookMonitored) VALUES
                    (1, 1, 'The Boyfriend', 1, 0, 1),
                    (2, 1, 'The Wife Upstairs', 1, 0, 1),
                    (3, 1, 'Unmonitored Recall Decoy', 1, 0, 0);
                INSERT INTO Editions (
                    Id, ForeignEditionId, BookId, Monitored, Language, Title, MatchingTitle,
                    NarratorNames, Narrator, Publisher, Images, DurationSeconds,
                    ReleaseDate, ReadingFormatId)
                VALUES
                    (11, 'hc:edition:boyfriend', 1, 1, 'eng', 'The Boyfriend', 'the boyfriend', '[]', '', ', and', '[]', 0, '2024-10-01', 3),
                    (22, 'hc:edition:wife', 2, 1, 'eng', 'The Wife Upstairs', 'the wife upstairs', '[]', '', 'Hollywood Upstairs Press', '[]', 0, '2020-03-23', 3),
                    (33, 'hc:edition:decoy', 3, 1, 'eng', 'Freida Freida Freida Freida', 'freida freida freida freida', '[]', '', '', '[]', 0, '2026-01-01', 3);
                INSERT INTO edition_fts (rowid, MatchingTitle, SeriesName, AuthorName, Narrator, Subtitle, Publisher) VALUES
                    (11, 'the boyfriend', '', 'Freida McFadden', '', '', ', and'),
                    (22, 'the wife upstairs', '', 'Freida McFadden', '', '', 'Hollywood Upstairs Press'),
                    (33, 'freida freida freida freida', '', 'Freida McFadden', '', '', '');
            ");
        }
    }
}
