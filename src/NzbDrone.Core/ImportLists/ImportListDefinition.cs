using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Books;
using NzbDrone.Core.ThingiProvider;

namespace NzbDrone.Core.ImportLists
{
    public class ImportListDefinition : ProviderDefinition
    {
        private ImportListMonitorType _shouldMonitor;
        private bool _legacyMonitorSpecified;

        public bool EnableAutomaticAdd { get; set; }
        public ImportListMonitorType ShouldMonitor
        {
            get => _shouldMonitor;
            set
            {
                _shouldMonitor = value;
                _legacyMonitorSpecified = true;
            }
        }
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
            var supplied = MonitoringOptions;
            var previous = existing?.GetMonitoringOptions();
            var merged = supplied?.MergeWith(previous) ?? previous?.MergeWith(null);
            var audioChanged = supplied != null &&
                ((supplied.WasSpecified(nameof(supplied.AudiobookMonitor)) && (existing == null || supplied.AudiobookMonitor != previous.AudiobookMonitor)) ||
                 (supplied.WasSpecified(nameof(supplied.AudiobookMonitorListedBooks)) && (existing == null || supplied.AudiobookMonitorListedBooks != previous.AudiobookMonitorListedBooks)));
            var ebookChanged = supplied != null &&
                ((supplied.WasSpecified(nameof(supplied.EbookMonitor)) && (existing == null || supplied.EbookMonitor != previous.EbookMonitor)) ||
                 (supplied.WasSpecified(nameof(supplied.EbookMonitorListedBooks)) && (existing == null || supplied.EbookMonitorListedBooks != previous.EbookMonitorListedBooks)));
            if (!_legacyMonitorSpecified && existing != null)
            {
                _shouldMonitor = existing.ShouldMonitor;
            }
            else if (_legacyMonitorSpecified && (existing == null || ShouldMonitor != existing.ShouldMonitor))
            {
                merged ??= new ImportListMonitoringOptions();
                var legacyPolicy = ShouldMonitor switch
                {
                    ImportListMonitorType.EntireAuthor => MonitorTypes.All,
                    ImportListMonitorType.SpecificBook => MonitorTypes.SpecificBook,
                    _ => MonitorTypes.None
                };
                // Legacy clients echo modern GET fields unchanged. A changed modern side takes precedence.
                if (!audioChanged)
                {
                    merged.AudiobookMonitor = legacyPolicy;
                    merged.AudiobookMonitorListedBooks = ShouldMonitor != ImportListMonitorType.None;
                }
                if (!ebookChanged)
                {
                    merged.EbookMonitor = legacyPolicy;
                    merged.EbookMonitorListedBooks = ShouldMonitor != ImportListMonitorType.None;
                }
            }
            MonitoringOptions = merged;
            if ((audioChanged || ebookChanged) && merged != null && Settings is DualMediaImportListSettings settings)
            {
                ImportListMonitorType? projection = null;
                foreach (var side in new[] { BookMediaType.Audiobook, BookMediaType.Ebook })
                {
                    if (!(side == BookMediaType.Audiobook ? settings.MonitorAudiobooks : settings.MonitorEbooks)) continue;
                    var candidate = merged.GetLegacyMonitorType(side, ShouldMonitor);
                    if (!candidate.HasValue || (projection.HasValue && projection != candidate)) return;
                    projection = candidate;
                }
                // Keep the legacy value truthful only when every enabled format has the same representable policy.
                if (projection.HasValue) _shouldMonitor = projection.Value;
            }
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
