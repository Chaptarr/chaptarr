using System;
using NzbDrone.Core.Books;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Hardcover.Library;

namespace Chaptarr.Api.V1.ImportLists
{
    public class ImportListResource : ProviderResource<ImportListResource>
    {
        public bool EnableAutomaticAdd { get; set; }
        public ImportListMonitorType ShouldMonitor { get; set; }
        public bool ShouldMonitorExisting { get; set; }
        public bool ShouldSearch { get; set; }
        public string RootFolderPath { get; set; }
        public NewItemMonitorTypes MonitorNewItems { get; set; }
        public int QualityProfileId { get; set; }
        public int MetadataProfileId { get; set; }
        public ImportListType ListType { get; set; }
        public TimeSpan MinRefreshInterval { get; set; }

        public string HardcoverUsername { get; set; }
        public string HardcoverAvatarUrl { get; set; }
    }

    public class ImportListResourceMapper : ProviderResourceMapper<ImportListResource, ImportListDefinition>
    {
        public override ImportListResource ToResource(ImportListDefinition definition)
        {
            if (definition == null)
            {
                return null;
            }

            var resource = base.ToResource(definition);

            resource.EnableAutomaticAdd = definition.EnableAutomaticAdd;
            resource.ShouldMonitor = definition.ShouldMonitor;
            resource.ShouldMonitorExisting = definition.ShouldMonitorExisting;
            resource.ShouldSearch = definition.ShouldSearch;
            resource.RootFolderPath = definition.RootFolderPath;
            resource.MonitorNewItems = definition.MonitorNewItems;
            resource.QualityProfileId = definition.QualityProfileId;
            resource.MetadataProfileId = definition.MetadataProfileId;
            resource.ListType = definition.ListType;
            resource.MinRefreshInterval = definition.MinRefreshInterval;

            if (definition.Implementation == nameof(HardcoverLibraryImportList))
            {
                var settings = definition.Settings as HardcoverLibraryImportListSettings;
                if (settings != null)
                {
                    resource.HardcoverUsername = settings.CachedUsername;
                    resource.HardcoverAvatarUrl = settings.CachedAvatarUrl;
                }
            }

            return resource;
        }

        public override ImportListDefinition ToModel(ImportListResource resource)
        {
            if (resource == null)
            {
                return null;
            }

            var definition = base.ToModel(resource);

            definition.EnableAutomaticAdd = resource.EnableAutomaticAdd;
            definition.ShouldMonitor = resource.ShouldMonitor;
            definition.ShouldMonitorExisting = resource.ShouldMonitorExisting;
            definition.ShouldSearch = resource.ShouldSearch;
            definition.RootFolderPath = resource.RootFolderPath;
            definition.MonitorNewItems = resource.MonitorNewItems;
            definition.QualityProfileId = resource.QualityProfileId;
            definition.MetadataProfileId = resource.MetadataProfileId;
            definition.ListType = resource.ListType;
            definition.MinRefreshInterval = resource.MinRefreshInterval;

            return definition;
        }
    }
}
