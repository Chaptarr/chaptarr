using System.Linq;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.RootFolders
{
    public interface IRootFolderSettingsResolver
    {
        ResolvedRootFolderSettings ResolveSettings(int rootFolderId, BookMediaType mediaType);
        ResolvedRootFolderSettings ResolveSettings(RootFolder rootFolder, BookMediaType mediaType);
    }

    public class RootFolderSettingsResolver : IRootFolderSettingsResolver
    {
        private readonly IRootFolderService _rootFolderService;

        public RootFolderSettingsResolver(IRootFolderService rootFolderService)
        {
            _rootFolderService = rootFolderService;
        }

        public ResolvedRootFolderSettings ResolveSettings(int rootFolderId, BookMediaType mediaType)
        {
            var rootFolder = _rootFolderService.Get(rootFolderId);
            return ResolveSettings(rootFolder, mediaType);
        }

        public ResolvedRootFolderSettings ResolveSettings(RootFolder rootFolder, BookMediaType mediaType)
        {
            if (rootFolder == null)
            {
                return new ResolvedRootFolderSettings
                {
                    IsConfigured = false,
                    Source = "Unconfigured"
                };
            }

            MediaTypeSettings mediaSettings = null;
            
            // Try to get media-specific settings first
            if (mediaType == BookMediaType.Audiobook)
            {
                mediaSettings = rootFolder.GetAudiobookSettings();
            }
            else if (mediaType == BookMediaType.Ebook)
            {
                mediaSettings = rootFolder.GetEbookSettings();
            }

            // If we have media-specific settings, use them
            if (mediaSettings != null)
            {
                return new ResolvedRootFolderSettings
                {
                    QualityProfileId = mediaSettings.QualityProfileId,
                    MetadataProfileId = mediaSettings.MetadataProfileId,
                    Monitored = mediaSettings.Monitored,
                    MonitorExistingMode = mediaSettings.MonitorExistingMode,
                    MonitorNewItems = mediaSettings.MonitorNewItems,
                    Tags = mediaSettings.Tags ?? new System.Collections.Generic.List<int>(),
                    IsConfigured = true,
                    Source = "MediaSpecific"
                };
            }

            // No legacy fallback - fail fast if settings not configured
            // No configuration found
            return new ResolvedRootFolderSettings
            {
                IsConfigured = false,
                Source = "Unconfigured"
            };
        }

        public static MonitorTypes? ResolveInitialMonitorMode(MonitorTypes? monitorExistingMode)
        {
            return monitorExistingMode;
        }
    }
}
