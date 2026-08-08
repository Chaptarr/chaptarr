using Microsoft.Data.Sqlite;
using System.IO;
using NLog;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Backup
{
    public interface IMakeDatabaseBackup
    {
        void BackupDatabase(IDatabase database, string targetDirectory);
    }

    public class MakeDatabaseBackup : IMakeDatabaseBackup
    {
        private readonly Logger _logger;

        public MakeDatabaseBackup(Logger logger)
        {
            _logger = logger;
        }

        public void BackupDatabase(IDatabase database, string targetDirectory)
        {
            var sourceConnectionString = "";
            using (var db = database.OpenConnection())
            {
                sourceConnectionString = db.ConnectionString;
            }

            var backupConnectionStringBuilder = new SqliteConnectionStringBuilder(sourceConnectionString);

            backupConnectionStringBuilder.DataSource = Path.Combine(targetDirectory, Path.GetFileName(backupConnectionStringBuilder.DataSource));

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
        }
    }
}
