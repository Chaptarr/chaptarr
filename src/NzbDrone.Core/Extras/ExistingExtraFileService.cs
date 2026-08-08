using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Extras
{
    public class ExistingExtraFileService : IHandle<AuthorScannedEvent>
    {
        private readonly IDiskProvider _diskProvider;
        private readonly IDiskScanService _diskScanService;
        private readonly List<IImportExistingExtraFiles> _existingExtraFileImporters;
        private readonly Logger _logger;

        public ExistingExtraFileService(IDiskProvider diskProvider,
                                        IDiskScanService diskScanService,
                                        IEnumerable<IImportExistingExtraFiles> existingExtraFileImporters,
                                        Logger logger)
        {
            _diskProvider = diskProvider;
            _diskScanService = diskScanService;
            _existingExtraFileImporters = existingExtraFileImporters.OrderBy(e => e.Order).ToList();
            _logger = logger;
        }

        public void Handle(AuthorScannedEvent message)
        {
            var author = message.Author;
            var extraFiles = new List<ExtraFile>();

            var authorBasePaths = ExtraFilePathHelper.GetAuthorBasePaths(author)
                .Where(p => p.IsNotNullOrWhiteSpace())
                .Distinct(PathEqualityComparer.Instance)
                .ToList();

            if (authorBasePaths.Empty() || authorBasePaths.All(p => !_diskProvider.FolderExists(p)))
            {
                return;
            }

            _logger.Debug("Looking for existing extra files in {0}", authorBasePaths.Join(", "));

            var possibleExtraFiles = new List<string>();

            foreach (var basePath in authorBasePaths)
            {
                if (!_diskProvider.FolderExists(basePath))
                {
                    continue;
                }

                var filesOnDisk = _diskScanService.GetNonBookFiles(basePath);
                possibleExtraFiles.AddRange(_diskScanService.FilterPaths(basePath, filesOnDisk));
            }

            possibleExtraFiles = possibleExtraFiles.Distinct(PathEqualityComparer.Instance).ToList();

            var filteredFiles = possibleExtraFiles;
            var importedFiles = new List<string>();
            var possibleExtraFilesSet = new HashSet<string>(possibleExtraFiles, PathEqualityComparer.Instance);

            foreach (var existingExtraFileImporter in _existingExtraFileImporters)
            {
                var imported = existingExtraFileImporter.ProcessFiles(author, filteredFiles, importedFiles);

                foreach (var file in imported)
                {
                    if (file?.RelativePath.IsNullOrWhiteSpace() != false)
                    {
                        continue;
                    }

                    var fullPath = ExtraFilePathHelper.GetAuthorBasePaths(author)
                        .Select(p => Path.Combine(p, file.RelativePath))
                        .FirstOrDefault(possibleExtraFilesSet.Contains)
                        ?? ExtraFilePathHelper.ResolveFullPath(author, file.RelativePath, _diskProvider.FileExists);

                    if (fullPath.IsNotNullOrWhiteSpace())
                    {
                        importedFiles.Add(fullPath);
                    }
                }
            }

            _logger.Info("Found {0} extra files", extraFiles.Count);
        }
    }
}
