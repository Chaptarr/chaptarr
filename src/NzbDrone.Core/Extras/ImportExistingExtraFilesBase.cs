using System.Collections.Generic;
using System.IO;
using System.Linq;
using NzbDrone.Common;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Extras.Files;

namespace NzbDrone.Core.Extras
{
    public abstract class ImportExistingExtraFilesBase<TExtraFile> : IImportExistingExtraFiles
        where TExtraFile : ExtraFile, new()
    {
        private readonly IExtraFileService<TExtraFile> _extraFileService;

        public ImportExistingExtraFilesBase(IExtraFileService<TExtraFile> extraFileService)
        {
            _extraFileService = extraFileService;
        }

        public abstract int Order { get; }
        public abstract IEnumerable<ExtraFile> ProcessFiles(Author author, List<string> filesOnDisk, List<string> importedFiles);

        public virtual ImportExistingExtraFileFilterResult<TExtraFile> FilterAndClean(Author author, List<string> filesOnDisk, List<string> importedFiles)
        {
            var authorFiles = _extraFileService.GetFilesByAuthor(author.Id);

            Clean(author, filesOnDisk, importedFiles, authorFiles);

            return Filter(author, filesOnDisk, importedFiles, authorFiles);
        }

        private ImportExistingExtraFileFilterResult<TExtraFile> Filter(Author author, List<string> filesOnDisk, List<string> importedFiles, List<TExtraFile> authorFiles)
        {
            var filesOnDiskSet = new HashSet<string>(filesOnDisk, PathEqualityComparer.Instance);
            var importedFilesSet = new HashSet<string>(importedFiles, PathEqualityComparer.Instance);

            var previouslyImported = new List<TExtraFile>();
            var previouslyImportedPaths = new HashSet<string>(PathEqualityComparer.Instance);

            foreach (var authorFile in authorFiles)
            {
                var matchedPath = GetCandidatePaths(author, authorFile.RelativePath)
                    .FirstOrDefault(filesOnDiskSet.Contains);

                if (matchedPath == null)
                {
                    continue;
                }

                if (previouslyImportedPaths.Add(matchedPath))
                {
                    previouslyImported.Add(authorFile);
                }
            }

            var filteredFiles = filesOnDisk
                .Where(p => !previouslyImportedPaths.Contains(p) && !importedFilesSet.Contains(p))
                .ToList();

            // Return files that are already imported so they aren't imported again by other importers.
            // Filter out files that were previously imported and as well as ones imported by other importers.
            return new ImportExistingExtraFileFilterResult<TExtraFile>(previouslyImported, filteredFiles);
        }

        private void Clean(Author author, List<string> filesOnDisk, List<string> importedFiles, List<TExtraFile> authorFiles)
        {
            var filesOnDiskSet = new HashSet<string>(filesOnDisk, PathEqualityComparer.Instance);
            var importedFilesSet = new HashSet<string>(importedFiles, PathEqualityComparer.Instance);

            var alreadyImportedFileIds = new List<int>();
            var deletedFileIds = new List<int>();

            foreach (var authorFile in authorFiles)
            {
                var candidates = GetCandidatePaths(author, authorFile.RelativePath).ToList();

                if (candidates.Any(importedFilesSet.Contains))
                {
                    alreadyImportedFileIds.Add(authorFile.Id);
                    continue;
                }

                if (!candidates.Any(filesOnDiskSet.Contains))
                {
                    deletedFileIds.Add(authorFile.Id);
                }
            }

            _extraFileService.DeleteMany(alreadyImportedFileIds);
            _extraFileService.DeleteMany(deletedFileIds);
        }

        private static IEnumerable<string> GetCandidatePaths(Author author, string relativePath)
        {
            if (author == null || relativePath.IsNullOrWhiteSpace())
            {
                yield break;
            }

            foreach (var basePath in ExtraFilePathHelper.GetAuthorBasePaths(author))
            {
                if (basePath.IsNullOrWhiteSpace())
                {
                    continue;
                }

                yield return Path.Combine(basePath, relativePath);
            }
        }
    }
}
