using System;
using Microsoft.Data.Sqlite;
using Npgsql;
using NUnit.Framework;
using NzbDrone.Core.Books;

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
    }
}
