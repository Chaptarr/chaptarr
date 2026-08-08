using System;
using System.Collections.Generic;
using System.IO;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles.BookImport
{
    internal static class BookImportUnmappedFileHelper
    {
        internal static void MarkUnmapped(
            IMediaFileService mediaFileService,
            IDiskProvider diskProvider,
            string filePath,
            Logger logger,
            string logPrefix)
        {
            TryEnsureUnmapped(mediaFileService, diskProvider, filePath, logger, logPrefix);
        }

        internal static bool TryEnsureUnmapped(
            IMediaFileService mediaFileService,
            IDiskProvider diskProvider,
            string filePath,
            Logger logger,
            string logPrefix,
            Dictionary<string, List<string>> tags = null,
            int? durationSeconds = null)
        {
            try
            {
                var existing = mediaFileService.GetFileWithPath(filePath);
                if (existing != null)
                {
                    if (existing.EditionId != 0)
                    {
                        logger.Debug("{0} Not marking unmapped; file already tracked (EditionId={1}): {2}",
                            logPrefix,
                            existing.EditionId,
                            filePath);

                        return false;
                    }

                    return true;
                }

                var fi = diskProvider.GetFileInfo(filePath);
                if (!fi.Exists)
                {
                    return false;
                }

                var ext = Path.GetExtension(filePath);
                var quality = MediaFileExtensions.GetQualityForExtension(ext);
                var qualityModel = new NzbDrone.Core.Qualities.QualityModel { Quality = quality };

                var bookFile = new BookFile
                {
                    Path = filePath,
                    Size = fi.Length,
                    Modified = fi.LastWriteTime,
                    DateAdded = DateTime.UtcNow,
                    EditionId = 0,
                    Quality = qualityModel,
                    MediaInfo = new MediaInfoModel(),
                    MediaType = BookFile.DetermineMediaType(qualityModel),
                    AllTags = tags,
                    DurationSeconds = durationSeconds
                };

                mediaFileService.Add(bookFile);
                return true;
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "{0} Failed to mark unmapped: {1}", logPrefix, filePath);
                return false;
            }
        }
    }
}
