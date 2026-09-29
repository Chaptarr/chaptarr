using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using Npgsql;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class ProviderAliasRepositoryRetryFixture
    {
        private static PostgresException PgError(string sqlState) =>
            new PostgresException("boom", "ERROR", "ERROR", sqlState);

        [Test]
        public void should_retry_after_a_postgres_unique_violation_and_then_succeed()
        {
            var calls = 0;

            ProviderAliasRepository.RetryOnUniqueViolation(
                () =>
                {
                    calls++;
                    if (calls == 1)
                    {
                        throw PgError(PostgresErrorCodes.UniqueViolation);
                    }
                },
                maxAttempts: 3);

            Assert.That(calls, Is.EqualTo(2));
        }

        [Test]
        public void should_retry_after_a_sqlite_unique_violation()
        {
            var calls = 0;

            ProviderAliasRepository.RetryOnUniqueViolation(
                () =>
                {
                    calls++;
                    if (calls == 1)
                    {
                        throw new SqliteException("SQLite Error 19: 'UNIQUE constraint failed: ProviderAliasIndex.EntityType'.", 19);
                    }
                },
                maxAttempts: 3);

            Assert.That(calls, Is.EqualTo(2));
        }

        [Test]
        public void should_find_the_violation_when_it_is_wrapped()
        {
            Assert.That(
                ProviderAliasRepository.IsUniqueViolation(new InvalidOperationException("wrapper", PgError(PostgresErrorCodes.UniqueViolation))),
                Is.True);
        }

        [Test]
        public void should_not_retry_other_database_errors()
        {
            var calls = 0;

            Assert.Throws<PostgresException>(() => ProviderAliasRepository.RetryOnUniqueViolation(
                () =>
                {
                    calls++;
                    throw PgError(PostgresErrorCodes.UndefinedTable);
                },
                maxAttempts: 3));

            Assert.That(calls, Is.EqualTo(1), "only unique violations are retried");
        }

        [Test]
        public void should_not_retry_sqlite_constraint_errors_that_are_not_unique_violations()
        {
            Assert.That(ProviderAliasRepository.IsUniqueViolation(new SqliteException("SQLite Error 19: 'NOT NULL constraint failed: X.Y'.", 19)), Is.False);
        }

        [Test]
        public void should_give_up_and_rethrow_after_the_last_attempt()
        {
            var calls = 0;

            Assert.Throws<PostgresException>(() => ProviderAliasRepository.RetryOnUniqueViolation(
                () =>
                {
                    calls++;
                    throw PgError(PostgresErrorCodes.UniqueViolation);
                },
                maxAttempts: 3));

            Assert.That(calls, Is.EqualTo(3), "a persistent violation is surfaced, not swallowed");
        }

        private sealed class NoopEventAggregator : IEventAggregator
        {
            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
            }
        }

        private sealed class FailingFirstAttemptRepository : ProviderAliasRepository
        {
            private readonly Func<int, Exception> _failure;

            public FailingFirstAttemptRepository(IMainDatabase database, Func<int, Exception> failure)
                : base(database, new NoopEventAggregator())
            {
                _failure = failure;
            }

            public int Attempts { get; private set; }
            public List<List<ProviderAlias>> ItemsPerAttempt { get; } = new List<List<ProviderAlias>>();

            internal override void ReplaceAliasesOnce(string entityType, int entityId, string scope, List<ProviderAlias> items)
            {
                Attempts++;
                ItemsPerAttempt.Add(items);

                var failure = _failure(Attempts);
                if (failure != null)
                {
                    throw failure;
                }
            }
        }

        // The repository constructor probes Database.DatabaseType, which opens a connection; hand it an
        // in-memory SQLite one. The subclass overrides ReplaceAliasesOnce, so no query ever runs on it.
        private static MainDatabase UnusedDatabase() => new MainDatabase(new Database("main", () =>
        {
            var conn = new SqliteConnection("Data Source=:memory:");
            conn.Open();
            return conn;
        }));

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            if (TableMapping.Mapper.TableMap.Count == 0)
            {
                TableMapping.Map();
            }
        }

        [Test]
        public void replace_aliases_should_go_through_the_retry_and_pass_the_same_aliases_each_attempt()
        {
            var sut = new FailingFirstAttemptRepository(
                UnusedDatabase(),
                attempt => attempt == 1 ? PgError(PostgresErrorCodes.UniqueViolation) : null);

            var aliases = new List<ProviderAlias>
            {
                new ProviderAlias { EntityType = "Author", EntityId = 7, Scope = "author", Provider = "hc", NormalizedProviderId = "123" }
            };

            Assert.DoesNotThrow(() => sut.ReplaceAliases("Author", 7, "author", aliases));

            Assert.That(sut.Attempts, Is.EqualTo(2), "the wiring must retry a unique violation");
            Assert.That(sut.ItemsPerAttempt.Select(x => x.Count), Is.EqualTo(new[] { 1, 1 }));
        }

        [Test]
        public void replace_aliases_should_not_retry_other_errors()
        {
            var sut = new FailingFirstAttemptRepository(UnusedDatabase(), attempt => PgError(PostgresErrorCodes.UndefinedTable));

            Assert.Throws<PostgresException>(() => sut.ReplaceAliases("Author", 7, "author", new List<ProviderAlias>()));

            Assert.That(sut.Attempts, Is.EqualTo(1));
        }

        [Test]
        public void replace_aliases_should_replace_the_entitys_rows_in_a_real_database_and_leave_other_entities_alone()
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"provider_alias_{Guid.NewGuid():N}.db");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();

            try
            {
                using (var setup = new SqliteConnection(connectionString))
                {
                    setup.Open();
                    setup.Execute(@"
                        CREATE TABLE ""ProviderAliasIndex"" (
                            ""Id"" INTEGER PRIMARY KEY,
                            ""EntityType"" TEXT NOT NULL,
                            ""EntityId"" INTEGER NOT NULL,
                            ""Scope"" TEXT NOT NULL,
                            ""Provider"" TEXT NOT NULL,
                            ""NormalizedProviderId"" TEXT NOT NULL,
                            ""CreatedAt"" TEXT NOT NULL,
                            ""UpdatedAt"" TEXT NOT NULL
                        );
                        CREATE UNIQUE INDEX ""IX_ProviderAliasIndex_Unique"" ON ""ProviderAliasIndex"" (""EntityType"", ""EntityId"", ""Scope"", ""Provider"", ""NormalizedProviderId"");
                        INSERT INTO ""ProviderAliasIndex"" (""EntityType"", ""EntityId"", ""Scope"", ""Provider"", ""NormalizedProviderId"", ""CreatedAt"", ""UpdatedAt"")
                        VALUES ('Author', 7, 'author', 'hc', 'old', '2026-01-01', '2026-01-01'),
                               ('Author', 8, 'author', 'hc', 'other-author', '2026-01-01', '2026-01-01');");
                }

                var database = new MainDatabase(new Database("main", () =>
                {
                    var conn = new SqliteConnection(connectionString);
                    conn.Open();
                    return conn;
                }));
                var sut = new ProviderAliasRepository(database, new NoopEventAggregator());
                var now = DateTime.UtcNow;

                var aliases = new List<ProviderAlias>
                {
                    new ProviderAlias { EntityType = "Author", EntityId = 7, Scope = "author", Provider = "hc", NormalizedProviderId = "123", CreatedAt = now, UpdatedAt = now },
                    new ProviderAlias { EntityType = "Author", EntityId = 7, Scope = "author", Provider = "gr", NormalizedProviderId = "456", CreatedAt = now, UpdatedAt = now }
                };

                sut.ReplaceAliases("Author", 7, "author", aliases);
                sut.ReplaceAliases("Author", 7, "author", aliases); // idempotent: replacing twice must not collide with itself

                using (var verify = new SqliteConnection(connectionString))
                {
                    verify.Open();
                    var author7 = verify.Query<string>(@"SELECT ""Provider"" || ':' || ""NormalizedProviderId"" FROM ""ProviderAliasIndex"" WHERE ""EntityId"" = 7 ORDER BY 1;").ToList();
                    var author8 = verify.Query<string>(@"SELECT ""NormalizedProviderId"" FROM ""ProviderAliasIndex"" WHERE ""EntityId"" = 8;").ToList();

                    Assert.That(author7, Is.EqualTo(new[] { "gr:456", "hc:123" }), "old alias replaced, new ones inserted once each");
                    Assert.That(author8, Is.EqualTo(new[] { "other-author" }), "another entity's aliases are untouched");
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
    }
}
