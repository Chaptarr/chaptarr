using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using NLog;
using Npgsql;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Extensions;
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
                    connection.Execute("UPDATE Books SET EbookMonitored = false WHERE Id = 2");
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
                    connection.Execute("UPDATE Authors SET EbookMonitored = false, EbookMonitorNewItems = 0 WHERE Id = 1");
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
                    connection.Execute("UPDATE Authors SET EbookMonitored = true WHERE Id = 1");
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

        [TestCase("Astorias𠀀Smut")]
        [TestCase("𠀀")]
        public void recall_should_preserve_supplementary_letters_through_the_sqlite_query(string title)
        {
            var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"unicode_fts_{Guid.NewGuid():N}.db");
            var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
            try
            {
                SeedDatabase(connectionString);
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    connection.Execute("UPDATE Books SET Title = @title WHERE Id = 1; UPDATE Editions SET Title = @title, MatchingTitle = @title WHERE Id = 11; UPDATE edition_fts SET MatchingTitle = @title WHERE rowid = 11;", new { title });
                }

                var database = new Database("main", () =>
                {
                    var connection = new SqliteConnection(connectionString);
                    connection.Open();
                    return connection;
                });
                var repository = new EditionFtsRepository(new MainDatabase(database), LogManager.GetCurrentClassLogger());
                var tokens = NzbDrone.Core.MediaFiles.BookImport.Services.BookImportUnitGroupingService.TokenizeText(title);
                var results = repository.RecallBooks(null, tokens, BookMediaType.Ebook, limit: 20, monitoredOnly: true);
                Assert.That(results.Select(b => b.BookId), Does.Contain(1));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestCase("Astorias𠀀Smut", "astorias𠀀smut")]
        [TestCase("𠀀", "𠀀")]
        [TestCase("𠀀 | ! ' book:* 🌶️", "𠀀 | book")]
        [TestCase("Two", "two")]
        [TestCase("②", "2")]
        [TestCase("ⅩⅣ", "xiv")]
        [TestCase("1Q84", "1q84")]
        [TestCase("½", "0.5")]
        [TestCase("0.5", "0.5")]
        public void postgres_query_builder_should_preserve_letters_without_admitting_query_syntax(string input, string expected)
        {
            var method = typeof(EditionFtsRepository).GetMethod("BuildPostgresTsQuery", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.That(method.Invoke(null, new object[] { new[] { input } }), Is.EqualTo(expected));
        }

        [Test]
        public void recall_rank_and_legacy_search_should_fold_numeric_titles_without_merging_ready_player_siblings()
        {
            var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"numeric_fts_{Guid.NewGuid():N}.db");
            var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
            try
            {
                SeedDatabase(connectionString);
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    void SetTitle(int bookId, int editionId, string title)
                    {
                        var matchingTitle = StringSuperNormalizer.ComputeMatchingTitle(title);
                        connection.Execute(
                            "UPDATE Books SET Title = @title WHERE Id = @bookId; " +
                            "UPDATE Editions SET Title = @title, MatchingTitle = @matchingTitle WHERE Id = @editionId; " +
                            "UPDATE edition_fts SET MatchingTitle = @matchingTitle WHERE rowid = @editionId;",
                            new { bookId, editionId, title, matchingTitle });
                    }

                    SetTitle(1, 11, "Ready Player 1");
                    SetTitle(2, 22, "Ready Player 2");
                    SetTitle(3, 33, "1Q84");
                }

                var database = new Database("main", () =>
                {
                    var connection = new SqliteConnection(connectionString);
                    connection.Open();
                    return connection;
                });
                var repository = new EditionFtsRepository(new MainDatabase(database), LogManager.GetCurrentClassLogger());

                void AssertReadyPlayerQuery(string numericGlyph, int expectedBookId, int expectedEditionId)
                {
                    var terms = new[] { "Ready", "Player", numericGlyph };
                    var recalls = repository.RecallBooks(null, terms, BookMediaType.Ebook, limit: 20);
                    Assert.That(recalls, Is.Not.Empty);
                    Assert.That(recalls[0].BookId, Is.EqualTo(expectedBookId), "numeric sibling must rank by the folded number");

                    var ranked = repository.RankEditions(recalls, new[]
                    {
                        new EditionFtsFieldQuery
                        {
                            Key = "TITLE[0]",
                            ResidualValue = string.Join(" ", terms),
                            Terms = terms,
                            SourceFields = new[] { "TITLE[0]" }
                        }
                    }, BookMediaType.Ebook);
                    Assert.That(ranked, Is.Not.Empty);
                    Assert.That(ranked[0].BookId, Is.EqualTo(expectedBookId));
                    Assert.That(ranked[0].EditionId, Is.EqualTo(expectedEditionId));

                    var legacy = repository.SearchWithTwoStep(null, terms, BookMediaType.Ebook, limit: 20);
                    Assert.That(legacy, Is.Not.Empty);
                    Assert.That(legacy[0].BookId, Is.EqualTo(expectedBookId));
                    Assert.That(legacy[0].EditionId, Is.EqualTo(expectedEditionId));
                }

                AssertReadyPlayerQuery("①", expectedBookId: 1, expectedEditionId: 11);
                AssertReadyPlayerQuery("𝟚", expectedBookId: 2, expectedEditionId: 22);

                var oneQ84Terms = new[] { "１Q84" };
                var oneQ84Recalls = repository.RecallBooks(null, oneQ84Terms, BookMediaType.Ebook, limit: 20);
                Assert.That(oneQ84Recalls.Select(book => book.BookId), Is.EqualTo(new[] { 3 }));
                var oneQ84Ranked = repository.RankEditions(oneQ84Recalls, new[]
                {
                    new EditionFtsFieldQuery
                    {
                        Key = "TITLE[0]",
                        ResidualValue = "１Q84",
                        Terms = oneQ84Terms,
                        SourceFields = new[] { "TITLE[0]" }
                    }
                }, BookMediaType.Ebook);
                Assert.That(oneQ84Ranked.Select(edition => edition.EditionId), Is.EqualTo(new[] { 33 }));
                var oneQ84Legacy = repository.SearchWithTwoStep(null, oneQ84Terms, BookMediaType.Ebook, limit: 20);
                Assert.That(oneQ84Legacy.Select(edition => edition.EditionId), Is.EqualTo(new[] { 33 }));
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }


        [Test]
        public void sqlite_staged_recall_should_use_language_scoped_numeric_aliases_on_titles_only()
        {
            var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"numeric_alias_fts_{Guid.NewGuid():N}.db");
            var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
            try
            {
                SeedDatabase(connectionString);
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    connection.Execute(@"
                        INSERT INTO Authors (Id, Name) VALUES (2, 'Two');
                        INSERT INTO Books (Id, AuthorId, Title, MediaType) VALUES
                            (4, 1, 'Two English', 1),
                            (5, 1, 'Two Null', 1),
                            (6, 1, 'Two Unsupported', 1),
                            (7, 1, 'Roman Two', 1),
                            (8, 1, 'Twenty One', 1),
                            (9, 2, 'Author Alias Decoy', 1),
                            (10, 1, 'Digit English', 1),
                            (11, 1, 'Audio Narrator Alias Decoy', 0),
                            (12, 1, 'Two English Short', 1),
                            (13, 1, 'Two English Name', 1),
                            (14, 1, 'Two English Region Hyphen', 1),
                            (15, 1, 'Two English Region Underscore', 1);
                        INSERT INTO Editions (
                            Id, ForeignEditionId, BookId, Monitored, Language, Title, MatchingTitle,
                            NarratorNames, Narrator, Publisher, Images, DurationSeconds, ReleaseDate, ReadingFormatId)
                        VALUES
                            (44, 'hc:edition:numeric-alias-english', 4, 1, 'eng', 'Two English', 'two', '[]', '', '', '[]', 0, '2026-01-01', 3),
                            (55, 'hc:edition:numeric-alias-null', 5, 1, NULL, 'Two Null', 'two', '[]', '', '', '[]', 0, '2026-01-01', 3),
                            (66, 'hc:edition:numeric-alias-unsupported', 6, 1, 'fra', 'Two Unsupported', 'two', '[]', '', '', '[]', 0, '2026-01-01', 3),
                            (77, 'hc:edition:numeric-alias-roman', 7, 1, NULL, 'Roman Two', 'ii', '[]', '', '', '[]', 0, '2026-01-01', 3),
                            (88, 'hc:edition:numeric-alias-twenty-one', 8, 1, 'eng', 'Twenty One', 'twenty one', '[]', '', '', '[]', 0, '2026-01-01', 3),
                            (99, 'hc:edition:numeric-alias-author-decoy', 9, 1, 'eng', 'Author Alias Decoy', 'author alias decoy', '[]', '', '', '[]', 0, '2026-01-01', 3),
                            (100, 'hc:edition:numeric-alias-digit', 10, 1, 'eng', 'Digit English', '2', '[]', '', '2', '[]', 0, '2026-01-01', 3),
                            (111, 'hc:edition:numeric-alias-narrator-decoy', 11, 1, 'eng', 'Audio Narrator Alias Decoy', 'unrelated audio', '[2]', '2', '', '[]', 0, '2026-01-01', 2),
                            (112, 'hc:edition:numeric-alias-en', 12, 1, 'en', 'Two English Short', 'two', '[]', '', '', '[]', 0, '2026-01-01', 3),
                            (113, 'hc:edition:numeric-alias-english-name', 13, 1, 'English', 'Two English Name', 'two', '[]', '', '', '[]', 0, '2026-01-01', 3),
                            (114, 'hc:edition:numeric-alias-en-us', 14, 1, ' En - US ', 'Two English Region Hyphen', 'two', '[]', '', '', '[]', 0, '2026-01-01', 3),
                            (115, 'hc:edition:numeric-alias-en-us-underscore', 15, 1, 'en_US', 'Two English Region Underscore', 'two', '[]', '', '', '[]', 0, '2026-01-01', 3);
                        INSERT INTO edition_fts (rowid, MatchingTitle, SeriesName, AuthorName, Narrator, Subtitle, Publisher) VALUES
                            (44, 'two', '', 'Freida McFadden', '', '', ''),
                            (55, 'two', '', 'Freida McFadden', '', '', ''),
                            (66, 'two', '', 'Freida McFadden', '', '', ''),
                            (77, 'ii', '', 'Freida McFadden', '', '', ''),
                            (88, 'twenty one', '', 'Freida McFadden', '', '', ''),
                            (99, 'author alias decoy', '', 'Two', '', '', ''),
                            (100, '2', '', 'Freida McFadden', '', '', '2'),
                            (111, 'unrelated audio', '', 'Freida McFadden', '2', '', ''),
                            (112, 'two', '', 'Freida McFadden', '', '', ''),
                            (113, 'two', '', 'Freida McFadden', '', '', ''),
                            (114, 'two', '', 'Freida McFadden', '', '', ''),
                            (115, 'two', '', 'Freida McFadden', '', '', '');");
                }

                var database = new Database("numeric-alias-sqlite", () =>
                {
                    var connection = new SqliteConnection(connectionString);
                    connection.Open();
                    return connection;
                });
                var repository = new EditionFtsRepository(new MainDatabase(database), LogManager.GetCurrentClassLogger());

                var numericRecalls = repository.RecallBooks(null, new[] { "2" }, BookMediaType.Ebook, limit: 20);
                Assert.That(numericRecalls.Select(book => book.BookId), Does.Contain(4), "English numeric words may recall matching title text");
                Assert.That(numericRecalls.Select(book => book.BookId), Is.SupersetOf(new[] { 12, 13, 14, 15 }),
                    "English aliases follow the shared canonicalizer for codes, names, and locale suffixes");
                Assert.That(numericRecalls.Select(book => book.BookId), Does.Contain(7), "Roman title aliases apply for editions with no stored language");
                Assert.That(numericRecalls.Select(book => book.BookId), Does.Not.Contain(5), "English words must not be inferred for a null language");
                Assert.That(numericRecalls.Select(book => book.BookId), Does.Not.Contain(6), "English words must not be inferred for an unsupported language");
                Assert.That(numericRecalls.Select(book => book.BookId), Does.Not.Contain(9), "an author-name alias cannot establish title recall");

                var englishWordRecalls = repository.RecallBooks(null, new[] { "Two" }, BookMediaType.Ebook, limit: 20);
                Assert.That(englishWordRecalls.Select(book => book.BookId), Does.Contain(10), "English title words may recall their numeric title");
                var multiWordRecalls = repository.RecallBooks(null, new[] { "21" }, BookMediaType.Ebook, limit: 20);
                Assert.That(multiWordRecalls.Select(book => book.BookId), Does.Contain(8), "a multiword English cardinal remains one title alternative");

                var recalled = new[]
                {
                    new BookFtsMatch { BookId = 4, AuthorId = 1, MatchScore = 4 },
                    new BookFtsMatch { BookId = 5, AuthorId = 1, MatchScore = 3 },
                    new BookFtsMatch { BookId = 6, AuthorId = 1, MatchScore = 2 },
                    new BookFtsMatch { BookId = 7, AuthorId = 1, MatchScore = 1 },
                    new BookFtsMatch { BookId = 12, AuthorId = 1, MatchScore = 0.9 },
                    new BookFtsMatch { BookId = 13, AuthorId = 1, MatchScore = 0.8 },
                    new BookFtsMatch { BookId = 14, AuthorId = 1, MatchScore = 0.7 },
                    new BookFtsMatch { BookId = 15, AuthorId = 1, MatchScore = 0.6 }
                };
                var ranked = repository.RankEditions(recalled, new[]
                {
                    new EditionFtsFieldQuery { Key = "TITLE[0]", Terms = new[] { "2" }, SourceFields = new[] { "TITLE[0]" } }
                }, BookMediaType.Ebook).ToDictionary(edition => edition.EditionId);
                Assert.That(ranked[44].Stage2FieldHits.Single(hit => hit.FieldKey == "TITLE[0]").TitleHit, Is.True);
                Assert.That(ranked[55].Stage2TitleScore, Is.EqualTo(0), "null-language edition title must not get an English word alias");
                Assert.That(ranked[66].Stage2TitleScore, Is.EqualTo(0), "unsupported-language edition title must not get an English word alias");
                Assert.That(ranked[77].Stage2FieldHits.Single(hit => hit.FieldKey == "TITLE[0]").TitleHit, Is.True,
                    "Roman title aliases remain available without an edition language");
                Assert.That(ranked[112].Stage2TitleScore, Is.GreaterThan(0));
                Assert.That(ranked[113].Stage2TitleScore, Is.GreaterThan(0));
                Assert.That(ranked[114].Stage2TitleScore, Is.GreaterThan(0));
                Assert.That(ranked[115].Stage2TitleScore, Is.GreaterThan(0),
                    "Stage 2 uses the same canonicalizer aliases as Stage 1");

                var titleOnly = repository.RankEditions(new[]
                {
                    new BookFtsMatch { BookId = 10, AuthorId = 1, MatchScore = 1 }
                }, new[]
                {
                    new EditionFtsFieldQuery { Key = "TITLE[1]", Terms = new[] { "Two" }, SourceFields = new[] { "TITLE[1]" } }
                }, BookMediaType.Ebook).Single();
                var titleHit = titleOnly.Stage2FieldHits.Single(hit => hit.FieldKey == "TITLE[1]");
                Assert.That(titleHit.TitleHit, Is.True);
                Assert.That(titleHit.DetailHit, Is.False, "publisher text does not inherit title-number aliases");
                var narratorOnly = repository.RankEditions(new[]
                {
                    new BookFtsMatch { BookId = 11, AuthorId = 1, MatchScore = 1 }
                }, new[]
                {
                    new EditionFtsFieldQuery { Key = "TITLE[2]", Terms = new[] { "Two" }, SourceFields = new[] { "TITLE[2]" } }
                }, BookMediaType.Audiobook).Single();
                Assert.That(narratorOnly.Stage2TitleScore, Is.EqualTo(0));
                Assert.That(narratorOnly.Stage2DetailScore, Is.EqualTo(0), "narrator text does not inherit title-number aliases");
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        [Test]
        public void postgres_recall_rank_and_legacy_search_should_fold_numeric_titles_without_merging_ready_player_siblings()
        {
            var adminConnectionString = Environment.GetEnvironmentVariable("CHAPTARR_TEST_POSTGRES_CONNECTION_STRING");
            if (string.IsNullOrWhiteSpace(adminConnectionString))
            {
                Assert.Ignore("Set CHAPTARR_TEST_POSTGRES_CONNECTION_STRING to exercise PostgreSQL edition FTS");
            }

            var schema = "numeric_fts_" + Guid.NewGuid().ToString("N");
            using (var adminConnection = new NpgsqlConnection(adminConnectionString))
            {
                adminConnection.Open();
                adminConnection.Execute($"CREATE SCHEMA \"{schema}\"");
            }

            try
            {
                var postgresConnectionString = new NpgsqlConnectionStringBuilder(adminConnectionString)
                {
                    SearchPath = schema,
                    Pooling = false
                }.ConnectionString;
                SeedPostgresDatabase(postgresConnectionString);
                using (var connection = new NpgsqlConnection(postgresConnectionString))
                {
                    connection.Open();
                    foreach (var (id, title) in new[] { (11, "Ready Player 1"), (22, "Ready Player 2") })
                    {
                        connection.Execute("UPDATE \"Editions\" SET \"Title\" = @title, \"MatchingTitle\" = @key WHERE \"Id\" = @id",
                            new { id, title, key = StringSuperNormalizer.ComputeMatchingTitle(title) });
                    }
                }


                var database = new Database("postgres-numeric-fts", () =>
                {
                    var connection = new NpgsqlConnection(postgresConnectionString);
                    connection.Open();
                    return connection;
                });
                var repository = new EditionFtsRepository(new MainDatabase(database), LogManager.GetCurrentClassLogger());

                var queryBuilder = typeof(EditionFtsRepository).GetMethod("BuildPostgresTsQuery", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                var halfQuery = (string)queryBuilder.Invoke(null, new object[] { new[] { "½" } });
                using (var connection = new NpgsqlConnection(postgresConnectionString))
                {
                    connection.Open();
                    var decimalMatch = connection.ExecuteScalar<bool>(
                        "SELECT to_tsvector('simple', '0.5') @@ to_tsquery('simple', @query)",
                        new { query = halfQuery });
                    Assert.That(halfQuery, Is.EqualTo("0.5"));
                    Assert.That(decimalMatch, Is.True, "fractional numeric FTS terms must stay a single PostgreSQL lexeme");
                }

                void AssertReadyPlayerQuery(string numericGlyph, int expectedBookId, int expectedEditionId)
                {
                    var terms = new[] { "Ready", "Player", numericGlyph };
                    var recalls = repository.RecallBooks(null, terms, BookMediaType.Ebook, limit: 20);
                    Assert.That(recalls.Select(book => book.BookId), Is.EquivalentTo(new[] { 1, 2 }));
                    Assert.That(recalls[0].BookId, Is.EqualTo(expectedBookId), "numeric sibling must rank by the folded number");

                    var fieldQueries = new[]
                    {
                        new EditionFtsFieldQuery
                        {
                            Key = "TITLE[0]",
                            ResidualValue = string.Join(" ", terms),
                            Terms = terms,
                            SourceFields = new[] { "TITLE[0]" }
                        }
                    };
                    var ranked = repository.RankEditions(recalls, fieldQueries, BookMediaType.Ebook);
                    Assert.That(ranked.Select(edition => edition.EditionId), Is.EquivalentTo(new[] { 11, 22 }));
                    Assert.That(ranked[0].BookId, Is.EqualTo(expectedBookId));
                    Assert.That(ranked[0].EditionId, Is.EqualTo(expectedEditionId));

                    var legacy = repository.SearchWithTwoStep(null, terms, BookMediaType.Ebook, limit: 20);
                    Assert.That(legacy.Select(edition => edition.EditionId), Is.EquivalentTo(new[] { 11, 22 }));
                    Assert.That(legacy[0].BookId, Is.EqualTo(expectedBookId));
                    Assert.That(legacy[0].EditionId, Is.EqualTo(expectedEditionId));
                }

                AssertReadyPlayerQuery("①", expectedBookId: 1, expectedEditionId: 11);
                AssertReadyPlayerQuery("𝟚", expectedBookId: 2, expectedEditionId: 22);
            }
            finally
            {
                using var adminConnection = new NpgsqlConnection(adminConnectionString);
                adminConnection.Open();
                adminConnection.Execute($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
            }
        }


        [Test]
        public void postgres_staged_recall_should_use_language_scoped_numeric_aliases_on_titles_only()
        {
            var adminConnectionString = Environment.GetEnvironmentVariable("CHAPTARR_TEST_POSTGRES_CONNECTION_STRING");
            if (string.IsNullOrWhiteSpace(adminConnectionString))
            {
                Assert.Ignore("Set CHAPTARR_TEST_POSTGRES_CONNECTION_STRING to exercise PostgreSQL edition FTS");
            }

            var schema = "numeric_alias_fts_" + Guid.NewGuid().ToString("N");
            using (var adminConnection = new NpgsqlConnection(adminConnectionString))
            {
                adminConnection.Open();
                adminConnection.Execute($"CREATE SCHEMA \"{schema}\"");
            }

            try
            {
                var connectionString = new NpgsqlConnectionStringBuilder(adminConnectionString)
                {
                    SearchPath = schema,
                    Pooling = false
                }.ConnectionString;
                SeedPostgresDatabase(connectionString);
                using (var connection = new NpgsqlConnection(connectionString))
                {
                    connection.Open();
                    connection.Execute(@"
                        INSERT INTO ""Authors"" (""Id"", ""Name"", ""CleanName"", ""TitleSlug"")
                        VALUES (2, 'Two', 'two', 'two');
                        INSERT INTO ""Books"" (""Id"", ""AuthorId"", ""Title"", ""MediaType"") VALUES
                            (4, 1, 'Two English', 1),
                            (5, 1, 'Two Null', 1),
                            (6, 1, 'Two Unsupported', 1),
                            (7, 1, 'Roman Two', 1),
                            (8, 1, 'Twenty One', 1),
                            (9, 2, 'Author Alias Decoy', 1),
                            (10, 1, 'Digit English', 1),
                            (11, 1, 'Audio Narrator Alias Decoy', 0),
                            (12, 1, 'Two English Short', 1),
                            (13, 1, 'Two English Name', 1),
                            (14, 1, 'Two English Region Hyphen', 1),
                            (15, 1, 'Two English Region Underscore', 1);
                        INSERT INTO ""Editions"" (
                            ""Id"", ""ForeignEditionId"", ""BookId"", ""Language"", ""Title"", ""MatchingTitle"",
                            ""NarratorNames"", ""Narrator"", ""Publisher"", ""Images"", ""DurationSeconds"", ""ReleaseDate"", ""ReadingFormatId"")
                        VALUES
                            (44, 'hc:edition:numeric-alias-english', 4, 'eng', 'Two English', 'two', '[]', '', '', '[]', 0, '2026-01-01', 3),
                            (55, 'hc:edition:numeric-alias-null', 5, NULL, 'Two Null', 'two', '[]', '', '', '[]', 0, '2026-01-01', 3),
                            (66, 'hc:edition:numeric-alias-unsupported', 6, 'fra', 'Two Unsupported', 'two', '[]', '', '', '[]', 0, '2026-01-01', 3),
                            (77, 'hc:edition:numeric-alias-roman', 7, NULL, 'Roman Two', 'ii', '[]', '', '', '[]', 0, '2026-01-01', 3),
                            (88, 'hc:edition:numeric-alias-twenty-one', 8, 'eng', 'Twenty One', 'twenty one', '[]', '', '', '[]', 0, '2026-01-01', 3),
                            (99, 'hc:edition:numeric-alias-author-decoy', 9, 'eng', 'Author Alias Decoy', 'author alias decoy', '[]', '', '', '[]', 0, '2026-01-01', 3),
                            (100, 'hc:edition:numeric-alias-digit', 10, 'eng', 'Digit English', '2', '[]', '', '2', '[]', 0, '2026-01-01', 3),
                            (111, 'hc:edition:numeric-alias-narrator-decoy', 11, 'eng', 'Audio Narrator Alias Decoy', 'unrelated audio', '[2]', '2', '', '[]', 0, '2026-01-01', 2),
                            (112, 'hc:edition:numeric-alias-en', 12, 'en', 'Two English Short', 'two', '[]', '', '', '[]', 0, '2026-01-01', 3),
                            (113, 'hc:edition:numeric-alias-english-name', 13, 'English', 'Two English Name', 'two', '[]', '', '', '[]', 0, '2026-01-01', 3),
                            (114, 'hc:edition:numeric-alias-en-us', 14, ' En - US ', 'Two English Region Hyphen', 'two', '[]', '', '', '[]', 0, '2026-01-01', 3),
                            (115, 'hc:edition:numeric-alias-en-us-underscore', 15, 'en_US', 'Two English Region Underscore', 'two', '[]', '', '', '[]', 0, '2026-01-01', 3)");
                }

                var database = new Database("numeric-alias-postgres", () =>
                {
                    var connection = new NpgsqlConnection(connectionString);
                    connection.Open();
                    return connection;
                });
                var repository = new EditionFtsRepository(new MainDatabase(database), LogManager.GetCurrentClassLogger());

                var numericRecalls = repository.RecallBooks(null, new[] { "2" }, BookMediaType.Ebook, limit: 20);
                Assert.That(numericRecalls.Select(book => book.BookId), Does.Contain(4), "English numeric words may recall matching title text");
                Assert.That(numericRecalls.Select(book => book.BookId), Is.SupersetOf(new[] { 12, 13, 14, 15 }),
                    "English aliases follow the shared canonicalizer for codes, names, and locale suffixes");
                Assert.That(numericRecalls.Select(book => book.BookId), Does.Contain(7), "Roman title aliases apply for editions with no stored language");
                Assert.That(numericRecalls.Select(book => book.BookId), Does.Not.Contain(5), "English words must not be inferred for a null language");
                Assert.That(numericRecalls.Select(book => book.BookId), Does.Not.Contain(6), "English words must not be inferred for an unsupported language");
                Assert.That(numericRecalls.Select(book => book.BookId), Does.Not.Contain(9), "an author-name alias cannot establish title recall");

                var englishWordRecalls = repository.RecallBooks(null, new[] { "Two" }, BookMediaType.Ebook, limit: 20);
                Assert.That(englishWordRecalls.Select(book => book.BookId), Does.Contain(10), "English title words may recall their numeric title");
                var multiWordRecalls = repository.RecallBooks(null, new[] { "21" }, BookMediaType.Ebook, limit: 20);
                Assert.That(multiWordRecalls.Select(book => book.BookId), Does.Contain(8), "a multiword English cardinal remains one title alternative");

                var recalled = new[]
                {
                    new BookFtsMatch { BookId = 4, AuthorId = 1, MatchScore = 4 },
                    new BookFtsMatch { BookId = 5, AuthorId = 1, MatchScore = 3 },
                    new BookFtsMatch { BookId = 6, AuthorId = 1, MatchScore = 2 },
                    new BookFtsMatch { BookId = 7, AuthorId = 1, MatchScore = 1 },
                    new BookFtsMatch { BookId = 12, AuthorId = 1, MatchScore = 0.9 },
                    new BookFtsMatch { BookId = 13, AuthorId = 1, MatchScore = 0.8 },
                    new BookFtsMatch { BookId = 14, AuthorId = 1, MatchScore = 0.7 },
                    new BookFtsMatch { BookId = 15, AuthorId = 1, MatchScore = 0.6 }
                };
                var ranked = repository.RankEditions(recalled, new[]
                {
                    new EditionFtsFieldQuery { Key = "TITLE[0]", Terms = new[] { "2" }, SourceFields = new[] { "TITLE[0]" } }
                }, BookMediaType.Ebook).ToDictionary(edition => edition.EditionId);
                Assert.That(ranked[44].Stage2FieldHits.Single(hit => hit.FieldKey == "TITLE[0]").TitleHit, Is.True);
                Assert.That(ranked[55].Stage2TitleScore, Is.EqualTo(0), "null-language edition title must not get an English word alias");
                Assert.That(ranked[66].Stage2TitleScore, Is.EqualTo(0), "unsupported-language edition title must not get an English word alias");
                Assert.That(ranked[77].Stage2FieldHits.Single(hit => hit.FieldKey == "TITLE[0]").TitleHit, Is.True,
                    "Roman title aliases remain available without an edition language");
                Assert.That(ranked[112].Stage2TitleScore, Is.GreaterThan(0));
                Assert.That(ranked[113].Stage2TitleScore, Is.GreaterThan(0));
                Assert.That(ranked[114].Stage2TitleScore, Is.GreaterThan(0));
                Assert.That(ranked[115].Stage2TitleScore, Is.GreaterThan(0),
                    "Stage 2 uses the same canonicalizer aliases as Stage 1");

                var titleOnly = repository.RankEditions(new[]
                {
                    new BookFtsMatch { BookId = 10, AuthorId = 1, MatchScore = 1 }
                }, new[]
                {
                    new EditionFtsFieldQuery { Key = "TITLE[1]", Terms = new[] { "Two" }, SourceFields = new[] { "TITLE[1]" } }
                }, BookMediaType.Ebook).Single();
                var titleHit = titleOnly.Stage2FieldHits.Single(hit => hit.FieldKey == "TITLE[1]");
                Assert.That(titleHit.TitleHit, Is.True);
                Assert.That(titleHit.DetailHit, Is.False, "publisher text does not inherit title-number aliases");
                var narratorOnly = repository.RankEditions(new[]
                {
                    new BookFtsMatch { BookId = 11, AuthorId = 1, MatchScore = 1 }
                }, new[]
                {
                    new EditionFtsFieldQuery { Key = "TITLE[2]", Terms = new[] { "Two" }, SourceFields = new[] { "TITLE[2]" } }
                }, BookMediaType.Audiobook).Single();
                Assert.That(narratorOnly.Stage2TitleScore, Is.EqualTo(0));
                Assert.That(narratorOnly.Stage2DetailScore, Is.EqualTo(0), "narrator text does not inherit title-number aliases");
            }
            finally
            {
                using var adminConnection = new NpgsqlConnection(adminConnectionString);
                adminConnection.Open();
                adminConnection.Execute($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
            }
        }

        private static void SeedPostgresDatabase(string connectionString)
        {
            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();
            connection.Execute(@"
                CREATE TABLE ""Authors"" (
                    ""Id"" INTEGER PRIMARY KEY,
                    ""Name"" TEXT NOT NULL,
                    ""CleanName"" TEXT,
                    ""TitleSlug"" TEXT
                );
                CREATE TABLE ""Books"" (
                    ""Id"" INTEGER PRIMARY KEY,
                    ""AuthorId"" INTEGER NOT NULL,
                    ""Title"" TEXT NOT NULL,
                    ""SeriesName"" TEXT,
                    ""SeriesPosition"" TEXT,
                    ""MediaType"" INTEGER NOT NULL
                );
                CREATE TABLE ""Editions"" (
                    ""Id"" INTEGER PRIMARY KEY,
                    ""ForeignEditionId"" TEXT,
                    ""BookId"" INTEGER NOT NULL,
                    ""Language"" TEXT,
                    ""Title"" TEXT,
                    ""Subtitle"" TEXT,
                    ""MatchingTitle"" TEXT,
                    ""NarratorNames"" TEXT,
                    ""Narrator"" TEXT,
                    ""Publisher"" TEXT,
                    ""Images"" TEXT,
                    ""DurationSeconds"" INTEGER,
                    ""ReleaseDate"" TIMESTAMP,
                    ""ReadingFormatId"" INTEGER
                );

                INSERT INTO ""Authors"" (""Id"", ""Name"", ""CleanName"", ""TitleSlug"")
                VALUES (1, 'Test Author', 'test author', 'test-author');
                INSERT INTO ""Books"" (""Id"", ""AuthorId"", ""Title"", ""MediaType"") VALUES
                    (1, 1, 'Ready Player One', 1),
                    (2, 1, 'Ready Player Two', 1);
                INSERT INTO ""Editions"" (
                    ""Id"", ""ForeignEditionId"", ""BookId"", ""Language"", ""Title"", ""MatchingTitle"",
                    ""NarratorNames"", ""Narrator"", ""Publisher"", ""Images"", ""DurationSeconds"", ""ReleaseDate"", ""ReadingFormatId"")
                VALUES
                    (11, 'hc:edition:ready-player-one', 1, 'eng', 'Ready Player One', 'ready player one', '[]', '', '', '[]', 0, '2011-08-16', 3),
                    (22, 'hc:edition:ready-player-two', 2, 'eng', 'Ready Player Two', 'ready player two', '[]', '', '', '[]', 0, '2020-11-24', 3);
            ");
        }

        private static void SeedDatabase(string connectionString)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            connection.Execute(@"
                CREATE TABLE Authors (
                    Id INTEGER PRIMARY KEY,
                    Name TEXT NOT NULL,
                    AudiobookMonitored INTEGER,
                    AudiobookMonitorNewItems INTEGER,
                    EbookMonitored INTEGER,
                    EbookMonitorNewItems INTEGER
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

                INSERT INTO Authors (Id, Name, AudiobookMonitored, AudiobookMonitorNewItems, EbookMonitored, EbookMonitorNewItems)
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
