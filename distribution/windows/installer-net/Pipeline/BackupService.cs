using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace ChaptarrNG.Installer.Pipeline;

public sealed record BackupResult(string Method, string Path);

public static class BackupService
{
    public static string GetBackupRoot(string dataDirectory)
    {
        var parent = Directory.GetParent(dataDirectory)?.FullName
            ?? throw new InvalidOperationException($"Could not determine a backup location next to {dataDirectory}.");
        var leaf = Path.GetFileName(dataDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return Path.Combine(parent, leaf + ".backups", "ChaptarrNG-Migration");
    }

    public static string? GetUnsupportedSourceReason(string dataDirectory)
    {
        if (!Directory.Exists(dataDirectory))
        {
            return $"The BookshelfNG data directory does not exist: {dataDirectory}";
        }

        var sourceDirectoryReason = GetReparsePointReason(dataDirectory, "BookshelfNG data directory", isDirectory: true);
        if (sourceDirectoryReason is not null)
        {
            return sourceDirectoryReason;
        }

        var serviceAccount = ServiceLifecycle.GetBookshelfServiceAccount();
        var unsupportedAccount = ServiceAccountPolicy.GetUnsupportedReason(serviceAccount);
        if (unsupportedAccount is not null)
        {
            return unsupportedAccount;
        }

        var serviceStartType = ServiceLifecycle.GetBookshelfServiceStartType();
        if (serviceStartType is not (2 or 3 or 4))
        {
            return $"The Readarr service uses unsupported Windows start type {serviceStartType}; use the manual migration path so its service startup policy can be preserved.";
        }

        var configPath = Path.Combine(dataDirectory, "config.xml");
        if (!File.Exists(configPath))
        {
            return $"The BookshelfNG config file was not found: {configPath}";
        }

        var configReason = GetReparsePointReason(configPath, "BookshelfNG config.xml", isDirectory: false);
        if (configReason is not null)
        {
            return configReason;
        }

        var binDirectory = Path.Combine(dataDirectory, "bin");
        if (!Directory.Exists(binDirectory))
        {
            return $"The BookshelfNG binary directory was not found: {binDirectory}";
        }

        var binReason = GetReparseTreeReason(binDirectory, "BookshelfNG binary directory");
        if (binReason is not null)
        {
            return binReason;
        }

        var backupsDirectory = Path.Combine(dataDirectory, "Backups");
        if (Directory.Exists(backupsDirectory))
        {
            var backupsReason = GetReparseTreeReason(backupsDirectory, "BookshelfNG Backups directory");
            if (backupsReason is not null)
            {
                return backupsReason;
            }
        }

        string? postgresHost;
        var environment = BookshelfServiceEnvironment.GetEffectiveConfigurationEnvironment();
        var environmentSelectsPostgres = environment.TryGetValue("CHAPTARR__POSTGRES__HOST", out var configuredPostgresHost) &&
            !string.IsNullOrWhiteSpace(configuredPostgresHost);
        try
        {
            var config = XDocument.Load(configPath);
            postgresHost = environment.TryGetValue("CHAPTARR__POSTGRES__HOST", out configuredPostgresHost)
                ? configuredPostgresHost
                : config
                .Descendants()
                .FirstOrDefault(e => string.Equals(e.Name.LocalName, "PostgresHost", StringComparison.OrdinalIgnoreCase))
                ?.Value;
        }
        catch (Exception ex)
        {
            return $"The BookshelfNG config could not be read safely: {ex.Message}";
        }

        var postgresSource = !string.IsNullOrWhiteSpace(postgresHost) || environmentSelectsPostgres;
        try
        {
            BookshelfDatabaseFiles.ValidateLegacyDatabasePaths(dataDirectory);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException)
        {
            return ex.Message;
        }

        if (!postgresSource)
        {
            try
            {
                BookshelfDatabaseFiles.ResolveExistingDatabaseName(dataDirectory);
            }
            catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException)
            {
                return ex.Message;
            }
        }

        return null;
    }

    public static bool IsPostgresSource(string dataDirectory)
    {
        var environment = BookshelfServiceEnvironment.GetEffectiveConfigurationEnvironment();
        if (environment.TryGetValue("CHAPTARR__POSTGRES__HOST", out var configuredHost))
        {
            return !string.IsNullOrWhiteSpace(configuredHost);
        }

        var configPath = Path.Combine(dataDirectory, "config.xml");
        if (File.Exists(configPath))
        {
            try
            {
                var config = XDocument.Load(configPath);
                var host = config
                    .Descendants()
                    .FirstOrDefault(e => string.Equals(e.Name.LocalName, "PostgresHost", StringComparison.OrdinalIgnoreCase))
                    ?.Value;
                if (!string.IsNullOrWhiteSpace(host))
                {
                    return true;
                }
            }
            catch
            {
                // The preflight reports malformed config before the installer proceeds.
            }
        }

        return false;
    }

    public static async Task<BackupResult> BackupAsync(
        string dataDirectory,
        IProgress<LogLine> log,
        bool postgresSource,
        Func<string, Task>? createPostgresDump,
        CancellationToken ct = default)
    {
        EnsureNoReparsePoint(dataDirectory, "BookshelfNG data directory", isDirectory: true);
        EnsureNoReparsePoint(Path.Combine(dataDirectory, "config.xml"), "BookshelfNG config.xml", isDirectory: false);
        BookshelfDatabaseFiles.ValidateLegacyDatabasePaths(dataDirectory);
        EnsureNoReparseTree(Path.Combine(dataDirectory, "bin"), "BookshelfNG binary directory");
        var existingBackups = Path.Combine(dataDirectory, "Backups");
        if (Directory.Exists(existingBackups))
        {
            EnsureNoReparseTree(existingBackups, "BookshelfNG Backups directory");
        }

        var backupRoot = GetBackupRoot(dataDirectory);
        Directory.CreateDirectory(backupRoot);
        SecureBackupDirectory(backupRoot);
        var target = Path.Combine(
            backupRoot,
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..8]);

        log.Report(new LogLine(LogStream.Stdout, $"[backup] Target: {target}"));
        Directory.CreateDirectory(target);

        try
        {
            SaveBookshelfServiceEnvironment(target, log);

            if (postgresSource)
            {
                CopySqliteSetIfPresent(dataDirectory, target, log);
                if (createPostgresDump is null)
                {
                    throw new InvalidOperationException("A PostgreSQL database backup callback was not provided.");
                }

                await createPostgresDump(target).ConfigureAwait(false);
                var mainDumpPath = Path.Combine(target, "postgres-main.dump");
                if (!File.Exists(mainDumpPath) || new FileInfo(mainDumpPath).Length == 0)
                {
                    throw new InvalidDataException("The independent PostgreSQL main-database backup was not created or was empty.");
                }

                log.Report(new LogLine(LogStream.Stdout, "[backup] Independent PostgreSQL database backup set created and verified."));
            }
            else
            {
                await CopyBinAsync(dataDirectory, target, log, ct).ConfigureAwait(false);
                CopySqliteSet(dataDirectory, target, log);
            }

            if (postgresSource)
            {
                await CopyBinAsync(dataDirectory, target, log, ct).ConfigureAwait(false);
            }

            CopySingleFile(Path.Combine(dataDirectory, "config.xml"), target, log);
            CopyBackupsFolder(dataDirectory, target, log);
            if (!postgresSource)
            {
                RunIntegrityCheck(target, log);
            }

            log.Report(new LogLine(LogStream.Stdout, $"[backup] Complete and retained: {target}"));
            return new BackupResult(postgresSource ? "PostgreSQLDump" : "FileCopy", target);
        }
        catch
        {
            try
            {
                Directory.Delete(target, recursive: true);
            }
            catch (Exception ex)
            {
                log.Report(new LogLine(LogStream.Stderr, $"[backup] Could not remove incomplete backup {target}: {ex.Message}"));
            }

            throw;
        }
    }

    private static void SaveBookshelfServiceEnvironment(string target, IProgress<LogLine> log)
    {
        var path = Path.Combine(target, "bookshelf-service-environment.json");
        var environment = ServiceLifecycle.GetBookshelfServiceEnvironment()
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        File.WriteAllText(path, JsonSerializer.Serialize(environment));
        log.Report(new LogLine(LogStream.Stdout, "[backup] Original Windows service environment retained in the restricted rollback backup."));
    }

    private static void SecureBackupDirectory(string backupRoot)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inherited = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            inherited,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            inherited,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(backupRoot).SetAccessControl(security);
    }

    public static string GetDatabaseFingerprint(string dataDirectory)
    {
        return SqliteDatabaseFingerprint.Compute(dataDirectory);
    }

    public static string? GetPreviousVersion(string dataDirectory)
    {
        var exe = Path.Combine(dataDirectory, "bin", "Readarr.exe");
        if (!File.Exists(exe))
        {
            exe = Path.Combine(dataDirectory, "bin", "Readarr.Console.exe");
        }

        if (!File.Exists(exe))
        {
            return null;
        }

        try
        {
            return System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).FileVersion;
        }
        catch
        {
            return null;
        }
    }

    private static async Task CopyBinAsync(
        string dataDirectory,
        string target,
        IProgress<LogLine> log,
        CancellationToken ct)
    {
        var source = Path.Combine(dataDirectory, "bin");
        if (!Directory.Exists(source))
        {
            throw new InvalidOperationException($"The BookshelfNG binary directory was not found: {source}");
        }

        EnsureNoReparseTree(source, "BookshelfNG binary directory");

        var fileCount = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Count();
        var sizeBytes = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Sum(f => new FileInfo(f).Length);
        var sizeMb = Math.Round(sizeBytes / 1024.0 / 1024.0);
        log.Report(new LogLine(LogStream.Stdout, $"[backup] Copying BookshelfNG binaries ({fileCount} files, ~{sizeMb:F0} MB)..."));

        var destination = Path.Combine(target, "bin");
        var exit = await ProcessStreamer.RunAsync(
            "robocopy.exe",
            new[] { source, destination, "/E", "/XJ", "/COPY:DAT", "/DCOPY:DAT", "/MT:8", "/NFL", "/NDL", "/NC", "/NS", "/NP" },
            null,
            log,
            ct).ConfigureAwait(false);

        // robocopy exit codes 0-7 are success bit flags; 8+ indicate failure.
        if (exit >= 8)
        {
            throw new InvalidOperationException($"robocopy failed with exit code {exit}; the upgrade was not applied.");
        }
    }

    private static void CopySqliteSet(string dataDirectory, string target, IProgress<LogLine> log)
    {
        foreach (var databaseName in BookshelfDatabaseFiles.GetPopulatedDatabaseNames(dataDirectory))
        {
            var names = new[] { databaseName, databaseName + "-journal", databaseName + "-wal" };
            var primary = Path.Combine(dataDirectory, databaseName);
            var dbMb = Math.Round(new FileInfo(primary).Length / 1024.0 / 1024.0);
            log.Report(new LogLine(LogStream.Stdout, $"[backup] Copying {databaseName} SQLite database, WAL, and rollback journal ({dbMb:F0} MB)..."));

            foreach (var name in names)
            {
                var source = Path.Combine(dataDirectory, name);
                if (File.Exists(source))
                {
                    File.Copy(source, Path.Combine(target, name), overwrite: false);
                }
            }
        }
    }

    private static void CopySqliteSetIfPresent(string dataDirectory, string target, IProgress<LogLine> log)
    {
        BookshelfDatabaseFiles.ValidateLegacyDatabasePaths(dataDirectory);
        var names = BookshelfDatabaseFiles.LegacyDatabaseNames
            .SelectMany(name => new[] { name, name + "-journal", name + "-wal" });
        foreach (var name in names)
        {
            var source = Path.Combine(dataDirectory, name);
            if (File.Exists(source))
            {
                File.Copy(source, Path.Combine(target, name), overwrite: false);
                log.Report(new LogLine(LogStream.Stdout, $"[backup] Preserved coexisting file {name}."));
            }
        }
    }

    private static void CopySingleFile(string source, string targetDirectory, IProgress<LogLine> log)
    {
        EnsureNoReparsePoint(source, "BookshelfNG config.xml", isDirectory: false);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("BookshelfNG config.xml disappeared before the backup completed.", source);
        }

        File.Copy(source, Path.Combine(targetDirectory, Path.GetFileName(source)), overwrite: false);
        log.Report(new LogLine(LogStream.Stdout, "[backup] Copied config.xml."));
    }

    private static void CopyBackupsFolder(string dataDirectory, string target, IProgress<LogLine> log)
    {
        var source = Path.Combine(dataDirectory, "Backups");
        if (!Directory.Exists(source))
        {
            return;
        }

        EnsureNoReparseTree(source, "BookshelfNG Backups directory");
        log.Report(new LogLine(LogStream.Stdout, "[backup] Copying existing BookshelfNG backups..."));
        CopyDirectoryRecursive(source, Path.Combine(target, "Backups"));
    }

    private static void CopyDirectoryRecursive(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            EnsureNoReparsePoint(file, "BookshelfNG backup file", isDirectory: false);
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            EnsureNoReparsePoint(directory, "BookshelfNG backup subdirectory", isDirectory: true);
            CopyDirectoryRecursive(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private static string? GetReparsePointReason(string path, string description, bool isDirectory)
    {
        try
        {
            EnsureNoReparsePoint(path, description, isDirectory);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static string? GetReparseTreeReason(string root, string description)
    {
        try
        {
            EnsureNoReparseTree(root, description);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static void EnsureNoReparseTree(string root, string description)
    {
        EnsureNoReparsePoint(root, description, isDirectory: true);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            var enumerationOptions = new EnumerationOptions
            {
                AttributesToSkip = 0,
                IgnoreInaccessible = false,
                RecurseSubdirectories = false
            };
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", enumerationOptions))
            {
                var isDirectory = Directory.Exists(entry);
                EnsureNoReparsePoint(entry, $"{description} entry '{entry}'", isDirectory);
                if (isDirectory)
                {
                    pending.Push(entry);
                }
            }
        }
    }

    private static void EnsureNoReparsePoint(string path, string description, bool isDirectory)
    {
        FileSystemInfo item = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
        try
        {
            if (item.LinkTarget is not null ||
                (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0))
            {
                throw new InvalidDataException($"The {description} is a symbolic link or reparse point ('{path}'). Resolve it to a regular local path before migrating.");
            }
        }
        catch (FileNotFoundException)
        {
            // The caller reports missing required paths separately.
        }
        catch (DirectoryNotFoundException)
        {
            // The caller reports missing required paths separately.
        }
        catch (IOException ex) when (!item.Exists)
        {
            throw new InvalidDataException($"The {description} could not be inspected safely ('{path}'): {ex.Message}", ex);
        }
    }

    private static void RunIntegrityCheck(string target, IProgress<LogLine> log)
    {
        var databaseNames = BookshelfDatabaseFiles.GetPopulatedDatabaseNames(target);
        if (databaseNames.Count == 0)
        {
            throw new InvalidDataException("The SQLite backup contains no populated BookshelfNG databases.");
        }

        foreach (var databaseName in databaseNames)
        {
            var databasePath = Path.Combine(target, databaseName);
            log.Report(new LogLine(LogStream.Stdout, $"[backup] Running SQLite PRAGMA integrity_check on {databaseName}..."));

            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private
            }.ToString();

            try
            {
                using var connection = new SqliteConnection(connectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA integrity_check;";
                using var reader = command.ExecuteReader();

                var rows = new List<string>();
                while (reader.Read())
                {
                    rows.Add(reader.GetString(0));
                }

                if (rows.Count == 1 && string.Equals(rows[0], "ok", StringComparison.OrdinalIgnoreCase))
                {
                    log.Report(new LogLine(LogStream.Stdout, $"[backup] {databaseName} integrity_check: ok"));
                    continue;
                }

                var result = rows.Count == 0 ? "(no output)" : string.Join("; ", rows);
                log.Report(new LogLine(LogStream.Stderr, $"[backup] {databaseName} integrity_check failed: {result}"));
                throw new InvalidOperationException($"SQLite integrity_check failed on {databaseName} in the backup copy: {result}");
            }
            catch (SqliteException ex)
            {
                throw new InvalidOperationException($"SQLite integrity check error for {databaseName}: {ex.Message}", ex);
            }
        }
    }
}
