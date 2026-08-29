using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore.Events;
using NzbDrone.Core.Localization;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.HealthCheck.Checks
{
    [CheckOn(typeof(ModelEvent<RootFolder>))]
    public class RootFolderMediaSettingsCheck : HealthCheckBase
    {
        private readonly IRootFolderService _rootFolderService;
        private readonly IRootFolderSettingsResolver _rootFolderSettingsResolver;

        public RootFolderMediaSettingsCheck(
            IRootFolderService rootFolderService,
            IRootFolderSettingsResolver rootFolderSettingsResolver,
            ILocalizationService localizationService)
            : base(localizationService)
        {
            _rootFolderService = rootFolderService;
            _rootFolderSettingsResolver = rootFolderSettingsResolver;
        }

        public override HealthCheck Check()
        {
            var incomplete = new List<string>();

            foreach (var rootFolder in _rootFolderService.All())
            {
                if (rootFolder.FolderType == FolderType.Mixed &&
                    string.IsNullOrWhiteSpace(rootFolder.AudiobookSettings) &&
                    string.IsNullOrWhiteSpace(rootFolder.EbookSettings))
                {
                    incomplete.Add($"{rootFolder.Name} ({_localizationService.GetLocalizedString("NoConfiguredMediaType")})");
                    continue;
                }

                AddIfIncomplete(rootFolder, BookMediaType.Audiobook, incomplete);
                AddIfIncomplete(rootFolder, BookMediaType.Ebook, incomplete);
            }

            if (!incomplete.Any())
            {
                return new HealthCheck(GetType());
            }

            var message = string.Format(
                _localizationService.GetLocalizedString("RootFolderMediaSettingsIncomplete"),
                string.Join(" | ", incomplete));

            return new HealthCheck(
                GetType(),
                HealthCheckResult.Warning,
                message,
                "#root-folder-media-settings-are-incomplete");
        }

        private void AddIfIncomplete(RootFolder rootFolder, BookMediaType mediaType, ICollection<string> incomplete)
        {
            if (!RequiresSettings(rootFolder, mediaType) ||
                _rootFolderSettingsResolver.ResolveSettings(rootFolder, mediaType).IsConfigured)
            {
                return;
            }

            var mediaLabel = _localizationService.GetLocalizedString(
                mediaType == BookMediaType.Audiobook ? "Audiobook" : "Ebook");
            incomplete.Add($"{rootFolder.Name} ({mediaLabel})");
        }

        private static bool RequiresSettings(RootFolder rootFolder, BookMediaType mediaType)
        {
            if (rootFolder == null)
            {
                return false;
            }

            if (rootFolder.FolderType == FolderType.Audiobook)
            {
                return mediaType == BookMediaType.Audiobook;
            }

            if (rootFolder.FolderType == FolderType.Ebook)
            {
                return mediaType == BookMediaType.Ebook;
            }

            return mediaType == BookMediaType.Audiobook
                ? !string.IsNullOrWhiteSpace(rootFolder.AudiobookSettings)
                : !string.IsNullOrWhiteSpace(rootFolder.EbookSettings);
        }
    }
}
