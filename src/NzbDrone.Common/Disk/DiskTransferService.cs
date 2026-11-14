using System;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Threading;
using NLog;
using NzbDrone.Common.EnsureThat;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Common.Disk
{
    public interface IDiskTransferService
    {
        TransferMode TransferFolder(string sourcePath, string targetPath, TransferMode mode);
        TransferMode TransferFolder(string sourcePath, string targetPath, TransferMode mode, bool includeIgnored, Action<string> onMoved = null, bool overwrite = true);
        TransferMode TransferFile(string sourcePath, string targetPath, TransferMode mode, bool overwrite = false);
        int MirrorFolder(string sourcePath, string targetPath);
    }

    public class DiskTransferService : IDiskTransferService
    {
        private readonly IDiskProvider _diskProvider;
        private readonly Logger _logger;

        public DiskTransferService(IDiskProvider diskProvider, Logger logger)
        {
            _diskProvider = diskProvider;
            _logger = logger;
        }

        private string ResolveRealParentPath(string path)
        {
            var normalizedPath = Path.GetFullPath(path).GetCleanPath();
            var parentPath = normalizedPath.GetParentPath();
            if (!_diskProvider.FolderExists(parentPath))
            {
                return path;
            }

            var realParentPath = parentPath.GetActualCasing();

            // Derive both parts from the same normalized path so terminal dot components
            // resolve to the selected folder. Expanding a short-name parent must not
            // change where the child name starts.
            return Path.Combine(realParentPath, Path.GetFileName(normalizedPath));
        }

        private static string DescribeMountRoot(IMount mount)
        {
            return mount != null && mount.RootDirectory.IsNotNullOrWhiteSpace() ? mount.RootDirectory : "unknown";
        }

        private static string DescribeDriveFormat(string driveFormat)
        {
            return driveFormat.IsNotNullOrWhiteSpace() ? driveFormat : "unknown";
        }

        public TransferMode TransferFolder(string sourcePath, string targetPath, TransferMode mode)
        {
            return TransferFolder(sourcePath, targetPath, mode, false);
        }

        public TransferMode TransferFolder(string sourcePath, string targetPath, TransferMode mode, bool includeIgnored, Action<string> onMoved = null, bool overwrite = true)
        {
            Ensure.That(sourcePath, () => sourcePath).IsValidPath(PathValidationType.CurrentOs);
            Ensure.That(targetPath, () => targetPath).IsValidPath(PathValidationType.CurrentOs);

            sourcePath = ResolveRealParentPath(sourcePath);
            targetPath = ResolveRealParentPath(targetPath);

            _logger.Debug("{0} Directory [{1}] > [{2}]", mode, sourcePath, targetPath);

            if (sourcePath == targetPath)
            {
                throw new IOException(string.Format("Source and destination can't be the same {0}", sourcePath));
            }

            if (mode == TransferMode.Move && sourcePath.PathEquals(targetPath, StringComparison.InvariantCultureIgnoreCase) && _diskProvider.FolderExists(targetPath))
            {
                // Move folder out of the way to allow case-insensitive renames
                var tempPath = sourcePath + ".backup~";
                _logger.Trace("Rename Intermediate Directory [{0}] > [{1}]", sourcePath, tempPath);
                _diskProvider.MoveFolder(sourcePath, tempPath);

                if (!_diskProvider.FolderExists(targetPath))
                {
                    _logger.Trace("Rename Intermediate Directory [{0}] > [{1}]", tempPath, targetPath);
                    _logger.Debug("Rename Directory [{0}] > [{1}]", sourcePath, targetPath);
                    _diskProvider.MoveFolder(tempPath, targetPath);
                    onMoved?.Invoke(sourcePath);
                    return mode;
                }

                // There were two separate folders, revert the intermediate rename and let the recursion deal with it
                _logger.Trace("Rename Intermediate Directory [{0}] > [{1}]", tempPath, sourcePath);
                _diskProvider.MoveFolder(tempPath, sourcePath);
            }

            if (mode == TransferMode.Move && !_diskProvider.FolderExists(targetPath))
            {
                var sourceMount = _diskProvider.GetMount(sourcePath);
                var targetMount = _diskProvider.GetMount(targetPath);

                // If we're on the same mount, do a simple folder move.
                if (sourceMount != null && targetMount != null && sourceMount.RootDirectory == targetMount.RootDirectory)
                {
                    _logger.Debug("Rename Directory [{0}] > [{1}]", sourcePath, targetPath);
                    _diskProvider.MoveFolder(sourcePath, targetPath);
                    onMoved?.Invoke(sourcePath);
                    return mode;
                }
            }

            if (!_diskProvider.FolderExists(targetPath))
            {
                _diskProvider.CreateFolder(targetPath);

                _diskProvider.CopyPermissions(sourcePath, targetPath);
            }

            var result = mode;

            foreach (var subDir in _diskProvider.GetDirectoryInfos(sourcePath))
            {
                if (!includeIgnored && ShouldIgnore(subDir))
                {
                    continue;
                }

                result &= TransferFolder(subDir.FullName, Path.Combine(targetPath, subDir.Name), mode, includeIgnored, onMoved, overwrite);
            }

            var sourceFiles = includeIgnored
                ? _diskProvider.GetDirectoryInfo(sourcePath).GetFiles().AsEnumerable()
                : _diskProvider.GetFileInfos(sourcePath);
            foreach (var sourceFile in sourceFiles)
            {
                if (!includeIgnored && ShouldIgnore(sourceFile))
                {
                    continue;
                }

                var destFile = Path.Combine(targetPath, sourceFile.Name);

                TransferMode fileResult;
                try
                {
                    fileResult = TransferFile(sourceFile.FullName, destFile, mode, overwrite);
                }
                catch (DestinationAlreadyExistsException) when (!overwrite)
                {
                    _logger.Warn("Keeping source '{0}': destination '{1}' already exists and was not overwritten", sourceFile.FullName, destFile);
                    continue;
                }

                result &= fileResult;
                if (fileResult.HasFlag(TransferMode.Move))
                {
                    onMoved?.Invoke(sourceFile.FullName);
                }
            }

            if (mode.HasFlag(TransferMode.Move))
            {
                // All selected transfers have completed. Cleanup cannot undo them or authorize
                // deleting skipped files/late arrivals; callers requiring removal of the whole
                // source folder must verify that separately.
                try
                {
                    _diskProvider.DeleteFolder(sourcePath, false);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    _logger.Warn(ex, "Files transferred to '{0}', but source folder '{1}' could not be removed; any remaining contents were preserved", targetPath, sourcePath);
                }
            }

            return result;
        }

        public int MirrorFolder(string sourcePath, string targetPath)
        {
            var filesCopied = 0;

            Ensure.That(sourcePath, () => sourcePath).IsValidPath(PathValidationType.CurrentOs);
            Ensure.That(targetPath, () => targetPath).IsValidPath(PathValidationType.CurrentOs);

            sourcePath = ResolveRealParentPath(sourcePath);
            targetPath = ResolveRealParentPath(targetPath);

            _logger.Debug("Mirror Folder [{0}] > [{1}]", sourcePath, targetPath);

            if (!_diskProvider.FolderExists(targetPath))
            {
                _diskProvider.CreateFolder(targetPath);
            }

            var sourceFolders = _diskProvider.GetDirectoryInfos(sourcePath);
            var targetFolders = _diskProvider.GetDirectoryInfos(targetPath);

            foreach (var subDir in targetFolders.Where(v => !sourceFolders.Any(d => d.Name == v.Name)))
            {
                if (ShouldIgnore(subDir))
                {
                    continue;
                }

                _diskProvider.DeleteFolder(subDir.FullName, true);
            }

            foreach (var subDir in sourceFolders)
            {
                if (ShouldIgnore(subDir))
                {
                    continue;
                }

                filesCopied += MirrorFolder(subDir.FullName, Path.Combine(targetPath, subDir.Name));
            }

            var sourceFiles = _diskProvider.GetFileInfos(sourcePath);
            var targetFiles = _diskProvider.GetFileInfos(targetPath);

            foreach (var targetFile in targetFiles.Where(v => !sourceFiles.Any(d => d.Name == v.Name)))
            {
                if (ShouldIgnore(targetFile))
                {
                    continue;
                }

                _diskProvider.DeleteFile(targetFile.FullName);
            }

            foreach (var sourceFile in sourceFiles)
            {
                if (ShouldIgnore(sourceFile))
                {
                    continue;
                }

                var targetFile = Path.Combine(targetPath, sourceFile.Name);

                if (_diskProvider.FilesEqual(sourceFile.FullName, targetFile))
                {
                    continue;
                }

                TransferFile(sourceFile.FullName, targetFile, TransferMode.Copy, true);
                filesCopied++;
            }

            return filesCopied;
        }

        public TransferMode TransferFile(string sourcePath, string targetPath, TransferMode mode, bool overwrite = false)
        {
            Ensure.That(sourcePath, () => sourcePath).IsValidPath(PathValidationType.CurrentOs);
            Ensure.That(targetPath, () => targetPath).IsValidPath(PathValidationType.CurrentOs);

            sourcePath = ResolveRealParentPath(sourcePath);
            targetPath = ResolveRealParentPath(targetPath);

            _logger.Debug("{0} [{1}] > [{2}]", mode, sourcePath, targetPath);

            var originalSize = _diskProvider.GetFileSize(sourcePath);

            if (sourcePath == targetPath)
            {
                throw new IOException(string.Format("Source and destination can't be the same {0}", sourcePath));
            }

            if (sourcePath.PathEquals(targetPath, StringComparison.InvariantCultureIgnoreCase))
            {
                if (mode.HasFlag(TransferMode.HardLink) || mode.HasFlag(TransferMode.Copy))
                {
                    throw new IOException(string.Format("Source and destination can't be the same {0}", sourcePath));
                }

                if (mode.HasFlag(TransferMode.Move))
                {
                    var tempPath = sourcePath + ".backup~";

                    _diskProvider.MoveFile(sourcePath, tempPath, true);
                    try
                    {
                        ClearTargetPath(sourcePath, targetPath, overwrite);

                        _diskProvider.MoveFile(tempPath, targetPath);

                        return TransferMode.Move;
                    }
                    catch
                    {
                        RollbackMove(sourcePath, tempPath);
                        throw;
                    }
                }

                return TransferMode.None;
            }

            if (sourcePath.GetParentPath() == targetPath.GetParentPath())
            {
                if (mode.HasFlag(TransferMode.Move))
                {
                    TryMoveFileVerified(sourcePath, targetPath, originalSize);
                    return TransferMode.Move;
                }
            }

            if (sourcePath.IsParentPath(targetPath))
            {
                throw new IOException(string.Format("Destination cannot be a child of the source [{0}] => [{1}]", sourcePath, targetPath));
            }

            ClearTargetPath(sourcePath, targetPath, overwrite);

            var hardlinkFallbackToCopy = false;

            if (mode.HasFlag(TransferMode.HardLink))
            {
                var createdHardlink = _diskProvider.TryCreateHardLink(sourcePath, targetPath);
                if (createdHardlink)
                {
                    _logger.Debug("Hardlink created [{0}] > [{1}]", sourcePath, targetPath);
                    return TransferMode.HardLink;
                }

                if (!mode.HasFlag(TransferMode.Copy))
                {
                    throw new IOException("Hardlinking from '" + sourcePath + "' to '" + targetPath + "' failed.");
                }

                hardlinkFallbackToCopy = true;
            }

            // Adjust the transfer mode depending on the filesystems
            var sourceMount = _diskProvider.GetMount(sourcePath);
            var targetMount = _diskProvider.GetMount(targetPath);

            var isSameMount = sourceMount != null && targetMount != null && sourceMount.RootDirectory == targetMount.RootDirectory;

            var sourceDriveFormat = sourceMount?.DriveFormat ?? string.Empty;
            var targetDriveFormat = targetMount?.DriveFormat ?? string.Empty;

            var isCifs = targetDriveFormat == "cifs";
            var isBtrfs = sourceDriveFormat == "btrfs" && targetDriveFormat == "btrfs";
            var isZfs = sourceDriveFormat == "zfs" && targetDriveFormat == "zfs";

            if (mode.HasFlag(TransferMode.Copy))
            {
                if (isBtrfs || isZfs)
                {
                    if (_diskProvider.TryCreateRefLink(sourcePath, targetPath))
                    {
                        if (hardlinkFallbackToCopy)
                        {
                            _logger.Info("Hardlink failed for [{0}] > [{1}], created reflink instead. Source mount: {2} ({3}); target mount: {4} ({5}).",
                                sourcePath,
                                targetPath,
                                DescribeMountRoot(sourceMount),
                                DescribeDriveFormat(sourceDriveFormat),
                                DescribeMountRoot(targetMount),
                                DescribeDriveFormat(targetDriveFormat));
                        }

                        return TransferMode.Copy;
                    }
                }

                if (hardlinkFallbackToCopy)
                {
                    _logger.Info("Hardlink failed for [{0}] > [{1}], copying file instead. Source mount: {2} ({3}); target mount: {4} ({5}).",
                        sourcePath,
                        targetPath,
                        DescribeMountRoot(sourceMount),
                        DescribeDriveFormat(sourceDriveFormat),
                        DescribeMountRoot(targetMount),
                        DescribeDriveFormat(targetDriveFormat));
                }

                TryCopyFileVerified(sourcePath, targetPath, originalSize);
                return TransferMode.Copy;
            }

            if (mode.HasFlag(TransferMode.Move))
            {
                if (isBtrfs || isZfs)
                {
                    if (isSameMount && _diskProvider.TryRenameFile(sourcePath, targetPath))
                    {
                        _logger.Trace("Renamed [{0}] to [{1}].", sourcePath, targetPath);
                        return TransferMode.Move;
                    }

                    if (_diskProvider.TryCreateRefLink(sourcePath, targetPath))
                    {
                        _logger.Trace("Reflink successful, deleting source [{0}].", sourcePath);
                        _diskProvider.DeleteFile(sourcePath);
                        return TransferMode.Move;
                    }
                }

                if (isCifs && !isSameMount)
                {
                    _logger.Trace("On cifs mount. Starting verified copy [{0}] to [{1}].", sourcePath, targetPath);
                    TryCopyFileVerified(sourcePath, targetPath, originalSize);
                    _logger.Trace("Copy successful, deleting source [{0}].", sourcePath);
                    _diskProvider.DeleteFile(sourcePath);
                    return TransferMode.Move;
                }

                TryMoveFileVerified(sourcePath, targetPath, originalSize);
                return TransferMode.Move;
            }

            return TransferMode.None;
        }

        private void ClearTargetPath(string sourcePath, string targetPath, bool overwrite)
        {
            // Loose read matches are not evidence that a destination may be overwritten.
            if (_diskProvider.FileExistsCanonical(targetPath))
            {
                if (overwrite)
                {
                    _diskProvider.DeleteFile(targetPath);
                }
                else
                {
                    throw new DestinationAlreadyExistsException($"Destination {targetPath} already exists.");
                }
            }
        }

        private void RollbackPartialMove(string sourcePath, string targetPath)
        {
            try
            {
                _logger.Debug("Rolling back incomplete file move [{0}] to [{1}].", sourcePath, targetPath);

                WaitForIO();

                if (_diskProvider.FileExists(sourcePath))
                {
                    _diskProvider.DeleteFile(targetPath);
                }
                else
                {
                    _logger.Error("Failed to properly rollback the file move [{0}] to [{1}], incomplete file may be left in target path.", sourcePath, targetPath);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to properly rollback the file move [{0}] to [{1}], incomplete file may be left in target path.", sourcePath, targetPath);
            }
        }

        private void RollbackMove(string sourcePath, string targetPath)
        {
            try
            {
                _logger.Debug("Rolling back file move [{0}] to [{1}].", sourcePath, targetPath);

                WaitForIO();

                _diskProvider.MoveFile(targetPath, sourcePath);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to properly rollback the file move [{0}] to [{1}], file may be left in target path.", sourcePath, targetPath);
            }
        }

        private void RollbackCopy(string sourcePath, string targetPath)
        {
            try
            {
                _logger.Debug("Rolling back file copy [{0}] to [{1}].", sourcePath, targetPath);

                WaitForIO();

                if (_diskProvider.FileExists(targetPath))
                {
                    _diskProvider.DeleteFile(targetPath);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to properly rollback the file copy [{0}] to [{1}], file may be left in target path.", sourcePath, targetPath);
            }
        }

        private void WaitForIO()
        {
            // This delay is intended to give the IO stack a bit of time to recover, this is especially required if remote NAS devices are involved.
            Thread.Sleep(3000);
        }

        private void TryCopyFileVerified(string sourcePath, string targetPath, long originalSize)
        {
            try
            {
                _diskProvider.CopyFile(sourcePath, targetPath);

                var targetSize = _diskProvider.GetFileSize(targetPath);
                if (targetSize != originalSize)
                {
                    throw new IOException(string.Format("File copy incomplete. [{0}] was {1} bytes long instead of {2} bytes.", targetPath, targetSize, originalSize));
                }
            }
            catch (Exception ex)
            {
                if (ex is not FileAlreadyExistsException)
                {
                    RollbackCopy(sourcePath, targetPath);
                }

                throw;
            }
        }

        private void TryMoveFileVerified(string sourcePath, string targetPath, long originalSize)
        {
            try
            {
                _diskProvider.MoveFile(sourcePath, targetPath);

                var targetSize = _diskProvider.GetFileSize(targetPath);
                if (targetSize != originalSize)
                {
                    throw new IOException(string.Format("File move incomplete, data loss may have occurred. [{0}] was {1} bytes long instead of the expected {2}.", targetPath, targetSize, originalSize));
                }
            }
            catch (Exception ex)
            {
                if (ex is not FileAlreadyExistsException)
                {
                    RollbackPartialMove(sourcePath, targetPath);
                }

                throw;
            }
        }

        private bool ShouldIgnore(IDirectoryInfo folder)
        {
            if (folder.Name.StartsWith(".nfs"))
            {
                _logger.Trace("Ignoring folder {0}", folder.FullName);
                return true;
            }

            return false;
        }

        private bool ShouldIgnore(IFileInfo file)
        {
            if (file.Name.StartsWith(".nfs") || file.Name == "debug.log" || file.Name.EndsWith(".socket"))
            {
                _logger.Trace("Ignoring file {0}", file.FullName);
                return true;
            }

            return false;
        }
    }
}
