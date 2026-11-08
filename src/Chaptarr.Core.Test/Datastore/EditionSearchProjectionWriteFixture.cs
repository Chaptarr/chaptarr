using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using NzbDrone.Common.Messaging;
using NLog;
using NLog.Config;
using NLog.Targets;
using Npgsql;
using NUnit.Framework;
using Chaptarr.Core.Test.Common;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Extensions;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration.Framework;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    [NonParallelizable]
    public class EditionSearchProjectionWriteFixture
    {
        private sealed class StubEvents : IEventAggregator
        {
            public void PublishEvent<TEvent>(TEvent value) where TEvent : class, IEvent { }
        }

        // Runtime repositories require the current schema; historical migration tests below keep explicit versions.
        private sealed class Writer<T> : BasicRepository<T> where T : ModelBase, new()
        {
            public Writer(IDatabase database) : base(database, new StubEvents()) { }
            public void UpdateInTransaction(IList<T> models, IDbConnection connection, IDbTransaction transaction) => UpdateMany(models, connection, transaction);
        }

        [OneTimeSetUp]
        public void MapModels()
        {
            if (TableMapping.Mapper.TableMap.Count == 0) TableMapping.Map();
        }

        [TestCase(DatabaseType.SQLite)]
        [TestCase(DatabaseType.PostgreSQL)]
        public void canonical_writers_should_keep_folded_index_and_raw_metadata_coherent(DatabaseType type)
        {
            WithDatabase(type, int.MaxValue, (connection, database, migrate) =>
            {
                var authors = new Writer<Author>(database);
                var books = new Writer<Book>(database);
                var editions = new Writer<Edition>(database);
                var author = new Author { Name = "Søren Reader", CleanName = "sorenreader", Path = "/fixture", Added = DateTime.UtcNow };
                authors.Insert(author);
                var book = new Book { AuthorId = author.Id, Title = "Control Story 2", CleanTitle = "controlstory2", SeriesName = "Łukasz Archive", MediaType = BookMediaType.Audiobook, Added = DateTime.UtcNow };
                books.Insert(book);
                var edition = new Edition { BookId = book.Id, Title = book.Title, MatchingTitle = StringSuperNormalizer.ComputeMatchingTitle(book.Title), Subtitle = "Ælfric Notes", Publisher = "Straße Press", Narrator = "Legacy Voice", NarratorNames = new List<string> { "Королёв Voice", "Ὀδυσσεύς Reader" } };
                editions.Insert(edition);
                var fts = new EditionFtsRepository(new MainDatabase(database), LogManager.GetCurrentClassLogger());
                Assert.That(fts.RecallBooks(null, new[] { "Soren" }, BookMediaType.Audiobook).Select(row => row.BookId), Is.EqualTo(new[] { book.Id }));
                Assert.That(fts.RecallBooks(null, new[] { "Lukasz" }, BookMediaType.Audiobook).Select(row => row.BookId), Is.EqualTo(new[] { book.Id }));
                Assert.That(fts.RecallBooks(null, new[] { "Soren" }, BookMediaType.Ebook), Is.Empty);
                Assert.That(DetailHit(fts, book, "Королев"), Is.True);
                Assert.That(DetailHit(fts, book, "Οδυσσευσ"), Is.True);
                Assert.That(connection.ExecuteScalar<string>("SELECT \"SearchSubtitle\" FROM \"Editions\" WHERE \"Id\"=@Id", edition), Is.EqualTo("aelfric notes"));
                Assert.That(System.Text.Json.JsonSerializer.Deserialize<string[]>(connection.ExecuteScalar<string>("SELECT \"NarratorNames\" FROM \"Editions\" WHERE \"Id\"=@Id", edition)), Is.EqualTo(edition.NarratorNames));

                var originalCast = fts.RankEditions(new[] { new BookFtsMatch { BookId = book.Id, AuthorId = book.AuthorId, MatchScore = 1 } },
                    new[] { new EditionFtsFieldQuery { Key = "DETAIL[0]", Terms = new[] { "Королев" }, SourceFields = new[] { "DETAIL[0]" } } }, book.MediaType).Single().NarratorNames;

                author.Name = "Ælfric Reader";
                authors.SetFields(author, row => row.Name);
                book.SeriesName = "Straße Archive";
                books.SetFields(new List<Book> { book }, row => row.SeriesName);
                edition.NarratorNames.Add("Søren Newvoice");
                editions.SetFields(edition, row => row.NarratorNames);
                Assert.That(fts.RecallBooks(null, new[] { "Soren" }, BookMediaType.Audiobook), Is.Empty);
                Assert.That(fts.RecallBooks(null, new[] { "Aelfric" }, BookMediaType.Audiobook).Single().BookId, Is.EqualTo(book.Id));
                Assert.That(fts.RecallBooks(null, new[] { "Strasse" }, BookMediaType.Audiobook).Single().BookId, Is.EqualTo(book.Id));
                Assert.That(DetailHit(fts, book, "Soren"), Is.True);
                Assert.That(DetailHit(fts, book, "Королев"), Is.True);

                var oldProjection = connection.ExecuteScalar<string>("SELECT \"SearchNarratorNames\" FROM \"Editions\" WHERE \"Id\"=@Id", edition);
                edition.NarratorNames.Clear();
                edition.Narrator = "Unsaved Voice";
                editions.SetFields(edition, row => row.Monitored);
                Assert.That(connection.ExecuteScalar<string>("SELECT \"SearchNarratorNames\" FROM \"Editions\" WHERE \"Id\"=@Id", edition), Is.EqualTo(oldProjection));
                Assert.That(DetailHit(fts, book, "Soren"), Is.True);
                var partialLegacy = new Edition { Id = edition.Id, Narrator = "Ælfric Legacy" };
                editions.SetFields(partialLegacy, row => row.Narrator);
                Assert.That(DetailHit(fts, book, "Soren"), Is.True);
                Assert.That(DetailHit(fts, book, "Aelfric"), Is.False);
                var clearCast = new Edition { Id = edition.Id, NarratorNames = new List<string> { " " } };
                editions.SetFields(clearCast, row => row.NarratorNames);
                Assert.That(DetailHit(fts, book, "Aelfric"), Is.True);
                var returned = fts.RankEditions(new[] { new BookFtsMatch { BookId = book.Id, AuthorId = book.AuthorId, MatchScore = 1 } },
                    new[] { new EditionFtsFieldQuery { Key = "DETAIL[0]", Terms = new[] { "Aelfric" }, SourceFields = new[] { "DETAIL[0]" } } }, book.MediaType).Single();
                Assert.Multiple(() =>
                {
                    Assert.That(originalCast, Is.EqualTo("Королёв Voice; Ὀδυσσεύς Reader"));
                    Assert.That(returned.NarratorNames, Is.EqualTo("Ælfric Legacy"));
                });
                Assert.That(DetailHit(fts, book, "Soren"), Is.False);
                edition.Narrator = "Łukasz Legacy";
                editions.UpdateMany(new List<Edition> { edition });
                Assert.That(DetailHit(fts, book, "Lukasz"), Is.True);
                Assert.That(DetailHit(fts, book, "Soren"), Is.False);

                using (var transaction = connection.BeginTransaction())
                {
                    author.Name = "Søren Rolledback";
                    authors.UpdateInTransaction(new List<Author> { author }, connection, transaction);
                    Assert.That(connection.ExecuteScalar<string>("SELECT \"SearchName\" FROM \"Authors\" WHERE \"Id\"=@Id", author, transaction), Is.EqualTo("soren rolledback"));
                    transaction.Rollback();
                }
                Assert.That(fts.RecallBooks(null, new[] { "Aelfric" }, BookMediaType.Audiobook).Single().BookId, Is.EqualTo(book.Id));
                Assert.That(connection.ExecuteScalar<string>("SELECT \"Name\" FROM \"Authors\" WHERE \"Id\"=@Id", author), Is.EqualTo("Ælfric Reader"));

                book.MediaType = BookMediaType.Ebook;
                books.SetFields(book, row => row.MediaType);
                edition.Publisher = "Søren Press";
                editions.SetFields(edition, row => row.Publisher);
                Assert.That(DetailHit(fts, book, "Soren"), Is.True);
                var before = fts.RecallBooks(null, new[] { "Aelfric", "Strasse" }, BookMediaType.Ebook).Single().MatchScore;
                fts.RebuildIndex();
                Assert.That(fts.RecallBooks(null, new[] { "Aelfric", "Strasse" }, BookMediaType.Ebook).Single().MatchScore, Is.EqualTo(before));
                Assert.That(DetailHit(fts, book, "Soren"), Is.True);
                editions.Delete(edition.Id);
                Assert.That(fts.RecallBooks(null, new[] { "Control" }, BookMediaType.Ebook), Is.Empty);
            });
        }

        [TestCase(DatabaseType.SQLite, 112)]
        [TestCase(DatabaseType.PostgreSQL, 112)]
        [TestCase(DatabaseType.SQLite, 114)]
        [TestCase(DatabaseType.PostgreSQL, 114)]
        public void upgrade_should_backfill_complete_casts_preserve_sources_and_be_idempotent(DatabaseType type, int baseline)
        {
            WithDatabase(type, baseline, (connection, database, migrate) =>
            {
                var identity = type == DatabaseType.PostgreSQL ? "OVERRIDING SYSTEM VALUE" : string.Empty;
                connection.Execute($@"INSERT INTO ""Authors"" (""Id"", ""Name"", ""CleanName"", ""Path"", ""Monitored"", ""Added"") {identity} VALUES(1,'Søren Reader','sorenreader','/fixture',@Yes,@Now);
                    INSERT INTO ""Books"" (""Id"", ""AuthorId"", ""Title"", ""CleanTitle"", ""SeriesName"", ""MediaType"", ""Added"", ""AnyEditionOk"") {identity} VALUES(1,1,'KING ARTHUR’S COURT','stale','Łukasz Archive',0,@Now,@Yes);
                    INSERT INTO ""Editions"" (""Id"", ""BookId"", ""Title"", ""MatchingTitle"", ""NarratorNames"", ""Narrator"", ""Subtitle"", ""Publisher"", ""Monitored"") {identity} VALUES(1,1,'KING ARTHUR’S COURT','stale',@Cast,'Legacy Voice','Ælfric Notes','Straße Press',@Yes);",
                    new { Yes = true, Now = DateTime.UtcNow, Cast = System.Text.Json.JsonSerializer.Serialize(new[] { "Søren Voice", "Королёв Reader", "Ὀδυσσεύς Reader", new string('a', 600) }) });
                var source = connection.ExecuteScalar<string>("SELECT \"NarratorNames\" FROM \"Editions\" WHERE \"Id\"=1");
                migrate(115);
                var fts = new EditionFtsRepository(new MainDatabase(database), LogManager.GetCurrentClassLogger());
                Assert.That(fts.RecallBooks(null, new[] { "Soren" }, BookMediaType.Audiobook).Single().BookId, Is.EqualTo(1));
                Assert.That(fts.RecallBooks(null, new[] { "Lukasz" }, BookMediaType.Audiobook).Single().BookId, Is.EqualTo(1));
                Assert.That(DetailHit(fts, new Book { Id = 1, MediaType = BookMediaType.Audiobook }, "Королев"), Is.True);
                Assert.That(connection.ExecuteScalar<string>("SELECT \"MatchingTitle\" FROM \"Editions\" WHERE \"Id\"=1"), Is.EqualTo("king arthurs court"));
                Assert.That(connection.ExecuteScalar<string>("SELECT \"SearchNarratorNames\" FROM \"Editions\" WHERE \"Id\"=1"), Has.Length.GreaterThan(600));
                Assert.That(connection.ExecuteScalar<string>("SELECT \"NarratorNames\" FROM \"Editions\" WHERE \"Id\"=1"), Is.EqualTo(source));
                var before = connection.ExecuteScalar<string>("SELECT \"SearchNarratorNames\" FROM \"Editions\" WHERE \"Id\"=1");
                migrate(115);
                Assert.That(connection.ExecuteScalar<string>("SELECT \"SearchNarratorNames\" FROM \"Editions\" WHERE \"Id\"=1"), Is.EqualTo(before));
                Assert.That(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"VersionInfo\" WHERE \"Version\"=115"), Is.EqualTo(1));
            });
        }

        [TestCase(DatabaseType.SQLite)]
        [TestCase(DatabaseType.PostgreSQL)]
        public void bulk_writers_should_preserve_batching_and_transaction_rollback(DatabaseType type)
        {
            WithDatabase(type, int.MaxValue, (connection, database, migrate) =>
            {
                var authors = new Writer<Author>(database);
                var rows = Enumerable.Range(1, 1200).Select(index => new Author { Name = "Søren Reader " + index, CleanName = "reader" + index, Path = "/fixture/" + index, Added = DateTime.UtcNow }).ToList();
                var stopwatch = Stopwatch.StartNew();
                authors.InsertMany(rows);
                Assert.That(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"Authors\" WHERE \"SearchName\" LIKE 'soren reader %'"), Is.EqualTo(1200));
                foreach (var row in rows) row.Name = "Łukasz Reader " + row.Id;
                authors.SetFields(rows, row => row.Name);
                Assert.That(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"Authors\" WHERE \"SearchName\" LIKE 'lukasz reader %'"), Is.EqualTo(1200));
                using (var transaction = connection.BeginTransaction())
                {
                    var inserted = new List<Author> { new Author { Name = "Ælfric Temporary", CleanName = "temporary", Path = "/fixture/temp", Added = DateTime.UtcNow } };
                    authors.InsertMany(inserted, connection, transaction);
                    Assert.That(connection.ExecuteScalar<string>("SELECT \"SearchName\" FROM \"Authors\" WHERE \"Id\"=@Id", inserted.Single(), transaction), Is.EqualTo("aelfric temporary"));
                    transaction.Rollback();
                }
                Assert.That(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"Authors\""), Is.EqualTo(1200));
                TestContext.Progress.WriteLine($"{type}: 1200 normal bulk inserts and 1200 selective source updates; caller-owned insert rollback preserved; elapsed {stopwatch.ElapsedMilliseconds} ms.");
            });
        }

        [TestCase(DatabaseType.SQLite)]
        [TestCase(DatabaseType.PostgreSQL)]
        public void failed_backfill_should_roll_back_schema_keys_and_version(DatabaseType type)
        {
            WithDatabase(type, 114, (connection, database, migrate) =>
            {
                var identity = type == DatabaseType.PostgreSQL ? "OVERRIDING SYSTEM VALUE" : string.Empty;
                connection.Execute($@"INSERT INTO ""Authors"" (""Id"", ""Name"", ""CleanName"", ""Path"", ""Monitored"", ""Added"") {identity} VALUES(1,'Søren Reader','sorenreader','/fixture',@Yes,@Now)",
                    new { Yes = true, Now = DateTime.UtcNow });
                if (type == DatabaseType.SQLite)
                {
                    connection.Execute("CREATE TRIGGER fixture_reject BEFORE UPDATE ON Authors BEGIN SELECT RAISE(ABORT, 'fixture rollback'); END;");
                }
                else
                {
                    connection.Execute(@"CREATE FUNCTION fixture_reject_update() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'fixture rollback'; END; $$;
                        CREATE TRIGGER fixture_reject BEFORE UPDATE ON ""Authors"" FOR EACH ROW EXECUTE FUNCTION fixture_reject_update();");
                }

                Assert.That(() => migrate(115), Throws.Exception);
                var columns = type == DatabaseType.SQLite
                    ? connection.ExecuteScalar<int>("SELECT COUNT(*) FROM pragma_table_info('Authors') WHERE name='SearchName';")
                    : connection.ExecuteScalar<int>("SELECT COUNT(*) FROM information_schema.columns WHERE table_name='Authors' AND column_name='SearchName';");
                Assert.Multiple(() =>
                {
                    Assert.That(columns, Is.Zero);
                    Assert.That(connection.ExecuteScalar<int>("SELECT MAX(\"Version\") FROM \"VersionInfo\""), Is.EqualTo(114));
                    Assert.That(connection.ExecuteScalar<string>("SELECT \"Name\" FROM \"Authors\" WHERE \"Id\"=1"), Is.EqualTo("Søren Reader"));
                    Assert.That(connection.ExecuteScalar<string>("SELECT \"CleanName\" FROM \"Authors\" WHERE \"Id\"=1"), Is.EqualTo("sorenreader"));
                });
            });
        }

        [TestCase(DatabaseType.SQLite)]
        [TestCase(DatabaseType.PostgreSQL)]
        public void projection_backfill_should_use_bounded_pages(DatabaseType type)
        {
            WithDatabase(type, 114, (connection, database, migrate) =>
            {
                var identity = type == DatabaseType.PostgreSQL ? "OVERRIDING SYSTEM VALUE" : string.Empty;
                connection.Execute($@"WITH RECURSIVE ids(id) AS (SELECT 1 UNION ALL SELECT id+1 FROM ids WHERE id<1200)
                    INSERT INTO ""Authors"" (""Id"", ""Name"", ""CleanName"", ""Path"", ""Monitored"", ""Added"") {identity}
                    SELECT id, 'Søren Reader ' || CAST(id AS TEXT), 'stale', '/fixture', @Yes, @Now FROM ids;", new { Yes = true, Now = DateTime.UtcNow });
                var logs = migrate(115);
                Assert.That(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"Authors\" WHERE \"SearchName\" LIKE 'soren reader %';"), Is.EqualTo(1200));
                Assert.That(logs, Has.Some.Contains("Folded search rebuild Authors: scanned 1200, updated rows 1200, read queries 3, update batches 3,"));
                foreach (var line in logs.Where(line => line.StartsWith("Folded search rebuild", StringComparison.Ordinal))) TestContext.Progress.WriteLine(type + " " + line);
            });
        }

        [TestCase(DatabaseType.SQLite)]
        [TestCase(DatabaseType.PostgreSQL)]
        public void edition_backfill_should_skip_old_incremental_rewrites_and_rebuild_once(DatabaseType type)
        {
            WithDatabase(type, 114, (connection, database, migrate) =>
            {
                const int count = 1200;
                var identity = type == DatabaseType.PostgreSQL ? "OVERRIDING SYSTEM VALUE" : string.Empty;
                connection.Execute($@"INSERT INTO ""Authors"" (""Id"", ""Name"", ""CleanName"", ""Path"", ""Monitored"", ""Added"") {identity} VALUES(1,'Søren Reader','sorenreader','/fixture',@Yes,@Now);
                    INSERT INTO ""Books"" (""Id"", ""AuthorId"", ""Title"", ""CleanTitle"", ""SeriesName"", ""MediaType"", ""Added"", ""AnyEditionOk"") {identity} VALUES(1,1,'Control Story','controlstory','Łukasz Archive',0,@Now,@Yes);
                    WITH RECURSIVE ids(id) AS (SELECT 1 UNION ALL SELECT id+1 FROM ids WHERE id<@Count)
                    INSERT INTO ""Editions"" (""Id"", ""BookId"", ""Title"", ""MatchingTitle"", ""NarratorNames"", ""Narrator"", ""Subtitle"", ""Publisher"", ""Monitored"") {identity}
                    SELECT id, 1, 'Control Story', 'control story', '[""Королёв Reader"",""Søren Voice""]', 'Legacy Voice', 'Ælfric Notes', 'Straße Press', @Yes FROM ids;",
                    new { Count = count, Yes = true, Now = DateTime.UtcNow });
                if (type == DatabaseType.SQLite)
                {
                    var trigger = connection.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type='trigger' AND name='edition_fts_au';");
                    connection.Execute("CREATE TABLE FixtureFtsRewrites (Id INTEGER); DROP TRIGGER edition_fts_au;" + trigger.Replace("BEGIN", "BEGIN INSERT INTO FixtureFtsRewrites VALUES(new.Id);"));
                    connection.Execute("UPDATE Editions SET Subtitle=Subtitle WHERE Id=1;");
                    Assert.That(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM FixtureFtsRewrites;"), Is.EqualTo(1));
                    connection.Execute("DELETE FROM FixtureFtsRewrites;");
                }

                var stopwatch = Stopwatch.StartNew();
                var logs = migrate(115);
                Assert.That(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"Editions\" WHERE \"SearchNarratorNames\"='королев reader soren voice';"), Is.EqualTo(count));
                var batchSize = SqliteVariableLimit.MaxParameters / 5;
                Assert.That(logs, Has.Some.Contains($"Folded search rebuild Editions: scanned {count}, updated rows {count}, read queries {(count / batchSize) + 1}, update batches {(int)Math.Ceiling(count / (double)batchSize)},"));
                if (type == DatabaseType.SQLite)
                {
                    Assert.That(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM FixtureFtsRewrites;"), Is.Zero);
                    Assert.That(logs.Count(line => line.StartsWith("Rebuilt folded SQLite search indexes", StringComparison.Ordinal)), Is.EqualTo(1));
                    Assert.That(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM edition_fts WHERE edition_fts MATCH 'Narrator:soren';"), Is.EqualTo(count));
                }
                foreach (var line in logs.Where(line => line.StartsWith("Folded search rebuild", StringComparison.Ordinal) || line.StartsWith("Rebuilt folded edition", StringComparison.Ordinal))) TestContext.Progress.WriteLine(type + " " + line);
                TestContext.Progress.WriteLine($"{type}: complete115 backfill/index transaction for {count} editions, elapsed {stopwatch.ElapsedMilliseconds} ms; raw metadata preserved.");
            });
        }

        [TestCase(114)]
        [TestCase(115)]
        public void actual_postgres_upgrade_should_install_folded_vector_indexes(int baseline)
        {
            WithDatabase(DatabaseType.PostgreSQL, baseline, (connection, _, migrate) =>
            {
                if (baseline == 114) migrate(115);
                var indexes = connection.Query<(string Name, string Definition)>(
                    "SELECT indexname, indexdef FROM pg_indexes WHERE schemaname='public';").ToDictionary(row => row.Name, row => row.Definition);
                Assert.Multiple(() =>
                {
                    Assert.That(indexes.GetValueOrDefault("idx_authors_fts"), Does.Contain("SearchName"));
                    Assert.That(indexes.GetValueOrDefault("idx_books_series_fts"), Does.Contain("SearchSeriesName"));
                    Assert.That(indexes.GetValueOrDefault("idx_editions_narrator_fts"), Does.Contain("SearchNarratorNames").And.Contains("SearchNarrator"));
                    Assert.That(indexes.GetValueOrDefault("idx_editions_publisher_fts"), Does.Contain("SearchPublisher"));
                    Assert.That(indexes.GetValueOrDefault("idx_editions_matching_title_fts"), Does.Contain("MatchingTitle"));
                    Assert.That(indexes.ContainsKey("idx_editions_matching_fts"), Is.False);
                    Assert.That(indexes.ContainsKey("idx_editions_ebook_fts"), Is.False);
                    Assert.That(indexes.ContainsKey("idx_editions_subtitle_fts"), Is.False);
                });
                foreach (var index in indexes.Where(pair => pair.Key.Contains("fts", StringComparison.Ordinal)))
                    TestContext.Progress.WriteLine($"NATIVE_FTS_INDEX {index.Key} {index.Value}");
            });
        }

        [Test]
        public void actual_postgres_recall_vectors_should_match_upgraded_indexes_and_remain_eligible()
        {
            WithDatabase(DatabaseType.PostgreSQL, int.MaxValue, (connection, database, _) =>
            {
                var author = new Writer<Author>(database).Insert(new Author { Name = "Søren Reader", CleanName = "sorenreader", Path = "/vectors", Added = DateTime.UtcNow });
                var books = new Writer<Book>(database);
                var editions = new Writer<Edition>(database);
                foreach (var media in new[] { BookMediaType.Audiobook, BookMediaType.Ebook })
                {
                    var book = books.Insert(new Book { AuthorId = author.Id, Title = "Vector Story", CleanTitle = "vectorstory", SeriesName = "Łukasz Archive", MediaType = media, Added = DateTime.UtcNow });
                    editions.Insert(new Edition { BookId = book.Id, Title = book.Title, MatchingTitle = "vector story", Publisher = "Straße Press", NarratorNames = new List<string> { "Ælfric Voice" } });
                }

                var commands = new List<string>();
                string plan = null;
                var observed = new Database("native-vector-parity", () => CommandObserver.Wrap(database.OpenConnection(), command =>
                {
                    commands.Add(command.CommandText);
                    if (!command.CommandText.Contains("WITH candidates AS", StringComparison.Ordinal)) return;
                    command.Connection.Execute("SET enable_seqscan=off; SET jit=off;");
                    using var explain = new NpgsqlCommand("EXPLAIN (FORMAT JSON) " + command.CommandText, (NpgsqlConnection)command.Connection);
                    foreach (NpgsqlParameter parameter in command.Parameters) explain.Parameters.Add(parameter.Clone());
                    plan = (string)explain.ExecuteScalar();
                }));
                using var factory = new LogFactory();
                var messages = new MemoryTarget { Layout = "${message}" };
                var configuration = new LoggingConfiguration(factory);
                configuration.AddRule(LogLevel.Debug, LogLevel.Fatal, messages);
                factory.Configuration = configuration;
                var fts = new EditionFtsRepository(new MainDatabase(observed), factory.GetLogger("native-vectors"));
                foreach (var media in new[] { BookMediaType.Audiobook, BookMediaType.Ebook })
                {
                    var recalls = fts.RecallBooks(null, new[] { "Soren", "Lukasz", "Vector" }, media);
                    Assert.That(recalls, Has.Count.EqualTo(1));
                    Assert.That(fts.RankEditions(recalls, new[] { new EditionFtsFieldQuery { Key = "DETAIL[0]", Terms = new[] { media == BookMediaType.Audiobook ? "Aelfric" : "Strasse" } } }, media), Has.Count.EqualTo(1));
                }

                var indexes = connection.Query<(string Name, string Expression)>(@"SELECT c.relname, pg_get_expr(i.indexprs, i.indrelid)
                    FROM pg_index i JOIN pg_class c ON c.oid=i.indexrelid WHERE c.relname IN
                    ('idx_authors_fts','idx_books_series_fts','idx_editions_matching_title_fts','idx_editions_narrator_fts','idx_editions_publisher_fts');");
                var matched = new HashSet<string>();
                string Canonical(string expression) => Regex.Replace(expression, @"[\s()]", "");
                foreach (var expression in commands.SelectMany(sql => Regex.Matches(sql,
                    @"to_tsvector\('simple', (?:[^()]|\((?:[^()]|\([^()]*\))*\))*\)").Select(match => match.Value)).Distinct())
                {
                    var alias = Regex.Match(expression, @"\b([abe])\.""[A-Za-z]+""").Groups[1].Value;
                    var table = alias == "a" ? "Authors" : alias == "b" ? "Books" : "Editions";
                    connection.Execute($"CREATE OR REPLACE TEMP VIEW fixture_vector AS SELECT {expression} AS value FROM \"{table}\" {alias};");
                    var view = connection.ExecuteScalar<string>("SELECT pg_get_viewdef('fixture_vector'::regclass);");
                    var rendered = view[(view.IndexOf("SELECT", StringComparison.Ordinal) + 6)..view.IndexOf(" AS value", StringComparison.Ordinal)].Replace(alias + ".", "");
                    foreach (var index in indexes.Where(index => Canonical(index.Expression) == Canonical(rendered))) matched.Add(index.Name);
                }

                Assert.That(matched, Is.EquivalentTo(indexes.Select(index => index.Name)));
                Assert.That(plan, Does.Contain("idx_authors_fts").And.Contains("idx_books_series_fts").And.Contains("idx_editions_matching_title_fts"));
                Assert.That(messages.Logs.Count(line => line.StartsWith("Edition FTS recall", StringComparison.Ordinal)), Is.EqualTo(2));
                Assert.That(messages.Logs.Count(line => line.StartsWith("Edition FTS rank", StringComparison.Ordinal)), Is.EqualTo(2));
                TestContext.Progress.WriteLine("NATIVE_RUNTIME_VECTOR_INDEX_PARITY " + string.Join(",", matched.OrderBy(name => name)));
                TestContext.Progress.WriteLine("NATIVE_RUNTIME_RECALL_PLAN " + plan);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void sqlite_migration_and_runtime_triggers_should_skip_irrelevant_or_equal_updates(bool runtimeRebuild)
        {
            WithDatabase(DatabaseType.SQLite, int.MaxValue, (connection, database, _) =>
            {
                var authors = new Writer<Author>(database);
                var books = new Writer<Book>(database);
                var editions = new Writer<Edition>(database);
                var author = authors.Insert(new Author { Name = "Native Reader", CleanName = "nativereader", Path = "/native", Added = DateTime.UtcNow });
                var book = books.Insert(new Book { AuthorId = author.Id, Title = "Native Story", CleanTitle = "nativestory", MediaType = BookMediaType.Ebook, Added = DateTime.UtcNow });
                var edition = editions.Insert(new Edition { BookId = book.Id, Title = book.Title, MatchingTitle = "native story", Publisher = "Original Press" });
                var fts = new EditionFtsRepository(new MainDatabase(database), LogManager.GetCurrentClassLogger());
                if (runtimeRebuild) fts.RebuildIndex();
                long UpdateCost(string sql)
                {
                    var before = connection.ExecuteScalar<long>("SELECT total_changes();");
                    connection.Execute(sql, new { edition.Id });
                    return connection.ExecuteScalar<long>("SELECT total_changes();") - before;
                }
                Assert.That(UpdateCost("UPDATE Editions SET Monitored=1 WHERE Id=@Id;"), Is.EqualTo(1));
                Assert.That(UpdateCost("UPDATE Editions SET MatchingTitle=MatchingTitle, SearchPublisher=SearchPublisher WHERE Id=@Id;"), Is.EqualTo(1));
                Assert.That(UpdateCost("UPDATE Editions SET SearchSubtitle=NULL WHERE Id=@Id;"), Is.EqualTo(1));
                Assert.That(UpdateCost("UPDATE Editions SET SearchSubtitle='' WHERE Id=@Id;"), Is.EqualTo(1));
                Assert.That(UpdateCost("UPDATE Editions SET SearchPublisher='changed press' WHERE Id=@Id;"), Is.GreaterThan(1));
                Assert.That(DetailHit(fts, book, "Changed"), Is.True);
                Assert.That(fts.RecallBooks(null, new[] { "Native" }, BookMediaType.Ebook).Select(row => row.BookId), Is.EqualTo(new[] { book.Id }));
                Assert.That(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM edition_fts;"), Is.EqualTo(1));
            });
        }

        [TestCase(DatabaseType.SQLite)]
        [TestCase(DatabaseType.PostgreSQL)]
        public void search_backfill_should_update_each_changed_edition_once_and_leave_empty_nullable_fields(DatabaseType type)
        {
            WithDatabase(type, 114, (connection, _, migrate) =>
            {
                const int count = 1201;
                var identity = type == DatabaseType.PostgreSQL ? "OVERRIDING SYSTEM VALUE" : string.Empty;
                connection.Execute($@"
                    WITH RECURSIVE ids(id) AS (SELECT 1 UNION ALL SELECT id+1 FROM ids WHERE id<@Count)
                    INSERT INTO ""Authors"" (""Id"", ""Name"", ""Path"", ""Monitored"", ""Added"", ""CleanName"") {identity}
                    SELECT id, 'Reader ' || CAST(id AS TEXT), '/native/' || CAST(id AS TEXT), @Monitored, @Added, 'reader' || CAST(id AS TEXT) FROM ids;
                    INSERT INTO ""Books"" (""Id"", ""AuthorId"", ""Title"", ""CleanTitle"", ""Added"", ""MediaType"", ""AnyEditionOk"") {identity}
                    SELECT ""Id"", ""Id"", 'Volume ' || CAST(""Id"" AS TEXT), 'volume' || CAST(""Id"" AS TEXT), @Added, ""Id""%2, @Monitored FROM ""Authors"";
                    INSERT INTO ""Editions"" (""Id"", ""BookId"", ""Title"", ""MatchingTitle"", ""Subtitle"", ""Narrator"", ""NarratorNames"", ""Publisher"", ""Monitored"") {identity}
                    SELECT ""Id"", ""Id"", ""Title"", 'volume ' || CAST(""Id"" AS TEXT),
                        CASE WHEN ""Id""%2=0 THEN 'Subtitle' ELSE NULL END,
                        CASE WHEN ""Id""%2=0 THEN 'Legacy Voice' ELSE NULL END,
                        CASE WHEN ""Id""%2=0 THEN '[""First Reader"",""Second Voice""]' ELSE '[]' END,
                        CASE WHEN ""Id""%2=0 THEN 'Publisher' ELSE NULL END, @Monitored FROM ""Books"";",
                    new { Count = count, Added = DateTime.UtcNow, Monitored = true });
                var sources = connection.Query<(int Id, string Subtitle, string Narrator, string NarratorNames, string Publisher)>(
                    "SELECT \"Id\", \"Subtitle\", \"Narrator\", \"NarratorNames\", \"Publisher\" FROM \"Editions\" ORDER BY \"Id\";").ToArray();
                connection.Execute("CREATE TABLE ProjectionUpdates (Id INTEGER NOT NULL);");
                if (type == DatabaseType.PostgreSQL)
                {
                    connection.Execute(@"CREATE FUNCTION projection_audit() RETURNS trigger LANGUAGE plpgsql AS $$
                        BEGIN INSERT INTO ProjectionUpdates VALUES(NEW.""Id"");RETURN NEW;END;$$;
                        CREATE TRIGGER projection_audit AFTER UPDATE ON ""Editions"" FOR EACH ROW EXECUTE FUNCTION projection_audit();");
                }
                else connection.Execute("CREATE TRIGGER projection_audit AFTER UPDATE ON Editions BEGIN INSERT INTO ProjectionUpdates VALUES(new.Id); END;");
                var timer = Stopwatch.StartNew();
                var logs = migrate(115);
                var updated = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM ProjectionUpdates;");
                var empty = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"Editions\" WHERE \"SearchSubtitle\" IS NULL AND \"SearchNarrator\" IS NULL AND \"SearchNarratorNames\" IS NULL AND \"SearchPublisher\" IS NULL;");
                TestContext.Progress.WriteLine($"NATIVE_FTS_BACKFILL {type}: rows={count}, updated={updated}, untouched-empty={empty}, elapsedMs={timer.ElapsedMilliseconds}");
                Assert.That(updated, Is.EqualTo(count / 2));
                Assert.That(empty, Is.EqualTo((count + 1) / 2));
                Assert.That(connection.Query<(int Id, string Subtitle, string Narrator, string NarratorNames, string Publisher)>(
                    "SELECT \"Id\", \"Subtitle\", \"Narrator\", \"NarratorNames\", \"Publisher\" FROM \"Editions\" ORDER BY \"Id\";"), Is.EqualTo(sources));
                Assert.That(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"Editions\" WHERE \"Id\"%2=0 AND \"SearchNarratorNames\"='first reader second voice' AND \"SearchPublisher\"='publisher';"), Is.EqualTo(count / 2));
                Assert.That(logs, Has.Some.Contains("Folded search rebuild Editions:").And.Contains("scanned 1201, updated rows 600"));
                Assert.That(logs, Has.Some.Contains("Folded search rebuild Books:").And.Contains("update batches 0"));
            });
        }

        private static bool DetailHit(EditionFtsRepository fts, Book book, string term)
        {
            var result = fts.RankEditions(new[] { new BookFtsMatch { BookId = book.Id, AuthorId = book.AuthorId, MatchScore = 1 } },
                new[] { new EditionFtsFieldQuery { Key = "DETAIL[0]", Terms = new[] { term }, SourceFields = new[] { "DETAIL[0]" } } }, book.MediaType).Single();
            return result.Stage2FieldHits.Any(hit => hit.DetailHit);
        }

        private static void WithDatabase(DatabaseType type, int baseline, Action<IDbConnection, IDatabase, Func<int, string[]>> verify)
        {
            var postgres = type == DatabaseType.PostgreSQL;
            var adminString = Environment.GetEnvironmentVariable("CHAPTARR_TEST_POSTGRES_CONNECTION_STRING");
            if (postgres && string.IsNullOrWhiteSpace(adminString)) Assert.Ignore("Set CHAPTARR_TEST_POSTGRES_CONNECTION_STRING for native PostgreSQL coverage");
            var name = "folded_fts_" + Guid.NewGuid().ToString("N");
            var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, name + ".db");
            if (postgres) { using var admin = new NpgsqlConnection(adminString); admin.Open(); admin.Execute($"CREATE DATABASE \"{name}\""); }
            var connectionString = postgres ? new NpgsqlConnectionStringBuilder(adminString) { Database = name, Pooling = false }.ConnectionString
                : new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString;
            IDbConnection Open() { IDbConnection connection = postgres ? new NpgsqlConnection(connectionString) : new SqliteConnection(connectionString); connection.Open(); return connection; }
            string[] Migrate(int version) => RebuildSharedComparisonKeysMigrationFixture.CaptureMigration(connectionString, type, version);
            try
            {
                Migrate(baseline);
                using var connection = Open();
                verify(connection, new Database("folded-index-fixture", Open), Migrate);
            }
            finally
            {
                if (postgres) { using var admin = new NpgsqlConnection(adminString); admin.Open(); admin.Execute($"DROP DATABASE IF EXISTS \"{name}\""); }
                else File.Delete(path);
            }
        }
    }
}
