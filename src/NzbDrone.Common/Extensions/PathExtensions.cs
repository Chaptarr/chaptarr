using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnsureThat;
using NzbDrone.Common.EnvironmentInfo;

namespace NzbDrone.Common.Extensions
{
    public static class PathExtensions
    {
        private const string APP_CONFIG_FILE = "config.xml";
        private const string DB = "chaptarr.db";
        private const string DB_RESTORE = "chaptarr.restore";
        private const string LOG_DB = "logs.db";
        private const string CACHE_DB = "cache.db";
        private const string NLOG_CONFIG_FILE = "nlog.config";
        private const string UPDATE_CLIENT_EXE_NAME = "Chaptarr.Update";

        private static readonly string UPDATE_SANDBOX_FOLDER_NAME = "chaptarr_update" + Path.DirectorySeparatorChar;
        private static readonly string UPDATE_PACKAGE_FOLDER_NAME = "Chaptarr" + Path.DirectorySeparatorChar;
        private static readonly string UPDATE_BACKUP_FOLDER_NAME = "chaptarr_backup" + Path.DirectorySeparatorChar;
        private static readonly string UPDATE_BACKUP_APPDATA_FOLDER_NAME = "chaptarr_appdata_backup" + Path.DirectorySeparatorChar;
        private static readonly string UPDATE_CLIENT_FOLDER_NAME = "Chaptarr.Update" + Path.DirectorySeparatorChar;
        private static readonly string UPDATE_LOG_FOLDER_NAME = "UpdateLogs" + Path.DirectorySeparatorChar;

        private static readonly Regex PARENT_PATH_END_SLASH_REGEX = new Regex(@"(?<!:)\\$", RegexOptions.Compiled);

        public static string CleanFilePath(this string path)
        {
            Ensure.That(path, () => path).IsNotNullOrWhiteSpace();
            Ensure.That(path, () => path).IsValidPath(PathValidationType.AnyOs);

            var info = new FileInfo(path.Trim());
            return info.FullName.CleanFilePathBasic();
        }

        public static string CleanFilePathBasic(this string path)
        {
            //UNC
            if (!path.Contains('/') && path.StartsWith(@"\\"))
            {
                return path.TrimEnd('/', '\\', ' ');
            }

            if (OsInfo.IsNotWindows && path.TrimEnd('/').Length == 0)
            {
                return "/";
            }

            return path.TrimEnd('/').Trim('\\', ' ');
        }

        public static bool PathNotEquals(this string firstPath, string secondPath, StringComparison? comparison = null)
        {
            return !PathEquals(firstPath, secondPath, comparison);
        }

        public static bool PathEquals(this string firstPath, string secondPath, StringComparison? comparison = null)
        {
            // Handle null cases
            if (firstPath == null && secondPath == null)
            {
                return true;
            }

            if (firstPath == null || secondPath == null)
            {
                return false;
            }

            if (!comparison.HasValue)
            {
                comparison = DiskProviderBase.PathStringComparison;
            }

            if (firstPath.Equals(secondPath, comparison.Value))
            {
                return true;
            }

            return string.Equals(firstPath.CleanFilePath(), secondPath.CleanFilePath(), comparison.Value);
        }

        public static string GetPathExtension(this string path)
        {
            var idx = path.LastIndexOf('.');
            if (idx == -1 || idx == path.Length - 1)
            {
                return string.Empty;
            }

            return path.Substring(idx);
        }

        public static string GetRelativePath(this string parentPath, string childPath)
        {
            if (!parentPath.IsParentPath(childPath))
            {
                throw new NotParentException("{0} is not a child of {1}", childPath, parentPath);
            }

            return childPath.Substring(parentPath.Length).Trim(Path.DirectorySeparatorChar);
        }

        public static string GetParentPath(this string childPath)
        {
            var cleanPath = OsInfo.IsWindows
                ? PARENT_PATH_END_SLASH_REGEX.Replace(childPath, "")
                : childPath.TrimEnd(Path.DirectorySeparatorChar);

            if (cleanPath.IsNullOrWhiteSpace())
            {
                return null;
            }

            return Directory.GetParent(cleanPath)?.FullName;
        }

        public static string GetCleanPath(this string path)
        {
            var cleanPath = OsInfo.IsWindows
                ? PARENT_PATH_END_SLASH_REGEX.Replace(path, "")
                : path.TrimEnd(Path.DirectorySeparatorChar);

            return cleanPath;
        }

        public static bool IsParentPath(this string parentPath, string childPath)
        {
            // Handle null cases
            if (parentPath == null || childPath == null)
            {
                return false;
            }

            if (parentPath != "/" && !parentPath.EndsWith(":\\"))
            {
                parentPath = parentPath.TrimEnd(Path.DirectorySeparatorChar);
            }

            if (childPath != "/" && !parentPath.EndsWith(":\\"))
            {
                childPath = childPath.TrimEnd(Path.DirectorySeparatorChar);
            }

            var parent = new DirectoryInfo(parentPath);
            var child = new DirectoryInfo(childPath);

            while (child.Parent != null)
            {
                if (child.Parent.FullName.Equals(parent.FullName, DiskProviderBase.PathStringComparison))
                {
                    return true;
                }

                child = child.Parent;
            }

            return false;
        }

        private static readonly Regex WindowsPathWithDriveRegex = new Regex(@"^[a-zA-Z]:\\", RegexOptions.Compiled);

        public static bool IsPathValid(this string path, PathValidationType validationType)
        {
            if (string.IsNullOrWhiteSpace(path) || path.ContainsInvalidPathChars())
            {
                return false;
            }

            if (validationType == PathValidationType.AnyOs)
            {
                return IsPathValidForWindows(path) || IsPathValidForNonWindows(path);
            }

            if (OsInfo.IsNotWindows)
            {
                return IsPathValidForNonWindows(path);
            }

            return IsPathValidForWindows(path);
        }

        public static bool ContainsInvalidPathChars(this string text)
        {
            if (text.IsNullOrWhiteSpace())
            {
                throw new ArgumentNullException(nameof(text));
            }

            return text.IndexOfAny(Path.GetInvalidPathChars()) >= 0;
        }

        private static string GetProperCapitalization(DirectoryInfo dirInfo)
        {
            var parentDirInfo = dirInfo.Parent;
            if (parentDirInfo == null)
            {
                //Drive letter
                return dirInfo.Name.ToUpper();
            }

            var folderName = dirInfo.Name;

            if (dirInfo.Exists)
            {
                folderName = parentDirInfo.GetDirectories(dirInfo.Name)[0].Name;
            }

            return Path.Combine(GetProperCapitalization(parentDirInfo), folderName);
        }

        public static string GetActualCasing(this string path)
        {
            if (OsInfo.IsNotWindows || path.StartsWith("\\"))
            {
                return path;
            }

            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.Directory) == FileAttributes.Directory)
            {
                return GetProperCapitalization(new DirectoryInfo(path));
            }

            var fileInfo = new FileInfo(path);
            var dirInfo = fileInfo.Directory;

            var fileName = fileInfo.Name;

            if (dirInfo != null && fileInfo.Exists)
            {
                fileName = dirInfo.GetFiles(fileInfo.Name)[0].Name;
            }

            return Path.Combine(GetProperCapitalization(dirInfo), fileName);
        }

        public static List<string> GetAncestorFolders(this string path)
        {
            var directory = new DirectoryInfo(path);
            var directories = new List<string>();

            while (directory != null)
            {
                directories.Insert(0, directory.Name);

                directory = directory.Parent;
            }

            return directories;
        }

        public static string GetAncestorPath(this string path, string ancestorName)
        {
            var parent = Path.GetDirectoryName(path);

            while (parent != null)
            {
                var currentPath = parent;
                parent = Path.GetDirectoryName(parent);

                if (Path.GetFileName(currentPath) == ancestorName)
                {
                    return currentPath;
                }
            }

            return null;
        }

        public static string ProcessNameToExe(this string processName)
        {
            if (OsInfo.IsWindows)
            {
                processName += ".exe";
            }

            return processName;
        }

        public static string GetLongestCommonPath(this List<string> paths)
        {
            var firstPath = paths.First();
            var length = firstPath.Length;

            for (var i = 1; i < paths.Count; i++)
            {
                var path = paths[i];

                length = Math.Min(length, path.Length);

                for (var characterIndex = 0; characterIndex < length; characterIndex++)
                {
                    if (path[characterIndex] != firstPath[characterIndex])
                    {
                        length = characterIndex;
                        break;
                    }
                }
            }

            var substring = firstPath.Substring(0, length);
            var lastSeparatorIndex = substring.LastIndexOfAny(new[] { '/', '\\' });

            return substring.Substring(0, lastSeparatorIndex);
        }

        public static string GetAppDataPath(this IAppFolderInfo appFolderInfo)
        {
            return appFolderInfo.AppDataFolder;
        }

        public static string GetDataProtectionPath(this IAppFolderInfo appFolderInfo)
        {
            return Path.Combine(GetAppDataPath(appFolderInfo), "asp");
        }

        public static string GetLogFolder(this IAppFolderInfo appFolderInfo)
        {
            return Path.Combine(GetAppDataPath(appFolderInfo), "logs");
        }

        public static string GetConfigPath(this IAppFolderInfo appFolderInfo)
        {
            return Path.Combine(GetAppDataPath(appFolderInfo), APP_CONFIG_FILE);
        }

        public static string GetMediaCoverPath(this IAppFolderInfo appFolderInfo)
        {
            return Path.Combine(GetAppDataPath(appFolderInfo), "MediaCover");
        }

        public static string GetUpdateLogFolder(this IAppFolderInfo appFolderInfo)
        {
            return Path.Combine(GetAppDataPath(appFolderInfo), UPDATE_LOG_FOLDER_NAME);
        }

        public static string GetUpdateSandboxFolder(this IAppFolderInfo appFolderInfo)
        {
            return Path.Combine(appFolderInfo.TempFolder, UPDATE_SANDBOX_FOLDER_NAME);
        }

        public static string GetUpdateBackUpFolder(this IAppFolderInfo appFolderInfo)
        {
            return Path.Combine(GetUpdateSandboxFolder(appFolderInfo), UPDATE_BACKUP_FOLDER_NAME);
        }

        public static string GetUpdateBackUpAppDataFolder(this IAppFolderInfo appFolderInfo)
        {
            return Path.Combine(GetUpdateSandboxFolder(appFolderInfo), UPDATE_BACKUP_APPDATA_FOLDER_NAME);
        }

        public static string GetUpdateBackupConfigFile(this IAppFolderInfo appFolderInfo)
        {
            return Path.Combine(GetUpdateBackUpAppDataFolder(appFolderInfo), APP_CONFIG_FILE);
        }

        public static string GetUpdateBackupDatabase(this IAppFolderInfo appFolderInfo)
        {
            return Path.Combine(GetUpdateBackUpAppDataFolder(appFolderInfo), DB);
        }

        public static string GetUpdatePackageFolder(this IAppFolderInfo appFolderInfo)
        {
            return Path.Combine(GetUpdateSandboxFolder(appFolderInfo), UPDATE_PACKAGE_FOLDER_NAME);
        }

        public static string GetUpdateClientFolder(this IAppFolderInfo appFolderInfo)
        {
            return Path.Combine(GetUpdatePackageFolder(appFolderInfo), UPDATE_CLIENT_FOLDER_NAME);
        }

        public static string GetUpdateClientExePath(this IAppFolderInfo appFolderInfo)
        {
            return Path.Combine(GetUpdateSandboxFolder(appFolderInfo), UPDATE_CLIENT_EXE_NAME).ProcessNameToExe();
        }

        public static string GetDatabase(this IAppFolderInfo appFolderInfo)
        {
            var appDataPath = GetAppDataPath(appFolderInfo);
            var primaryPath = Path.Combine(appDataPath, DB);

            // Keep the resolver file-only. Opening the primary database here runs
            // before DbFactory's restore/recovery path and can prevent recovery of
            // a damaged database. The migration lineage guard checks for sibling
            // legacy databases after restore and before any migration is applied.
            var primaryHasData = File.Exists(primaryPath) && new FileInfo(primaryPath).Length > 0;
            if (primaryHasData && !appFolderInfo.MergeBookshelfDatabases)
            {
                return primaryPath;
            }

            // A prior startup may have created a zero-byte chaptarr.db before
            // discovering an existing BookshelfNG/Readarr database. Keep this
            // check file-only; schema validation belongs after database recovery.
            var legacyDatabasePaths = GetLegacyDatabasePaths(appDataPath);
            var hasBookshelfDatabasePair =
                legacyDatabasePaths.Any(path => string.Equals(Path.GetFileName(path), "readarr.db", StringComparison.OrdinalIgnoreCase)) &&
                legacyDatabasePaths.Any(path => string.Equals(Path.GetFileName(path), "audioarr.db", StringComparison.OrdinalIgnoreCase));
            var primaryHasRecoveryData = HasNonEmptyDurableSqliteSidecar(primaryPath);
            if (primaryHasRecoveryData && !File.Exists(primaryPath))
            {
                throw new InvalidOperationException(
                    "The Chaptarr database file is missing but its WAL or rollback journal contains data. Preserve the database and sidecars and resolve recovery before starting Chaptarr.");
            }

            if (primaryHasRecoveryData && legacyDatabasePaths.Count > 0)
            {
                throw new InvalidOperationException(
                    "A Chaptarr database sidecar and a BookshelfNG database both contain data. Preserve all database files and resolve the source before starting Chaptarr.");
            }

            if (primaryHasData)
            {
                if (appFolderInfo.MergeBookshelfDatabases && hasBookshelfDatabasePair)
                {
                    // Let explicit preview/apply inspect both legacy sources even
                    // when a prior launch left a SQLite target. The converter will
                    // fail closed unless that target is only an empty placeholder.
                    return Path.Combine(appDataPath, "readarr.db");
                }

                return primaryPath;
            }

            if (legacyDatabasePaths.Count > 1)
            {
                if (!primaryHasRecoveryData && hasBookshelfDatabasePair)
                {
                    if (appFolderInfo.MergeBookshelfDatabases)
                    {
                        // The explicit preview/apply utility starts from the stable
                        // BookshelfNG identity database. Normal startup instead keeps
                        // the native target path so MigrationController can stage and
                        // validate both legacy sources before promoting it.
                        return Path.Combine(appDataPath, "readarr.db");
                    }

                    return primaryPath;
                }

                throw new InvalidOperationException("Multiple legacy database files were found. Choose one before starting Chaptarr.");
            }

            if (primaryHasRecoveryData)
            {
                return primaryPath;
            }

            if (legacyDatabasePaths.Count == 1)
            {
                return legacyDatabasePaths[0];
            }

            return primaryPath;
        }

        private static List<string> GetLegacyDatabasePaths(string appDataPath)
        {
            var paths = new List<string>();
            foreach (var legacyName in new[] { "readarr.db", "audioarr.db" })
            {
                var legacyPath = Path.Combine(appDataPath, legacyName);
                EnsureLegacyDatabasePathsAreRegular(legacyPath, legacyName);

                var databaseExists = File.Exists(legacyPath);
                var hasDurableSidecar = HasNonEmptyDurableSqliteSidecar(legacyPath);
                if (hasDurableSidecar && !databaseExists)
                {
                    throw new InvalidOperationException(
                        $"The BookshelfNG database file '{legacyName}' is missing but its WAL or rollback journal contains data. Preserve the database and sidecars and resolve recovery before starting Chaptarr.");
                }

                if ((databaseExists && new FileInfo(legacyPath).Length > 0) || hasDurableSidecar)
                {
                    paths.Add(legacyPath);
                }
            }

            return paths;
        }

        private static void EnsureLegacyDatabasePathsAreRegular(string databasePath, string databaseName)
        {
            EnsureRegularFileSystemPath(databasePath, $"BookshelfNG database '{databaseName}'");
            foreach (var suffix in new[] { "-journal", "-wal", "-shm" })
            {
                EnsureRegularFileSystemPath(databasePath + suffix, $"BookshelfNG SQLite sidecar '{databaseName + suffix}'");
            }
        }

        private static void EnsureRegularFileSystemPath(string path, string description)
        {
            var file = new FileInfo(path);
            var directory = new DirectoryInfo(path);
            if (file.LinkTarget is not null || directory.LinkTarget is not null)
            {
                throw new InvalidOperationException($"The {description} is a symbolic link. Resolve it to a regular file before starting Chaptarr.");
            }

            if (Directory.Exists(path))
            {
                throw new InvalidOperationException($"The {description} is a directory, not a regular file.");
            }

            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException($"The {description} is a reparse point. Resolve it to a regular file before starting Chaptarr.");
            }
        }

        private static bool HasNonEmptyDurableSqliteSidecar(string databasePath)
        {
            foreach (var suffix in new[] { "-journal", "-wal" })
            {
                var sidecarPath = databasePath + suffix;
                if (File.Exists(sidecarPath) && new FileInfo(sidecarPath).Length > 0)
                {
                    return true;
                }
            }

            return false;
        }
        public static string GetDatabaseRestore(this IAppFolderInfo appFolderInfo)
        {
            return Path.Combine(GetAppDataPath(appFolderInfo), DB_RESTORE);
        }

        public static string GetLogDatabase(this IAppFolderInfo appFolderInfo)
        {
            return Path.Combine(GetAppDataPath(appFolderInfo), LOG_DB);
        }

        public static string GetCacheDatabase(this IAppFolderInfo appFolderInfo)
        {
            return Path.Combine(GetAppDataPath(appFolderInfo), CACHE_DB);
        }

        public static string GetNlogConfigPath(this IAppFolderInfo appFolderInfo)
        {
            return Path.Combine(appFolderInfo.StartUpFolder, NLOG_CONFIG_FILE);
        }

        private static bool IsPathValidForWindows(string path)
        {
            return path.StartsWith("\\") || WindowsPathWithDriveRegex.IsMatch(path);
        }

        private static bool IsPathValidForNonWindows(string path)
        {
            return path.StartsWith("/");
        }
    }
}
