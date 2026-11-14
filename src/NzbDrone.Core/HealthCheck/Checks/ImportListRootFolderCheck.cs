using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Datastore.Events;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Localization;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.ThingiProvider.Events;

namespace NzbDrone.Core.HealthCheck.Checks
{
    [CheckOn(typeof(ProviderAddedEvent<IImportList>))]
    [CheckOn(typeof(ProviderUpdatedEvent<IImportList>))]
    [CheckOn(typeof(ProviderDeletedEvent<IImportList>))]
    [CheckOn(typeof(ModelEvent<RootFolder>))]
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
            var importLists = _importListFactory.All();
            var missingRootFolders = new Dictionary<string, List<ImportListDefinition>>();
            var comparer = OsInfo.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

            foreach (var importList in importLists)
            {
                foreach (var rootFolderPath in importList.GetCreationRootFolderPaths().Distinct(comparer))
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

            var inactiveLists = importLists.Where(list => list.Enable && list.Settings is DualMediaImportListSettings
                { MonitorAudiobooks: false, MonitorEbooks: false }).Select(list => list.Name).ToList();
            if (inactiveLists.Count > 0)
            {
                return new HealthCheck(GetType(), HealthCheckResult.Warning,
                    string.Format(_localizationService.GetLocalizedString("ImportListNoFormatEnabled"), string.Join(", ", inactiveLists)));
            }

            return new HealthCheck(GetType());
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
