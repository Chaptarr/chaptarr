using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Extras.Metadata.Files;

namespace NzbDrone.Core.Housekeeping.Housekeepers
{
    public class DeleteBadMediaCovers : IHousekeepingTask
    {
        private readonly IMetadataFileService _metaFileService;
        private readonly IAuthorService _authorService;
        private readonly IDiskProvider _diskProvider;
        private readonly IConfigService _configService;
        private readonly Logger _logger;

        public DeleteBadMediaCovers(IMetadataFileService metaFileService,
                                    IAuthorService authorService,
                                    IDiskProvider diskProvider,
                                    IConfigService configService,
                                    Logger logger)
        {
            _metaFileService = metaFileService;
            _authorService = authorService;
            _diskProvider = diskProvider;
            _configService = configService;
            _logger = logger;
        }

        public void Clean()
        {
            if (!_configService.CleanupMetadataImages)
            {
                return;
            }

            var authorPaths = _authorService.AllAuthorPaths()
                .GroupBy(author => author.Key);
            var imageExtensions = new List<string> { ".jpg", ".png", ".gif" };

            foreach (var author in authorPaths)
            {
                var images = _metaFileService.GetFilesByAuthor(author.Key)
                    .Where(c => c.LastUpdated > new DateTime(2014, 12, 27) && imageExtensions.Any(x => c.RelativePath.EndsWith(x, StringComparison.InvariantCultureIgnoreCase)));

                foreach (var image in images)
                {
                    try
                    {
                        var paths = author
                            .Select(entry => entry.Value)
                            .Where(basePath => image.RelativePath.IsNotNullOrWhiteSpace() && !Path.IsPathRooted(image.RelativePath))
                            .Select(basePath => Path.GetFullPath(Path.Combine(basePath, image.RelativePath)))
                            .Distinct(NzbDrone.Common.PathEqualityComparer.Instance)
                            .Where(_diskProvider.FileExistsCanonical)
                            .ToList();
                        if (paths.Count > 1)
                        {
                            _logger.Debug("Skipping ambiguous metadata image {0} for author {1}: found at {2} base paths", image.RelativePath, author.Key, paths.Count);
                        }
                        else if (paths.Count == 1 && !IsValid(paths[0]))
                        {
                            _logger.Debug("Deleting invalid image file " + paths[0]);
                            DeleteMetadata(image.Id, paths[0]);
                        }
                    }
                    catch (Exception e)
                    {
                        _logger.Error(e, "Couldn't validate image {0}", image.RelativePath);
                    }
                }
            }

            _configService.CleanupMetadataImages = false;
        }

        private void DeleteMetadata(int id, string path)
        {
            _metaFileService.Delete(id);
            _diskProvider.DeleteFile(path);
        }

        private bool IsValid(string path)
        {
            var buffer = new byte[10];

            using (var imageStream = _diskProvider.OpenReadStream(path))
            {
                if (imageStream.Length < buffer.Length)
                {
                    return false;
                }

                imageStream.ReadExactly(buffer, 0, buffer.Length);
            }

            var text = System.Text.Encoding.Default.GetString(buffer);

            return !text.ToLowerInvariant().Contains("html");
        }
    }
}
