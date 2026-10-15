using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Books;
using NzbDrone.Core.ThingiProvider;

namespace NzbDrone.Core.ImportLists
{
    public class ImportListDefinition : ProviderDefinition
    {
        public bool EnableAutomaticAdd { get; set; }
        public ImportListMonitorType ShouldMonitor { get; set; }
        public bool ShouldMonitorExisting { get; set; }
        public bool ShouldSearch { get; set; }
        public NewItemMonitorTypes MonitorNewItems { get; set; }
        public int QualityProfileId { get; set; }
        public int MetadataProfileId { get; set; }
        public string RootFolderPath { get; set; }
        public ImportListMonitoringOptions MonitoringOptions { get; set; }

        public ImportListMonitoringOptions GetMonitoringOptions()
        {
            return MonitoringOptions ?? new ImportListMonitoringOptions();
        }

        public void PreserveMonitoringOptions(ImportListDefinition existing)
        {
            MonitoringOptions ??= existing?.MonitoringOptions;
        }

        public IEnumerable<string> GetCreationRootFolderPaths()
        {
            if (!Enable || !GetMonitoringOptions().AddMissingAuthors || Settings is not DualMediaImportListSettings mediaSettings)
            {
                yield break;
            }

            if (mediaSettings.MonitorAudiobooks)
            {
                yield return mediaSettings.AudiobookRootFolderPath?.Trim() ?? string.Empty;
            }

            if (mediaSettings.MonitorEbooks)
            {
                yield return mediaSettings.EbookRootFolderPath?.Trim() ?? string.Empty;
            }
        }

        public IEnumerable<int> GetReferencedTags()
        {
            var mediaSettings = Settings as DualMediaImportListSettings;
            return (Tags ?? Enumerable.Empty<int>())
                .Concat(mediaSettings?.AudiobookTags ?? Enumerable.Empty<int>())
                .Concat(mediaSettings?.EbookTags ?? Enumerable.Empty<int>());
        }

        public override bool Enable => EnableAutomaticAdd;

        public ImportListStatus Status { get; set; }
        public ImportListType ListType { get; set; }
        public TimeSpan MinRefreshInterval { get; set; }
    }

    public enum ImportListMonitorType
    {
        None,
        SpecificBook,
        EntireAuthor
    }
}
