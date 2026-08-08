using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Instrumentation;

namespace NzbDrone.Core.MediaFiles
{
    public interface IMediaFileTableCleanupService
    {
        void Clean(string folder, List<string> filesOnDisk);
        void Clean(string folder, List<string> filesOnDisk, string mediaType);
    }

    public class MediaFileTableCleanupService : IMediaFileTableCleanupService
    {
        private readonly IMediaFileService _mediaFileService;
        private readonly IDiskProvider _diskProvider;
        private readonly Logger _logger;

        public MediaFileTableCleanupService(IMediaFileService mediaFileService,
                                            IDiskProvider diskProvider,
                                            Logger logger)
        {
            _mediaFileService = mediaFileService;
            _diskProvider = diskProvider;
            _logger = logger;
        }

        public void Clean(string folder, List<string> filesOnDisk)
        {
            Clean(folder, filesOnDisk, null);
        }

        public void Clean(string folder, List<string> filesOnDisk, string mediaType)
        {
            // Cleanup only needs id/path/media-type for the scan comparison. Avoid loading persisted
            // tag/media-info JSON for every tracked file in a large root folder.
            LogMemorySnapshot("[CLEANUP] before db file stat load folder='{0}' mediaType='{1}' diskFiles={2}",
                folder,
                mediaType ?? "<any>",
                filesOnDisk?.Count ?? 0);

            var dbFiles = _mediaFileService is MediaFileService concreteMediaFileService
                ? concreteMediaFileService.GetFileStatsWithBasePath(folder, mediaType)
                : _mediaFileService.GetFilesWithBasePath(folder, mediaType);

            LogMemorySnapshot("[CLEANUP] after db file stat load folder='{0}' mediaType='{1}' dbFiles={2}",
                folder,
                mediaType ?? "<any>",
                dbFiles?.Count ?? 0);

            // Get files that appear missing from current scan
            var notSeen = dbFiles.ExceptBy(x => x.Path, filesOnDisk, x => x, PathEqualityComparer.Instance).ToList();
            LogMemorySnapshot("[CLEANUP] after not-seen diff folder='{0}' mediaType='{1}' notSeen={2}",
                folder,
                mediaType ?? "<any>",
                notSeen.Count);

            // Safety guard: only delete files that truly don't exist on disk
            var trulyMissing = new List<BookFile>();
            var stillPresent = new List<BookFile>();

            foreach (var file in notSeen)
            {
                // Path safety check: ensure file is under the expected folder
                var normalizedFilePath = Path.GetFullPath(file.Path);
                var normalizedFolder = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                
                if (!normalizedFilePath.StartsWith(normalizedFolder, System.StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Warn("Skipping file outside folder scope: {0} (not under {1})", file.Path, folder);
                    continue;
                }

                // MediaType safety check: skip files with wrong MediaType
                if (!string.IsNullOrEmpty(mediaType) && !string.IsNullOrEmpty(file.MediaType) && 
                    file.MediaType != mediaType)
                {
                    _logger.Warn("Skipping file with wrong MediaType: {0} (expected {1}, got {2})", 
                                file.Path, mediaType, file.MediaType);
                    continue;
                }

                // A tracked row is only still present when its path resolves to the same file the
                // write layer could safely mutate. Loose Unicode recovery may find a renamed file,
                // but must not keep the stale row alive forever.
                if (_diskProvider.FileExistsCanonical(file.Path))
                {
                    _logger.Debug("File still exists on disk, skipping deletion: {0}", file.Path);
                    stillPresent.Add(file);
                }
                else
                {
                    trulyMissing.Add(file);
                }
            }

            LogMemorySnapshot("[CLEANUP] after missing verification folder='{0}' mediaType='{1}' missing={2} stillPresent={3}",
                folder,
                mediaType ?? "<any>",
                trulyMissing.Count,
                stillPresent.Count);

            // Log what we're skipping and deleting
            if (stillPresent.Any())
            {
                _logger.Debug("Skipping deletion for {0} files still present on disk:\n{1}",
                    stillPresent.Count, string.Join("\n", stillPresent.Select(x => x.Path)));
            }

            if (trulyMissing.Any())
            {
                _logger.Debug("The following {0} files no longer exist on disk, removing from db:\n{1}",
                              trulyMissing.Count, string.Join("\n", trulyMissing.Select(x => x.Path)));

                LogMemorySnapshot("[CLEANUP] before deleting missing files folder='{0}' mediaType='{1}' missing={2}",
                    folder,
                    mediaType ?? "<any>",
                    trulyMissing.Count);

                _mediaFileService.DeleteMany(trulyMissing, DeleteMediaFileReason.MissingFromDisk);

                LogMemorySnapshot("[CLEANUP] after deleting missing files folder='{0}' mediaType='{1}' missing={2}",
                    folder,
                    mediaType ?? "<any>",
                    trulyMissing.Count);
            }
        }

        private void LogMemorySnapshot(string message, params object[] args)
        {
            if (!_logger.IsDebugEnabled)
            {
                return;
            }

            try
            {
                var formatted = args == null || args.Length == 0 ? message : string.Format(message, args);
                _logger.Debug("[MEMORY] {0}: {1}", formatted, MemorySnapshot.CaptureDetailed());
            }
            catch
            {
                // Diagnostics must never affect cleanup.
            }
        }
    }
}
