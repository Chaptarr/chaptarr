using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using Chaptarr.Core.Test.Common;
using Dapper;
using Microsoft.Data.Sqlite;
using NLog;
using Npgsql;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Extensions;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Parser;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class EditionFtsRepositoryStagedFixture
    {
        [TestCase(DatabaseType.SQLite, BookMediaType.Audiobook, true)]
        [TestCase(DatabaseType.SQLite, BookMediaType.Audiobook, false)]
        [TestCase(DatabaseType.PostgreSQL, BookMediaType.Audiobook, true)]
        [TestCase(DatabaseType.PostgreSQL, BookMediaType.Audiobook, false)]
        [TestCase(DatabaseType.SQLite, BookMediaType.Ebook, true)]
        [TestCase(DatabaseType.SQLite, BookMediaType.Ebook, false)]
        [TestCase(DatabaseType.PostgreSQL, BookMediaType.Ebook, true)]
        [TestCase(DatabaseType.PostgreSQL, BookMediaType.Ebook, false)]
        public void recalled_roman_title_must_pass_numeric_proof_without_losing_the_number(
            DatabaseType databaseType, BookMediaType mediaType, bool storedRoman)
        {
            WithSeededFtsRepository(databaseType, (repository, connection) =>
            {
                var six = storedRoman ? "Wild Cards VI" : "Wild Cards 6";
                var seven = storedRoman ? "Wild Cards VII" : "Wild Cards 7";
                connection.Execute("UPDATE \"Books\" SET \"MediaType\" = @mediaType WHERE \"Id\" IN (1, 2); " +
                    "UPDATE \"Editions\" SET \"Title\" = @six, \"MatchingTitle\" = @sixKey, \"Language\" = NULL WHERE \"Id\" = 11; " +
                    "UPDATE \"Editions\" SET \"Title\" = @seven, \"MatchingTitle\" = @sevenKey, \"Language\" = NULL WHERE \"Id\" = 22;",
                    new { mediaType = (int)mediaType, six, seven, sixKey = StringSuperNormalizer.ComputeMatchingTitle(six), sevenKey = StringSuperNormalizer.ComputeMatchingTitle(seven) });
                RefreshProjections(connection);
                if (databaseType == DatabaseType.SQLite)
                {
                    repository.RebuildIndex();
                }

                var observed = storedRoman ? "Wild Cards 7" : "Wild Cards VII";
                var terms = ReleaseTitleMatchScorer.Tokenize(observed);
                var recalled = repository.RecallBooks(1, terms, mediaType, limit: 0);
                Assert.That(recalled.Select(book => book.BookId), Does.Contain(2));
                var ranked = repository.RankEditions(recalled, new[]
                {
                    new EditionFtsFieldQuery { Key = "CUSTOM[0]", ResidualValue = observed, Terms = terms, SourceFields = new[] { "CUSTOM[0]" } }
                }, mediaType);
                var proven = ranked.Where(edition => ReleaseTitleMatchScorer.FindNumberEquivalentTitleSpans(
                    terms, ReleaseTitleMatchScorer.Tokenize(edition.EditionTitle), edition.Lang).Any()).ToList();

                Assert.That(proven.Select(edition => edition.EditionId), Is.EqualTo(new[] { 22 }));
                Assert.That(proven.Single().EditionTitle, Is.EqualTo(seven));
                Assert.That(connection.ExecuteScalar<string>("SELECT \"Title\" FROM \"Editions\" WHERE \"Id\" = 22"), Is.EqualTo(seven));
            });
        }

        [TestCase(DatabaseType.SQLite, 20)]
        [TestCase(DatabaseType.SQLite, 21)]
        [TestCase(DatabaseType.PostgreSQL, 20)]
        [TestCase(DatabaseType.PostgreSQL, 21)]
        public void recall_cutoff_should_be_stable_and_trace_only_returned_books(DatabaseType databaseType, int count)
        {
            WithSeededFtsRepository(databaseType, (repository, connection) =>
            {
                foreach (var id in Enumerable.Range(100, count).Reverse())
                {
                    connection.Execute("INSERT INTO \"Books\" (\"Id\", \"AuthorId\", \"Title\", \"MediaType\") VALUES (@id, 1, 'Deterministic Beacon', @mediaType); " +
                        "INSERT INTO \"Editions\" (\"Id\", \"BookId\", \"Title\", \"MatchingTitle\", \"NarratorNames\", \"Images\") VALUES (@id, @id, 'Deterministic Beacon', 'deterministic beacon', '[]', '[]');",
                        new { id, mediaType = (int)BookMediaType.Audiobook });
                }
                RefreshProjections(connection);
                if (databaseType == DatabaseType.SQLite) repository.RebuildIndex();
                var terms = new[] { "deterministic", "beacon" };
                Assert.That(repository.RecallBooks(1, terms, BookMediaType.Audiobook, limit: 0), Has.Count.EqualTo(count));
                for (var pass = 0; pass < 2; pass++)
                {
                    var trace = new List<EditionFtsTraceEvent>();
                    var recalled = repository.RecallBooks(1, terms, BookMediaType.Audiobook, trace.Add);
                    Assert.That(recalled.Select(row => row.BookId), Is.EqualTo(Enumerable.Range(100, 20)));
                    Assert.That(trace.Where(row => row.EventType == "candidate").Select(row => row.BookId),
                        Is.EqualTo(recalled.Select(row => (int?)row.BookId)));
                }
            });
        }

        [OneTimeSetUp]
        public void SetUpFixture()
        {
            if (TableMapping.Mapper.TableMap.Count == 0)
            {
                TableMapping.Map();
            }
        }

        [TestCase(DatabaseType.SQLite, BookMediaType.Audiobook)]
        [TestCase(DatabaseType.PostgreSQL, BookMediaType.Audiobook)]
        [TestCase(DatabaseType.SQLite, BookMediaType.Ebook)]
        [TestCase(DatabaseType.PostgreSQL, BookMediaType.Ebook)]
        public void complete_supplied_evidence_should_enter_recall_before_repeated_partial_matches(DatabaseType databaseType, BookMediaType mediaType)
        {
            WithSeededFtsRepository(databaseType, (repository, connection) =>
            {
                connection.Execute("UPDATE \"Authors\" SET \"Name\" = 'Amber Vale'; " +
                    "UPDATE \"Books\" SET \"MediaType\" = @mediaType; " +
                    "UPDATE \"Books\" SET \"Title\" = 'Silver Sea Rare' WHERE \"Id\" = 1; " +
                    "UPDATE \"Editions\" SET \"Title\" = 'Silver Sea Rare', \"MatchingTitle\" = 'silver sea rare' WHERE \"Id\" = 11;",
                    new { mediaType = (int)mediaType });
                var repeated = string.Join(" ", Enumerable.Repeat("silver sea amber vale", 12));
                for (var id = 100; id < 121; id++)
                {
                    connection.Execute("INSERT INTO \"Books\" (\"Id\", \"AuthorId\", \"Title\", \"MediaType\") VALUES (@id, 1, 'Silver Sea', @mediaType); " +
                        "INSERT INTO \"Editions\" (\"Id\", \"BookId\", \"Title\", \"MatchingTitle\", \"NarratorNames\", \"Images\") VALUES (@id, @id, @repeated, @repeated, '[]', '[]');",
                        new { id, mediaType = (int)mediaType, repeated });
                }
                RefreshProjections(connection);
                if (databaseType == DatabaseType.SQLite)
                {
                    repository.RebuildIndex();
                }

                var terms = new[] { "silver", "sea", "rare", "amber", "vale" };
                var trace = new List<EditionFtsTraceEvent>();
                var recalled = repository.RecallBooks(1, terms, mediaType, trace.Add, limit: 20);
                var duplicates = repository.RecallBooks(1, terms.Concat(new[] { "SILVER", "silver" }), mediaType, limit: 20);
                var incomplete = repository.RecallBooks(1, terms.Concat(new[] { "unmatched" }), mediaType, limit: 20);
                var uncapped = repository.RecallBooks(1, terms.Concat(new[] { "unmatched" }), mediaType, limit: 0);
                Assert.Multiple(() =>
                {
                    Assert.That(uncapped.Select(row => row.BookId), Does.Contain(1));
                    Assert.That(uncapped.Select(row => row.BookId), Is.SupersetOf(Enumerable.Range(100, 21)));
                    Assert.That(uncapped.Count, Is.GreaterThan(20));
                    Assert.That(incomplete.Select(row => row.MatchScore), Is.Ordered.Descending);
                    Assert.That(recalled, Has.Count.EqualTo(20));
                    Assert.That(recalled[0].BookId, Is.EqualTo(1));
                    Assert.That(duplicates[0].BookId, Is.EqualTo(1));
                    Assert.That(duplicates[0].MatchScore, Is.EqualTo(recalled[0].MatchScore));
                    Assert.That(trace.Count(entry => entry.EventType == "query" && entry.Step == "stage1_book_recall"), Is.EqualTo(1));
                });
            });
        }

        [TestCase(DatabaseType.SQLite)]
        [TestCase(DatabaseType.PostgreSQL)]
        public void stage2_should_preserve_edition_title_scores_when_siblings_share_broad_recall(DatabaseType databaseType)
        {
            WithSeededFtsRepository(databaseType, (repository, connection) =>
            {
                connection.Execute("UPDATE \"Books\" SET \"Title\" = 'Red Country', \"MediaType\" = 0 WHERE \"Id\" = 1; " +
                    "UPDATE \"Editions\" SET \"BookId\" = 1, \"Title\" = 'Red Country', \"MatchingTitle\" = 'red country' WHERE \"Id\" = 11; " +
                    "UPDATE \"Editions\" SET \"BookId\" = 1, \"Title\" = 'Red Country: Booktrack Edition', \"MatchingTitle\" = 'red country booktrack edition' WHERE \"Id\" = 22;");
                RefreshProjections(connection);
                if (databaseType == DatabaseType.SQLite)
                {
                    repository.RebuildIndex();
                }

                var ranked = repository.RankEditions(
                    new[] { new BookFtsMatch { BookId = 1, AuthorId = 1, MatchScore = 26.052885745008062 } },
                    new[] { new EditionFtsFieldQuery { Key = "ALBUM[0]", ResidualValue = "Red Country", Terms = new[] { "red", "country" } } },
                    BookMediaType.Audiobook);
                var ordinary = ranked.Single(edition => edition.EditionId == 11);
                var booktrack = ranked.Single(edition => edition.EditionId == 22);
                Assert.Multiple(() =>
                {
                    Assert.That(ordinary.BroadRecallScore, Is.EqualTo(booktrack.BroadRecallScore));
                    // SQLite BM25 distinguishes these lengths; PostgreSQL's native rank can tie.
                    // Preserve the engine's actual evidence without substituting the shared recall score.
                    Assert.That(ordinary.Stage2TitleScore, Is.GreaterThanOrEqualTo(booktrack.Stage2TitleScore));
                    if (databaseType == DatabaseType.SQLite)
                    {
                        Assert.That(ordinary.Stage2TitleScore, Is.GreaterThan(booktrack.Stage2TitleScore));
                    }
                    Assert.That(ordinary.MatchScore, Is.EqualTo(ordinary.Stage2TitleScore));
                    Assert.That(booktrack.MatchScore, Is.EqualTo(booktrack.Stage2TitleScore));
                });
            });
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
                Assert.That(ranked[0].MatchScore, Is.EqualTo(ranked[0].Stage2TitleScore), "edition ranking preserves title evidence without summing narrator/publisher hits into it");
                Assert.That(ranked[1].MatchScore, Is.EqualTo(ranked[1].Stage2TitleScore));
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

        [TestCase("AND", "ordinary")]
        [TestCase("OR", "ordinary")]
        [TestCase("NOT", "ordinary")]
        [TestCase("NEAR", "ordinary")]
        [TestCase("plain", "plainness")]
        [TestCase("21", "210")]
        [TestCase("0.5", "0 5")]
        [TestCase("ready-player", "ready player")]
        [TestCase("alpha beta", "alpha filler beta")]
        [TestCase("alpha \"beta\"", "alpha filler beta")]
        [TestCase("alpha*", "alphabet")]
        [TestCase("NEAR(alpha beta)", "alpha filler beta")]
        public void sqlite_query_terms_should_execute_as_literal_phrases(string term, string decoy)
        {
            using var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            connection.Execute(@"
                CREATE VIRTUAL TABLE literal_terms USING fts5(
                    Value, tokenize = 'unicode61 remove_diacritics 1 tokenchars ''-.''');");
            connection.Execute("INSERT INTO literal_terms(rowid, Value) VALUES (1, @term), (2, @decoy);", new { term, decoy });
            var method = typeof(EditionFtsRepository).GetMethod("TokenToFtsQueryTerm", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            var query = (string)method.Invoke(null, new object[] { term });

            Assert.That(connection.Query<int>("SELECT rowid FROM literal_terms WHERE literal_terms MATCH @query ORDER BY rowid;", new { query }),
                Is.EqualTo(new[] { 1 }));
        }

        [TestCase(DatabaseType.SQLite)]
        [TestCase(DatabaseType.PostgreSQL)]
        public void uppercase_operator_words_should_preserve_public_recall_and_ranking(DatabaseType databaseType)
        {
            WithSeededFtsRepository(databaseType, (repository, connection) =>
            {
                const string title = "WAR AND PEACE NOT OR NEAR";
                var tokens = new[] { title };
                var lowercaseTokens = new[] { title.ToLowerInvariant() };
                connection.Execute("UPDATE \"Books\" SET \"Title\" = @title; " +
                    "UPDATE \"Editions\" SET \"Title\" = @title, \"MatchingTitle\" = @key, \"Publisher\" = @title, \"Narrator\" = @title;",
                    new { title, key = StringSuperNormalizer.ComputeMatchingTitle(title) });
                foreach (var mediaType in new[] { BookMediaType.Ebook, BookMediaType.Audiobook })
                {
                    connection.Execute("UPDATE \"Books\" SET \"MediaType\" = CASE WHEN \"Id\" = 1 THEN @mediaType ELSE @otherMediaType END;",
                        new { mediaType = (int)mediaType, otherMediaType = mediaType == BookMediaType.Ebook ? 0 : 1 });
                    RefreshProjections(connection);
                    if (databaseType == DatabaseType.SQLite)
                    {
                        RefreshProjections(connection);
                        repository.RebuildIndex();
                    }

                    RefreshProjections(connection);
                        var recallTrace = new List<EditionFtsTraceEvent>();
                    var recalled = repository.RecallBooks(null, tokens, mediaType, recallTrace.Add);
                    var baseline = repository.RecallBooks(null, lowercaseTokens, mediaType);
                    var rankTrace = new List<EditionFtsTraceEvent>();
                    var ranked = repository.RankEditions(recalled, new[]
                    {
                        new EditionFtsFieldQuery { Key = "TITLE[0]", ResidualValue = title, Terms = tokens, SourceFields = new[] { "TITLE[0]" } }
                    }, mediaType, rankTrace.Add);

                    Assert.Multiple(() =>
                    {
                        Assert.That(recalled.Select(book => book.BookId), Is.EqualTo(new[] { 1 }));
                        Assert.That(recalled.Single().MatchScore, Is.EqualTo(baseline.Single().MatchScore));
                        Assert.That(ranked.Select(edition => edition.BookId), Is.EqualTo(new[] { 1 }));
                        Assert.That(ranked.Single().Stage2TitleScore, Is.GreaterThan(0));
                        Assert.That(ranked.Single().Stage2DetailScore, Is.GreaterThan(0));
                        Assert.That(recallTrace.Count(entry => entry.EventType == "query" && entry.Step == "stage1_book_recall"), Is.EqualTo(1));
                        Assert.That(rankTrace.Count(entry => entry.EventType == "query" && entry.Step == "stage2_field_ranking"), Is.EqualTo(2));
                    });
                }
            });
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
        [TestCase("Søren", "soren")]
        [TestCase("Łukasz", "lukasz")]
        [TestCase("Ælfric", "aelfric")]
        [TestCase("Straße", "strasse")]
        [TestCase("Королёв", "королев")]
        [TestCase("Ready②", "ready2")]
        [TestCase("Søren②", "soren2")]
        [TestCase("１Q84", "1q84")]
        public void postgres_query_builder_should_preserve_letters_without_admitting_query_syntax(string input, string expected)
        {
            var method = typeof(EditionFtsRepository).GetMethod("BuildPostgresTsQuery", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.That(method.Invoke(null, new object[] { new[] { input }, false }), Is.EqualTo(expected));
        }

        [TestCase(DatabaseType.SQLite, "Søren")]
        [TestCase(DatabaseType.PostgreSQL, "Søren")]
        [TestCase(DatabaseType.SQLite, "Łukasz")]
        [TestCase(DatabaseType.PostgreSQL, "Łukasz")]
        [TestCase(DatabaseType.SQLite, "Ælfric")]
        [TestCase(DatabaseType.PostgreSQL, "Ælfric")]
        [TestCase(DatabaseType.SQLite, "Straße")]
        [TestCase(DatabaseType.PostgreSQL, "Straße")]
        [TestCase(DatabaseType.SQLite, "Королёв")]
        [TestCase(DatabaseType.PostgreSQL, "Королёв")]
        [TestCase(DatabaseType.SQLite, "άγνωστος")]
        [TestCase(DatabaseType.PostgreSQL, "άγνωστος")]
        [TestCase(DatabaseType.SQLite, "がとう")]
        [TestCase(DatabaseType.PostgreSQL, "がとう")]
        [TestCase(DatabaseType.SQLite, "か\u3099とう")]
        [TestCase(DatabaseType.PostgreSQL, "か\u3099とう")]
        [TestCase(DatabaseType.SQLite, "𠀀Reader")]
        [TestCase(DatabaseType.PostgreSQL, "𠀀Reader")]
        [TestCase(DatabaseType.SQLite, "οδυσσεας")]
        [TestCase(DatabaseType.PostgreSQL, "οδυσσεας")]
        public void recall_and_detail_ranking_should_retain_original_metadata_spelling_beside_folded_title_keys(DatabaseType databaseType, string query)
        {
            WithSeededFtsRepository(databaseType, (repository, connection) =>
            {
                Assert.Multiple(() =>
                {
                    var spelling = query.Normalize(System.Text.NormalizationForm.FormC);
                    foreach (var mediaType in new[] { BookMediaType.Ebook, BookMediaType.Audiobook })
                    {
                        connection.Execute("UPDATE \"Books\" SET \"MediaType\" = CASE WHEN \"Id\" = 1 THEN @mediaType ELSE @otherMediaType END;",
                            new { mediaType = (int)mediaType, otherMediaType = mediaType == BookMediaType.Ebook ? 0 : 1 });
                        foreach (var field in new[] { "MatchingTitle", "AuthorName", "SeriesName" })
                        {
                            var title = field == "MatchingTitle" ? spelling + " Story" : "Neutral Story";
                            var author = field == "AuthorName" ? spelling + " Reader" : "Neutral Reader";
                            connection.Execute("UPDATE \"Authors\" SET \"Name\" = @author; UPDATE \"Books\" SET \"SeriesName\" = CASE WHEN \"Id\" = 1 THEN @series ELSE '' END; " +
                                "UPDATE \"Editions\" SET \"Title\" = @title, \"MatchingTitle\" = @key WHERE \"Id\" = 11;",
                                new { author, series = field == "SeriesName" ? spelling : string.Empty, title, key = StringSuperNormalizer.ComputeMatchingTitle(title) });
                            if (databaseType == DatabaseType.PostgreSQL)
                            {
                                connection.Execute("UPDATE \"Authors\" SET \"CleanName\" = @key, \"TitleSlug\" = 'neutral';",
                                    new { key = NzbDrone.Core.Parser.Parser.CleanAuthorName(author) });
                            }
                            else
                            {
                                RefreshProjections(connection);
                                repository.RebuildIndex();
                            }

                            RefreshProjections(connection);
                            var trace = new List<EditionFtsTraceEvent>();
                            var timer = System.Diagnostics.Stopwatch.StartNew();
                            var recalled = repository.RecallBooks(null, new[] { query }, mediaType, trace.Add);
                            timer.Stop();
                            Assert.Multiple(() =>
                            {
                                Assert.That(recalled.Select(book => book.BookId), Is.EqualTo(new[] { 1 }), field + " recall");
                                Assert.That(trace.Count(entry => entry.EventType == "query" && entry.Step == "stage1_book_recall"), Is.EqualTo(1));
                            });
                            TestContext.Progress.WriteLine($"{databaseType} {mediaType} {field}: one FTS query, {timer.Elapsed.TotalMilliseconds:F3} ms.");
                        }

                        connection.Execute("UPDATE \"Editions\" SET \"Publisher\" = @publisher, \"Narrator\" = @narrator, \"NarratorNames\" = '[]' WHERE \"Id\" = 11;",
                            new { publisher = spelling + " Press", narrator = spelling + " Voice" });
                        RefreshProjections(connection);
                        var ranked = repository.RankEditions(new[]
                        {
                            new BookFtsMatch { BookId = 1, AuthorId = 1, BookTitle = "Neutral Story", MatchScore = 1 }
                        }, new[]
                        {
                            new EditionFtsFieldQuery { Key = "DETAIL[0]", ResidualValue = query, Terms = new[] { query }, SourceFields = new[] { "DETAIL[0]" } }
                        }, mediaType);
                        Assert.That(ranked.Single().Stage2DetailScore, Is.GreaterThan(0), "raw metadata detail must retain its queried spelling");
                    }
                });
            });
        }

        [TestCase(DatabaseType.SQLite)]
        [TestCase(DatabaseType.PostgreSQL)]
        public void original_metadata_case_should_preserve_lowercase_recall(DatabaseType databaseType)
        {
            WithSeededFtsRepository(databaseType, (repository, connection) =>
            {
                const string query = "Łukasz";
                const string spelling = "łukasz";
                foreach (var mediaType in new[] { BookMediaType.Ebook, BookMediaType.Audiobook })
                {
                    connection.Execute("UPDATE \"Books\" SET \"MediaType\" = CASE WHEN \"Id\" = 1 THEN @mediaType ELSE @otherMediaType END;",
                        new { mediaType = (int)mediaType, otherMediaType = mediaType == BookMediaType.Ebook ? 0 : 1 });
                    foreach (var field in new[] { "AuthorName", "SeriesName" })
                    {
                        var author = field == "AuthorName" ? spelling + " Reader" : "Neutral Reader";
                        connection.Execute("UPDATE \"Authors\" SET \"Name\" = @author; UPDATE \"Books\" SET \"SeriesName\" = CASE WHEN \"Id\" = 1 THEN @series ELSE '' END; " +
                            "UPDATE \"Editions\" SET \"Publisher\" = @publisher, \"Narrator\" = @narrator, \"NarratorNames\" = '[]' WHERE \"Id\" = 11;",
                            new { author, series = field == "SeriesName" ? spelling : string.Empty, publisher = spelling + " Press", narrator = spelling + " Voice" });
                        if (databaseType == DatabaseType.PostgreSQL)
                        {
                            connection.Execute("UPDATE \"Authors\" SET \"CleanName\" = @key, \"TitleSlug\" = 'neutral';",
                                new { key = NzbDrone.Core.Parser.Parser.CleanAuthorName(author) });
                        }
                        else
                        {
                            RefreshProjections(connection);
                            repository.RebuildIndex();
                        }

                        RefreshProjections(connection);
                        var expectedIds = new[] { 1 };
                        Assert.Multiple(() =>
                        {
                            Assert.That(repository.RecallBooks(null, new[] { query }, mediaType).Select(book => book.BookId), Is.EqualTo(expectedIds), field + " recall");
                        });
                    }

                    var ranked = repository.RankEditions(new[]
                    {
                        new BookFtsMatch { BookId = 1, AuthorId = 1, MatchScore = 1 }
                    }, new[]
                    {
                        new EditionFtsFieldQuery { Key = "DETAIL[0]", ResidualValue = query, Terms = new[] { query }, SourceFields = new[] { "DETAIL[0]" } }
                    }, mediaType);
                    Assert.That(ranked.Single().Stage2DetailScore, Is.GreaterThan(0), "detail lowercase recall");
                }
            });
        }

        [TestCase("Café", "cafe")]
        [TestCase("Søren", "søren")]
        public void native_equivalent_query_spellings_should_preserve_scores_across_recall_and_field_ranking(string query, string equivalent)
        {
            WithSeededFtsRepository(DatabaseType.SQLite, (repository, connection) =>
            {
                foreach (var mediaType in new[] { BookMediaType.Ebook, BookMediaType.Audiobook })
                {
                    connection.Execute("UPDATE \"Books\" SET \"MediaType\" = CASE WHEN \"Id\" = 1 THEN @mediaType ELSE @otherMediaType END, " +
                        "\"SeriesName\" = CASE WHEN \"Id\" = 1 THEN @series ELSE '' END; UPDATE \"Authors\" SET \"Name\" = @author; " +
                        "UPDATE \"Editions\" SET \"Title\" = @title, \"MatchingTitle\" = @key, \"Publisher\" = @publisher, \"Narrator\" = @narrator WHERE \"Id\" = 11;",
                        new { mediaType = (int)mediaType, otherMediaType = mediaType == BookMediaType.Ebook ? 0 : 1,
                            series = query + " Archive", author = query + " Reader", title = query + " Story",
                            key = StringSuperNormalizer.ComputeMatchingTitle(query + " Story"), publisher = query + " Press", narrator = query + " Voice" });
                    repository.RebuildIndex();

                    RefreshProjections(connection);
                        var recallTrace = new List<EditionFtsTraceEvent>();
                    var baselineRecall = repository.RecallBooks(null, new[] { equivalent }, mediaType);
                    var expandedRecall = repository.RecallBooks(null, new[] { query }, mediaType, recallTrace.Add);
                    EditionFtsFieldQuery[] Fields(string term) => new[]
                    {
                        new EditionFtsFieldQuery { Key = "TITLE[0]", ResidualValue = term, Terms = new[] { term }, SourceFields = new[] { "TITLE[0]" } },
                        new EditionFtsFieldQuery { Key = "TITLE[1]", ResidualValue = term, Terms = new[] { term }, SourceFields = new[] { "TITLE[1]" } },
                        new EditionFtsFieldQuery { Key = "DETAIL[0]", ResidualValue = term, Terms = new[] { term }, SourceFields = new[] { "DETAIL[0]" } }
                    };
                    var rankTrace = new List<EditionFtsTraceEvent>();
                    var baselineRank = repository.RankEditions(baselineRecall, Fields(equivalent), mediaType);
                    var expandedRank = repository.RankEditions(expandedRecall, Fields(query), mediaType, rankTrace.Add);

                    Assert.Multiple(() =>
                    {
                        Assert.That(baselineRecall.Select(book => book.BookId), Is.EqualTo(new[] { 1 }));
                        Assert.That(expandedRecall.Select(book => book.BookId), Is.EqualTo(new[] { 1 }));
                        Assert.That(expandedRecall.Single().MatchScore, Is.EqualTo(baselineRecall.Single().MatchScore));
                        Assert.That(expandedRank.Single().Stage2TitleScore, Is.GreaterThan(0).And.EqualTo(baselineRank.Single().Stage2TitleScore));
                        Assert.That(expandedRank.Single().Stage2DetailScore, Is.GreaterThan(0).And.EqualTo(baselineRank.Single().Stage2DetailScore));
                        Assert.That(recallTrace.Count(entry => entry.EventType == "query" && entry.Step == "stage1_book_recall"), Is.EqualTo(1));
                        Assert.That(recallTrace.Count(entry => entry.Step == "sqlite_query_tokenization"), Is.Zero);
                        Assert.That(rankTrace.Count(entry => entry.Step == "sqlite_query_tokenization"), Is.Zero);
                        Assert.That(rankTrace.Where(entry => entry.EventType == "query" && entry.Step == "stage2_field_ranking").Select(entry => entry.FieldKey).Distinct(),
                            Is.EquivalentTo(new[] { "TITLE[0]", "TITLE[1]", "DETAIL[0]" }));
                    });
                }
            });
        }

        [Test]
        public void native_query_term_keys_should_preserve_whole_sequences_and_clean_temporary_tables()
        {
            WithSeededFtsRepository(DatabaseType.SQLite, (repository, connection) =>
            {
                var terms = new[] { "cafe", "café", "Café", "0.5", "0 5", "ready-player", "ready player", "がとう", "かとう" };
                var method = typeof(EditionFtsRepository).GetMethod("PrepareSqliteQueryTermKeys", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                Assert.That(method, Is.Not.Null);
                RefreshProjections(connection);
                var trace = new List<EditionFtsTraceEvent>();
                IReadOnlyDictionary<string, string> Prepare() => (IReadOnlyDictionary<string, string>)method.Invoke(null,
                    new object[] { connection, terms, (Action<EditionFtsTraceEvent>)trace.Add });
                var schemaBefore = connection.Query<string>("SELECT name FROM main.sqlite_schema ORDER BY name;").ToList();
                var dataBefore = connection.Query<string>("SELECT Name FROM Authors ORDER BY Id;").ToList();
                var keys = Prepare();
                Assert.Multiple(() =>
                {
                    Assert.That(keys["cafe"], Is.EqualTo(keys["café"]).And.EqualTo(keys["Café"]));
                    Assert.That(keys["0.5"], Is.Not.EqualTo(keys["0 5"]));
                    Assert.That(keys["ready-player"], Is.Not.EqualTo(keys["ready player"]));
                    Assert.That(keys["がとう"], Is.Not.EqualTo(keys["かとう"]));
                    Assert.That(terms.DistinctBy(term => keys[term], StringComparer.Ordinal),
                        Is.EqualTo(new[] { "cafe", "0.5", "0 5", "ready-player", "ready player", "がとう", "かとう" }));
                    Assert.That(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM temp.sqlite_schema WHERE name LIKE 'query_terms_%';"), Is.Zero);
                    Assert.That(trace.Count(entry => entry.Step == "sqlite_query_tokenization"), Is.EqualTo(1));
                });
                connection.Execute("BEGIN;");
                try
                {
                    Prepare();
                }
                finally
                {
                    connection.Execute("ROLLBACK;");
                }
                Assert.Multiple(() =>
                {
                    Assert.That(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM temp.sqlite_schema WHERE name LIKE 'query_terms_%';"), Is.Zero);
                    Assert.That(connection.Query<string>("SELECT name FROM main.sqlite_schema ORDER BY name;"), Is.EqualTo(schemaBefore));
                    Assert.That(connection.Query<string>("SELECT Name FROM Authors ORDER BY Id;"), Is.EqualTo(dataBefore));
                });
            });
        }

        [TestCase(DatabaseType.SQLite)]
        [TestCase(DatabaseType.PostgreSQL)]
        public void folded_index_should_recall_ascii_queries_for_stored_metadata(DatabaseType databaseType)
        {
            WithSeededFtsRepository(databaseType, (repository, connection) =>
            {
                connection.Execute("UPDATE \"Authors\" SET \"Name\" = 'Søren Reader'; UPDATE \"Books\" SET \"SeriesName\" = 'Søren Archive';");
                if (databaseType == DatabaseType.PostgreSQL)
                {
                    connection.Execute("UPDATE \"Authors\" SET \"CleanName\" = 'sorenreader', \"TitleSlug\" = 'neutral';");
                }
                else
                {
                    repository.RebuildIndex();
                }

                RefreshProjections(connection);
                if (databaseType == DatabaseType.SQLite) repository.RebuildIndex();
                Assert.That(repository.RecallBooks(null, new[] { "Soren" }, BookMediaType.Ebook).Select(book => book.BookId),
                    Is.EquivalentTo(connection.Query<int>("SELECT \"Id\" FROM \"Books\" WHERE \"MediaType\"=1;")));
            });
        }

        [TestCase(DatabaseType.SQLite)]
        [TestCase(DatabaseType.PostgreSQL)]
        public void mixed_numeric_query_alternatives_should_keep_alphanumeric_boundaries(DatabaseType databaseType)
        {
            WithSeededFtsRepository(databaseType, (repository, connection) =>
            {
                foreach (var (id, title) in new[] { (11, "Ready②"), (22, "Ready 2") })
                {
                    connection.Execute("UPDATE \"Editions\" SET \"Title\" = @title, \"MatchingTitle\" = @key WHERE \"Id\" = @id;",
                        new { id, title, key = StringSuperNormalizer.ComputeMatchingTitle(title) });
                }
                RefreshProjections(connection);
                if (databaseType == DatabaseType.SQLite)
                {
                    repository.RebuildIndex();
                }

                var result = repository.RecallBooks(null, new[] { "Ready②" }, BookMediaType.Ebook);
                Assert.That(result.Select(book => book.BookId), Is.EqualTo(new[] { 1 }));
                var ranked = repository.RankEditions(new[]
                {
                    new BookFtsMatch { BookId = 1, AuthorId = 1, MatchScore = 1 },
                    new BookFtsMatch { BookId = 2, AuthorId = 1, MatchScore = 1 }
                }, new[]
                {
                    new EditionFtsFieldQuery { Key = "TITLE[0]", ResidualValue = "Ready②", Terms = new[] { "Ready②" }, SourceFields = new[] { "TITLE[0]" } }
                }, BookMediaType.Ebook);
                Assert.Multiple(() =>
                {
                    Assert.That(ranked.Single(edition => edition.BookId == 1).Stage2TitleScore, Is.GreaterThan(0));
                    Assert.That(ranked.Single(edition => edition.BookId == 2).Stage2TitleScore, Is.Zero);
                });
            });
        }

        [TestCase(DatabaseType.SQLite)]
        [TestCase(DatabaseType.PostgreSQL)]
        public void original_spelling_recall_should_use_one_indexed_query_among_five_thousand_unrelated_rows(DatabaseType databaseType)
        {
            var sqlitePlans = new List<List<string>>();
            void ObserveRecall(IDbCommand command)
            {
                if (databaseType != DatabaseType.SQLite || !command.CommandText.Contains("WITH fts_matches AS MATERIALIZED", StringComparison.Ordinal))
                {
                    return;
                }
                using var explain = new SqliteCommand("EXPLAIN QUERY PLAN " + command.CommandText, (SqliteConnection)command.Connection);
                foreach (SqliteParameter parameter in command.Parameters)
                {
                    explain.Parameters.AddWithValue(parameter.ParameterName, parameter.Value);
                }
                using var reader = explain.ExecuteReader();
                var details = new List<string>();
                while (reader.Read())
                {
                    details.Add(reader.GetString(3));
                }
                sqlitePlans.Add(details);
            }
            void AssertIndexedRecall(List<string> plan)
            {
                Assert.That(plan.Any(detail => detail.Contains("edition_fts VIRTUAL TABLE INDEX", StringComparison.Ordinal)), Is.True);
                Assert.That(plan.Any(detail => detail.StartsWith("SEARCH b ", StringComparison.Ordinal) && detail.Contains("USING", StringComparison.Ordinal)), Is.True);
                Assert.That(plan.Any(detail => detail.StartsWith("SEARCH e ", StringComparison.Ordinal) && detail.Contains("USING", StringComparison.Ordinal)), Is.True);
            }
            WithSeededFtsRepository(databaseType, (repository, connection) =>
            {
                var numbers = databaseType == DatabaseType.SQLite
                    ? "WITH RECURSIVE numbers(n) AS (SELECT 100 UNION ALL SELECT n + 1 FROM numbers WHERE n < 5099) "
                    : "WITH numbers(n) AS (SELECT generate_series(100, 5099)) ";
                var authorColumns = databaseType == DatabaseType.SQLite ? string.Empty : ", \"CleanName\", \"TitleSlug\"";
                var authorValues = databaseType == DatabaseType.SQLite ? string.Empty : ", 'neutralauthor' || CAST(n AS TEXT), 'neutral-' || CAST(n AS TEXT)";
                connection.Execute(numbers + "INSERT INTO \"Authors\" (\"Id\", \"Name\"" + authorColumns + ") SELECT n, 'Neutral Author ' || CAST(n AS TEXT)" + authorValues + " FROM numbers;");
                connection.Execute(numbers + "INSERT INTO \"Books\" (\"Id\", \"AuthorId\", \"Title\", \"MediaType\") SELECT n, n, 'Neutral Work ' || CAST(n AS TEXT), 1 FROM numbers;");
                connection.Execute(numbers + "INSERT INTO \"Editions\" (\"Id\", \"BookId\", \"Title\", \"MatchingTitle\", \"NarratorNames\", \"Images\") SELECT n + 10000, n, 'Neutral Work ' || CAST(n AS TEXT), 'neutral work ' || CAST(n AS TEXT), '[]', '[]' FROM numbers;");
                connection.Execute("UPDATE \"Books\" SET \"SeriesName\" = 'Søren Archive' WHERE \"Id\" = 1;");
                RefreshProjections(connection);
                if (databaseType == DatabaseType.SQLite)
                {
                    repository.RebuildIndex();
                    connection.Execute("CREATE INDEX IX_Books_AuthorId ON Books(AuthorId); " +
                        "CREATE INDEX IX_Editions_BookId_Monitored_Id ON Editions(BookId, Monitored, Id); " +
                        "ANALYZE Books; ANALYZE Editions; ANALYZE Authors;");
                }
                else
                {
                    connection.Execute(@"
                        CREATE INDEX fixture_series_fts ON ""Books"" USING GIN (to_tsvector('simple', COALESCE(""SearchSeriesName"", '')));
                        CREATE INDEX fixture_title_fts ON ""Editions"" USING GIN (to_tsvector('simple', COALESCE(""MatchingTitle"", '')));
                        CREATE INDEX fixture_authors_fts ON ""Authors"" USING GIN (to_tsvector('simple', COALESCE(""SearchName"", '') || ' ' || COALESCE(""CleanName"", '') || ' ' || COALESCE(""TitleSlug"", '')));
                        ANALYZE ""Books""; ANALYZE ""Editions""; ANALYZE ""Authors"";");
                    var plan = connection.Query<string>("EXPLAIN (COSTS false) SELECT \"Id\" FROM \"Books\" WHERE to_tsvector('simple', COALESCE(\"SearchSeriesName\", '')) @@ to_tsquery('simple', 'søren');");
                    Assert.That(string.Join("\n", plan), Does.Contain("fixture_series_fts"));
                }

                RefreshProjections(connection);
                var trace = new List<EditionFtsTraceEvent>();
                var timer = System.Diagnostics.Stopwatch.StartNew();
                var recalled = repository.RecallBooks(null, new[] { "Søren" }, BookMediaType.Ebook, trace.Add);
                timer.Stop();
                Assert.Multiple(() =>
                {
                    Assert.That(recalled.Select(book => book.BookId), Is.EqualTo(new[] { 1 }));
                    Assert.That(trace.Count(entry => entry.EventType == "query" && entry.Step == "stage1_book_recall"), Is.EqualTo(1));
                });
                if (databaseType == DatabaseType.SQLite)
                {
                    Assert.That(sqlitePlans, Has.Count.EqualTo(1));
                    AssertIndexedRecall(sqlitePlans.Single());
                    TestContext.Progress.WriteLine("SQLite unscoped production plan:\n" + string.Join("\n", sqlitePlans.Single()));
                    var scopedTrace = new List<EditionFtsTraceEvent>();
                    var scoped = repository.RecallBooks(1, new[] { "Søren" }, BookMediaType.Ebook, scopedTrace.Add);
                    Assert.That(scoped.Select(book => book.BookId), Is.EqualTo(new[] { 1 }));
                    Assert.That(scopedTrace.Count(entry => entry.EventType == "query" && entry.Step == "stage1_book_recall"), Is.EqualTo(1));
                    Assert.That(sqlitePlans, Has.Count.EqualTo(2));
                    AssertIndexedRecall(sqlitePlans.Last());
                    TestContext.Progress.WriteLine("SQLite scoped production plan:\n" + string.Join("\n", sqlitePlans.Last()));
                }
                TestContext.Progress.WriteLine($"{databaseType}: 5,000 unrelated rows, one FTS query per recall, instrumented elapsed (includes SQLite EXPLAIN): {timer.Elapsed.TotalMilliseconds:F3} ms.");
            }, ObserveRecall);
        }

        [Test]
        public void recall_and_rank_should_fold_numeric_titles_without_merging_ready_player_siblings()
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
        public void postgres_recall_and_rank_should_fold_numeric_titles_without_merging_ready_player_siblings()
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
                var halfQuery = (string)queryBuilder.Invoke(null, new object[] { new[] { "½" }, false });
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

        [Test]
        public void native_contentless_row_replacement_should_preserve_statistics_scores_and_order()
        {
            using var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            connection.Execute("CREATE VIRTUAL TABLE stable_stats USING fts5(Body, content='', contentless_delete=1);");
            var documents = new[] { "apple banana", "banana cherry cherry cherry", "banana cherry", "date elderberry fig grape", "apple cherry cherry" };
            string statistics = null;
            (long Id, double Score)[] baseline = null;
            for (var round = 0; round < 4; round++)
            {
                using (var transaction = connection.BeginTransaction())
                {
                    for (var index = 0; index < documents.Length; index++)
                    {
                        if (round > 0) connection.Execute("DELETE FROM stable_stats WHERE rowid=@Id;", new { Id = index + 1 }, transaction);
                        connection.Execute("INSERT INTO stable_stats(rowid, Body) VALUES(@Id, @Body);", new { Id = index + 1, Body = documents[index] }, transaction);
                    }
                    connection.Execute("INSERT INTO stable_stats(stable_stats) VALUES('optimize');", transaction: transaction);
                    transaction.Commit();
                }
                var currentStatistics = connection.ExecuteScalar<string>("SELECT hex(block) FROM stable_stats_data WHERE id=1;");
                var rows = connection.Query<(long Id, double Score)>("SELECT rowid, bm25(stable_stats) AS Score FROM stable_stats WHERE stable_stats MATCH 'apple OR banana OR cherry' ORDER BY Score, rowid;").ToArray();
                statistics ??= currentStatistics;
                baseline ??= rows;
                Assert.Multiple(() =>
                {
                    Assert.That(currentStatistics, Is.EqualTo(statistics));
                    Assert.That(rows, Is.EqualTo(baseline));
                    Assert.That(rows.Select(row => row.Id), Is.EqualTo(new long[] { 1, 5, 2, 3 }));
                    Assert.That(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM stable_stats;"), Is.EqualTo(5));
                });
            }
            TestContext.Progress.WriteLine("Loaded native SQLite " + connection.ExecuteScalar<string>("SELECT sqlite_version();") + ": four unchanged-content replacement rounds preserve totals, scores and ordered hit set.");
        }

        private static void RefreshProjections(System.Data.IDbConnection connection)
        {
            using var transaction = connection.BeginTransaction();
            foreach (var author in connection.Query<Author>("SELECT * FROM \"Authors\";", transaction: transaction))
            {
                author.PrepareForPersistence(null).ToArray();
                connection.Execute("UPDATE \"Authors\" SET \"SearchName\"=@SearchName WHERE \"Id\"=@Id;", author, transaction);
            }
            foreach (var book in connection.Query<Book>("SELECT * FROM \"Books\";", transaction: transaction))
            {
                book.PrepareForPersistence(null).ToArray();
                connection.Execute("UPDATE \"Books\" SET \"SearchSeriesName\"=@SearchSeriesName WHERE \"Id\"=@Id;", book, transaction);
            }
            foreach (var edition in connection.Query<Edition>("SELECT * FROM \"Editions\";", transaction: transaction))
            {
                edition.PrepareForPersistence(null).ToArray();
                connection.Execute("UPDATE \"Editions\" SET \"SearchSubtitle\"=@SearchSubtitle, \"SearchNarrator\"=@SearchNarrator, \"SearchNarratorNames\"=@SearchNarratorNames, \"SearchPublisher\"=@SearchPublisher WHERE \"Id\"=@Id;", edition, transaction);
            }
            transaction.Commit();
        }

        private static void WithSeededFtsRepository(DatabaseType databaseType, Action<EditionFtsRepository, System.Data.IDbConnection> action, Action<IDbCommand> observeCommand = null)
        {
            if (databaseType == DatabaseType.SQLite)
            {
                var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"raw_fts_{Guid.NewGuid():N}.db");
                var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
                try
                {
                    SeedDatabase(connectionString);
                    var database = new Database("main", () =>
                    {
                        var connection = new SqliteConnection(connectionString);
                        connection.Open();
                        return connection;
                    });
                    using var connection = new SqliteConnection(connectionString);
                    connection.Open();
                    connection.Execute(@"
                        DROP TABLE edition_fts;
                        CREATE VIRTUAL TABLE edition_fts USING fts5(
                            MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator, Publisher,
                            content='', content_rowid='rowid', contentless_delete=1,
                            tokenize = 'unicode61 remove_diacritics 1 tokenchars ''-.''');");
                    var observedDatabase = observeCommand == null ? (IDatabase)database : InterfaceProxy.Proxy<IDatabase>((method, args) =>
                        method.Name == nameof(IDatabase.OpenConnection)
                            ? CommandObserver.Wrap<IDbConnection>(database.OpenConnection(), observeCommand)
                            : method.Invoke(database, args));
                    action(new EditionFtsRepository(new MainDatabase(observedDatabase), LogManager.GetCurrentClassLogger()), connection);
                }
                finally
                {
                    File.Delete(path);
                }
                return;
            }

            var adminConnectionString = Environment.GetEnvironmentVariable("CHAPTARR_TEST_POSTGRES_CONNECTION_STRING");
            if (string.IsNullOrWhiteSpace(adminConnectionString))
            {
                Assert.Ignore("Set CHAPTARR_TEST_POSTGRES_CONNECTION_STRING to exercise PostgreSQL edition FTS");
            }
            var schema = "raw_fts_" + Guid.NewGuid().ToString("N");
            using (var admin = new NpgsqlConnection(adminConnectionString))
            {
                admin.Open();
                admin.Execute($"CREATE SCHEMA \"{schema}\"");
            }
            try
            {
                var connectionString = new NpgsqlConnectionStringBuilder(adminConnectionString) { SearchPath = schema, Pooling = false }.ConnectionString;
                SeedPostgresDatabase(connectionString);
                var database = new Database("postgres-raw-fts", () =>
                {
                    var connection = new NpgsqlConnection(connectionString);
                    connection.Open();
                    return connection;
                });
                using var connection = new NpgsqlConnection(connectionString);
                connection.Open();
                action(new EditionFtsRepository(new MainDatabase(database), LogManager.GetCurrentClassLogger()), connection);
            }
            finally
            {
                using var admin = new NpgsqlConnection(adminConnectionString);
                admin.Open();
                admin.Execute($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
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
                    ""Monitored"" BOOLEAN NOT NULL DEFAULT FALSE,
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
            connection.Execute(@"
                ALTER TABLE ""Authors"" ADD COLUMN ""SearchName"" TEXT;
                ALTER TABLE ""Books"" ADD COLUMN ""SearchSeriesName"" TEXT;
                ALTER TABLE ""Editions"" ADD COLUMN ""SearchSubtitle"" TEXT;
                ALTER TABLE ""Editions"" ADD COLUMN ""SearchNarrator"" TEXT;
                ALTER TABLE ""Editions"" ADD COLUMN ""SearchNarratorNames"" TEXT;
                ALTER TABLE ""Editions"" ADD COLUMN ""SearchPublisher"" TEXT;");
            RefreshProjections(connection);
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
            connection.Execute(@"
                ALTER TABLE ""Authors"" ADD COLUMN ""SearchName"" TEXT;
                ALTER TABLE ""Books"" ADD COLUMN ""SearchSeriesName"" TEXT;
                ALTER TABLE ""Editions"" ADD COLUMN ""SearchSubtitle"" TEXT;
                ALTER TABLE ""Editions"" ADD COLUMN ""SearchNarrator"" TEXT;
                ALTER TABLE ""Editions"" ADD COLUMN ""SearchNarratorNames"" TEXT;
                ALTER TABLE ""Editions"" ADD COLUMN ""SearchPublisher"" TEXT;");
            RefreshProjections(connection);
        }
    }
}
