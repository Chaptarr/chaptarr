using System;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using NLog;
using Npgsql;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Backup
{
    public interface IMakeDatabaseBackup
    {
        void BackupDatabase(IDatabase database, string targetDirectory);
        void BackupDatabase(IDatabase database, string targetDirectory, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BackupDatabase(database, targetDirectory);
        }
    }

    public class MakeDatabaseBackup : IMakeDatabaseBackup
    {
        private readonly Logger _logger;
        private readonly IConnectionStringFactory _connectionStrings;

        public MakeDatabaseBackup(Logger logger, IConnectionStringFactory connectionStringFactory = null)
        {
            _logger = logger;
            _connectionStrings = connectionStringFactory;
        }

        public void BackupDatabase(IDatabase database, string targetDirectory)
        {
            BackupDatabase(database, targetDirectory, CancellationToken.None);
        }

        public void BackupDatabase(IDatabase database, string targetDirectory, CancellationToken cancellationToken)
        {
            var timer = Stopwatch.StartNew();
            cancellationToken.ThrowIfCancellationRequested();
            var sourceConnectionString = "";
            var postgres = false;
            using (var db = database.OpenConnection())
            {
                sourceConnectionString = db.ConnectionString;
                postgres = db is NpgsqlConnection;
                if (db is NpgsqlConnection connection)
                {
                    // Opened Npgsql connections remove passwords; reuse the existing configured credentials.
                    sourceConnectionString = new[] { _connectionStrings?.MainDbConnection, _connectionStrings?.LogDbConnection, _connectionStrings?.CacheDbConnection }
                        .Where(info => info?.DatabaseType == DatabaseType.PostgreSQL)
                        .Select(info => new NpgsqlConnectionStringBuilder(info.ConnectionString))
                        .SingleOrDefault(info => info.Host == connection.Host && info.Port == connection.Port && info.Database == connection.Database && info.Username == connection.UserName)?.ConnectionString
                        ?? throw new InvalidOperationException("PostgreSQL backup requires the configured database connection factory.");
                }
            }
            if (postgres)
            {
                PostgresBackup.Dump(sourceConnectionString, Path.Combine(targetDirectory, "database.dump"), cancellationToken);
                return;
            }

            var backupConnectionStringBuilder = new SqliteConnectionStringBuilder(sourceConnectionString);

            backupConnectionStringBuilder.DataSource = Path.Combine(targetDirectory, Path.GetFileName(backupConnectionStringBuilder.DataSource));
            // Snapshot files must release their handles before archiving or restoring on Windows.
            backupConnectionStringBuilder.Pooling = false;
            backupConnectionStringBuilder.Mode = SqliteOpenMode.ReadWriteCreate;

            // We MUST use truncate journal mode (not WAL) when restoring backups to avoid WAL/page-size issues.
            // Microsoft.Data.Sqlite does not expose JournalMode in the connection string; we'll set it via PRAGMA after backup.

            using (var sourceConnection = new SqliteConnection(sourceConnectionString))
            using (var backupConnection = new SqliteConnection(backupConnectionStringBuilder.ToString()))
            {
                sourceConnection.Open();
                backupConnection.Open();
                // Perform full backup (copy all pages) using Microsoft.Data.Sqlite API
                sourceConnection.BackupDatabase(backupConnection);

                // The backup changes the journal_mode, force it to truncate again.
                using (var command = backupConnection.CreateCommand())
                {
                    command.CommandText = "PRAGMA journal_mode=TRUNCATE";
                    command.ExecuteNonQuery();
                }
            }
            _logger.Info("SQLite snapshot created: {0} bytes in {1} ms", new FileInfo(backupConnectionStringBuilder.DataSource).Length, timer.ElapsedMilliseconds);
        }
    }
}
