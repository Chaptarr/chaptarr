using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Localization;
using NzbDrone.Core.MediaFiles.Events;

namespace NzbDrone.Core.HealthCheck.Checks
{
    [CheckOn(typeof(AuthorDeletedEvent))]
    [CheckOn(typeof(AuthorMovedEvent))]
    [CheckOn(typeof(BookImportedEvent), CheckOnCondition.FailedOnly)]
    [CheckOn(typeof(TrackImportedEvent), CheckOnCondition.FailedOnly)]
    [CheckOn(typeof(TrackImportFailedEvent), CheckOnCondition.SuccessfulOnly)]
    public class ImportListRootFolderCheck : HealthCheckBase
    {
        private readonly IImportListFactory _importListFactory;
        private readonly IDiskProvider _diskProvider;

        public ImportListRootFolderCheck(IImportListFactory importListFactory, IDiskProvider diskProvider, ILocalizationService localizationService)
            : base(localizationService)
        {
            _importListFactory = importListFactory;
            _diskProvider = diskProvider;
        }

        public override HealthCheck Check()
        {
            // Only enabled (automatic add) import lists can create items that need to land in a root folder.
            var importLists = _importListFactory.All().Where(l => l.Enable).ToList();
            var missingRootFolders = new Dictionary<string, List<ImportListDefinition>>();

            foreach (var importList in importLists)
            {
                var rootFolderPaths = GetRootFolderPaths(importList).ToList();

                if (!rootFolderPaths.Any())
                {
                    AddMissingRootFolder(missingRootFolders, string.Empty, importList);
                    continue;
                }

                foreach (var rootFolderPath in rootFolderPaths)
                {
                    if (SafeFolderExists(rootFolderPath))
                    {
                        continue;
                    }

                    AddMissingRootFolder(missingRootFolders, rootFolderPath, importList);
                }
            }

            if (missingRootFolders.Any())
            {
                if (missingRootFolders.Count == 1)
                {
                    var missingRootFolder = missingRootFolders.First();
                    return new HealthCheck(GetType(), HealthCheckResult.Error, string.Format(_localizationService.GetLocalizedString("ImportListMissingRoot"), FormatRootFolder(missingRootFolder.Key, missingRootFolder.Value)), "#import-list-missing-root-folder");
                }

                var message = string.Format(_localizationService.GetLocalizedString("ImportListMultipleMissingRoots"), string.Join(" | ", missingRootFolders.Select(m => FormatRootFolder(m.Key, m.Value))));
                return new HealthCheck(GetType(), HealthCheckResult.Error, message, "#import-list-missing-root-folder");
            }

            return new HealthCheck(GetType());
        }

        private static IEnumerable<string> GetRootFolderPaths(ImportListDefinition importList)
        {
            if (importList == null)
            {
                return Enumerable.Empty<string>();
            }

            var paths = new List<string>();

            AddPath(paths, importList.RootFolderPath);

            if (importList.Settings is IMediaTypeRootFolderSettings mediaSettings)
            {
                // Newer import lists store per-media root folders in their settings.
                AddPath(paths, mediaSettings.AudiobookRootFolderPath);
                AddPath(paths, mediaSettings.EbookRootFolderPath);
            }

            var comparer = OsInfo.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

            return paths
                .Select(p => p?.Trim())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(comparer);
        }

        private static void AddPath(List<string> paths, string value)
        {
            if (paths == null || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            paths.Add(value);
        }

        private bool SafeFolderExists(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                return _diskProvider.FolderExists(path);
            }
            catch
            {
                // Invalid path or other disk issues should not crash the health check.
                return false;
            }
        }

        private static void AddMissingRootFolder(Dictionary<string, List<ImportListDefinition>> missingRootFolders, string rootFolderPath, ImportListDefinition importList)
        {
            if (!missingRootFolders.TryGetValue(rootFolderPath, out var importListsForRoot))
            {
                importListsForRoot = new List<ImportListDefinition>();
                missingRootFolders[rootFolderPath] = importListsForRoot;
            }

            importListsForRoot.Add(importList);
        }

        private string FormatRootFolder(string rootFolderPath, List<ImportListDefinition> importLists)
        {
            var displayRootFolderPath = string.IsNullOrWhiteSpace(rootFolderPath) ? _localizationService.GetLocalizedString("None") : rootFolderPath;
            return $"{displayRootFolderPath} ({string.Join(", ", importLists.Select(l => l.Name))})";
        }
    }
}
