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

        private static void SeedDatabase(string connectionString)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            connection.Execute(@"
                CREATE TABLE Authors (
                    Id INTEGER PRIMARY KEY,
                    Name TEXT NOT NULL
                );
                CREATE TABLE Books (
                    Id INTEGER PRIMARY KEY,
                    AuthorId INTEGER NOT NULL,
                    Title TEXT NOT NULL,
                    SeriesName TEXT,
                    SeriesPosition TEXT,
                    MediaType INTEGER NOT NULL
                );
                CREATE TABLE Editions (
                    Id INTEGER PRIMARY KEY,
                    ForeignEditionId TEXT,
                    BookId INTEGER NOT NULL,
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

                INSERT INTO Authors (Id, Name) VALUES (1, 'Freida McFadden');
                INSERT INTO Books (Id, AuthorId, Title, MediaType) VALUES
                    (1, 1, 'The Boyfriend', 1),
                    (2, 1, 'The Wife Upstairs', 1);
                INSERT INTO Editions (
                    Id, ForeignEditionId, BookId, Language, Title, MatchingTitle,
                    NarratorNames, Narrator, Publisher, Images, DurationSeconds,
                    ReleaseDate, ReadingFormatId)
                VALUES
                    (11, 'hc:edition:boyfriend', 1, 'eng', 'The Boyfriend', 'the boyfriend', '[]', '', ', and', '[]', 0, '2024-10-01', 3),
                    (22, 'hc:edition:wife', 2, 'eng', 'The Wife Upstairs', 'the wife upstairs', '[]', '', 'Hollywood Upstairs Press', '[]', 0, '2020-03-23', 3);
                INSERT INTO edition_fts (rowid, MatchingTitle, SeriesName, AuthorName, Narrator, Subtitle, Publisher) VALUES
                    (11, 'the boyfriend', '', 'Freida McFadden', '', '', ', and'),
                    (22, 'the wife upstairs', '', 'Freida McFadden', '', '', 'Hollywood Upstairs Press');
            ");
        }
    }
}
