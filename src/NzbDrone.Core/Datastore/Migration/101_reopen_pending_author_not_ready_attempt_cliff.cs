using System;
using System.Data;
using Dapper;
using FluentMigrator;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(101)]
    public class reopen_pending_author_not_ready_attempt_cliff : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("PendingAuthorImport").Exists())
            {
                return;
            }

            Execute.WithConnection((connection, transaction) =>
            {
                PendingAuthorNotReadyAttemptCliffRepair.Apply(connection, transaction, DateTime.UtcNow);
            });
        }
    }

    internal static class PendingAuthorNotReadyAttemptCliffRepair
    {
        public static int Apply(IDbConnection connection, IDbTransaction transaction, DateTime retryAt)
        {
            // Normalize active legacy rows to the compatibility marker that the API
            // now documents. This does not activate or reopen any row.
            connection.Execute(@"
                UPDATE ""PendingAuthorImport""
                SET ""MaxAttempts"" = 0
                WHERE ""OverallStatus"" IN (@Pending, @InProgress, @Retrying)
                  AND ""MaxAttempts"" <> 0;",
                new
                {
                    Pending = PendingImportStatus.Pending,
                    InProgress = PendingImportStatus.InProgress,
                    Retrying = PendingImportStatus.Retrying
                },
                transaction);

            return connection.Execute(@"
                UPDATE ""PendingAuthorImport"" AS candidate
                SET ""AudiobookStatus"" = CASE
                        WHEN candidate.""AudiobookStatus"" = @Failed THEN @Retrying
                        ELSE candidate.""AudiobookStatus""
                    END,
                    ""EbookStatus"" = CASE
                        WHEN candidate.""EbookStatus"" = @Failed THEN @Retrying
                        ELSE candidate.""EbookStatus""
                    END,
                    ""OverallStatus"" = @Retrying,
                    ""NextAttemptAt"" = @RetryAt,
                    ""UpdatedAt"" = @RetryAt,
                    ""MaxAttempts"" = 0
                WHERE candidate.""OverallStatus"" = @Failed
                  AND candidate.""AttemptCount"" >= candidate.""MaxAttempts""
                  AND candidate.""MaxAttempts"" = 100
                  AND candidate.""LastError"" = @NotReadyError
                  AND candidate.""Id"" = (
                      SELECT latest.""Id""
                      FROM ""PendingAuthorImport"" AS latest
                      WHERE latest.""ProviderId"" = candidate.""ProviderId""
                      ORDER BY latest.""CreatedAt"" DESC, latest.""Id"" DESC
                      LIMIT 1
                  )
                  AND NOT EXISTS (
                      SELECT 1
                      FROM ""PendingAuthorImport"" AS active
                      WHERE active.""ProviderId"" = candidate.""ProviderId""
                        AND active.""Id"" <> candidate.""Id""
                        AND active.""OverallStatus"" IN (@Pending, @InProgress, @Retrying)
                  );",
                new
                {
                    Failed = PendingImportStatus.Failed,
                    Pending = PendingImportStatus.Pending,
                    InProgress = PendingImportStatus.InProgress,
                    Retrying = PendingImportStatus.Retrying,
                    RetryAt = retryAt,
                    NotReadyError = PendingAuthorImportRetryReason.AuthorNotYetAvailable
                },
                transaction);
        }
    }
}
