using System;
using System.IO;
using Dapper;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore.Migration;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    public class PendingAuthorNotReadyAttemptCliffRepairFixture
    {
        [Test]
        public void should_reopen_only_the_historical_not_ready_100_attempt_cliff()
        {
            WithDatabase(connection =>
            {
                var retryAt = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);
                using var transaction = connection.BeginTransaction();
                var changed = PendingAuthorNotReadyAttemptCliffRepair.Apply(connection, transaction, retryAt);
                transaction.Commit();

                Assert.That(changed, Is.EqualTo(2));

                var reopened = connection.QuerySingle<PendingRow>(@"SELECT * FROM ""PendingAuthorImport"" WHERE ""Id"" = 1;");
                Assert.That(reopened.OverallStatus, Is.EqualTo((int)PendingImportStatus.Retrying));
                Assert.That(reopened.AudiobookStatus, Is.EqualTo((int)PendingImportStatus.Retrying));
                Assert.That(reopened.EbookStatus, Is.EqualTo((int)PendingImportStatus.NotRequested));
                Assert.That(reopened.AttemptCount, Is.EqualTo(100));
                Assert.That(reopened.MaxAttempts, Is.Zero);
                Assert.That(reopened.LastError, Is.EqualTo(PendingAuthorImportRetryReason.AuthorNotYetAvailable));
                Assert.That(reopened.NextAttemptAt, Is.EqualTo(retryAt));

                AssertUnchanged(connection, 2, "provider_redirect_unresolvable: Redirect is unresolved");
                AssertUnchanged(connection, 3, "Cancelled by user");
                AssertUnchanged(connection, 4, "metadata request timed out");
                AssertUnchanged(connection, 5, PendingAuthorImportRetryReason.AuthorNotYetAvailable);
                Assert.That(connection.QuerySingle<int>(@"SELECT ""OverallStatus"" FROM ""PendingAuthorImport"" WHERE ""Id"" = 6;"),
                    Is.EqualTo((int)PendingImportStatus.Retrying));
                Assert.That(connection.QuerySingle<int>(@"SELECT ""MaxAttempts"" FROM ""PendingAuthorImport"" WHERE ""Id"" = 6;"), Is.Zero);
                AssertUnchanged(connection, 7, PendingAuthorImportRetryReason.AuthorNotYetAvailable);
                Assert.That(connection.QuerySingle<int>(@"SELECT ""OverallStatus"" FROM ""PendingAuthorImport"" WHERE ""Id"" = 8;"),
                    Is.EqualTo((int)PendingImportStatus.Pending));
                AssertUnchanged(connection, 9, PendingAuthorImportRetryReason.AuthorNotYetAvailable);
                Assert.That(connection.QuerySingle<int>(@"SELECT ""OverallStatus"" FROM ""PendingAuthorImport"" WHERE ""Id"" = 10;"),
                    Is.EqualTo((int)PendingImportStatus.Retrying));
                AssertUnchanged(connection, 11, PendingAuthorImportRetryReason.AuthorNotYetAvailable);
                AssertUnchanged(connection, 12, "Cancelled by user");
            });
        }

        private static void AssertUnchanged(SqliteConnection connection, int id, string expectedError)
        {
            var row = connection.QuerySingle<PendingRow>(@"SELECT * FROM ""PendingAuthorImport"" WHERE ""Id"" = @Id;", new { Id = id });
            Assert.That(row.OverallStatus, Is.EqualTo((int)PendingImportStatus.Failed));
            Assert.That(row.MaxAttempts, Is.EqualTo(id == 5 ? 50 : 100));
            Assert.That(row.LastError, Is.EqualTo(expectedError));
        }

        private static void WithDatabase(Action<SqliteConnection> test)
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"pending_author_cliff_repair_{Guid.NewGuid():N}.db");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();

            try
            {
                using var connection = new SqliteConnection(connectionString);
                connection.Open();
                connection.Execute(@"
                    CREATE TABLE ""PendingAuthorImport"" (
                        ""Id"" INTEGER PRIMARY KEY,
                        ""ProviderId"" TEXT,
                        ""AudiobookStatus"" INTEGER,
                        ""EbookStatus"" INTEGER,
                        ""OverallStatus"" INTEGER,
                        ""CreatedAt"" DATETIME NOT NULL,
                        ""NextAttemptAt"" DATETIME,
                        ""UpdatedAt"" DATETIME,
                        ""AttemptCount"" INTEGER,
                        ""MaxAttempts"" INTEGER,
                        ""LastError"" TEXT
                    );

                    INSERT INTO ""PendingAuthorImport""
                        (""Id"", ""ProviderId"", ""AudiobookStatus"", ""EbookStatus"", ""OverallStatus"", ""CreatedAt"", ""NextAttemptAt"", ""UpdatedAt"", ""AttemptCount"", ""MaxAttempts"", ""LastError"")
                    VALUES
                        (1, 'gr:1', @Failed, @NotRequested, @Failed, @Old, @Old, @Old, 100, 100, @NotReady),
                        (2, 'gr:2', @Failed, @NotRequested, @Failed, @Old, @Old, @Old, 100, 100, 'provider_redirect_unresolvable: Redirect is unresolved'),
                        (3, 'gr:3', @Failed, @NotRequested, @Failed, @Old, @Old, @Old, 100, 100, 'Cancelled by user'),
                        (4, 'gr:4', @Failed, @NotRequested, @Failed, @Old, @Old, @Old, 100, 100, 'metadata request timed out'),
                        (5, 'gr:5', @Failed, @NotRequested, @Failed, @Old, @Old, @Old, 50, 50, @NotReady),
                        (6, 'gr:6', @Pending, @NotRequested, @Retrying, @Old, @Old, @Old, 100, 100, @NotReady),
                        (7, 'gr:shared', @Failed, @NotRequested, @Failed, @Old, @Old, @Old, 100, 100, @NotReady),
                        (8, 'gr:shared', @Pending, @NotRequested, @Pending, @Newer, @Old, @Old, 0, 0, NULL),
                        (9, 'gr:duplicate-failed', @Failed, @NotRequested, @Failed, @Old, @Old, @Old, 100, 100, @NotReady),
                        (10, 'gr:duplicate-failed', @Failed, @NotRequested, @Failed, @Newer, @Old, @Old, 100, 100, @NotReady),
                        (11, 'gr:newer-terminal', @Failed, @NotRequested, @Failed, @Old, @Old, @Old, 100, 100, @NotReady),
                        (12, 'gr:newer-terminal', @Failed, @NotRequested, @Failed, @Newer, @Old, @Old, 100, 100, 'Cancelled by user');

                    CREATE UNIQUE INDEX ""UX_PendingAuthorImport_Active""
                        ON ""PendingAuthorImport""(""ProviderId"")
                        WHERE ""OverallStatus"" IN (1, 2, 3);",
                    new
                    {
                        Failed = PendingImportStatus.Failed,
                        Pending = PendingImportStatus.Pending,
                        Retrying = PendingImportStatus.Retrying,
                        NotRequested = PendingImportStatus.NotRequested,
                        Old = DateTime.UtcNow.AddDays(-1),
                        Newer = DateTime.UtcNow,
                        NotReady = PendingAuthorImportRetryReason.AuthorNotYetAvailable
                    });

                test(connection);
            }
            finally
            {
                if (File.Exists(databasePath))
                {
                    File.Delete(databasePath);
                }
            }
        }

        private sealed class PendingRow
        {
            public int AudiobookStatus { get; set; }
            public int EbookStatus { get; set; }
            public int OverallStatus { get; set; }
            public DateTime NextAttemptAt { get; set; }
            public int AttemptCount { get; set; }
            public int MaxAttempts { get; set; }
            public string LastError { get; set; }
        }
    }
}
