using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.Extras.Metadata.Files
{
    public interface ICleanMetadataService
    {
        void Clean(Author author);
    }

    public class CleanExtraFileService : ICleanMetadataService
    {
        private readonly IMetadataFileService _metadataFileService;
        private readonly IDiskProvider _diskProvider;
        private readonly Logger _logger;
        private readonly IMediaFileService _mediaFileService;

        public CleanExtraFileService(IMetadataFileService metadataFileService,
                                    IDiskProvider diskProvider,
                                    Logger logger,
                                    IMediaFileService mediaFileService = null)
        {
            _metadataFileService = metadataFileService;
            _diskProvider = diskProvider;
            _logger = logger;
            _mediaFileService = mediaFileService;
        }

        public void Clean(Author author)
        {
            _logger.Debug("Cleaning missing metadata files for author: {0}", author.Name);

            var metadataFiles = _metadataFileService.GetFilesByAuthor(author.Id);
            if (metadataFiles.Count == 0)
            {
                return;
            }

            // Sidecar files are written under ExtraFilePathHelper.GetPreferredBasePath(author, bookFile), so
            // for rows that point at a book file, probe that base path first. Probing a fixed audiobook ->
            // ebook -> legacy order made every ebook sidecar miss under the audiobook folder first, and a
            // miss is the expensive case (FileExists falls back to listing directories).
            var bookFilesById = _mediaFileService == null
                ? new Dictionary<int, BookFile>()
                : (_mediaFileService.GetFilesByAuthor(author.Id) ?? new List<BookFile>())
                    .GroupBy(file => file.Id)
                    .ToDictionary(group => group.Key, group => group.First());

            var basePathsByPreferred = new Dictionary<string, List<string>>();
            var missingIds = new List<int>();

            foreach (var metadataFile in metadataFiles)
            {
                if (metadataFile.RelativePath.IsNullOrWhiteSpace())
                {
                    continue;
                }

                string preferredBasePath = null;
                if (metadataFile.BookFileId.HasValue && bookFilesById.TryGetValue(metadataFile.BookFileId.Value, out var bookFile))
                {
                    preferredBasePath = ExtraFilePathHelper.GetPreferredBasePath(author, bookFile);
                }

                var cacheKey = preferredBasePath ?? string.Empty;
                if (!basePathsByPreferred.TryGetValue(cacheKey, out var basePaths))
                {
                    basePaths = ExtraFilePathHelper.GetAuthorBasePaths(author, preferredBasePath);
                    basePathsByPreferred[cacheKey] = basePaths;
                }

                var exists = basePaths
                    .Select(p => Path.Combine(p, metadataFile.RelativePath))
                    .Any(_diskProvider.FileExists);

                if (!exists)
                {
                    _logger.Debug("Deleting metadata file from database: {0}", metadataFile.RelativePath);
                    missingIds.Add(metadataFile.Id);
                }
            }

            // One statement for all missing rows instead of one DELETE per row.
            if (missingIds.Count > 0)
            {
                _metadataFileService.DeleteMany(missingIds);
            }
        }
    }
}
