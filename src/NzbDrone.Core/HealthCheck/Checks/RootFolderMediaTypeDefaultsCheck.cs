using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Datastore.Events;
using NzbDrone.Core.Localization;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.HealthCheck.Checks
{
    [CheckOn(typeof(ModelEvent<RootFolder>))]
    public class RootFolderMediaTypeDefaultsCheck : HealthCheckBase
    {
        private readonly IRootFolderService _rootFolderService;

        public RootFolderMediaTypeDefaultsCheck(IRootFolderService rootFolderService, ILocalizationService localizationService)
            : base(localizationService)
        {
            _rootFolderService = rootFolderService;
        }

        public override HealthCheck Check()
        {
            var incomplete = _rootFolderService.All()
                .Where(IsMissingCompatibleDefaults)
                .Select(f => f.Path)
                .ToList();

            if (!incomplete.Any())
            {
                return new HealthCheck(GetType());
            }

            if (incomplete.Count == 1)
            {
                return new HealthCheck(
                    GetType(),
                    HealthCheckResult.Warning,
                    string.Format(_localizationService.GetLocalizedString("RootFolderMissingMediaTypeDefaultsSingleMessage"), incomplete.First()),
                    "#root-folder-missing-media-type-defaults");
            }

            var message = string.Format(
                _localizationService.GetLocalizedString("RootFolderMissingMediaTypeDefaultsMultipleMessage"),
                string.Join(" | ", incomplete));

            return new HealthCheck(GetType(), HealthCheckResult.Warning, message, "#root-folder-missing-media-type-defaults");
        }

        // A root folder created via the API without going through the UI wizard can be
        // saved with no AudiobookSettings/EbookSettings (or with settings that have no
        // quality/metadata profile chosen) for a media type its FolderType accepts.
        // DiscoveryWorker then fails every author it tries to create there
        // (AuthorLibraryService.NormalizeMonitoringConfigForMediaType) with a warn-level
        // log entry that is never surfaced anywhere else and never retried. This mirrors
        // that method's own null/settings and quality/metadata profile id checks so this
        // check can't report Ok while DiscoveryWorker is still failing; it does not
        // detect a profile id that points at a since-deleted profile, since that needs
        // IQualityProfileService/IMetadataProfileService and was left out of this pass.
        private static bool IsMissingCompatibleDefaults(RootFolder rootFolder)
        {
            var acceptsAudiobooks = rootFolder.FolderType == FolderType.Mixed || rootFolder.FolderType == FolderType.Audiobook;
            var acceptsEbooks = rootFolder.FolderType == FolderType.Mixed || rootFolder.FolderType == FolderType.Ebook;

            if (acceptsAudiobooks && IsMissingUsableDefaults(rootFolder.GetAudiobookSettings()))
            {
                return true;
            }

            if (acceptsEbooks && IsMissingUsableDefaults(rootFolder.GetEbookSettings()))
            {
                return true;
            }

            return false;
        }

        private static bool IsMissingUsableDefaults(MediaTypeSettings settings)
        {
            if (settings == null)
            {
                return true;
            }

            return !HasValidProfileId(settings.QualityProfileId) || !HasValidProfileId(settings.MetadataProfileId);
        }

        private static bool HasValidProfileId(int? profileId)
        {
            return profileId.HasValue && profileId.Value > 0;
        }
    }
}
