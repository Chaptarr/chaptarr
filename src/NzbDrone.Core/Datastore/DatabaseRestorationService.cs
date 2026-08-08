using System;
using System.IO;
using Microsoft.Data.Sqlite;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation;

namespace NzbDrone.Core.Datastore
{
    public interface IRestoreDatabase
    {
        void Validate(string path);
        bool Restore();
        void Commit();
        void Rollback();
    }

    public class DatabaseRestorationService : IRestoreDatabase
    {
        private readonly IDiskProvider _diskProvider;
        private readonly IAppFolderInfo _appFolderInfo;
        private static readonly Logger Logger = NzbDroneLogger.GetLogger(typeof(DatabaseRestorationService));

        public DatabaseRestorationService(IDiskProvider diskProvider, IAppFolderInfo appFolderInfo)
        {
            _diskProvider = diskProvider;
            _appFolderInfo = appFolderInfo;
        }

        public void Validate(string path)
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString();

            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            using (var integrityCommand = connection.CreateCommand())
            {
                integrityCommand.CommandText = "PRAGMA quick_check(1);";

                if (!string.Equals(integrityCommand.ExecuteScalar()?.ToString(), "ok", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The restore database failed SQLite's integrity check.");
                }
            }

            // These dual-root columns are present in Chaptarr's earliest schema as well as current
            // databases, but not upstream Readarr. Later monitoring columns are intentionally not
            // required so an old, legitimate Chaptarr/AudioArr backup can still migrate forward.
            if (!ColumnExists(connection, "Authors", "AudiobookRootFolderPath") ||
                !ColumnExists(connection, "Authors", "EbookRootFolderPath"))
            {
                throw new InvalidDataException("The restore database is not a Chaptarr database.");
            }
        }

        public bool Restore()
        {
            var dbRestorePath = _appFolderInfo.GetDatabaseRestore();
            var dbPath = _appFolderInfo.GetDatabase();
            var previousDatabasePath = GetPreviousDatabasePath(dbPath);

            if (!_diskProvider.FileExists(dbRestorePath))
            {
                RecoverInterruptedRestore(dbPath, previousDatabasePath);
                return false;
            }

            try
            {
                Validate(dbRestorePath);
                Logger.Info("Restoring Database");

                RecoverInterruptedRestore(dbPath, previousDatabasePath);
                MoveDatabaseFiles(dbPath, previousDatabasePath);
                _diskProvider.MoveFile(dbRestorePath, dbPath);

                return true;
            }
            catch (Exception e)
            {
                if (_diskProvider.FileExists(previousDatabasePath))
                {
                    Rollback();
                }

                if (_diskProvider.FileExists(dbRestorePath))
                {
                    QuarantineRestore(dbRestorePath);
                }

                Logger.Error(e, "Failed to restore database");
                throw;
            }
        }

        public void Commit()
        {
            DeleteDatabaseFiles(GetPreviousDatabasePath(_appFolderInfo.GetDatabase()));
        }

        public void Rollback()
        {
            var dbPath = _appFolderInfo.GetDatabase();
            var previousDatabasePath = GetPreviousDatabasePath(dbPath);

            if (_diskProvider.FileExists(dbPath))
            {
                MoveDatabaseFiles(dbPath, dbPath + ".failed-restore");
            }

            MoveDatabaseFiles(previousDatabasePath, dbPath);
        }

        private static bool ColumnExists(SqliteConnection connection, string tableName, string columnName)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info(\"{tableName}\");";

            using var reader = command.ExecuteReader();
            var nameOrdinal = reader.GetOrdinal("name");

            while (reader.Read())
            {
                if (string.Equals(reader.GetString(nameOrdinal), columnName, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private void RecoverInterruptedRestore(string dbPath, string previousDatabasePath)
        {
            if (!_diskProvider.FileExists(previousDatabasePath))
            {
                return;
            }

            Logger.Warn("Recovering the database saved before an interrupted restore");

            if (_diskProvider.FileExists(dbPath))
            {
                MoveDatabaseFiles(dbPath, dbPath + ".failed-restore");
            }

            MoveDatabaseFiles(previousDatabasePath, dbPath);
        }

        private void QuarantineRestore(string dbRestorePath)
        {
            _diskProvider.MoveFile(dbRestorePath, dbRestorePath + ".failed", true);
        }

        private void MoveDatabaseFiles(string sourcePath, string destinationPath)
        {
            foreach (var suffix in DatabaseFileSuffixes)
            {
                var source = sourcePath + suffix;

                if (_diskProvider.FileExists(source))
                {
                    _diskProvider.MoveFile(source, destinationPath + suffix, true);
                }
            }
        }

        private void DeleteDatabaseFiles(string path)
        {
            foreach (var suffix in DatabaseFileSuffixes)
            {
                _diskProvider.DeleteFile(path + suffix);
            }
        }

        private static string GetPreviousDatabasePath(string dbPath)
        {
            return dbPath + ".pre-restore";
        }

        private static readonly string[] DatabaseFileSuffixes = { string.Empty, "-shm", "-wal", "-journal" };
    }
}
