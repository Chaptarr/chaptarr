using System;
using System.Collections.Generic;
using System.IO;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.MediaFiles.BookImport
{
    internal static class StagingQueueFileDispositionHelper
    {
        internal static bool IsFileAllowedForRootFolderType(string filePath, RootFolder rootFolder)
        {
            if (rootFolder == null || rootFolder.FolderType == FolderType.Mixed)
            {
                return true;
            }

            var ext = Path.GetExtension(filePath)?.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(ext))
            {
                return true;
            }

            var isAudio = MediaFileExtensions.AudioExtensions.Contains(ext);
            var isText = MediaFileExtensions.TextExtensions.Contains(ext);

            if (!isAudio && !isText)
            {
                return true;
            }

            return rootFolder.FolderType switch
            {
                FolderType.Audiobook => isAudio,
                FolderType.Ebook => isText,
                _ => true
            };
        }

        internal static (ImportOutcome Outcome, string Reason) EnsureVisibleOrIgnored(
            string filePath,
            Dictionary<string, List<string>> tags,
            int? durationSeconds,
            IMediaFileService mediaFileService,
            IDiskProvider diskProvider,
            Func<string, RootFolder> rootFolderResolver,
            Logger logger,
            string logPrefix)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return (ImportOutcome.Ignored, "EMPTY_PATH");
            }

            var existing = mediaFileService.GetFileWithPath(filePath);
            if (existing != null)
            {
                return existing.EditionId == 0
                    ? (ImportOutcome.Unmapped, "ALREADY_UNMAPPED")
                    : (ImportOutcome.Ignored, "ALREADY_TRACKED");
            }

            var fileInfo = diskProvider.GetFileInfo(filePath);
            if (!fileInfo.Exists)
            {
                return (ImportOutcome.Ignored, "FILE_MISSING");
            }

            var rootFolder = rootFolderResolver?.Invoke(filePath);
            if (rootFolder == null)
            {
                return (ImportOutcome.Ignored, "NO_ROOT_FOLDER");
            }

            if (!IsFileAllowedForRootFolderType(filePath, rootFolder))
            {
                var reason = $"ROOT_FOLDER_TYPE_{rootFolder.FolderType}";
                var rootMismatchVisible = BookImportUnmappedFileHelper.TryEnsureUnmapped(
                    mediaFileService,
                    diskProvider,
                    filePath,
                    logger,
                    logPrefix,
                    tags,
                    durationSeconds);

                return rootMismatchVisible
                    ? (ImportOutcome.Unmapped, reason)
                    : (ImportOutcome.Ignored, "UNMAPPED_CREATE_FAILED");
            }

            var visible = BookImportUnmappedFileHelper.TryEnsureUnmapped(
                mediaFileService,
                diskProvider,
                filePath,
                logger,
                logPrefix,
                tags,
                durationSeconds);

            if (visible)
            {
                return (ImportOutcome.Unmapped, "UNMAPPED");
            }

            existing = mediaFileService.GetFileWithPath(filePath);
            if (existing != null)
            {
                return existing.EditionId == 0
                    ? (ImportOutcome.Unmapped, "ALREADY_UNMAPPED")
                    : (ImportOutcome.Ignored, "ALREADY_TRACKED");
            }

            return (ImportOutcome.Ignored, "UNMAPPED_CREATE_FAILED");
        }
    }
}
