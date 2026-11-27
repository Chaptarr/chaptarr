using System;
using System.Diagnostics;
using System.IO;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Core.MediaFiles
{
    public interface IFileMutationSafetyService
    {
        bool EnsureMutableFile(string path);
    }

    public sealed class FileSymlinkMutationException : IOException
    {
        public FileSymlinkMutationException(string path)
            : base($"Skipping tag writes through file symlink '{path}'.")
        {
        }
    }

    public class FileMutationSafetyService : IFileMutationSafetyService
    {
        private readonly IDiskProvider _diskProvider;
        private readonly IDiskTransferService _diskTransferService;
        private readonly IMediaFileAttributeService _mediaFileAttributeService;
        private readonly Logger _logger;

        public FileMutationSafetyService(IDiskProvider diskProvider, IDiskTransferService diskTransferService, IMediaFileAttributeService mediaFileAttributeService, Logger logger)
        {
            _diskProvider = diskProvider;
            _diskTransferService = diskTransferService;
            _mediaFileAttributeService = mediaFileAttributeService;
            _logger = logger;
        }

        internal static bool IsFileSymlink(string path) => new FileInfo(path).LinkTarget != null;

        public bool EnsureMutableFile(string path)
        {
            if (path.IsNullOrWhiteSpace() || !_diskProvider.FileExists(path))
            {
                return false;
            }

            // A write through a file symlink modifies its target, even when the target
            // has one hardlink. A copy can also preserve the symlink.
            if (IsFileSymlink(path))
            {
                throw new FileSymlinkMutationException(path);
            }

            var linkCount = _diskProvider.GetFileLinkCount(path);
            if (!linkCount.HasValue)
            {
                throw new IOException($"Cannot safely mutate '{path}' because its hardlink count could not be determined.");
            }

            if (linkCount.Value <= 1)
            {
                return false;
            }

            var temporaryPath = Path.Combine(Path.GetDirectoryName(path), $".{Guid.NewGuid():N}.chaptarr-tmp");
            var timer = Stopwatch.StartNew();
            var size = _diskProvider.GetFileSize(path);
            try
            {
                _diskTransferService.TransferFile(path, temporaryPath, TransferMode.Copy);

                // The temporary copy is in the same directory. Replacing the directory entry keeps
                // the original hardlinked inode intact for the download client while Chaptarr gets
                // an independent inode to mutate.
                File.Move(temporaryPath, path, overwrite: true);
                _mediaFileAttributeService.SetFilePermissions(path);
                _logger.Info("Separated hardlinked file {0}: {1} bytes in {2} ms", path, size, timer.ElapsedMilliseconds);
                return true;
            }
            catch
            {
                if (_diskProvider.FileExists(temporaryPath))
                {
                    _diskProvider.DeleteFile(temporaryPath);
                }

                throw;
            }
        }
    }
}
