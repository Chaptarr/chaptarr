using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Extras;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.Extras.Others
{
    public class ExistingOtherExtraImporter : ImportExistingExtraFilesBase<OtherExtraFile>
    {
        private readonly IExtraFileService<OtherExtraFile> _otherExtraFileService;
        private readonly IMediaFileService _mediaFileService;
        private readonly Logger _logger;

        public ExistingOtherExtraImporter(IExtraFileService<OtherExtraFile> otherExtraFileService,
                                          IMediaFileService mediaFileService,
                                          Logger logger)
            : base(otherExtraFileService)
        {
            _otherExtraFileService = otherExtraFileService;
            _mediaFileService = mediaFileService;
            _logger = logger;
        }

        public override int Order => 2;

        private static BookFile SelectBestBookFileForExtra(string extraFilePath, List<BookFile> bookFilesInFolder)
        {
            if (bookFilesInFolder == null || bookFilesInFolder.Count == 0)
            {
                return null;
            }

            if (bookFilesInFolder.Count == 1)
            {
                return bookFilesInFolder[0];
            }

            var extraBaseName = Path.GetFileNameWithoutExtension(extraFilePath);
            if (extraBaseName.IsNotNullOrWhiteSpace())
            {
                var matches = bookFilesInFolder
                    .Where(f => f?.Path.IsNotNullOrWhiteSpace() == true)
                    .Where(f => string.Equals(Path.GetFileNameWithoutExtension(f.Path), extraBaseName, System.StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (matches.Count == 1)
                {
                    return matches[0];
                }
            }

            // If multiple book files exist in the same folder for the same edition, pick deterministically.
            // Prefer the first part when parting is in use; otherwise prefer the largest file.
            var hasParts = bookFilesInFolder.Any(f => f?.Part > 0);

            return bookFilesInFolder
                .Where(f => f != null)
                .OrderBy(f => hasParts && f.Part > 0 ? f.Part : int.MaxValue)
                .ThenByDescending(f => f.Size)
                .ThenBy(f => f.Id)
                .FirstOrDefault();
        }

        public override IEnumerable<ExtraFile> ProcessFiles(Author author, List<string> filesOnDisk, List<string> importedFiles)
        {
            _logger.Debug("Looking for existing extra files in {0}", author.Path);

            var authorBookFilesByFolder = _mediaFileService.GetFilesByAuthor(author.Id)
                .Where(f => !string.IsNullOrWhiteSpace(f.Path))
                .GroupBy(f => Path.GetDirectoryName(f.Path))
                .Where(g => g.Key.IsNotNullOrWhiteSpace())
                .ToDictionary(g => g.Key, g => g.ToList(), PathEqualityComparer.Instance);

            var extraFiles = new List<OtherExtraFile>();
            var filterResult = FilterAndClean(author, filesOnDisk, importedFiles);

            foreach (var possibleExtraFile in filterResult.FilesOnDisk)
            {
                var extension = Path.GetExtension(possibleExtraFile);

                if (extension.IsNullOrWhiteSpace())
                {
                    _logger.Debug("No extension for file: {0}", possibleExtraFile);
                    continue;
                }

                // Other extras are associated to a BookFile when possible; resolve the best candidate based on the folder.
                var folder = Path.GetDirectoryName(possibleExtraFile);
                if (folder.IsNullOrWhiteSpace() ||
                    !authorBookFilesByFolder.TryGetValue(folder, out var bookFilesInFolder) ||
                    bookFilesInFolder.Count == 0)
                {
                    _logger.Debug("Cannot find related book file for: {0}", possibleExtraFile);
                    continue;
                }

                var distinctEditionsInFolder = bookFilesInFolder.DistinctBy(f => f.EditionId).ToList();
                if (distinctEditionsInFolder.Count != 1)
                {
                    _logger.Debug("Extra file folder has multiple Editions: {0}", possibleExtraFile);
                    continue;
                }

                var editionId = distinctEditionsInFolder[0].EditionId;
                var editionBookFiles = bookFilesInFolder.Where(f => f.EditionId == editionId).ToList();
                var bookFile = SelectBestBookFileForExtra(possibleExtraFile, editionBookFiles);

                if (bookFile.Id <= 0)
                {
                    _logger.Debug("Cannot find related book file ID for: {0}", possibleExtraFile);
                    continue;
                }

                var localBook = bookFile.Edition?.Book;
                if (localBook == null)
                {
                    _logger.Debug("Cannot find related book for: {0}", possibleExtraFile);
                    continue;
                }

                var extraFile = new OtherExtraFile
                {
                    AuthorId = author.Id,
                    BookId = localBook.Id,
                    BookFileId = bookFile.Id,
                    Extension = extension
                };

                var preferredBase = ExtraFilePathHelper.GetPreferredBasePath(author, bookFile);
                if (!ExtraFilePathHelper.TryGetRelativePath(author, possibleExtraFile, out var relativePath, out _, preferredBase))
                {
                    _logger.Debug("Cannot resolve relative path for: {0}", possibleExtraFile);
                    continue;
                }

                extraFile.RelativePath = relativePath;
                extraFiles.Add(extraFile);
            }

            _logger.Info("Found {0} existing other extra files", extraFiles.Count);
            _otherExtraFileService.Upsert(extraFiles);

            // Return files that were just imported along with files that were
            // previously imported so previously imported files aren't imported twice
            return extraFiles.Concat(filterResult.PreviouslyImported);
        }
    }
}
