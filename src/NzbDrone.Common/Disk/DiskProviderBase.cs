using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Text;
using System.Threading;
using NLog;
using NzbDrone.Common.EnsureThat;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation;

namespace NzbDrone.Common.Disk
{
    public abstract class DiskProviderBase : IDiskProvider
    {
        private static readonly Logger Logger = NzbDroneLogger.GetLogger(typeof(DiskProviderBase));
        protected readonly IFileSystem _fileSystem;

        private enum PathResolutionMode
        {
            Read,
            WriteSafe
        }

        public DiskProviderBase(IFileSystem fileSystem)
        {
            _fileSystem = fileSystem;
        }

        public static StringComparison PathStringComparison
        {
            get
            {
                if (OsInfo.IsWindows)
                {
                    return StringComparison.OrdinalIgnoreCase;
                }

                return StringComparison.Ordinal;
            }
        }

        public abstract long? GetAvailableSpace(string path);
        public abstract void InheritFolderPermissions(string filename);
        public abstract void SetEveryonePermissions(string filename);
        public abstract void SetFilePermissions(string path, string mask, string group);
        public abstract void SetPermissions(string path, string mask, string group);
        public abstract void CopyPermissions(string sourcePath, string targetPath);
        public abstract long? GetTotalSize(string path);

        public DateTime FolderGetCreationTime(string path)
        {
            CheckFolderExists(path);

            return _fileSystem.DirectoryInfo.FromDirectoryName(path).CreationTimeUtc;
        }

        public DateTime FolderGetLastWrite(string path)
        {
            CheckFolderExists(path);

            var dirFiles = GetFiles(path, true).ToList();

            if (!dirFiles.Any())
            {
                return _fileSystem.DirectoryInfo.FromDirectoryName(path).LastWriteTimeUtc;
            }

            return dirFiles.Select(f => _fileSystem.FileInfo.FromFileName(f)).Max(c => c.LastWriteTimeUtc);
        }

        public DateTime FileGetLastWrite(string path)
        {
            var resolvedPath = ResolveExistingFilePath(path) ?? path;
            CheckFileExists(resolvedPath);

            return _fileSystem.FileInfo.FromFileName(resolvedPath).LastWriteTimeUtc;
        }

        private void CheckFolderExists(string path)
        {
            Ensure.That(path, () => path).IsValidPath(PathValidationType.CurrentOs);

            if (!FolderExists(path))
            {
                throw new DirectoryNotFoundException("Directory doesn't exist. " + path);
            }
        }

        private void CheckFileExists(string path)
        {
            Ensure.That(path, () => path).IsValidPath(PathValidationType.CurrentOs);

            if (!FileExists(path))
            {
                throw new FileNotFoundException("File doesn't exist: " + path);
            }
        }

        public void EnsureFolder(string path)
        {
            if (!FolderExists(path))
            {
                CreateFolder(path);
            }
        }

        public bool FolderExists(string path)
        {
            Ensure.That(path, () => path).IsValidPath(PathValidationType.CurrentOs);
            return _fileSystem.Directory.Exists(path);
        }

        public bool FileExists(string path)
        {
            Ensure.That(path, () => path).IsValidPath(PathValidationType.CurrentOs);
            return FileExists(path, PathStringComparison);
        }

        public bool FileExistsCanonical(string path)
        {
            Ensure.That(path, () => path).IsValidPath(PathValidationType.CurrentOs);
            return ResolveExistingFilePath(path, PathResolutionMode.WriteSafe).IsNotNullOrWhiteSpace();
        }

        public bool FileExists(string path, StringComparison stringComparison)
        {
            Ensure.That(path, () => path).IsValidPath(PathValidationType.CurrentOs);

            var exists = false;

            switch (stringComparison)
            {
                case StringComparison.CurrentCulture:
                case StringComparison.InvariantCulture:
                case StringComparison.Ordinal:
                    {
                        exists = _fileSystem.File.Exists(path) && path == path.GetActualCasing();
                        break;
                    }

                default:
                    {
                        exists = _fileSystem.File.Exists(path);
                        break;
                    }
            }

            if (exists)
            {
                return true;
            }

            return ResolveExistingFilePath(path).IsNotNullOrWhiteSpace();
        }

        public bool FolderWritable(string path)
        {
            Ensure.That(path, () => path).IsValidPath(PathValidationType.CurrentOs);

            try
            {
                // Use a unique test file name to avoid false negatives when a stale test file exists
                // with different ownership/permissions (e.g. previously created by a different UID).
                var testPath = Path.Combine(path, $"chaptarr_write_test_{Guid.NewGuid():N}.txt");
                var testContent = $"This file was created to verify if '{path}' is writable. It should've been automatically deleted. Feel free to delete it.";

                // File.WriteAllText is broken on net core when writing to some CIFS mounts.
                // Use a FileStream-based workaround, but avoid exclusive locks for maximum compatibility.
                using (var fs = new FileStream(testPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                using (var writer = new StreamWriter(fs))
                {
                    writer.Write(testContent);
                }

                _fileSystem.File.Delete(testPath);
                return true;
            }
            catch (Exception e)
            {
                Logger.Trace("Directory '{0}' isn't writable. {1}", path, e.Message);
                return false;
            }
        }

        public bool FolderEmpty(string path)
        {
            Ensure.That(path, () => path).IsValidPath(PathValidationType.CurrentOs);

            return _fileSystem.Directory.EnumerateFileSystemEntries(path).Empty();
        }

        public IEnumerable<string> GetDirectories(string path)
        {
            Ensure.That(path, () => path).IsValidPath(PathValidationType.CurrentOs);

            return _fileSystem.Directory.EnumerateDirectories(path);
        }

        public string[] GetDirectories(string path, SearchOption searchOption)
        {
            Ensure.That(path, () => path).IsValidPath(PathValidationType.CurrentOs);

            return _fileSystem.Directory.GetDirectories(path, "*", searchOption);
        }

        public IEnumerable<string> GetFiles(string path, bool recursive)
        {
            Ensure.That(path, () => path).IsValidPath(PathValidationType.CurrentOs);

            return _fileSystem.Directory.EnumerateFiles(path, "*", new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true
            });
        }

        public long GetFolderSize(string path)
        {
            Ensure.That(path, () => path).IsValidPath(PathValidationType.CurrentOs);

            return GetFiles(path, true).Sum(e => _fileSystem.FileInfo.FromFileName(e).Length);
        }

        public long GetFileSize(string path)
        {
            Ensure.That(path, () => path).IsValidPath(PathValidationType.CurrentOs);

            var resolvedPath = ResolveExistingFilePath(path) ?? path;

            if (!FileExists(resolvedPath))
            {
                throw new FileNotFoundException("File doesn't exist: " + path);
            }

            var fi = _fileSystem.FileInfo.FromFileName(resolvedPath);
            return fi.Length;
        }

        public void CreateFolder(string path)
        {
            Ensure.That(path, () => path).IsValidPath(PathValidationType.CurrentOs);
            _fileSystem.Directory.CreateDirectory(path);
        }

        public void DeleteFile(string path)
        {
            Ensure.That(path, () => path).IsValidPath(PathValidationType.CurrentOs);
            path = ResolveExistingFilePathForWrite(path, "delete file", allowMissing: true);
            Logger.Trace("Deleting file: {0}", path);

            RemoveReadOnly(path);

            _fileSystem.File.Delete(path);
        }

        public void CloneFile(string source, string destination, bool overwrite = false)
        {
            Ensure.That(source, () => source).IsValidPath(PathValidationType.CurrentOs);
            Ensure.That(destination, () => destination).IsValidPath(PathValidationType.CurrentOs);
            source = ResolveExistingFilePathForWrite(source, "clone file");

            if (PathEqualsForWriteSafety(source, destination))
            {
                throw new IOException(string.Format("Source and destination can't be the same {0}", source));
            }

            PrepareDestinationForWrite(destination, overwrite, "clone file", source);

            CloneFileInternal(source, destination, overwrite);
        }

        protected virtual void CloneFileInternal(string source, string destination, bool overwrite = false)
        {
            CopyFileInternal(source, destination, overwrite);
        }

        public void CopyFile(string source, string destination, bool overwrite = false)
        {
            Ensure.That(source, () => source).IsValidPath(PathValidationType.CurrentOs);
            Ensure.That(destination, () => destination).IsValidPath(PathValidationType.CurrentOs);
            source = ResolveExistingFilePathForWrite(source, "copy file");

            if (PathEqualsForWriteSafety(source, destination))
            {
                throw new IOException(string.Format("Source and destination can't be the same {0}", source));
            }

            PrepareDestinationForWrite(destination, overwrite, "copy file", source);

            CopyFileInternal(source, destination, overwrite);
        }

        protected virtual void CopyFileInternal(string source, string destination, bool overwrite = false)
        {
            _fileSystem.File.Copy(source, destination, overwrite);
        }

        public void MoveFile(string source, string destination, bool overwrite = false)
        {
            Ensure.That(source, () => source).IsValidPath(PathValidationType.CurrentOs);
            Ensure.That(destination, () => destination).IsValidPath(PathValidationType.CurrentOs);
            source = ResolveExistingFilePathForWrite(source, "move file");

            if (PathEqualsForWriteSafety(source, destination))
            {
                throw new IOException(string.Format("Source and destination can't be the same {0}", source));
            }

            PrepareDestinationForWrite(destination, overwrite, "move file", source);

            RemoveReadOnly(source);
            MoveFileInternal(source, destination);
        }

        public void MoveFolder(string source, string destination)
        {
            Ensure.That(source, () => source).IsValidPath(PathValidationType.CurrentOs);
            Ensure.That(destination, () => destination).IsValidPath(PathValidationType.CurrentOs);

            Directory.Move(source, destination);
        }

        protected virtual void MoveFileInternal(string source, string destination)
        {
            if (File.Exists(destination))
            {
                throw new FileAlreadyExistsException("File already exists", destination);
            }

            _fileSystem.File.Move(source, destination);
        }

        public virtual bool TryRenameFile(string source, string destination)
        {
            return false;
        }

        public abstract bool TryCreateHardLink(string source, string destination);

        public virtual int? GetFileLinkCount(string path)
        {
            return null;
        }

        public virtual bool TryCreateRefLink(string source, string destination)
        {
            return false;
        }

        public void DeleteFolder(string path, bool recursive)
        {
            Ensure.That(path, () => path).IsValidPath(PathValidationType.CurrentOs);

            var files = GetFiles(path, recursive).ToList();

            files.ForEach(RemoveReadOnly);

            var attempts = 0;

            while (attempts < 3 && files.Any())
            {
                EmptyFolder(path);

                if (GetFiles(path, recursive).Any())
                {
                    // Wait for IO operations to complete  after emptying the folder since they aren't always
                    // instantly removed and it can lead to false positives that files are still present.
                    Thread.Sleep(3000);
                }

                attempts++;
                files = GetFiles(path, recursive).ToList();
            }

            _fileSystem.Directory.Delete(path, recursive);
        }

        public string ReadAllText(string filePath)
        {
            Ensure.That(filePath, () => filePath).IsValidPath(PathValidationType.CurrentOs);

            return _fileSystem.File.ReadAllText(filePath);
        }

        public void WriteAllText(string filename, string contents)
        {
            Ensure.That(filename, () => filename).IsValidPath(PathValidationType.CurrentOs);
            RemoveReadOnly(filename);

            // File.WriteAllText is broken on net core when writing to some CIFS mounts
            // This workaround from https://github.com/dotnet/runtime/issues/42790#issuecomment-700362617
            using (var fs = new FileStream(filename, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using (var writer = new StreamWriter(fs))
                {
                    writer.Write(contents);
                }
            }
        }

        public void FolderSetLastWriteTime(string path, DateTime dateTime)
        {
            Ensure.That(path, () => path).IsValidPath(PathValidationType.CurrentOs);

            _fileSystem.Directory.SetLastWriteTimeUtc(path, dateTime);
        }

        public void FileSetLastWriteTime(string path, DateTime dateTime)
        {
            Ensure.That(path, () => path).IsValidPath(PathValidationType.CurrentOs);

            _fileSystem.File.SetLastWriteTime(path, dateTime);
        }

        public bool IsFileLocked(string file)
        {
            try
            {
                using (_fileSystem.File.Open(file, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    return false;
                }
            }
            catch (IOException)
            {
                return true;
            }
        }

        public string GetPathRoot(string path)
        {
            Ensure.That(path, () => path).IsValidPath(PathValidationType.CurrentOs);

            return Path.GetPathRoot(path);
        }

        public string GetParentFolder(string path)
        {
            Ensure.That(path, () => path).IsValidPath(PathValidationType.CurrentOs);

            var parent = _fileSystem.Directory.GetParent(path.TrimEnd(Path.DirectorySeparatorChar));

            if (parent == null)
            {
                return null;
            }

            return parent.FullName;
        }

        private static void RemoveReadOnly(string path)
        {
            if (File.Exists(path))
            {
                var attributes = File.GetAttributes(path);

                if (attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    var newAttributes = attributes & ~FileAttributes.ReadOnly;
                    File.SetAttributes(path, newAttributes);
                }
            }
        }

        public FileAttributes GetFileAttributes(string path)
        {
            return _fileSystem.File.GetAttributes(path);
        }

        public void EmptyFolder(string path)
        {
            Ensure.That(path, () => path).IsValidPath(PathValidationType.CurrentOs);

            foreach (var file in GetFiles(path, false))
            {
                DeleteFile(file);
            }

            foreach (var directory in GetDirectories(path))
            {
                DeleteFolder(directory, true);
            }
        }

        public string[] GetFixedDrives()
        {
            return GetMounts().Where(x => x.DriveType == DriveType.Fixed).Select(x => x.RootDirectory).ToArray();
        }

        public string GetVolumeLabel(string path)
        {
            var driveInfo = GetMounts().SingleOrDefault(d => d.RootDirectory.PathEquals(path));

            if (driveInfo == null)
            {
                return null;
            }

            return driveInfo.VolumeLabel;
        }

        public FileStream OpenReadStream(string path)
        {
            var resolvedPath = ResolveExistingFilePath(path) ?? path;

            if (!FileExists(resolvedPath))
            {
                throw new FileNotFoundException("Unable to find file: " + path, path);
            }

            return (FileStream)_fileSystem.FileStream.Create(resolvedPath, FileMode.Open, FileAccess.Read);
        }

        public FileStream OpenWriteStream(string path)
        {
            return (FileStream)_fileSystem.FileStream.Create(path, FileMode.Create);
        }

        public List<IMount> GetMounts()
        {
            return GetAllMounts().Where(d => !IsSpecialMount(d)).ToList();
        }

        protected virtual List<IMount> GetAllMounts()
        {
            return GetDriveInfoMounts().Where(d => d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Network || d.DriveType == DriveType.Removable)
                                       .Select(d => new DriveInfoMount(d))
                                       .Cast<IMount>()
                                       .ToList();
        }

        protected virtual bool IsSpecialMount(IMount mount)
        {
            return false;
        }

        public virtual IMount GetMount(string path)
        {
            try
            {
                var mounts = GetAllMounts();

                return mounts.Where(drive => drive.RootDirectory.PathEquals(path) ||
                                             drive.RootDirectory.IsParentPath(path))
                          .MaxBy(drive => drive.RootDirectory.Length);
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, $"Failed to get mount for path {path}");
                return null;
            }
        }

        protected List<IDriveInfo> GetDriveInfoMounts()
        {
            return _fileSystem.DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .ToList();
        }

        public List<IDirectoryInfo> GetDirectoryInfos(string path)
        {
            Ensure.That(path, () => path).IsValidPath(PathValidationType.CurrentOs);

            var di = _fileSystem.DirectoryInfo.FromDirectoryName(path);

            return di.GetDirectories().ToList();
        }

        public IDirectoryInfo GetDirectoryInfo(string path)
        {
            Ensure.That(path, () => path).IsValidPath(PathValidationType.CurrentOs);
            return _fileSystem.DirectoryInfo.FromDirectoryName(path);
        }

        public List<IFileInfo> GetFileInfos(string path, bool recursive = false)
        {
            Ensure.That(path, () => path).IsValidPath(PathValidationType.CurrentOs);

            var di = _fileSystem.DirectoryInfo.FromDirectoryName(path);

            return di.EnumerateFiles("*", new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true
            }).ToList();
        }

        public IFileInfo GetFileInfo(string path)
        {
            Ensure.That(path, () => path).IsValidPath(PathValidationType.CurrentOs);
            return _fileSystem.FileInfo.FromFileName(ResolveExistingFilePath(path) ?? path);
        }

        private string ResolveExistingFilePath(string path)
        {
            return ResolveExistingFilePath(path, PathResolutionMode.Read);
        }

        private string ResolveExistingFilePath(string path, PathResolutionMode mode)
        {
            Ensure.That(path, () => path).IsValidPath(PathValidationType.CurrentOs);

            if (_fileSystem.File.Exists(path))
            {
                return path;
            }

            var root = Path.GetPathRoot(path);
            if (root.IsNullOrWhiteSpace())
            {
                return null;
            }

            var remaining = path.Substring(root.Length);
            if (remaining.IsNullOrWhiteSpace())
            {
                return null;
            }

            var segments = remaining
                .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

            if (segments.Length == 0)
            {
                return null;
            }

            var current = root;

            for (var i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];
                var isLast = i == segments.Length - 1;
                var direct = Path.Combine(current, segment);

                if ((!isLast && _fileSystem.Directory.Exists(direct)) ||
                    (isLast && _fileSystem.File.Exists(direct)))
                {
                    current = direct;
                    continue;
                }

                current = ResolvePathSegment(current, segment, isLast, mode);
                if (current.IsNullOrWhiteSpace())
                {
                    return null;
                }
            }

            if (_fileSystem.File.Exists(current))
            {
                Logger.Debug("Resolved best-effort file path '{0}' -> '{1}'", path, current);
                return current;
            }

            return null;
        }

        private string ResolveExistingFilePathForWrite(string path, string operation, bool allowMissing = false)
        {
            var resolvedPath = ResolveExistingFilePath(path, PathResolutionMode.WriteSafe);
            if (resolvedPath.IsNotNullOrWhiteSpace())
            {
                return resolvedPath;
            }

            var readResolvedPath = ResolveExistingFilePath(path, PathResolutionMode.Read);
            if (readResolvedPath.IsNotNullOrWhiteSpace())
            {
                Logger.Warn("Refusing to {0} '{1}' because it only matched '{2}' using loose Unicode path recovery. No files were changed.",
                    operation,
                    path,
                    readResolvedPath);

                throw new FileNotFoundException(
                    string.Format("File could not be safely resolved for {0}: {1}. Loose read match was: {2}", operation, path, readResolvedPath),
                    path);
            }

            if (!allowMissing)
            {
                throw new FileNotFoundException("File doesn't exist: " + path, path);
            }

            return path;
        }

        private void PrepareDestinationForWrite(string destination, bool overwrite, string operation, string source = null)
        {
            var resolvedDestination = ResolveExistingFilePath(destination, PathResolutionMode.WriteSafe);
            if (resolvedDestination.IsNotNullOrWhiteSpace())
            {
                if (source.IsNotNullOrWhiteSpace() && PathEqualsForWriteSafety(resolvedDestination, source))
                {
                    throw new IOException(string.Format("Source and destination can't be the same {0}", source));
                }

                if (overwrite)
                {
                    DeleteFile(resolvedDestination);
                    return;
                }

                throw new FileAlreadyExistsException("File already exists", destination);
            }

            if (!overwrite)
            {
                return;
            }

            var readResolvedDestination = ResolveExistingFilePath(destination, PathResolutionMode.Read);
            if (readResolvedDestination.IsNotNullOrWhiteSpace())
            {
                Logger.Warn("Refusing to overwrite '{0}' for {1} because it only matched '{2}' using loose Unicode path recovery. No files were changed.",
                    destination,
                    operation,
                    readResolvedDestination);

                throw new IOException(string.Format("Destination could not be safely resolved for {0}: {1}. Loose read match was: {2}", operation, destination, readResolvedDestination));
            }
        }

        private string ResolvePathSegment(string currentDirectory, string requestedSegment, bool expectFile, PathResolutionMode mode)
        {
            if (!_fileSystem.Directory.Exists(currentDirectory))
            {
                return null;
            }

            var candidates = expectFile
                ? _fileSystem.Directory.EnumerateFiles(currentDirectory)
                : _fileSystem.Directory.EnumerateDirectories(currentDirectory);

            var matches = candidates
                .Select(path => new PathSegmentCandidate(path, Path.GetFileName(path)))
                .Where(candidate => candidate.Name.IsNotNullOrWhiteSpace())
                .ToList();

            if (!matches.Any())
            {
                return null;
            }

            var exact = matches
                .Where(candidate => string.Equals(candidate.Name, requestedSegment, PathStringComparison))
                .ToList();

            if (exact.Count == 1)
            {
                return exact[0].Path;
            }

            var requestedCanonical = NormalizePathSegmentForCanonicalComparison(requestedSegment);
            var canonical = matches
                .Where(candidate => string.Equals(NormalizePathSegmentForCanonicalComparison(candidate.Name), requestedCanonical, PathStringComparison))
                .ToList();

            if (canonical.Count == 1)
            {
                return canonical[0].Path;
            }

            if (mode == PathResolutionMode.WriteSafe)
            {
                return null;
            }

            var requestedKey = NormalizePathSegmentForLookup(requestedSegment);
            var normalized = matches
                .Where(candidate => ExtensionMatches(candidate.Name, requestedSegment, expectFile) &&
                                    NormalizePathSegmentForLookup(candidate.Name) == requestedKey)
                .ToList();

            if (normalized.Count == 1)
            {
                return normalized[0].Path;
            }

            if (requestedSegment.Contains('\uFFFD'))
            {
                var replacementStrippedKey = NormalizePathSegmentForLookup(requestedSegment.Replace("\uFFFD", string.Empty));

                if (replacementStrippedKey.IsNotNullOrWhiteSpace())
                {
                    var loose = matches
                        .Where(candidate => ExtensionMatches(candidate.Name, requestedSegment, expectFile) &&
                                            IsLooseSegmentMatch(replacementStrippedKey, NormalizePathSegmentForLookup(candidate.Name)))
                        .ToList();

                    if (loose.Count == 1)
                    {
                        return loose[0].Path;
                    }
                }
            }

            return null;
        }

        private static string NormalizePathSegmentForCanonicalComparison(string text)
        {
            return string.IsNullOrWhiteSpace(text) ? string.Empty : text.Normalize(NormalizationForm.FormC);
        }

        private static bool PathEqualsForWriteSafety(string firstPath, string secondPath)
        {
            if (firstPath.PathEquals(secondPath))
            {
                return true;
            }

            var firstCanonical = NormalizePathForCanonicalComparison(firstPath);
            var secondCanonical = NormalizePathForCanonicalComparison(secondPath);

            return string.Equals(firstCanonical, secondCanonical, PathStringComparison);
        }

        private static string NormalizePathForCanonicalComparison(string path)
        {
            return path.IsNullOrWhiteSpace() ? string.Empty : path.CleanFilePath().Normalize(NormalizationForm.FormC);
        }

        private static bool ExtensionMatches(string actualSegment, string requestedSegment, bool expectFile)
        {
            if (!expectFile)
            {
                return true;
            }

            var requestedExtension = Path.GetExtension(requestedSegment);
            if (requestedExtension.IsNullOrWhiteSpace())
            {
                return true;
            }

            return string.Equals(Path.GetExtension(actualSegment), requestedExtension, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLooseSegmentMatch(string requestedKey, string actualKey)
        {
            if (requestedKey.IsNullOrWhiteSpace() || actualKey.IsNullOrWhiteSpace())
            {
                return false;
            }

            if (string.Equals(requestedKey, actualKey, StringComparison.Ordinal))
            {
                return true;
            }

            if (actualKey.Contains(requestedKey, StringComparison.Ordinal) ||
                requestedKey.Contains(actualKey, StringComparison.Ordinal))
            {
                return true;
            }

            return IsSubsequence(requestedKey, actualKey);
        }

        private static bool IsSubsequence(string needle, string haystack)
        {
            if (needle.Length == 0)
            {
                return false;
            }

            var needleIndex = 0;

            for (var i = 0; i < haystack.Length && needleIndex < needle.Length; i++)
            {
                if (haystack[i] == needle[needleIndex])
                {
                    needleIndex++;
                }
            }

            return needleIndex == needle.Length;
        }

        private static string NormalizePathSegmentForLookup(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var normalized = text.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(normalized.Length);

            foreach (var ch in normalized)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(ch);

                if (category == UnicodeCategory.NonSpacingMark ||
                    category == UnicodeCategory.SpacingCombiningMark ||
                    category == UnicodeCategory.EnclosingMark ||
                    ch == '\uFFFD')
                {
                    continue;
                }

                if (char.IsLetterOrDigit(ch))
                {
                    builder.Append(char.ToLowerInvariant(ch));
                }
            }

            return builder.ToString().Normalize(NormalizationForm.FormC);
        }

        private readonly struct PathSegmentCandidate
        {
            public PathSegmentCandidate(string path, string name)
            {
                Path = path;
                Name = name;
            }

            public string Path { get; }

            public string Name { get; }
        }

        public void RemoveEmptySubfolders(string path)
        {
            // Depth first search for empty subdirectories
            foreach (var subdir in Directory.EnumerateDirectories(path))
            {
                RemoveEmptySubfolders(subdir);

                if (Directory.EnumerateFileSystemEntries(subdir).Empty())
                {
                    try
                    {
                        Directory.Delete(subdir, false);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, "Failed to remove empty directory {0}", subdir);
                    }
                }
            }
        }

        public void SaveStream(Stream stream, string path)
        {
            using (var fileStream = OpenWriteStream(path))
            {
                stream.CopyTo(fileStream);
            }
        }

        public virtual bool IsValidFolderPermissionMask(string mask)
        {
            throw new NotSupportedException();
        }
    }
}
