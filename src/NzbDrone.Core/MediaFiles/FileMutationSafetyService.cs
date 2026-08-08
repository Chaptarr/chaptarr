using System;
using System.IO;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Core.MediaFiles
{
    public interface IFileMutationSafetyService
    {
        void PrepareImportDestination(BookFile bookFile, TransferMode actualTransferMode);
        void EnsureMutableFile(string path);
    }

    public class FileMutationSafetyService : IFileMutationSafetyService
    {
        private readonly IDiskProvider _diskProvider;
        private readonly IConfigService _configService;
        private readonly Logger _logger;

        public FileMutationSafetyService(IDiskProvider diskProvider, IConfigService configService, Logger logger)
        {
            _diskProvider = diskProvider;
            _configService = configService;
            _logger = logger;
        }

        public void PrepareImportDestination(BookFile bookFile, TransferMode actualTransferMode)
        {
            if (actualTransferMode != TransferMode.HardLink || !ImportCanMutate(bookFile))
            {
                return;
            }

            BreakHardLink(bookFile.Path, knownHardLink: true);
        }

        public void EnsureMutableFile(string path)
        {
            BreakHardLink(path, knownHardLink: false);
        }

        private bool ImportCanMutate(BookFile bookFile)
        {
            if (_configService.FileDate != FileDateType.None ||
                OsInfo.IsWindows ||
                _configService.SetPermissionsLinux)
            {
                return true;
            }

            var extension = Path.GetExtension(bookFile?.Path);
            if (MediaFileExtensions.CanWriteAudioTags(extension))
            {
                return _configService.WriteAudioTags != WriteAudioTagsType.No;
            }

            // Ebook writes are delegated to Calibre. Calibre-backed files have a durable CalibreId;
            // non-Calibre ebook files are not mutated by MetadataTagService.
            return bookFile?.CalibreId > 0;
        }

        private void BreakHardLink(string path, bool knownHardLink)
        {
            if (path.IsNullOrWhiteSpace() || !_diskProvider.FileExists(path))
            {
                return;
            }

            if (!knownHardLink)
            {
                var linkCount = _diskProvider.GetFileLinkCount(path);
                if (!linkCount.HasValue)
                {
                    throw new IOException($"Cannot safely mutate '{path}' because its hardlink count could not be determined.");
                }

                if (linkCount.Value <= 1)
                {
                    return;
                }
            }

            var temporaryPath = $"{path}.chaptarr-mutable~{Guid.NewGuid():N}";
            try
            {
                _logger.Info("Breaking destination hardlink before mutating file metadata: {0}", path);
                _diskProvider.CopyFile(path, temporaryPath, overwrite: false);

                // The temporary copy is in the same directory. Replacing the directory entry keeps
                // the original hardlinked inode intact for the download client while Chaptarr gets
                // an independent inode to mutate.
                File.Move(temporaryPath, path, overwrite: true);
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
