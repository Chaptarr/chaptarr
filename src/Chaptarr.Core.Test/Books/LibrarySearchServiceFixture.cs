using System;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection;
using Dapper;
using Microsoft.Data.Sqlite;
using NLog;
using NLog.Config;
using NLog.Targets;
using Npgsql;
using NUnit.Framework;
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

        private class CommandObserver : DispatchProxy
        {
            public object Target { get; set; }
            public Action<IDbCommand> Observe { get; set; }

            public static T Wrap<T>(T target, Action<IDbCommand> observe)
                where T : class
            {
                var proxy = Create<T, CommandObserver>();
                var state = (CommandObserver)(object)proxy;
                state.Target = target;
                state.Observe = observe;
                return proxy;
            }

            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (Target is IDbCommand command && method.Name == nameof(IDbCommand.ExecuteReader))
                {
                    Observe(command);
                }

                var result = method.Invoke(Target, args);
                return result is IDbCommand created ? Wrap(created, Observe) : result;
            }
        }

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
                _connectionString = new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = _databaseName, Pooling = false }.ConnectionString;
            }
            else
            {
                _databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"library_search_{Guid.NewGuid():N}.db");
                _connectionString = new SqliteConnectionStringBuilder { DataSource = _databasePath, Pooling = false }.ConnectionString;
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
            return _observeCommand == null ? connection : CommandObserver.Wrap(connection, _observeCommand);
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

                // A tiny fixture may prefer a sequential scan; test index eligibility explicitly.
                command.Connection.Execute("SET enable_seqscan = off; SET jit = off;");
                using var explain = new NpgsqlCommand("EXPLAIN (FORMAT JSON) " + command.CommandText, (NpgsqlConnection)command.Connection);
                foreach (NpgsqlParameter parameter in command.Parameters)
                {
                    explain.Parameters.Add(parameter.Clone());
                }

                plan = (string)explain.ExecuteScalar();
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

        [TestCase("Ready Player 2")]
        [TestCase("Ready Player 𝟚")]
        [TestCase("Ready Player ٢")]
        [TestCase("Ready Player ２")]
        [TestCase("Ready Player ②")]
        [TestCase("Ready Player Ⅱ")]
        [TestCase("player ready")]
        [TestCase("Ready Player %")]
        [TestCase("Ready Player _")]
        [TestCase("Ready Player Two' OR 1=1")]
        public void partial_search_should_offer_siblings_without_equating_their_titles(string query)
        {
            Assert.That(_service.Search(query, 2).Books.Select(book => book.Title),
                Is.EquivalentTo(new[] { "Ready Player One", "Ready Player Two" }));
        }

        [TestCase("Ready Player Two", "Ready Player Two")]
        [TestCase("Ready Player One", "Ready Player One")]
        [TestCase("dune messiah", "Dune Messiah")]
        [TestCase("messiah dune", "Dune Messiah")]
        [TestCase("mess mess mess dune", "Dune Messiah")]
        [TestCase("catch 22", "Catch-22")]
        [TestCase("Catch-22", "Catch-22")]
        [TestCase("hardcastle evelyn", "The Seven Deaths of Evelyn Hardcastle")]
        public void distinct_query_terms_should_rank_above_repeated_partial_matches(string query, string expected)
        {
            Assert.That(_service.Search(query, 1).Books.Single().Title, Is.EqualTo(expected));
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

        [Test]
        public void alternate_editions_and_media_should_return_one_provider_work_with_both_local_instances()
        {
            var books = _service.Search("Evelyn Hardcastle", 10).Books;
            Assert.That(books, Has.Count.EqualTo(1));
            Assert.That(books[0].ForeignBookId, Is.EqualTo("hc:1708426"));
            Assert.That(books[0].LocalAudiobookBooks, Has.Count.EqualTo(1));
            Assert.That(books[0].LocalEbookBooks, Has.Count.EqualTo(1));
        }

        private void Seed(IDbConnection connection)
        {
            var authorColumns = new[] { "Name", "SortName", "TitleSlug", "Images", "SelectedPosterHash", "HardcoverAuthorId", "GoodreadsAuthorId", "OpenLibraryAuthorId", "GoogleBooksAuthorId", "AudnexusAuthorId" };
            var bookColumns = new[] { "Title", "TitleSlug", "Images", "BaseBookId", "HardcoverBookId", "GoodreadsBookId", "GoodreadsWorkId", "OpenLibraryWorkId", "GoogleBooksId", "ASIN", "AudibleASIN", "Narrator", "SeriesName" };
            var boolean = _postgres ? "BOOLEAN DEFAULT TRUE" : "INTEGER DEFAULT 1";
            connection.Execute($"CREATE TABLE \"Authors\" (\"Id\" INTEGER PRIMARY KEY, {string.Join(", ", authorColumns.Select(column => $"\"{column}\" TEXT NULL"))});");
            connection.Execute($"CREATE TABLE \"Books\" (\"Id\" INTEGER PRIMARY KEY, \"AuthorId\" INTEGER, \"MediaType\" INTEGER, \"AudiobookMonitored\" {boolean}, \"EbookMonitored\" {boolean}, {string.Join(", ", bookColumns.Select(column => $"\"{column}\" TEXT NULL"))});");
            connection.Execute("CREATE TABLE \"Editions\" (\"Id\" INTEGER PRIMARY KEY, \"BookId\" INTEGER, \"Title\" TEXT, \"MatchingTitle\" TEXT, \"Subtitle\" TEXT, \"Narrator\" TEXT);");
            connection.Execute("CREATE TABLE \"BookFiles\" (\"Id\" INTEGER PRIMARY KEY, \"EditionId\" INTEGER);");
            if (!_postgres)
            {
                connection.Execute("CREATE VIRTUAL TABLE author_fts USING fts5(Name, SortName, TitleSlug, content='', tokenize = 'unicode61 remove_diacritics 1 tokenchars ''-.''');");
                connection.Execute("CREATE VIRTUAL TABLE edition_fts USING fts5(MatchingTitle, content='', tokenize = 'unicode61 remove_diacritics 1 tokenchars ''-.''');");
            }

            else
            {
                connection.Execute(@"
                    CREATE TABLE ""VersionInfo"" (""Version"" BIGINT PRIMARY KEY, ""AppliedOn"" TIMESTAMP, ""Description"" TEXT);
                    INSERT INTO ""VersionInfo"" SELECT version, CURRENT_TIMESTAMP, 'test baseline' FROM generate_series(1, 12) version;");
                new MigrationController(LogManager.GetCurrentClassLogger(), null)
                    .Migrate(_connectionString, new MigrationContext(MigrationType.Main, 13), DatabaseType.PostgreSQL);
            }

            var titles = new[] { "The Seven Deaths of Evelyn Hardcastle", "The Seven Deaths of Evelyn Hardcastle", "Ready Player One", "Ready Player Two", "23 ½ Lies", "I, Robot", "Dune", "Dune Messiah", "The Housemaid’s Secret", "Catch-22", "Mentats of Dune", "A Court of Mist and Fury" };
            var names = new[] { "Stuart Turton", "Stuart Turton", "Ernest Cline", "Ernest Cline", "James Patterson", "Cory Doctorow", "Frank Herbert", "Frank Herbert", "Freida McFadden", "Joseph Heller", "Brian Herbert", "Sarah J. Maas" };
            var editionId = 0;
            for (var i = 0; i < titles.Length; i++)
            {
                var id = i + 1;
                connection.Execute("INSERT INTO \"Authors\" (\"Id\", \"Name\", \"SortName\", \"TitleSlug\") VALUES (@Id, @Name, @Name, @Name);", new { Id = id, Name = names[i] });
                connection.Execute("INSERT INTO \"Books\" (\"Id\", \"AuthorId\", \"Title\", \"TitleSlug\", \"MediaType\", \"HardcoverBookId\") VALUES (@Id, @AuthorId, @Title, @Title, @MediaType, @ProviderId);",
                    new { Id = id, AuthorId = i == 1 ? 1 : id, Title = titles[i], MediaType = i == 1 ? 1 : 0, ProviderId = i < 2 ? "1708426" : null });
                var editions = i < 2 ? new[] { titles[i], "The 7½ Deaths of Evelyn Hardcastle", "The 7 ½ Deaths of Evelyn Hardcastle" }
                    : i == 2 ? Enumerable.Repeat(titles[i], 45).ToArray()
                    : i == 10 ? new[] { "Mentats of Dune (Dune Schools of Dune Trilogy 2)" }
                    : i == 11 ? Enumerable.Repeat("A Court of Mist and Fury (Part 2 of 2)", 45).ToArray()
                    : new[] { titles[i] };
                foreach (var title in editions)
                {
                    editionId++;
                    var key = StringSuperNormalizer.ComputeMatchingTitle(title);
                    connection.Execute("INSERT INTO \"Editions\" (\"Id\", \"BookId\", \"Title\", \"MatchingTitle\") VALUES (@Id, @BookId, @Title, @Key);", new { Id = editionId, BookId = id, Title = title, Key = key });
                    if (!_postgres)
                    {
                        connection.Execute("INSERT INTO edition_fts(rowid, MatchingTitle) VALUES (@Id, @Key);", new { Id = editionId, Key = key });
                    }
                }
            }

            connection.Execute("DELETE FROM \"Authors\" WHERE \"Id\" = 2;");
            if (!_postgres)
            {
                connection.Execute("INSERT INTO author_fts(rowid, Name, SortName, TitleSlug) SELECT Id, Name, SortName, TitleSlug FROM Authors;");
            }
        }
    }
}
