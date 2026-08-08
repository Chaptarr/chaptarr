using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
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

            public List<Edition> FindAllByAsin(string asin) => AsinMatches;
            public List<Edition> FindAllByAsin(string asin, BookMediaType? mediaType) => AsinMatches;
            public List<Edition> FindAllByIsbn(string isbn) => IsbnMatches;
            public List<Edition> FindAllByHardcoverEditionId(string hardcoverEditionId) => HardcoverMatches;

            public Edition FindByIsbn(string isbn) => FindAllByIsbn(isbn).OrderBy(e => e.Id).FirstOrDefault();
            public Edition FindByHardcoverEditionId(string hardcoverEditionId) => FindAllByHardcoverEditionId(hardcoverEditionId).OrderBy(e => e.Id).FirstOrDefault();

            public IEnumerable<Edition> All() => throw new NotImplementedException();
            public int Count() => throw new NotImplementedException();
            public Edition Find(int id) => throw new NotImplementedException();
            public Edition Get(int id) => throw new NotImplementedException();
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
            public void SetFields(IList<Edition> models, params System.Linq.Expressions.Expression<Func<Edition, object>>[] properties) => throw new NotImplementedException();
            public void DeleteMany(List<Edition> model) => throw new NotImplementedException();
            public void DeleteMany(IEnumerable<int> ids) => throw new NotImplementedException();
            public void Purge(bool vacuum = false) => throw new NotImplementedException();
            public bool HasItems() => throw new NotImplementedException();
            public Edition Single() => throw new NotImplementedException();
            public Edition SingleOrDefault() => throw new NotImplementedException();
            public PagingSpec<Edition> GetPaged(PagingSpec<Edition> pagingSpec) => throw new NotImplementedException();
            public List<Edition> GetAllMonitoredEditions() => throw new NotImplementedException();
            public Edition FindByForeignEditionId(string foreignEditionId) => throw new NotImplementedException();
            public List<Edition> FindAllByForeignEditionId(string foreignEditionId) => throw new NotImplementedException();
            public Edition FindByGoodreadsEditionId(long goodreadsEditionId) => throw new NotImplementedException();
            public List<Edition> FindAllByGoodreadsEditionId(long goodreadsEditionId) => throw new NotImplementedException();
            public Edition FindByOpenLibraryEditionId(string openLibraryEditionId) => throw new NotImplementedException();
            public List<Edition> FindAllByOpenLibraryEditionId(string openLibraryEditionId) => throw new NotImplementedException();
            public Edition FindByGoogleBooksEditionId(string googleBooksEditionId) => throw new NotImplementedException();
            public List<Edition> FindAllByGoogleBooksEditionId(string googleBooksEditionId) => throw new NotImplementedException();
            public List<Edition> FindByBook(IEnumerable<int> ids) => throw new NotImplementedException();
            public List<Edition> FindByAuthor(int id) => throw new NotImplementedException();
            public List<Edition> FindByAuthorId(int id, bool onlyMonitored) => throw new NotImplementedException();
            public Edition FindByTitle(int authorId, string title) => throw new NotImplementedException();
            public List<Edition> GetEditionsForRefresh(int bookId) => throw new NotImplementedException();
            public List<Edition> SetMonitored(Edition edition, bool isManualSelection = false) => throw new NotImplementedException();
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
                HardcoverMatches = new List<Edition> { new() { Id = 2 }, new() { Id = 1 } }
            };
            var service = new EditionService(repo, eventAggregator: null, LogManager.GetLogger("NzbDrone.Core.Books.EditionService"));

            var edition = service.GetEditionByProviderAndId("hc", "edition:123");

            Assert.That(edition?.Id, Is.EqualTo(1));
            Assert.That(logs.Logs.Any(log => log.StartsWith("Warn|", StringComparison.Ordinal) && log.Contains("legacy singular lookup", StringComparison.Ordinal)), Is.True);
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

        private static void WithRepository(Action<EditionRepository> action)
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"edition_legacy_lookup_{Guid.NewGuid():N}.db");
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
                    CreateSchema<Author>(connection);
                    CreateSchema<Book>(connection);
                    CreateSchema<Edition>(connection);

                    connection.Execute("INSERT INTO \"Authors\" (\"Id\") VALUES (1);");
                    connection.Execute("INSERT INTO \"Books\" (\"Id\", \"AuthorId\", \"MediaType\") VALUES (1, 1, 0), (2, 1, 1);");
                    connection.Execute("INSERT INTO \"Editions\" (\"Id\", \"BookId\", \"Asin\", \"AudibleASIN\", \"Isbn13\", \"HardcoverEditionId\") VALUES " +
                                       "(1, 1, 'B00EXPECTED', NULL, '9780123456789', 'edition:123'), " +
                                       "(2, 2, 'B00EXPECTED', NULL, '9780123456789', 'edition:123');");
                }

                var database = new Database("main", () =>
                {
                    var conn = new SqliteConnection(connectionString);
                    conn.Open();
                    return conn;
                });

                action(new EditionRepository(new MainDatabase(database), new StubEventAggregator()));
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

        private static void CreateSchema<T>(SqliteConnection connection)
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
                    ? "\"Id\" INTEGER PRIMARY KEY AUTOINCREMENT"
                    : $"\"{property.Name}\" {GetSqliteColumnType(property)} NULL")
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
