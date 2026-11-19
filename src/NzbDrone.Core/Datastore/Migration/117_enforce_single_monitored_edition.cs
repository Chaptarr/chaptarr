using System.Diagnostics;
using Dapper;
using FluentMigrator;
using Npgsql;
using NzbDrone.Core.Datastore.Migration.Framework;
using NzbDrone.Core.Housekeeping.Housekeepers;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(117)]
    public class enforce_single_monitored_edition : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Execute.WithConnection((connection, transaction) =>
            {
                var timer = Stopwatch.StartNew();
                // Editions without a parent are invalid and cannot participate in
                // selection repair, but are still covered by the unique index.
                var removed = CleanupOrphanedEditions.Cleanup(connection, transaction, MigrationController.CommandTimeoutSeconds);
                var repaired = FixMultipleMonitoredEditions.Repair(connection, transaction, connection is NpgsqlConnection ? DatabaseType.PostgreSQL : DatabaseType.SQLite, _logger, duplicatesOnly: true, commandTimeout: MigrationController.CommandTimeoutSeconds);
                connection.Execute("CREATE UNIQUE INDEX \"IX_Editions_OneMonitoredPerBook\" ON \"Editions\" (\"BookId\") WHERE \"Monitored\" = true", transaction: transaction, commandTimeout: MigrationController.CommandTimeoutSeconds);

                // Reject a stale insert/rehome and a parent deletion whose normal
                // edition cleanup failed. Do not cascade past EditionDeleted events.
                if (connection is NpgsqlConnection)
                {
                    connection.Execute(@"ALTER TABLE ""Editions""
                        ADD CONSTRAINT ""FK_Editions_Books"" FOREIGN KEY (""BookId"")
                        REFERENCES ""Books"" (""Id"") ON DELETE RESTRICT", transaction: transaction, commandTimeout: MigrationController.CommandTimeoutSeconds);
                }
                else
                {
                    // SQLite cannot add a foreign key in place, and its enforcement
                    // is connection scoped. Equivalent guards avoid rebuilding the
                    // edition table and preserve its FTS triggers and indexes.
                    connection.Execute(@"CREATE TRIGGER edition_book_reference_insert BEFORE INSERT ON ""Editions""
                        WHEN NEW.""BookId"" IS NULL OR NOT EXISTS (SELECT 1 FROM ""Books"" WHERE ""Id"" = NEW.""BookId"")
                        BEGIN SELECT RAISE(ABORT, 'FK_Editions_Books: edition requires an existing book'); END;
                        CREATE TRIGGER edition_book_reference_update BEFORE UPDATE OF ""BookId"" ON ""Editions""
                        WHEN NEW.""BookId"" IS NOT OLD.""BookId"" AND
                            (NEW.""BookId"" IS NULL OR NOT EXISTS (SELECT 1 FROM ""Books"" WHERE ""Id"" = NEW.""BookId""))
                        BEGIN SELECT RAISE(ABORT, 'FK_Editions_Books: edition requires an existing book'); END;
                        CREATE TRIGGER book_edition_reference_delete BEFORE DELETE ON ""Books""
                        WHEN EXISTS (SELECT 1 FROM ""Editions"" WHERE ""BookId"" = OLD.""Id"")
                        BEGIN SELECT RAISE(ABORT, 'FK_Editions_Books: remove its editions before deleting the book'); END;
                        CREATE TRIGGER book_edition_reference_update BEFORE UPDATE OF ""Id"" ON ""Books""
                        WHEN NEW.""Id"" <> OLD.""Id"" AND EXISTS (SELECT 1 FROM ""Editions"" WHERE ""BookId"" = OLD.""Id"")
                        BEGIN SELECT RAISE(ABORT, 'FK_Editions_Books: editions still reference the book'); END;", transaction: transaction, commandTimeout: MigrationController.CommandTimeoutSeconds);
                }
                _logger.Info("Edition integrity migration: removed {0} orphaned editions, repaired {1} books, elapsed {2} ms", removed, repaired, timer.ElapsedMilliseconds);
            });
        }
    }
}
