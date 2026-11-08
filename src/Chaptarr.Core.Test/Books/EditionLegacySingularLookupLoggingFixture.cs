using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using Npgsql;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class EditionLegacySingularLookupLoggingFixture
    {
        private LoggingConfiguration _originalConfiguration;

        private sealed class StubEventAggregator : IEventAggregator
        {
            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
            }
        }

        private sealed class StubEditionRepository : IEditionRepository
        {
            public List<Edition> AsinMatches { get; set; } = new();
            public List<Edition> IsbnMatches { get; set; } = new();
            public List<Edition> HardcoverMatches { get; set; } = new();
            public List<Edition> Deleted { get; } = new();
            public int FindByBookCallCount { get; private set; }

            public List<Edition> FindAllByAsin(string asin) => AsinMatches;
            public List<Edition> FindAllByAsin(string asin, BookMediaType? mediaType) => AsinMatches;
            public List<Edition> FindAllByIsbn(string isbn) => IsbnMatches;
            public List<Edition> FindAllByHardcoverEditionId(string hardcoverEditionId) => HardcoverMatches;
            public List<Edition> FindAllByHardcoverEditionId(IEnumerable<string> ids) => HardcoverMatches;
            public Dictionary<string, List<Edition>> FindAllByProviderIds(string provider, IEnumerable<string> ids) =>
                ids.ToDictionary(id => id, id => HardcoverMatches.OrderBy(edition => edition.Id).ToList(), StringComparer.OrdinalIgnoreCase);

            public Edition FindByIsbn(string isbn) => FindAllByIsbn(isbn).OrderBy(e => e.Id).FirstOrDefault();
            public Edition FindByHardcoverEditionId(string hardcoverEditionId) => FindAllByHardcoverEditionId(hardcoverEditionId).OrderBy(e => e.Id).FirstOrDefault();

            public IEnumerable<Edition> All() => throw new NotImplementedException();
            public int Count() => throw new NotImplementedException();
            public Edition Find(int id) => throw new NotImplementedException();
            public Edition Get(int id) => throw new NotImplementedException();
            public void RefreshProviderAliases(IList<Edition> editions) { }
            public Edition Insert(Edition model) => throw new NotImplementedException();
            public Edition Update(Edition model) => throw new NotImplementedException();
            public Edition Upsert(Edition model) => throw new NotImplementedException();
            public void SetFields(Edition model, params System.Linq.Expressions.Expression<Func<Edition, object>>[] properties) => throw new NotImplementedException();
            public void Delete(Edition model) => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
            public IEnumerable<Edition> Get(IEnumerable<int> ids) => throw new NotImplementedException();
            public void InsertMany(IList<Edition> model) => throw new NotImplementedException();
            public void InsertMany(IList<Edition> model, IDbConnection connection, IDbTransaction transaction) => throw new NotImplementedException();
            public void UpdateMany(IList<Edition> model) => throw new NotImplementedException();
            public void UpdateMany(IList<Edition> model, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction, Edition selectedEdition = null) => throw new NotImplementedException();
            public void SetFields(IList<Edition> models, params System.Linq.Expressions.Expression<Func<Edition, object>>[] properties) => throw new NotImplementedException();
            public void DeleteMany(List<Edition> model) => Deleted.AddRange(model);
            public void DeleteMany(IEnumerable<int> ids) => throw new NotImplementedException();
            public void Purge(bool vacuum = false) => throw new NotImplementedException();
            public bool HasItems() => throw new NotImplementedException();
            public Edition Single() => throw new NotImplementedException();
            public Edition SingleOrDefault() => throw new NotImplementedException();
            public PagingSpec<Edition> GetPaged(PagingSpec<Edition> pagingSpec) => throw new NotImplementedException();
            public List<Edition> GetAllMonitoredEditions() => throw new NotImplementedException();
            public Edition FindByForeignEditionId(string foreignEditionId) => throw new NotImplementedException();
            public List<Edition> FindAllByForeignEditionId(string foreignEditionId) => new();
            public List<Edition> FindAllByForeignEditionId(IEnumerable<string> ids) => new();
            public Edition FindByGoodreadsEditionId(long goodreadsEditionId) => throw new NotImplementedException();
            public List<Edition> FindAllByGoodreadsEditionId(long goodreadsEditionId) => throw new NotImplementedException();
            public Edition FindByOpenLibraryEditionId(string openLibraryEditionId) => throw new NotImplementedException();
            public List<Edition> FindAllByOpenLibraryEditionId(string openLibraryEditionId) => throw new NotImplementedException();
            public Edition FindByGoogleBooksEditionId(string googleBooksEditionId) => throw new NotImplementedException();
            public List<Edition> FindAllByGoogleBooksEditionId(string googleBooksEditionId) => throw new NotImplementedException();
            public List<Edition> FindByBook(IEnumerable<int> ids)
            {
                FindByBookCallCount++;
                return new List<Edition>();
            }
            public List<Edition> FindByAuthor(int id, bool onlyMonitored = false) => throw new NotImplementedException();
            public List<Edition> GetEditionsForRefresh(int bookId) => throw new NotImplementedException();
            public List<Edition> SetMonitored(Edition edition, bool isManualSelection = false) => throw new NotImplementedException();
            public void UpdateSelection(IList<Edition> editions) => throw new NotImplementedException();
            public HashSet<string> FindExistingTitleSlugsForUniqueness(IEnumerable<string> baseTitleSlugs) => throw new NotImplementedException();
            public int CountMissingMatchingTitles() => throw new NotImplementedException();
            public List<Edition> GetMissingMatchingTitles(int afterId, int limit) => throw new NotImplementedException();
        }

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            if (TableMapping.Mapper.TableMap.Count == 0)
            {
                TableMapping.Map();
            }
        }

        [SetUp]
        public void SetUp()
        {
            _originalConfiguration = LogManager.Configuration;
        }

        [TearDown]
        public void TearDown()
        {
            LogManager.Configuration = _originalConfiguration;
            LogManager.ReconfigExistingLoggers();
        }

        [TestCase("az", "B00EXPECTED")]
        [TestCase("isbn", "9780123456789")]
        public void should_not_warn_for_expected_plural_asin_or_isbn_service_lookup(string provider, string providerId)
        {
            var logs = ConfigureLogging();
            var repo = new StubEditionRepository
            {
                AsinMatches = new List<Edition> { new() { Id = 2 }, new() { Id = 1 } },
                IsbnMatches = new List<Edition> { new() { Id = 2 }, new() { Id = 1 } }
            };
            var service = new EditionService(repo, eventAggregator: null, LogManager.GetLogger("NzbDrone.Core.Books.EditionService"));

            var edition = service.GetEditionByProviderAndId(provider, providerId);

            Assert.That(edition?.Id, Is.EqualTo(1));
            Assert.That(logs.Logs.Any(log => log.StartsWith("Warn|", StringComparison.Ordinal)), Is.False);
            Assert.That(logs.Logs.Any(log => log.StartsWith("Debug|", StringComparison.Ordinal) && log.Contains("legacy singular lookup", StringComparison.Ordinal)), Is.True);
        }

        [Test]
        public void should_warn_for_plural_durable_provider_id_service_lookup()
        {
            var logs = ConfigureLogging();
            var repo = new StubEditionRepository
            {
                HardcoverMatches = new List<Edition> { new() { Id = 2, HardcoverEditionId = "hc:123" }, new() { Id = 1, HardcoverEditionId = "hc:123" } }
            };
            var service = new EditionService(repo, eventAggregator: null, LogManager.GetLogger("NzbDrone.Core.Books.EditionService"));

            var edition = service.GetEditionByProviderAndId("hc", "edition:123");

            Assert.That(edition?.Id, Is.EqualTo(1));
            Assert.That(logs.Logs.Any(log => log.StartsWith("Warn|", StringComparison.Ordinal) && log.Contains("legacy singular lookup", StringComparison.Ordinal)), Is.True);
        }

        [Test]
        public void edition_delete_should_use_the_supplied_rows_without_reloading_them()
        {
            var edition = new Edition { Id = 123, BookId = 42, Title = "Great Expectations" };
            var book = new Book
            {
                Id = 42,
                Title = "Great Expectations",
                Editions = new List<Edition> { edition }
            };
            var repo = new StubEditionRepository();
            var service = new EditionService(repo, new StubEventAggregator(), LogManager.GetCurrentClassLogger());

            service.DeleteMany(book.Editions);

            Assert.Multiple(() =>
            {
                Assert.That(repo.FindByBookCallCount, Is.Zero);
                Assert.That(repo.Deleted, Is.EqualTo(new[] { edition }));
            });
        }

        [Test]
        public void repository_should_not_warn_for_expected_plural_isbn_lookup()
        {
            var logs = ConfigureLogging();

            WithRepository(repository =>
            {
                var edition = repository.FindByIsbn("9780123456789");

                Assert.That(edition?.Id, Is.EqualTo(1));
            });

            Assert.That(logs.Logs.Any(log => log.StartsWith("Warn|", StringComparison.Ordinal)), Is.False);
        }

        [Test]
        public void repository_should_warn_for_plural_durable_provider_id_lookup()
        {
            var logs = ConfigureLogging();

            WithRepository(repository =>
            {
                var edition = repository.FindByHardcoverEditionId("edition:123");

                Assert.That(edition?.Id, Is.EqualTo(1));
            });

            Assert.That(logs.Logs.Any(log => log.StartsWith("Warn|", StringComparison.Ordinal) && log.Contains("legacy singular lookup", StringComparison.Ordinal)), Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void row_only_book_lookup_should_return_all_editions_without_joined_book_or_author(bool postgres)
        {
            var sql = new List<string>();

            WithRepository(repository =>
            {
                var rows = repository.FindRowsByBook(new[] { 1, 2, 4, 9999 });
                Assert.That(rows.Select(edition => edition.Id), Is.EquivalentTo(new[] { 1, 2, 4, 5 }));
                Assert.That(rows.All(edition => edition.LazyBook?.IsLoaded != true), Is.True);
                Assert.That(rows.Single(edition => edition.Id == 1).Asin, Is.EqualTo("B00EXPECTED"));

                var hydrated = repository.FindByBook(new[] { 1 });
                Assert.That(hydrated.Single().Book?.Author?.Id, Is.EqualTo(1));
            }, postgres, (_, commandText) => sql.Add(commandText));

            var editionQueries = sql.Where(command => command.Contains("Editions", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.That(editionQueries, Has.Count.EqualTo(2));
            Assert.That(sql.Count(command => command.Contains("ProviderAliasIndex", StringComparison.OrdinalIgnoreCase)), Is.Zero,
                "Provider IDs are stored on each edition row; loading editions must not re-read the derived index.");
            Assert.That(editionQueries[0], Does.Not.Contain("JOIN").IgnoreCase);
            Assert.That(editionQueries[1], Does.Contain("JOIN").IgnoreCase);
        }

        [Test]
        public void row_only_book_lookup_should_chunk_large_sqlite_id_sets()
        {
            var queryCount = 0;

            WithRepository(repository =>
            {
                var rows = repository.FindRowsByBook(Enumerable.Range(1, 950).Concat(new[] { 1 }));
                Assert.That(rows.Select(edition => edition.Id), Is.EquivalentTo(new[] { 1, 2, 3, 4 }));
            }, captureCommand: (_, commandText) =>
            {
                if (commandText.Contains("Editions", StringComparison.OrdinalIgnoreCase))
                {
                    queryCount++;
                }
            });

            Assert.That(queryCount, Is.EqualTo(2));
        }

        private static MemoryTarget ConfigureLogging()
        {
            var memoryTarget = new MemoryTarget("memory")
            {
                Layout = "${level}|${logger}|${message}"
            };

            var config = new LoggingConfiguration();
            config.AddRule(LogLevel.Debug, LogLevel.Fatal, memoryTarget, "NzbDrone.Core.Books.*");
            LogManager.Configuration = config;
            LogManager.ReconfigExistingLoggers();

            return memoryTarget;
        }

        [TestCase(false)]
        [TestCase(true)]
        public void displayed_edition_query_should_scope_by_author_book_ids_and_keep_monitored_semantics(bool postgres)
        {
            string capturedCommand = null;
            List<SqliteQueryPlanRow> plan = null;

            WithRepository(repository =>
            {
                var editions = repository.FindByAuthor(1).ToDictionary(edition => edition.BookId);
                editions[1].Title = "Dune";
                editions[1].Monitored = true;
                editions[2].Title = "Der Wüstenplanet";
                editions[2].Monitored = false;
                repository.SetFields(editions.Values.ToList(), edition => edition.Title, edition => edition.Monitored);

                Assert.That(repository.FindByAuthor(1, onlyMonitored: true).Select(edition => edition.BookId), Is.EquivalentTo(new[] { 1 }));
                Assert.That(repository.FindByAuthor(1).Select(edition => edition.BookId), Is.EquivalentTo(new[] { 1, 2 }));
                Assert.That(repository.FindByAuthor(2, onlyMonitored: true).Select(edition => edition.BookId), Is.EquivalentTo(new[] { 3 }));
                Assert.That(repository.FindByAuthor(999, onlyMonitored: true), Is.Empty, "A missing author row remains excluded");
                Assert.That(repository.FindByAuthor(1, onlyMonitored: true).Select(edition => edition.Id), Does.Not.Contain(4), "A book whose author row is missing remains excluded");
                Assert.That(repository.FindByAuthor(1, onlyMonitored: true).Select(edition => edition.Id), Does.Not.Contain(5), "An edition whose book row is missing remains excluded");
            }, postgres, (connection, commandTextValue) =>
            {
                if (capturedCommand != null ||
                    !commandTextValue.Contains("\"Editions\".\"Monitored\"", StringComparison.Ordinal))
                {
                    return;
                }

                capturedCommand = commandTextValue;
                if (!postgres)
                {
                    var explainSql = Regex.Replace(commandTextValue, "@[A-Za-z0-9_]+", "1");
                    plan = connection.Query<SqliteQueryPlanRow>("EXPLAIN QUERY PLAN " + explainSql).ToList();
                }
            });

            Assert.That(capturedCommand, Is.Not.Null);
            if (!postgres)
            {
                var details = string.Join(Environment.NewLine, plan.Select(row => row.Detail));
                Assert.That(details, Does.Contain("IX_Books_AuthorId_Id"), details);
                Assert.That(details, Does.Contain("IX_Editions_BookId_Monitored_Id"), details);
                Assert.That(details, Does.Not.Contain("IX_Editions_Monitored"), details);
            }
        }

        private sealed class SqliteQueryPlanRow
        {
            public int Id { get; set; }
            public int Parent { get; set; }
            public int NotUsed { get; set; }
            public string Detail { get; set; }
        }

        private static void WithRepository(Action<EditionRepository> action, bool postgres = false, Action<IDbConnection, string> captureCommand = null)
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"edition_legacy_lookup_{Guid.NewGuid():N}.db");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();

            string databaseName = null;
            if (postgres)
            {
                var adminString = Environment.GetEnvironmentVariable("CHAPTARR_TEST_POSTGRES_CONNECTION_STRING");
                if (string.IsNullOrWhiteSpace(adminString))
                {
                    Assert.Ignore("Set CHAPTARR_TEST_POSTGRES_CONNECTION_STRING to exercise the PostgreSQL edition query");
                }

                databaseName = "edition_query_" + Guid.NewGuid().ToString("N");
                using var admin = new NpgsqlConnection(adminString);
                admin.Open();
                admin.Execute($"CREATE DATABASE \"{databaseName}\"");
                connectionString = new NpgsqlConnectionStringBuilder(adminString) { Database = databaseName, Pooling = false }.ConnectionString;
            }

            IDbConnection OpenRawConnection()
            {
                IDbConnection connection = postgres ? new NpgsqlConnection(connectionString) : new SqliteConnection(connectionString);
                connection.Open();
                return connection;
            }

            IDbConnection OpenConnection()
            {
                var connection = OpenRawConnection();
                if (captureCommand == null)
                {
                    return connection;
                }

                return Chaptarr.Core.Test.MediaFiles.AuthorDeletionSafetyFixture.Proxy<IDbConnection>((method, args) =>
                {
                    if (method.Name == nameof(IDisposable.Dispose))
                    {
                        connection.Dispose();
                        return null;
                    }

                    var result = method.Invoke(connection, args);
                    if (result is IDbCommand command)
                    {
                        return Chaptarr.Core.Test.MediaFiles.AuthorDeletionSafetyFixture.Proxy<IDbCommand>((commandMethod, commandArgs) =>
                        {
                            if (commandMethod.Name == nameof(IDbCommand.ExecuteReader))
                            {
                                captureCommand(connection, command.CommandText);
                            }

                            return commandMethod.Invoke(command, commandArgs);
                        });
                    }

                    return result;
                });
            }

            try
            {
                using (var connection = OpenRawConnection())
                {
                    CreateSchema<Author>(connection);
                    CreateSchema<Book>(connection);
                    CreateSchema<Edition>(connection);
                    CreateSchema<ProviderAlias>(connection);

                    connection.Execute(@"
CREATE INDEX ""IX_Books_AuthorId_Id"" ON ""Books"" (""AuthorId"", ""Id"");
CREATE INDEX ""IX_Editions_Monitored"" ON ""Editions"" (""Monitored"");
CREATE INDEX ""IX_Editions_BookId_Monitored_Id"" ON ""Editions"" (""BookId"", ""Monitored"", ""Id"");
INSERT INTO ""Authors"" (""Id"") VALUES (1), (2);
INSERT INTO ""Books"" (""Id"", ""AuthorId"", ""MediaType"") VALUES
    (1, 1, 0),
    (2, 1, 1),
    (3, 2, 0),
    (4, 999, 0);
UPDATE ""Books"" SET ""AudiobookMonitored"" = FALSE, ""AnyEditionOk"" = TRUE;
INSERT INTO ""Editions"" (""Id"", ""BookId"", ""Monitored"", ""Asin"", ""AudibleASIN"", ""Isbn13"", ""HardcoverEditionId"") VALUES
    (1, 1, TRUE, 'B00EXPECTED', NULL, '9780123456789', 'edition:123'),
    (2, 2, FALSE, 'B00EXPECTED', NULL, '9780123456789', 'edition:123'),
    (3, 3, TRUE, NULL, NULL, NULL, NULL),
    (4, 4, TRUE, NULL, NULL, NULL, NULL),
    (5, 9999, TRUE, NULL, NULL, NULL, NULL);");
                }

                var database = Chaptarr.Core.Test.MediaFiles.AuthorDeletionSafetyFixture.Proxy<IMainDatabase>((method, args) => method.Name switch
                {
                    nameof(IMainDatabase.OpenConnection) => OpenConnection(),
                    "get_DatabaseType" => postgres ? DatabaseType.PostgreSQL : DatabaseType.SQLite,
                    _ => throw new NotImplementedException(method.Name)
                });

                action(new EditionRepository(database, new StubEventAggregator()));
            }
            finally
            {
                if (postgres && databaseName != null)
                {
                    using var admin = new NpgsqlConnection(Environment.GetEnvironmentVariable("CHAPTARR_TEST_POSTGRES_CONNECTION_STRING"));
                    admin.Open();
                    admin.Execute($"DROP DATABASE \"{databaseName}\"");
                }
                else if (File.Exists(databasePath))
                {
                    File.Delete(databasePath);
                }
            }
        }

        private static void CreateSchema<T>(IDbConnection connection)
            where T : ModelBase
        {
            var excluded = TableMapping.Mapper.ExcludeProperties(typeof(T))
                .Select(property => property.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var tableName = TableMapping.Mapper.TableNameMapping(typeof(T));
            var columns = typeof(T)
                .GetProperties()
                .Where(property => property.Name == nameof(ModelBase.Id) || (property.IsMappableProperty() && !excluded.Contains(property.Name)))
                .GroupBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Select(property => property.Name == nameof(ModelBase.Id)
                    ? "\"Id\" INTEGER PRIMARY KEY"
                    : $"\"{property.Name}\" {(connection is NpgsqlConnection && (Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType) == typeof(bool) ? "BOOLEAN" : GetSqliteColumnType(property))} NULL")
                .ToList();

            connection.Execute($"CREATE TABLE \"{tableName}\" ({string.Join(", ", columns)});");
        }

        private static string GetSqliteColumnType(System.Reflection.PropertyInfo property)
        {
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

            if (type.IsEnum || type == typeof(int) || type == typeof(long) || type == typeof(bool))
            {
                return "INTEGER";
            }

            return "TEXT";
        }
    }
}
