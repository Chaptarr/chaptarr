using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Backup
{
    public interface IBackupService
    {
        void Backup(BackupType backupType);
        List<Backup> GetBackups();
        void Restore(string backupFileName);
        string GetBackupFolder();
        string GetBackupFolder(BackupType backupType);
    }

    public class BackupService : IBackupService, IExecute<BackupCommand>
    {
        private readonly IMainDatabase _maindDb;
        private readonly IMakeDatabaseBackup _makeDatabaseBackup;
        private readonly IDiskTransferService _diskTransferService;
        private readonly IDiskProvider _diskProvider;
        private readonly IAppFolderInfo _appFolderInfo;
        private readonly IArchiveService _archiveService;
        private readonly IConfigService _configService;
        private readonly IRestoreDatabase _restoreDatabaseService;
        private readonly Logger _logger;

        private string _backupTempFolder;

        public static readonly Regex BackupFileRegex = new Regex(@"(chaptarr|readarr|audioarr)_backup_(v[0-9.]+_)?[._0-9]+\.zip", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public BackupService(IMainDatabase maindDb,
                             IMakeDatabaseBackup makeDatabaseBackup,
                             IDiskTransferService diskTransferService,
                             IDiskProvider diskProvider,
                             IAppFolderInfo appFolderInfo,
                             IArchiveService archiveService,
                             IConfigService configService,
                             IRestoreDatabase restoreDatabaseService,
                             Logger logger)
        {
            _maindDb = maindDb;
            _makeDatabaseBackup = makeDatabaseBackup;
            _diskTransferService = diskTransferService;
            _diskProvider = diskProvider;
            _appFolderInfo = appFolderInfo;
            _archiveService = archiveService;
            _configService = configService;
            _restoreDatabaseService = restoreDatabaseService;
            _logger = logger;

            _backupTempFolder = Path.Combine(_appFolderInfo.TempFolder, "chaptarr_backup");
        }

        public void Backup(BackupType backupType)
        {
            _logger.ProgressInfo("Starting Backup");

            var backupFolder = GetBackupFolder(backupType);

            _diskProvider.EnsureFolder(_backupTempFolder);
            _diskProvider.EnsureFolder(backupFolder);

            if (!_diskProvider.FolderWritable(backupFolder))
            {
                throw new UnauthorizedAccessException($"Backup folder {backupFolder} is not writable");
            }

            var dateNow = DateTime.Now;
            var backupFilename = $"chaptarr_backup_v{BuildInfo.Version}_{dateNow:yyyy.MM.dd_HH.mm.ss}.zip";
            var backupPath = Path.Combine(backupFolder, backupFilename);

            Cleanup();

            if (backupType != BackupType.Manual)
            {
                CleanupOldBackups(backupType);
            }

            BackupConfigFile();
            BackupDatabase();
            CreateVersionInfo(dateNow);

            _logger.ProgressDebug("Creating backup zip");

            // Delete journal file(s) created during database backup (DB filename may differ for legacy installs).
            foreach (var file in _diskProvider.GetFiles(_backupTempFolder, false))
            {
                if (file.EndsWith("-journal", StringComparison.OrdinalIgnoreCase))
                {
                    _diskProvider.DeleteFile(file);
                }
            }

            _archiveService.CreateZip(backupPath, _diskProvider.GetFiles(_backupTempFolder, false));

            Cleanup();

            _logger.ProgressDebug("Backup zip created");
        }

        public List<Backup> GetBackups()
        {
            var backups = new List<Backup>();

            foreach (var backupType in Enum.GetValues(typeof(BackupType)).Cast<BackupType>())
            {
                var folder = GetBackupFolder(backupType);

                if (_diskProvider.FolderExists(folder))
                {
                    backups.AddRange(GetBackupFiles(folder).Select(b => new Backup
                    {
                        Name = Path.GetFileName(b),
                        Type = backupType,
                        Size = _diskProvider.GetFileSize(b),
                        Time = _diskProvider.FileGetLastWrite(b)
                    }));
                }
            }

            return backups;
        }

        public void Restore(string backupFileName)
        {
            if (_maindDb.DatabaseType != DatabaseType.SQLite)
            {
                throw new RestoreBackupFailedException(HttpStatusCode.BadRequest, "Database restore is only supported for SQLite. PostgreSQL databases must be restored externally using pg_restore");
            }

            if (backupFileName.EndsWith(".zip", StringComparison.InvariantCultureIgnoreCase))
            {
                var temporaryPath = Path.Combine(_appFolderInfo.TempFolder, $"chaptarr_backup_restore_{Guid.NewGuid():N}");

                try
                {
                    _diskProvider.EnsureFolder(temporaryPath);
                    _archiveService.Extract(backupFileName, temporaryPath);

                    var files = _diskProvider.GetFiles(temporaryPath, false).ToList();
                    var databaseFile = files.FirstOrDefault(file =>
                        Path.GetFileName(file).Equals("chaptarr.db", StringComparison.InvariantCultureIgnoreCase) ||
                        Path.GetFileName(file).Equals("readarr.db", StringComparison.InvariantCultureIgnoreCase) ||
                        Path.GetFileName(file).Equals("audioarr.db", StringComparison.InvariantCultureIgnoreCase));

                    if (databaseFile == null)
                    {
                        throw new RestoreBackupFailedException(HttpStatusCode.NotFound, "Unable to restore database file from backup");
                    }

                    ValidateRestore(databaseFile);

                    var configFile = files.FirstOrDefault(file =>
                        Path.GetFileName(file).Equals("Config.xml", StringComparison.InvariantCultureIgnoreCase));

                    if (configFile != null)
                    {
                        _diskProvider.MoveFile(configFile, _appFolderInfo.GetConfigPath(), true);
                    }

                    _diskProvider.MoveFile(databaseFile, _appFolderInfo.GetDatabaseRestore(), true);
                    return;
                }
                finally
                {
                    try
                    {
                        if (_diskProvider.FolderExists(temporaryPath))
                        {
                            _diskProvider.DeleteFolder(temporaryPath, true);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn(ex, "Failed to clean up restore sandbox folder: {0}", temporaryPath);
                    }
                }
            }

            ValidateRestore(backupFileName);
            _diskProvider.MoveFile(backupFileName, _appFolderInfo.GetDatabaseRestore(), true);
        }

        private void ValidateRestore(string databaseFile)
        {
            try
            {
                _restoreDatabaseService.Validate(databaseFile);
            }
            catch (Exception ex)
            {
                throw new RestoreBackupFailedException(HttpStatusCode.BadRequest, "Unable to restore backup: {0}", ex.Message);
            }
        }

        public string GetBackupFolder()
        {
            var backupFolder = _configService.BackupFolder;

            if (Path.IsPathRooted(backupFolder))
            {
                return backupFolder;
            }

            return Path.Combine(_appFolderInfo.GetAppDataPath(), backupFolder);
        }

        public string GetBackupFolder(BackupType backupType)
        {
            return Path.Combine(GetBackupFolder(), backupType.ToString().ToLower());
        }

        private void Cleanup()
        {
            if (_diskProvider.FolderExists(_backupTempFolder))
            {
                _diskProvider.EmptyFolder(_backupTempFolder);
            }
        }

        private void BackupDatabase()
        {
            if (_maindDb.DatabaseType == DatabaseType.SQLite)
            {
                _logger.ProgressDebug("Backing up database");

                _makeDatabaseBackup.BackupDatabase(_maindDb, _backupTempFolder);
            }
            else
            {
                _logger.Warn("Database backup skipped: PostgreSQL databases must be backed up externally using pg_dump");
            }
        }

        private void BackupConfigFile()
        {
            _logger.ProgressDebug("Backing up config.xml");

            var configFile = _appFolderInfo.GetConfigPath();
            var tempConfigFile = Path.Combine(_backupTempFolder, Path.GetFileName(configFile));

            _diskTransferService.TransferFile(configFile, tempConfigFile, TransferMode.Copy);
        }

        private void CreateVersionInfo(DateTime dateNow)
        {
            var tempFile = Path.Combine(_backupTempFolder, "INFO");

            var builder = new StringBuilder();
            builder.AppendLine($"v{BuildInfo.Version}");
            builder.AppendLine($"{dateNow:yyyy-MM-dd HH:mm:ss}");

            _diskProvider.WriteAllText(tempFile, builder.ToString());
        }

        private void CleanupOldBackups(BackupType backupType)
        {
            var retention = _configService.BackupRetention;

            _logger.Debug("Cleaning up backup files older than {0} days", retention);
            var files = GetBackupFiles(GetBackupFolder(backupType));

            foreach (var file in files)
            {
                var lastWriteTime = _diskProvider.FileGetLastWrite(file);

                if (lastWriteTime.AddDays(retention) < DateTime.UtcNow)
                {
                    _logger.Debug("Deleting old backup file: {0}", file);
                    _diskProvider.DeleteFile(file);
                }
            }

            _logger.Debug("Finished cleaning up old backup files");
        }

        private IEnumerable<string> GetBackupFiles(string path)
        {
            var files = _diskProvider.GetFiles(path, false);

            return files.Where(f => BackupFileRegex.IsMatch(f));
        }

        public void Execute(BackupCommand message)
        {
            Backup(message.Type);
        }
    }
}
