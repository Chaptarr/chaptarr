using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Linq;
using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Dapper;
using Microsoft.Data.Sqlite;
using NLog;
using Npgsql;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Lifecycle;

namespace NzbDrone.Core.Backup
{
    public interface IBackupService
    {
        void Backup(BackupType backupType);
        Task WaitForBackupCompletion();
        List<Backup> GetBackups();
        BackupStatus GetStatus();
        void CancelRestore();
        void DismissFailedRestores();
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
        private readonly ILogDatabase _logDb;
        private readonly ICacheDatabase _cacheDb;
        private readonly Logger _logger;
        private readonly IHostApplicationLifetime _appLifetime;
        private readonly IEventAggregator _eventAggregator;
        private readonly object _backupGate = new();
        private int _activeBackups;
        private TaskCompletionSource _backupCompletion;
        private ScheduledBackupFailure _scheduledFailure;
        private bool _failureStateLoaded;

        public static readonly Regex BackupFileRegex = new Regex(@"\A(chaptarr|readarr|audioarr)_backup_[^/\\\r\n]+\.zip\z", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex GeneratedBackupFileRegex = new Regex(@"\A(chaptarr|readarr|audioarr)_backup_(v[0-9.]+_)?[._0-9]+\.zip\z", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public BackupService(IHostApplicationLifetime appLifetime,
                             IMainDatabase maindDb,
                             IMakeDatabaseBackup makeDatabaseBackup,
                             IDiskTransferService diskTransferService,
                             IDiskProvider diskProvider,
                             IAppFolderInfo appFolderInfo,
                             IArchiveService archiveService,
                             IConfigService configService,
                             IRestoreDatabase restoreDatabaseService,
                             IEventAggregator eventAggregator,
                             Logger logger,
                             ILogDatabase logDatabase = null,
                             ICacheDatabase cacheDatabase = null)
        {
            _appLifetime = appLifetime;
            _maindDb = maindDb;
            _makeDatabaseBackup = makeDatabaseBackup;
            _diskTransferService = diskTransferService;
            _diskProvider = diskProvider;
            _appFolderInfo = appFolderInfo;
            _archiveService = archiveService;
            _configService = configService;
            _restoreDatabaseService = restoreDatabaseService;
            _eventAggregator = eventAggregator;
            _logger = logger;
            _logDb = logDatabase;
            _cacheDb = cacheDatabase;
        }

        public void Backup(BackupType backupType)
        {
            Backup(backupType, CancellationToken.None);
        }

        public Task WaitForBackupCompletion()
        {
            lock (_backupGate)
            {
                return _backupCompletion?.Task ?? Task.CompletedTask;
            }
        }

        private void Backup(BackupType backupType, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _appLifetime.ApplicationStopping);
            lock (_backupGate)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (_activeBackups++ == 0)
                {
                    _backupCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
            try
            {
                BackupCore(backupType, linked.Token);
                if (backupType == BackupType.Scheduled) SaveScheduledFailure(null);
            }
            catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException))
            {
                if (backupType == BackupType.Scheduled)
                    SaveScheduledFailure(new ScheduledBackupFailure
                    {
                        FailedAt = DateTime.UtcNow,
                        Reason = NzbDrone.Common.Instrumentation.CleanseLogMessage.Cleanse(error.Message)
                    });
                throw;
            }
            finally
            {
                lock (_backupGate)
                {
                    if (--_activeBackups == 0)
                    {
                        _backupCompletion.TrySetResult();
                    }
                }
            }
        }

        private ScheduledBackupFailure ReadScheduledFailure()
        {
            lock (_backupGate)
            {
                if (!_failureStateLoaded)
                {
                    var path = _appFolderInfo.GetScheduledBackupFailure();
                    try
                    {
                        _scheduledFailure = File.Exists(path) ? JsonSerializer.Deserialize<ScheduledBackupFailure>(File.ReadAllText(path)) : null;
                        _failureStateLoaded = true;
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
                    {
                        if (error is JsonException) _failureStateLoaded = true;
                        _logger.Warn(error, "Unable to read the last scheduled backup failure");
                    }
                }
                return _scheduledFailure;
            }
        }

        private void SaveScheduledFailure(ScheduledBackupFailure failure)
        {
            lock (_backupGate)
            {
                _scheduledFailure = failure;
                _failureStateLoaded = true;
                var path = _appFolderInfo.GetScheduledBackupFailure();
                var temporary = path + ".tmp";
                try
                {
                    if (failure == null) File.Delete(path);
                    else
                    {
                        File.WriteAllText(temporary, JsonSerializer.Serialize(failure));
                        File.Move(temporary, path, overwrite: true);
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    _logger.Warn(error, "Unable to persist scheduled backup status; status is retained for this process");
                }
                finally
                {
                    try { File.Delete(temporary); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    { _logger.Warn(error, "Unable to remove temporary scheduled backup status"); }
                }
            }
            try { _eventAggregator.PublishEvent(new BackupRestoreStateChangedEvent()); }
            catch (Exception error) { _logger.Warn(error, "Unable to refresh backup health after status changed"); }
        }

        private void BackupCore(BackupType backupType, CancellationToken cancellationToken)
        {
            var timer = Stopwatch.StartNew();
            _logger.ProgressInfo("Starting Backup");

            var backupFolder = GetBackupFolder(backupType);

            _diskProvider.EnsureFolder(backupFolder);

            if (!_diskProvider.FolderWritable(backupFolder))
            {
                throw new UnauthorizedAccessException($"Backup folder {backupFolder} is not writable");
            }

            var dateNow = DateTime.Now;
            var backupFilename = $"chaptarr_backup_v{BuildInfo.Version}_{dateNow:yyyy.MM.dd_HH.mm.ss}.zip";
            var backupPath = Path.Combine(backupFolder, backupFilename);

            var backupTempFolder = CreateWorkingFolder(_appFolderInfo);
            CreatePrivateFolder(backupTempFolder);
            var partialPath = backupPath + "." + Path.GetFileName(backupTempFolder) + ".partial";
            var partialMarker = backupTempFolder + ".partial-path";
            try
            {
                BackupConfigFile(backupTempFolder);
                var omissions = new List<string>();
                var postgresSettings = new Dictionary<string, BackupManifest.PostgresDatabaseSettings>();
                var mainSettings = BackupDatabase(_maindDb, "chaptarr", backupTempFolder, cancellationToken);
                if (mainSettings != null)
                {
                    postgresSettings["chaptarr.dump"] = mainSettings;
                }
                if (_configService.BackupIncludeLogsAndCache)
                {
                    foreach (var entry in new[] { ("logs", (IDatabase)_logDb), ("cache", (IDatabase)_cacheDb) })
                    {
                        var optionalFolder = CreateWorkingFolder(_appFolderInfo);
                        var name = entry.Item1 + (_maindDb.DatabaseType == DatabaseType.SQLite ? ".db" : ".dump");
                        try
                        {
                            var settings = BackupDatabase(entry.Item2, entry.Item1, optionalFolder, cancellationToken);
                            _diskProvider.MoveFile(Path.Combine(optionalFolder, name), Path.Combine(backupTempFolder, name));
                            if (settings != null)
                            {
                                postgresSettings[name] = settings;
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            omissions.Add(name);
                            _logger.Warn(ex, "Backup omitted optional component {0}; the library and configuration are still backed up", name);
                        }
                        finally
                        {
                            CleanupRestoreSandbox(optionalFolder);
                        }
                    }
                }
                CreateVersionInfo(dateNow, backupTempFolder);

                _logger.ProgressDebug("Creating backup zip");

                // Delete journal file(s) created during database backup (DB filename may differ for legacy installs).
                foreach (var file in _diskProvider.GetFiles(backupTempFolder, false))
                {
                    if (file.EndsWith("-journal", StringComparison.OrdinalIgnoreCase))
                    {
                        _diskProvider.DeleteFile(file);
                    }
                }

                var manifest = BackupManifest.Create(backupTempFolder, _maindDb.DatabaseType);
                manifest.OmittedComponents = omissions;
                if (_maindDb.DatabaseType == DatabaseType.PostgreSQL)
                {
                    using var connection = _maindDb.OpenConnection();
                    manifest.PostgresMajorVersion = ((NpgsqlConnection)connection).PostgreSqlVersion.Major;
                    manifest.PostgresDatabases = postgresSettings;
                }
                manifest.Validate(backupTempFolder);
                manifest.Write(backupTempFolder);

                var payloadBytes = _diskProvider.GetFiles(backupTempFolder, false).Sum(path => new FileInfo(path).Length);
                // ZIP's worst case is the stored payload plus DEFLATE and directory overhead.
                EnsureBackupSpace(backupFolder, checked(payloadBytes + payloadBytes / 1000 + 65536));
                cancellationToken.ThrowIfCancellationRequested();
                File.WriteAllText(partialMarker, Path.GetFullPath(partialPath));
                using (File.Create(partialPath))
                {
                }
                TryRestrictArchiveAccess(partialPath);
                _archiveService.CreateZip(partialPath, _diskProvider.GetFiles(backupTempFolder, false));
                manifest.ValidateArchive(partialPath);
                cancellationToken.ThrowIfCancellationRequested();

                TryRestrictArchiveAccess(partialPath);
                _diskProvider.MoveFile(partialPath, backupPath);
                var bytes = new FileInfo(backupPath).Length;
                _logger.Info("Backup completed: {0} bytes in {1} ms", bytes, timer.ElapsedMilliseconds);
                if (bytes > RestoreUploadLimitBytes)
                {
                    _logger.Info("This backup exceeds the browser upload limit of {0} bytes. Restore it from the Backups list; on another instance, copy it into the manual subfolder of the configured backup folder, keeping the original filename", RestoreUploadLimitBytes);
                }
                if (backupType != BackupType.Manual)
                {
                    CleanupOldBackups(backupType);
                }

                _logger.ProgressDebug("Backup zip created");
            }
            finally
            {
                try
                {
                    _diskProvider.DeleteFile(partialPath);
                    File.Delete(partialMarker);
                }
                finally
                {
                    CleanupRestoreSandbox(backupTempFolder);
                }
            }
        }

        public List<Backup> GetBackups() => GetBackups(ZipFile.OpenRead);

        internal List<Backup> GetBackups(Func<string, ZipArchive> openArchive)
        {
            var timer = Stopwatch.StartNew();
            var backups = new List<Backup>();

            foreach (var backupType in Enum.GetValues(typeof(BackupType)).Cast<BackupType>())
            {
                var folder = GetBackupFolder(backupType);

                if (_diskProvider.FolderExists(folder))
                {
                    foreach (var path in GetBackupFiles(folder))
                    {
                        var backup = new Backup
                        {
                            Name = Path.GetFileName(path),
                            Type = backupType
                        };
                        try
                        {
                            backup.Size = _diskProvider.GetFileSize(path);
                            backup.Time = _diskProvider.FileGetLastWrite(path);
                            using var archive = openArchive(path);
                            backup.IncludedComponents = archive.Entries.Select(entry => entry.FullName)
                                .Where(name => name is not ("INFO" or BackupManifest.FileName)).ToList();
                            var entry = archive.GetEntry(BackupManifest.FileName);
                            if (entry != null)
                            {
                                using var stream = entry.Open();
                                backup.OmittedComponents = JsonSerializer.Deserialize<BackupManifest>(stream)?.OmittedComponents ?? new();
                            }
                        }
                        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
                        {
                            _logger.Warn(ex, "Unable to read the component list for backup {0}", backup.Name);
                        }
                        backups.Add(backup);
                    }
                }
            }

            _logger.Debug("Listed {0} backups in {1} ms", backups.Count, timer.ElapsedMilliseconds);
            return backups;
        }

        public BackupStatus GetStatus()
        {
            var status = DatabaseRestorationService.GetStatus(_appFolderInfo);
            status.ScheduleDisabled = _configService.BackupInterval == 0;
            status.ScheduledBackupFailure = ReadScheduledFailure();
            if (_maindDb.DatabaseType == DatabaseType.PostgreSQL)
            {
                int? major = null;
                try
                {
                    using var connection = _maindDb.OpenConnection();
                    major = ((NpgsqlConnection)connection).PostgreSqlVersion.Major;
                    PostgresBackup.Tool("pg_dump", major.Value);
                    PostgresBackup.Tool("pg_restore", major.Value);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Native PostgreSQL backup tools are unavailable");
                    status.DatabaseBackupUnavailableReason = major.HasValue
                        ? $"PostgreSQL {major} backups require pg_dump and pg_restore version {major}. Install matching client tools before backing up or updating."
                        : "Unable to connect to PostgreSQL to check backup availability. Check the database connection and logs before backing up or updating.";
                }
            }
            return status;
        }

        public void CancelRestore()
        {
            DatabaseRestorationService.CancelPendingRestore(_appFolderInfo);
            _eventAggregator.PublishEvent(new BackupRestoreStateChangedEvent());
        }

        public void DismissFailedRestores()
        {
            DatabaseRestorationService.DismissFailedRestores(_appFolderInfo);
            _eventAggregator.PublishEvent(new BackupRestoreStateChangedEvent());
        }

        public void Restore(string backupFileName)
        {
            if (_maindDb.DatabaseType == DatabaseType.PostgreSQL && !backupFileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                throw new RestoreBackupFailedException(HttpStatusCode.BadRequest, "PostgreSQL restore requires a complete Chaptarr backup archive. External pg_dump backups can still be restored with pg_restore.");
            }

            if (backupFileName.EndsWith(".zip", StringComparison.InvariantCultureIgnoreCase))
            {
                var temporaryPath = CreateWorkingFolder(_appFolderInfo);

                try
                {
                    CreatePrivateFolder(temporaryPath);
                    var available = _diskProvider.GetAvailableSpace(temporaryPath)
                        ?? throw new IOException("Unable to determine free disk space for backup extraction.");
                    using (var archive = ZipFile.OpenRead(backupFileName))
                    {
                        var unpackedBytes = archive.Entries.Aggregate(0L, (total, entry) => checked(total + entry.Length));
                        var required = checked(2 * unpackedBytes);
                        // The upload ZIP already occupies disk; free space is measured after uploading.
                        if (available < required)
                            throw new IOException($"Insufficient space for backup extraction and staging: need {required} bytes, have {available} bytes.");
                    }
                    var timer = Stopwatch.StartNew();
                    _archiveService.ExtractZip(backupFileName, temporaryPath, available);
                    _logger.Info("Backup extracted: {0} bytes in {1} ms", Directory.GetFiles(temporaryPath, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length), timer.ElapsedMilliseconds);

                    var hasManifest = File.Exists(Path.Combine(temporaryPath, BackupManifest.FileName));
                    if (hasManifest)
                    {
                        var manifest = BackupManifest.Read(temporaryPath);
                        manifest.ValidateArchive(backupFileName);
                        manifest.Validate(temporaryPath);
                        if (manifest.DatabaseType != _maindDb.DatabaseType)
                        {
                            throw new InvalidDataException("The backup and destination use different database types.");
                        }
                        if (manifest.DatabaseType == DatabaseType.PostgreSQL)
                        {
                            _restoreDatabaseService.Stage(temporaryPath);
                            return;
                        }
                    }
                    else if (_maindDb.DatabaseType == DatabaseType.PostgreSQL)
                    {
                        throw new InvalidDataException("A PostgreSQL backup must include a complete manifest.");
                    }

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

                    var stagingFile = files.FirstOrDefault(file =>
                        Path.GetFileName(file).Equals("staging.db", StringComparison.OrdinalIgnoreCase));

                    if (stagingFile != null)
                    {
                        DatabaseRestorationService.ValidateStaging(stagingFile);
                    }

                    if (!File.Exists(Path.Combine(temporaryPath, BackupManifest.FileName)))
                    {
                        if (Path.GetFileName(databaseFile) != "chaptarr.db")
                        {
                            _diskProvider.MoveFile(databaseFile, Path.Combine(temporaryPath, "chaptarr.db"));
                        }

                        var configFile = files.FirstOrDefault(file =>
                            Path.GetFileName(file).Equals("config.xml", StringComparison.OrdinalIgnoreCase));
                        if (configFile != null && Path.GetFileName(configFile) != "config.xml")
                        {
                            _diskProvider.MoveFile(configFile, Path.Combine(temporaryPath, "config.xml"));
                        }
                    }

                    _restoreDatabaseService.Stage(temporaryPath);
                    return;
                }
                catch (RestoreBackupFailedException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new RestoreBackupFailedException(HttpStatusCode.BadRequest, "Unable to restore backup: {0}", ex.Message);
                }
                finally
                {
                    CleanupRestoreSandbox(temporaryPath);
                }
            }

            ValidateRestore(backupFileName);
            var rawFolder = CreateWorkingFolder(_appFolderInfo);
            try
            {
                CreatePrivateFolder(rawFolder);
                _diskProvider.CopyFile(backupFileName, Path.Combine(rawFolder, "chaptarr.db"));
                _restoreDatabaseService.Stage(rawFolder);
            }
            catch (Exception ex)
            {
                throw new RestoreBackupFailedException(HttpStatusCode.BadRequest, "Unable to restore backup: {0}", ex.Message);
            }
            finally
            {
                CleanupRestoreSandbox(rawFolder);
            }
        }

        private void CleanupRestoreSandbox(string path)
        {
            try
            {
                if (_diskProvider.FolderExists(path))
                {
                    _diskProvider.DeleteFolder(path, true);
                    var working = _appFolderInfo.GetBackupWorkingFolder();
                    if (Path.GetDirectoryName(path) == working && Directory.Exists(working) && !Directory.EnumerateFileSystemEntries(working).Any())
                    {
                        Directory.Delete(working);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to clean up restore sandbox folder: {0}", path);
            }
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

        public static string CreateWorkingFolder(IAppFolderInfo folders)
        {
            var root = folders.GetBackupWorkingFolder();
            CreatePrivateFolder(root);
            var path = Path.Combine(root, Environment.ProcessId + "." + Guid.NewGuid().ToString("N"));
            CreatePrivateFolder(path);
            return path;
        }

        public static void CleanupWorkingFolder(IAppFolderInfo folders)
        {
            // Startup owns the AppData lock and has not accepted any backup requests yet.
            var working = folders.GetBackupWorkingFolder();
            if (Directory.Exists(working))
            {
                foreach (var path in Directory.GetFileSystemEntries(working))
                {
                    try
                    {
                        if (Directory.Exists(path))
                        {
                            Directory.Delete(path, true);
                        }
                        else
                        {
                            if (path.EndsWith(".partial-path", StringComparison.Ordinal))
                            {
                                // Backup destinations may be shared; remove only this instance's recorded unfinished archive.
                                var partial = File.ReadAllText(path);
                                var suffix = "." + Path.GetFileNameWithoutExtension(path) + ".partial";
                                if (!Path.IsPathFullyQualified(partial) || !partial.EndsWith(suffix, StringComparison.Ordinal) ||
                                    !BackupFileRegex.IsMatch(Path.GetFileName(partial)[..^suffix.Length]))
                                {
                                    throw new InvalidDataException("Invalid unfinished-backup ownership record");
                                }

                                File.Delete(partial);
                            }

                            File.Delete(path);
                        }
                    }
                    catch (Exception ex)
                    {
                        NzbDrone.Common.Instrumentation.NzbDroneLogger.GetLogger(typeof(BackupService))
                            .Warn(ex, "Unable to remove a leftover backup working file: {0}", path);
                    }
                }
            }
        }

        public const long RestoreUploadLimitBytes = 5_000_000_000;

        internal void TryRestrictArchiveAccess(string path, Action<string> restrict = null)
        {
            try
            {
                (restrict ?? RestrictFileAccess)(path);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
            {
                _logger.Warn(ex, "The backup drive could not restrict archive permissions. Backups contain saved credentials; keep the backup folder private");
            }
        }

        internal static void RestrictFileAccess(string path)
        {
            if (OperatingSystem.IsWindows())
            {
                using var identity = WindowsIdentity.GetCurrent();
                var permissions = new FileSecurity();
                permissions.SetAccessRuleProtection(true, false);
                permissions.AddAccessRule(new FileSystemAccessRule(identity.User, FileSystemRights.FullControl, AccessControlType.Allow));
                new FileInfo(path).SetAccessControl(permissions);
            }
            else
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        public static void CreatePrivateFolder(string path)
        {
            // Temporary database/configuration payloads must not be readable through a shared temp directory.
            if (OperatingSystem.IsWindows())
            {
                using var identity = WindowsIdentity.GetCurrent();
                var permissions = new DirectorySecurity();
                permissions.SetAccessRuleProtection(true, false);
                permissions.AddAccessRule(new FileSystemAccessRule(identity.User, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(path).Create(permissions);
            }
            else
            {
                Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        private BackupManifest.PostgresDatabaseSettings BackupDatabase(IDatabase database, string component, string backupTempFolder, CancellationToken cancellationToken)
        {
            if (database == null)
            {
                throw new InvalidOperationException($"The {component} database is unavailable; no complete backup can be created.");
            }
            var timer = Stopwatch.StartNew();
            _logger.ProgressDebug("Backing up {0} database", component);
            using var connection = database.OpenConnection();
            long estimate;
            if (database.DatabaseType == DatabaseType.SQLite)
            {
                estimate = checked(connection.ExecuteScalar<long>("PRAGMA page_count") * connection.ExecuteScalar<long>("PRAGMA page_size"));
            }
            else
            {
                using var command = new NpgsqlCommand("SET application_name = 'chaptarr_backup_size'", (NpgsqlConnection)connection);
                command.ExecuteNonQueryAsync(cancellationToken).GetAwaiter().GetResult();
                // Table-size reads can wait for DDL locks, so preflight must honor the backup's cancellation too.
                command.CommandText = @"SELECT COALESCE(SUM(pg_table_size(c.oid)), 0)::bigint
                    FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                    WHERE c.relkind IN ('r', 'm', 'S')
                      AND ((n.nspname !~ '^pg_' AND n.nspname <> 'information_schema')
                           OR c.oid = 'pg_catalog.pg_largeobject'::regclass)";
                estimate = (long)command.ExecuteScalarAsync(cancellationToken).GetAwaiter().GetResult();
            }
            EnsureBackupSpace(backupTempFolder, estimate);
            _makeDatabaseBackup.BackupDatabase(database, backupTempFolder, cancellationToken);
            var snapshot = database.DatabaseType == DatabaseType.SQLite
                ? Path.Combine(backupTempFolder, Path.GetFileName(new SqliteConnectionStringBuilder(connection.ConnectionString).DataSource))
                : Path.Combine(backupTempFolder, "database.dump");
            var canonical = Path.Combine(backupTempFolder, component + (database.DatabaseType == DatabaseType.SQLite ? ".db" : ".dump"));
            if (snapshot != canonical)
            {
                _diskProvider.MoveFile(snapshot, canonical);
            }
            _logger.Info("Backed up {0}: {1} bytes in {2} ms", component, new FileInfo(canonical).Length, timer.ElapsedMilliseconds);
            return connection is NpgsqlConnection postgres ? PostgresBackup.DatabaseSettings(postgres) : null;
        }

        private void EnsureBackupSpace(string folder, long required)
        {
            var available = _diskProvider.GetAvailableSpace(folder)
                ?? throw new IOException("Unable to determine free disk space for backup creation.");
            if (available < required)
            {
                throw new IOException($"Insufficient space for backup creation: need {required} bytes, have {available} bytes.");
            }
        }

        private void BackupConfigFile(string backupTempFolder)
        {
            _logger.ProgressDebug("Backing up config.xml");

            var configFile = _appFolderInfo.GetConfigPath();
            var tempConfigFile = Path.Combine(backupTempFolder, Path.GetFileName(configFile));

            _diskTransferService.TransferFile(configFile, tempConfigFile, TransferMode.Copy);
        }

        private void CreateVersionInfo(DateTime dateNow, string backupTempFolder)
        {
            var tempFile = Path.Combine(backupTempFolder, "INFO");

            var builder = new StringBuilder();
            builder.AppendLine($"v{BuildInfo.Version}");
            builder.AppendLine($"{dateNow:yyyy-MM-dd HH:mm:ss}");

            _diskProvider.WriteAllText(tempFile, builder.ToString());
        }

        private void CleanupOldBackups(BackupType backupType)
        {
            var retention = _configService.BackupRetention;

            _logger.Debug("Cleaning up backup files older than {0} days", retention);
            // Renamed archives remain available for restore, but are no longer owned by retention.
            var files = GetBackupFiles(GetBackupFolder(backupType))
                .Where(file => GeneratedBackupFileRegex.IsMatch(Path.GetFileName(file)));

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

            return files.Where(f => BackupFileRegex.IsMatch(Path.GetFileName(f)));
        }

        public void Execute(BackupCommand message)
        {
            Backup(message.Type);
        }

        public void Execute(BackupCommand message, CancellationToken cancellationToken)
        {
            Backup(message.Type, cancellationToken);
        }
    }
}
