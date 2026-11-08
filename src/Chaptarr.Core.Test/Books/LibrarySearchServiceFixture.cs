using System;
using System.Data;
using System.IO;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using NLog;
using NLog.Config;
using NLog.Targets;
using Npgsql;
using NUnit.Framework;
using Chaptarr.Core.Test.Common;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Extensions;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture(false)]
    [TestFixture(true)]
    [NonParallelizable]
    public class LibrarySearchServiceFixture
    {
        private readonly bool _postgres;
        private string _connectionString;
        private string _databasePath;
        private string _databaseName;
        private LibrarySearchService _service;
        private Action<IDbCommand> _observeCommand;

        public LibrarySearchServiceFixture(bool postgres)
        {
            _postgres = postgres;
        }

        [OneTimeSetUp]
        public void SetUp()
        {
            if (_postgres)
            {
                var adminConnectionString = Environment.GetEnvironmentVariable("CHAPTARR_TEST_POSTGRES_CONNECTION_STRING");
                if (string.IsNullOrWhiteSpace(adminConnectionString))
                {
                    Assert.Ignore("Set CHAPTARR_TEST_POSTGRES_CONNECTION_STRING to exercise PostgreSQL library search");
                }

                _databaseName = "library_search_" + Guid.NewGuid().ToString("N");
                using var admin = new NpgsqlConnection(adminConnectionString);
                admin.Open();
                admin.Execute($"CREATE DATABASE \"{_databaseName}\"");
                _connectionString = new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = _databaseName, Pooling = false, CommandTimeout = 7 }.ConnectionString;
            }
            else
            {
                _databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"library_search_{Guid.NewGuid():N}.db");
                _connectionString = new SqliteConnectionStringBuilder { DataSource = _databasePath, Pooling = false, DefaultTimeout = 7 }.ConnectionString;
            }

            using var connection = OpenConnection();
            Seed(connection);
            _service = new LibrarySearchService(new MainDatabase(new Database("library-search", OpenConnection)), LogManager.GetCurrentClassLogger());
        }

        [OneTimeTearDown]
        public void TearDown()
        {
            if (_postgres && _databaseName != null)
            {
                using var admin = new NpgsqlConnection(Environment.GetEnvironmentVariable("CHAPTARR_TEST_POSTGRES_CONNECTION_STRING"));
                admin.Open();
                admin.Execute($"DROP DATABASE IF EXISTS \"{_databaseName}\"");
            }
            else if (_databasePath != null && File.Exists(_databasePath))
            {
                File.Delete(_databasePath);
            }
        }

        private IDbConnection OpenConnection()
        {
            IDbConnection connection = _postgres ? new NpgsqlConnection(_connectionString) : new SqliteConnection(_connectionString);
            connection.Open();
            return _observeCommand == null ? connection : CommandObserver.Wrap<IDbConnection>(connection, _observeCommand);
        }

        [Test]
        public void production_book_search_should_remain_eligible_for_the_postgres_title_index()
        {
            if (!_postgres)
            {
                return;
            }

            string plan = null;
            _observeCommand = command =>
            {
                if (!command.CommandText.Contains("FROM ranked r", StringComparison.Ordinal))
                {
                    return;
                }

                plan = ExplainIndexEligibility(command);
            };

            try
            {
                using var factory = new LogFactory();
                var messages = new MemoryTarget { Layout = "${message} ${exception:format=tostring}" };
                var config = new LoggingConfiguration(factory);
                config.AddRule(LogLevel.Debug, LogLevel.Fatal, messages);
                factory.Configuration = config;
                var service = new LibrarySearchService(new MainDatabase(new Database("plan-check", OpenConnection)), factory.GetLogger("plan-check"));
                var books = service.Search("Dune Messiah", 10).Books;
                Assert.That(books, Is.Not.Empty, string.Join(Environment.NewLine, messages.Logs));
                Assert.That(books.First().Title, Is.EqualTo("Dune Messiah"));
                Assert.That(plan, Does.Contain("idx_editions_matching_title_fts"));
            }
            finally
            {
                _observeCommand = null;
            }
        }

        private static string ExplainIndexEligibility(IDbCommand command)
        {
            // A tiny fixture may prefer a sequential scan; test index eligibility explicitly.
            command.Connection.Execute("SET enable_seqscan = off; SET jit = off;");
            using var explain = new NpgsqlCommand("EXPLAIN (FORMAT JSON) " + command.CommandText, (NpgsqlConnection)command.Connection);
            foreach (NpgsqlParameter parameter in command.Parameters)
            {
                explain.Parameters.Add(parameter.Clone());
            }

            return (string)explain.ExecuteScalar();
        }

        [Test]
        public void native_author_header_reuses_existing_index_without_joining_whole_names()
        {
            DualDatabaseFixture.Run(_postgres, database =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                var events = InterfaceProxy.Proxy<NzbDrone.Core.Messaging.Events.IEventAggregator>((_, _) => null);
                var authors = new AuthorRepository(database, events, logger);
                foreach (var name in new[] { "Adam Scott Huerta", "Adam Silvera", "Adam Southward", "Sarah Adams", "J.K. Rowling", "Camille Pagán", "Frank Herbert", "C.N. Crawford", "Jean-Paul Sartre", "George R.R. Martin" })
                {
                    authors.Insert(new Author { Name = name, CleanName = NzbDrone.Core.Parser.Parser.CleanAuthorName(name), SortName = name.ToLowerInvariant(), Path = "/header-prefix/" + name, Added = DateTime.UtcNow });
                }

                var service = new LibrarySearchService(database, logger);
                Assert.That(service.Search("Adams", 10).Authors.Select(author => author.Name), Is.EqualTo(new[] { "Sarah Adams" }));
                Assert.That(service.Search("Adams", 1).Authors.Single().Name, Is.EqualTo("Sarah Adams"));
                Assert.That(service.Search("Sarah Adams", 1).Authors.Single().ExactMatch, Is.True);
                Assert.That(service.Search("camille pagan", 1).Authors.Single().Name, Is.EqualTo("Camille Pagán"));
                Assert.That(service.Search("frank h", 1).Authors.Single().Name, Is.EqualTo("Frank Herbert"));
                Assert.That(service.Search("frankx", 10).Authors, Is.Empty);
                Assert.That(service.Search("J.K. Row", 1).Authors.Single().Name, Is.EqualTo("J.K. Rowling"));
                foreach (var (query, expected) in new[] { ("jk rowling", "J.K. Rowling"), ("cn crawford", "C.N. Crawford"), ("jean paul sartre", "Jean-Paul Sartre"), ("george r r martin", "George R.R. Martin"), ("george rr martin", "George R.R. Martin"), ("martin rr george", "George R.R. Martin") })
                {
                    Assert.That(service.Search(query, 10).Authors.Select(author => author.Name), Does.Contain(expected), query);
                }
                if (_postgres)
                {
                    string plan = null;
                    var reads = 0;
                    var observed = new MainDatabase(new Database("header-index", () => CommandObserver.Wrap<IDbConnection>(database.OpenConnection(), command =>
                    {
                        reads++;
                        if (command.CommandText.Contains("FROM \"Authors\" CROSS JOIN query", StringComparison.Ordinal))
                        {
                            plan = ExplainIndexEligibility(command);
                        }
                    })));
                    Assert.That(new LibrarySearchService(observed, logger).Search("jk row", 1).Authors.Single().Name, Is.EqualTo("J.K. Rowling"));
                    Assert.That(reads, Is.EqualTo(2));
                    Assert.That(plan, Does.Contain("idx_authors_fts"));
                    using var connection = database.OpenConnection();
                    Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM pg_indexes WHERE indexname='idx_authors_header_fts'"), Is.Zero);
                }
            });
        }

        [TestCase("The Seven Deaths of Evelyn Hardcastle", "The Seven Deaths of Evelyn Hardcastle")]
        [TestCase("The 7½ Deaths of Evelyn Hardcastle", "The Seven Deaths of Evelyn Hardcastle")]
        [TestCase("The 7 ½ Deaths of Evelyn Hardcastle", "The Seven Deaths of Evelyn Hardcastle")]
        [TestCase("The 7 1/2 Deaths of Evelyn Hardcastle", "The Seven Deaths of Evelyn Hardcastle")]
        [TestCase("The 7.5 Deaths of Evelyn Hardcastle", "The Seven Deaths of Evelyn Hardcastle")]
        [TestCase("23 ½ Lies", "23 ½ Lies")]
        [TestCase("23.5 Lies", "23 ½ Lies")]
        [TestCase("I, Robot", "I, Robot")]
        [TestCase("Dune Messiah", "Dune Messiah")]
        [TestCase("The Housemaid’s Secret", "The Housemaid’s Secret")]
        public void equivalent_title_queries_should_find_the_work_without_merging_siblings(string query, string expected)
        {
            Assert.That(_service.Search(query, 10).Books.First().Title, Is.EqualTo(expected));
        }

        [TestCase("Evelyn Hardc", "The Seven Deaths of Evelyn Hardcastle")]
        [TestCase("Dune Mess", "Dune Messiah")]
        public void partial_title_words_should_still_match(string query, string expected)
        {
            Assert.That(_service.Search(query, 10).Books.First().Title, Is.EqualTo(expected));
        }

        [TestCase("")]
        [TestCase(" ")]
        [TestCase("--")]
        [TestCase("\u0301\u0300")]
        [TestCase("% _ ")]
        [TestCase("𠀀𠀁")]
        public void empty_or_literal_query_syntax_should_not_return_unrelated_books(string query)
        {
            Assert.That(_service.Search(query, 10).Books, Is.Empty);
        }

        [TestCase("Ready Player Two' OR 1=1")]
        [TestCase("Dune zzqqmissing")]
        [TestCase("Lessons in Chemistry")]
        [TestCase("The Long Journey Through Seven Distant Worlds Beyond zzqqmissing")]
        public void every_query_word_must_match_the_same_edition(string query)
        {
            Assert.That(_service.Search(query, 10).Books, Is.Empty);
        }

        // Header search adds numeric alternatives at query time only.
        [TestCase("Ready Player 2")]
        [TestCase("Ready Player 𝟚")]
        [TestCase("Ready Player ٢")]
        [TestCase("Ready Player ２")]
        [TestCase("Ready Player ②")]
        [TestCase("Ready Player Ⅱ")]
        [TestCase("Ready Player II")]
        public void numeric_query_alternatives_should_find_the_word_title(string query)
        {
            Assert.That(_service.Search(query, 10).Books.Select(book => book.Title), Does.Contain("Ready Player Two"));
        }

        [TestCase("Ready Player 2", "Ready Player Two")]
        [TestCase("Ready Player 2", "Ready Player II")]
        [TestCase("Ready Player II", "Ready Player Two")]
        [TestCase("I had seven children", "I had 7 children")]
        [TestCase("Volume Twenty One", "Volume 21")]
        [TestCase("Volume 21", "Volume twenty one")]
        [TestCase("Volume XXI", "Volume twenty-one")]
        public void number_aliases_should_preserve_stored_titles_and_keys(string title, string query)
        {
            using var connection = OpenConnection();
            var key = StringSuperNormalizer.ComputeMatchingTitle(title);
            connection.Execute(@"INSERT INTO ""Books"" (""Id"", ""AuthorId"", ""Title"", ""MediaType"") VALUES (900, 3, @title, 0);
                INSERT INTO ""Editions"" (""Id"", ""BookId"", ""Title"", ""MatchingTitle"") VALUES (900, 900, @title, @key);", new { title, key });
            try
            {
                Assert.That(_service.Search(query, 50).Books.Select(book => book.Id), Does.Contain(900));
                Assert.That(_service.Search(query + " missingword", 50).Books.Select(book => book.Id), Does.Not.Contain(900));
                Assert.That(connection.QuerySingle<string>(@"SELECT ""MatchingTitle"" FROM ""Editions"" WHERE ""Id""=900"), Is.EqualTo(key));
            }
            finally
            {
                connection.Execute(@"DELETE FROM ""Editions"" WHERE ""Id""=900; DELETE FROM ""Books"" WHERE ""Id""=900;");
            }
        }

        [TestCase("100", "Centennial", "C")]
        [TestCase("one", "Island", "I")]
        [TestCase("five", "Voyage", "V")]
        public void generated_number_aliases_are_complete_tokens_not_prefixes(string query, string unrelated, string equivalent)
        {
            using var connection = OpenConnection();
            foreach (var (id, title) in new[] { (900, unrelated), (901, equivalent) })
            {
                connection.Execute(@"INSERT INTO ""Books"" (""Id"", ""AuthorId"", ""Title"", ""MediaType"") VALUES (@id, 3, @title, 0);
                    INSERT INTO ""Editions"" (""Id"", ""BookId"", ""Title"", ""MatchingTitle"") VALUES (@id, @id, @title, @key);",
                    new { id, title, key = StringSuperNormalizer.ComputeMatchingTitle(title) });
            }
            try
            {
                var matches = _service.Search(query, 50).Books.Select(book => book.Id).ToArray();
                Assert.That(matches, Does.Contain(901), "retain number equivalence");
                Assert.That(matches, Does.Not.Contain(900), "generated Roman alias must not mean every word beginning with that letter");
                Assert.That(_service.Search(unrelated[..3], 50).Books.Select(book => book.Id), Does.Contain(900), "literal typed prefixes still work");
            }
            finally
            {
                connection.Execute(@"DELETE FROM ""Editions"" WHERE ""Id"" IN (900,901); DELETE FROM ""Books"" WHERE ""Id"" IN (900,901);");
            }
        }

        [TestCase("player ready")]
        [TestCase("Ready Player %")]
        [TestCase("Ready Player _")]
        public void prefix_search_should_offer_books_containing_every_word(string query)
        {
            Assert.That(_service.Search(query, 2).Books.Select(book => book.Title),
                Is.EquivalentTo(new[] { "Ready Player One", "Ready Player Two" }));
        }

        [TestCase("Ready Player Two", "Ready Player Two")]
        [TestCase("Ready Player One", "Ready Player One")]
        [TestCase("dune messiah", "Dune Messiah")]
        [TestCase("messiah dune", "Dune Messiah")]
        [TestCase("mess mess mess dune", "Dune Messiah")]
        [TestCase("Catch-22", "Catch-22")]
        [TestCase("hardcastle evelyn", "The Seven Deaths of Evelyn Hardcastle")]
        public void all_query_terms_should_find_the_book_in_any_order(string query, string expected)
        {
            Assert.That(_service.Search(query, 1).Books.Single().Title, Is.EqualTo(expected));
        }

        [TestCase("Spider man")]
        [TestCase("Spider-man")]
        [TestCase("spider m")]
        [TestCase("man spider")]
        [TestCase("man-spider")]
        [TestCase("man")]
        public void punctuation_variants_find_both_title_spellings(string query)
        {
            Assert.That(_service.Search(query, 10).Books.Select(book => book.Title),
                Is.EquivalentTo(new[] { "Spider-Man", "Spider Man" }));
        }

        [TestCase("Catch 22")]
        [TestCase("Catch-22")]
        [TestCase("22 Catch")]
        public void numeric_title_components_do_not_require_the_stored_separator(string query)
        {
            Assert.That(_service.Search(query, 1).Books.Single().Title, Is.EqualTo("Catch-22"));
        }

        [Test]
        public void repeated_editions_should_not_fill_the_first_page_for_a_short_prefix()
        {
            Assert.That(_service.Search("pl", 2).Books.Select(book => book.Title),
                Is.EqualTo(new[] { "Ready Player One", "Ready Player Two" }));
        }

        [Test]
        public void author_prefix_search_should_remain_unchanged()
        {
            Assert.That(_service.Search("Stuart Tur", 10).Authors.Select(author => author.Name), Is.EqualTo(new[] { "Stuart Turton" }));
        }

        [TestCase("It", "It")]
        [TestCase(" IT ", "It")]
        [TestCase("It St", "It Starts With Us")]
        [TestCase("It Starts With Us", "It Starts With Us")]
        [TestCase("Dune", "Dune")]
        [TestCase("Dune Messiah", "Dune Messiah")]
        [TestCase("The Long Journey Through Seven Distant Worlds Beyond", "The Long Journey Through Seven Distant Worlds Beyond")]
        public void exact_title_precedes_prefixes_before_the_result_limit(string query, string title)
        {
            Assert.That(_service.Search(query, 1).Books.Single().Title, Is.EqualTo(title));
        }

        [Test]
        public void exact_proof_comes_from_the_edition_and_survives_grouping()
        {
            var result = _service.Search("Alternate Edition Name", 1).Books.Single();
            Assert.That(result.Title, Is.EqualTo("Different Work Display Title"));
            Assert.That(result.ExactMatch, Is.True);
            Assert.That(_service.Search("It", 10).Books.Select(book => book.Title),
                Is.EqualTo(new[] { "It", "It Starts With Us" }));
            Assert.That(_service.Search("It St", 10).Books.Select(book => book.Title),
                Is.EqualTo(new[] { "It Starts With Us" }));
        }

        [TestCase("Ann", "Ann")]
        [TestCase("ANN", "Ann")]
        [TestCase("Ann P", "Ann Pat")]
        [TestCase("Pat Ann", "Ann Pat")]
        [TestCase("Turton Stuart", "Stuart Turton")]
        public void author_search_requires_all_prefixes_and_prefers_the_exact_name(string query, string expected)
        {
            Assert.That(_service.Search(query, 1).Authors.Single().Name, Is.EqualTo(expected));
        }

        [TestCase("Ann zzqqmissing")]
        [TestCase("kegaard")]
        [TestCase("One Two Three Four Five Six Seven zzqqmissing")]
        public void author_search_does_not_ignore_words_or_match_inside_a_word(string query)
        {
            Assert.That(_service.Search(query, 10).Authors, Is.Empty);
        }

        [Test]
        public void search_loads_series_in_one_batch_for_all_returned_books()
        {
            var count = 0;
            _observeCommand = _ => count++;
            try
            {
                var result = _service.Search("It", 10);
                Assert.That(result.Books, Has.Count.EqualTo(2));
                Assert.That(count, Is.EqualTo(3));
            }
            finally
            {
                _observeCommand = null;
            }
        }

        [Test]
        public void alternate_editions_and_media_should_return_one_provider_work_with_both_local_instances()
        {
            var books = _service.Search("Evelyn Hardcastle", 10).Books;
            Assert.That(books, Has.Count.EqualTo(1));
            Assert.That(books[0].ForeignBookId, Is.EqualTo("hc:1708426"));
            Assert.That(books[0].LocalAudiobookBooks, Has.Count.EqualTo(1));
            Assert.That(books[0].LocalEbookBooks, Has.Count.EqualTo(1));
        }

        [Test]
        public void folded_author_header_should_preserve_names_slugs_and_native_updates()
        {
            DualDatabaseFixture.Run(_postgres, database =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                var events = InterfaceProxy.Proxy<NzbDrone.Core.Messaging.Events.IEventAggregator>((_, _) => null);
                var authors = new AuthorRepository(database, events, logger);
                var original = authors.Insert(new Author { Name = "Søren Kierkegaard", CleanName = "sorenkierkegaard", SortName = "kierkegaard, soren", TitleSlug = "123456", Path = "/native-header", Added = DateTime.UtcNow });
                var other = authors.Insert(new Author { Name = "Other Reader", CleanName = "otherreader", SortName = "reader, other", TitleSlug = "654321", Path = "/native-other", Added = DateTime.UtcNow });
                var service = new LibrarySearchService(database, logger);
                Assert.That(service.Search("Soren", 10).Authors.Select(row => row.Id), Is.EqualTo(new[] { original.Id }));
                Assert.That(service.Search("Søren", 10).Authors.Select(row => row.Id), Is.EqualTo(new[] { original.Id }));
                Assert.That(service.Search("Kierkegaard Soren", 10).Authors.Select(row => row.Id), Is.EqualTo(new[] { original.Id }));
                Assert.That(service.Search("123", 10).Authors.Select(row => row.Id), Is.EqualTo(new[] { original.Id }));
                Assert.That(service.Search("kegaard", 10).Authors, Is.Empty);
                Assert.That(service.Search("zzqqmissing", 10).Authors, Is.Empty);
                Assert.That(service.Search("%", 10).Authors, Is.Empty);
                original.Name = "Ælfric Reader";
                original.SortName = "reader, aelfric";
                authors.SetFields(original, row => row.Name, row => row.SortName);
                Assert.That(service.Search("Soren", 10).Authors, Is.Empty);
                Assert.That(service.Search("Aelfric", 10).Authors.Select(row => row.Id), Is.EqualTo(new[] { original.Id }));
                Assert.That(service.Search("Aelfric Reader", 1).Authors.Single().ExactMatch, Is.True);
                Assert.That(service.Search("Other", 10).Authors.Select(row => row.Id), Is.EqualTo(new[] { other.Id }));
                authors.Delete(original.Id);
                Assert.That(service.Search("Aelfric", 10).Authors, Is.Empty);
            });
        }

        [Test]
        public void author_query_timeout_warns_once_and_keeps_book_results()
        {
            using var factory = new LogFactory();
            var messages = new MemoryTarget { Layout = "${level} ${message} ${exception:format=tostring}" };
            var config = new LoggingConfiguration(factory);
            config.AddRule(LogLevel.Warn, LogLevel.Fatal, messages);
            factory.Configuration = config;
            var authorQueries = 0;
            int? authorCommandTimeout = null;
            _observeCommand = command =>
            {
                if (!command.CommandText.Contains("FROM author_fts", StringComparison.Ordinal) &&
                    !command.CommandText.Contains("JOIN author_candidates", StringComparison.Ordinal))
                {
                    return;
                }

                authorQueries++;
                authorCommandTimeout = command.CommandTimeout;
                if (_postgres)
                {
                    // Exercise the real driver's cancellation and reuse the same connection for books.
                    command.CommandTimeout = 1;
                    command.CommandText = "SELECT pg_sleep(10)";
                }
                else
                {
                    // SQLite has no pg_sleep; exercise the same failure boundary without a real lock.
                    throw new TimeoutException("Author index read timed out");
                }
            };
            try
            {
                var database = InterfaceProxy.Proxy<IMainDatabase>((method, _) => method.Name switch
                {
                    "OpenConnection" => OpenConnection(),
                    "get_DatabaseType" => _postgres ? DatabaseType.PostgreSQL : DatabaseType.SQLite,
                    _ => throw new NotSupportedException(method.Name)
                });
                var result = new LibrarySearchService(database, factory.GetLogger("timeout")).Search("Dune", 10);
                Assert.That(authorQueries, Is.EqualTo(1), "A timeout must not repeat the expensive search");
                Assert.That(authorCommandTimeout, Is.EqualTo(30), "The explicit bound must override the test connection default of 7 seconds");
                Assert.That(result.Authors, Is.Empty);
                Assert.That(result.Books.Select(book => book.Title), Does.Contain("Dune"));
                Assert.That(messages.Logs, Has.Exactly(1).Contains("Author search failed"));
                Assert.That(messages.Logs.Single(), Does.Contain("after").And.Contain("ms").And.Contain("Timeout"));
            }
            finally
            {
                _observeCommand = null;
            }
        }

        [Test]
        public void book_search_failure_warns_but_preserves_author_results()
        {
            using var factory = new LogFactory();
            var messages = new MemoryTarget { Layout = "${level} ${message}" };
            var config = new LoggingConfiguration(factory);
            config.AddRule(LogLevel.Warn, LogLevel.Fatal, messages);
            factory.Configuration = config;
            _observeCommand = command =>
            {
                if (command.CommandText.Contains("FROM ranked r", StringComparison.Ordinal)) throw new InvalidOperationException("book index unavailable");
            };
            try
            {
                var database = InterfaceProxy.Proxy<IMainDatabase>((method, _) => method.Name switch
                {
                    "OpenConnection" => OpenConnection(),
                    "get_DatabaseType" => _postgres ? DatabaseType.PostgreSQL : DatabaseType.SQLite,
                    _ => throw new NotSupportedException(method.Name)
                });
                var service = new LibrarySearchService(database, factory.GetLogger("partial"));
                var result = service.Search("Frank Herbert", 10);
                Assert.That(result.Authors.Select(author => author.Name), Does.Contain("Frank Herbert"));
                Assert.That(result.Books, Is.Empty);
                Assert.That(messages.Logs, Has.Exactly(1).Contains("Book search failed"));
                Assert.That(messages.Logs.Single(), Does.Contain("after").And.Contain("ms"));
            }
            finally
            {
                _observeCommand = null;
            }
        }

        private void Seed(IDbConnection connection)
        {
            var authorColumns = new[] { "Name", "SearchName", "CleanName", "SortName", "TitleSlug", "Images", "SelectedPosterHash", "HardcoverAuthorId", "GoodreadsAuthorId", "OpenLibraryAuthorId", "GoogleBooksAuthorId", "AudnexusAuthorId" };
            var bookColumns = new[] { "Title", "TitleSlug", "Images", "BaseBookId", "HardcoverBookId", "GoodreadsBookId", "GoodreadsWorkId", "OpenLibraryWorkId", "GoogleBooksId", "ASIN", "AudibleASIN", "Narrator", "SeriesName" };
            var boolean = _postgres ? "BOOLEAN DEFAULT TRUE" : "INTEGER DEFAULT 1";
            connection.Execute($"CREATE TABLE \"Authors\" (\"Id\" INTEGER PRIMARY KEY, {string.Join(", ", authorColumns.Select(column => $"\"{column}\" TEXT NULL"))});");
            connection.Execute($"CREATE TABLE \"Books\" (\"Id\" INTEGER PRIMARY KEY, \"AuthorId\" INTEGER, \"MediaType\" INTEGER, \"SeriesId\" INTEGER NULL, \"AudiobookMonitored\" {boolean}, \"EbookMonitored\" {boolean}, {string.Join(", ", bookColumns.Select(column => $"\"{column}\" TEXT NULL"))});");
            connection.Execute("CREATE TABLE \"Editions\" (\"Id\" INTEGER PRIMARY KEY, \"BookId\" INTEGER, \"Title\" TEXT, \"MatchingTitle\" TEXT, \"Subtitle\" TEXT, \"Narrator\" TEXT, \"Monitored\" BOOLEAN DEFAULT FALSE);");
            connection.Execute("CREATE TABLE \"BookFiles\" (\"Id\" INTEGER PRIMARY KEY, \"EditionId\" INTEGER);");
            connection.Execute("CREATE TABLE \"Series\" (\"Id\" INTEGER PRIMARY KEY, \"Title\" TEXT, \"GoodreadsSeriesId\" TEXT, \"MediaType\" INTEGER);");
            connection.Execute("CREATE TABLE \"SeriesBookLink\" (\"BookId\" INTEGER, \"SeriesId\" INTEGER, \"Position\" TEXT);");
            if (!_postgres)
            {
                FtsMaintenanceService.RebuildSqliteAuthorIndex(connection, null, LogManager.GetCurrentClassLogger());
                EditionFtsRepository.RebuildSqliteHeaderIndex(connection, null, LogManager.GetCurrentClassLogger());
            }

            else
            {
                connection.Execute(@"
                    CREATE TABLE ""VersionInfo"" (""Version"" BIGINT PRIMARY KEY, ""AppliedOn"" TIMESTAMP, ""Description"" TEXT);
                    INSERT INTO ""VersionInfo"" SELECT version, CURRENT_TIMESTAMP, 'test baseline' FROM generate_series(1, 12) version;");
                new MigrationController(LogManager.GetCurrentClassLogger(), null)
                    .Migrate(_connectionString, new MigrationContext(MigrationType.Main, 13), DatabaseType.PostgreSQL);
            }

            var titles = new[] { "The Seven Deaths of Evelyn Hardcastle", "The Seven Deaths of Evelyn Hardcastle", "Ready Player One", "Ready Player Two", "23 ½ Lies", "I, Robot", "Dune", "Dune Messiah", "The Housemaid’s Secret", "Catch-22", "Mentats of Dune", "A Court of Mist and Fury", "It Starts With Us", "It", "The Long Journey Through Seven Distant Worlds Beyond", "Different Work Display Title", "Alternate Edition Name Extended", "Lessons In Faking", "Spider-Man", "Spider Man" };
            var names = new[] { "Stuart Turton", "Stuart Turton", "Ernest Cline", "Ernest Cline", "James Patterson", "Cory Doctorow", "Frank Herbert", "Frank Herbert", "Freida McFadden", "Joseph Heller", "Brian Herbert", "Sarah J. Maas", "Ann Pat", "Ann", "One Two Three Four Five Six Seven", "Other Writer", "Another Writer", "Lesson Writer", "Stan Lee", "Stan Lee" };
            var editionId = 0;
            for (var i = 0; i < titles.Length; i++)
            {
                var id = i + 1;
                connection.Execute("INSERT INTO \"Authors\" (\"Id\", \"Name\", \"SearchName\", \"SortName\", \"TitleSlug\") VALUES (@Id, @Name, @SearchName, @Name, @Name);", new { Id = id, Name = names[i], SearchName = StringSuperNormalizer.NormalizeForFts(names[i]) });
                connection.Execute("INSERT INTO \"Books\" (\"Id\", \"AuthorId\", \"Title\", \"TitleSlug\", \"MediaType\", \"HardcoverBookId\") VALUES (@Id, @AuthorId, @Title, @Title, @MediaType, @ProviderId);",
                    new { Id = id, AuthorId = i == 1 ? 1 : id, Title = titles[i], MediaType = i == 1 ? 1 : 0, ProviderId = i < 2 ? "1708426" : null });
                var editions = i < 2 ? new[] { titles[i], "The 7½ Deaths of Evelyn Hardcastle", "The 7 ½ Deaths of Evelyn Hardcastle" }
                    : i == 2 ? Enumerable.Repeat(titles[i], 45).ToArray()
                    : i == 10 ? new[] { "Mentats of Dune (Dune Schools of Dune Trilogy 2)" }
                    : i == 11 ? Enumerable.Repeat("A Court of Mist and Fury (Part 2 of 2)", 45).ToArray()
                    : i == 15 ? new[] { "Alternate Edition Name", "Alternate Edition Name Extended" }
                    : new[] { titles[i] };
                foreach (var title in editions)
                {
                    editionId++;
                    var key = StringSuperNormalizer.ComputeMatchingTitle(title);
                    connection.Execute("INSERT INTO \"Editions\" (\"Id\", \"BookId\", \"Title\", \"MatchingTitle\") VALUES (@Id, @BookId, @Title, @Key);", new { Id = editionId, BookId = id, Title = title, Key = key });
                }
            }

            connection.Execute("DELETE FROM \"Authors\" WHERE \"Id\" = 2;");
        }
    }
}
