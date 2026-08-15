using System.Collections.Generic;
using NLog;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.DecisionEngine.Specifications
{
    public interface IUpgradableSpecification
    {
        bool IsUpgradable(QualityProfile profile, QualityModel currentQuality, List<CustomFormat> currentCustomFormats, QualityModel newQuality, List<CustomFormat> newCustomFormats);
        bool QualityCutoffNotMet(QualityProfile profile, QualityModel currentQuality, QualityModel newQuality = null);
        bool CutoffNotMet(QualityProfile profile, List<QualityModel> currentQualities, List<CustomFormat> currentFormats, QualityModel newQuality = null);
        bool IsRevisionUpgrade(QualityModel currentQuality, QualityModel newQuality);
        bool IsUpgradeAllowed(QualityProfile qualityProfile, QualityModel currentQuality, List<CustomFormat> currentCustomFormats, QualityModel newQuality, List<CustomFormat> newCustomFormats);
    }

    public class UpgradableSpecification : IUpgradableSpecification
    {
        private readonly IConfigService _configService;
        private readonly Logger _logger;

        public UpgradableSpecification(IConfigService configService, Logger logger)
        {
            _configService = configService;
            _logger = logger;
        }

        private static QualityModel NormalizeQuality(QualityModel quality)
        {
            if (quality == null)
            {
                return new QualityModel(Quality.Unknown);
            }

            quality.Quality ??= Quality.Unknown;
            quality.Revision ??= new Revision();
            return quality;
        }

        public bool IsUpgradable(QualityProfile qualityProfile, QualityModel currentQualities, List<CustomFormat> currentCustomFormats, QualityModel newQuality, List<CustomFormat> newCustomFormats)
        {
            return IsUpgradable(qualityProfile, currentQualities, currentCustomFormats, newQuality, newCustomFormats, currentIsIncomingRelease: false);
        }

        public bool IsReleaseUpgradable(QualityProfile qualityProfile, QualityModel currentQuality, List<CustomFormat> currentCustomFormats, QualityModel newQuality, List<CustomFormat> newCustomFormats)
        {
            return IsUpgradable(qualityProfile, currentQuality, currentCustomFormats, newQuality, newCustomFormats, currentIsIncomingRelease: true);
        }

        private bool IsUpgradable(QualityProfile qualityProfile, QualityModel currentQualities, List<CustomFormat> currentCustomFormats, QualityModel newQuality, List<CustomFormat> newCustomFormats, bool currentIsIncomingRelease)
        {
            currentQualities = NormalizeQuality(currentQualities);
            newQuality = NormalizeQuality(newQuality);
            currentCustomFormats ??= new List<CustomFormat>();
            newCustomFormats ??= new List<CustomFormat>();

            var currentFormatScore = qualityProfile.CalculateCustomFormatScore(currentCustomFormats);
            var newFormatScore = qualityProfile.CalculateCustomFormatScore(newCustomFormats);
            var comparison = currentIsIncomingRelease
                ? QualityProfilePreferenceComparer.CompareReleases(
                    qualityProfile,
                    newQuality,
                    newFormatScore,
                    currentQualities,
                    currentFormatScore,
                    _configService?.DownloadPropersAndRepacks ?? ProperDownloadTypes.PreferAndUpgrade)
                : QualityProfilePreferenceComparer.CompareCandidateToStored(
                    qualityProfile,
                    newQuality,
                    newFormatScore,
                    currentQualities,
                    currentFormatScore,
                    _configService?.DownloadPropersAndRepacks ?? ProperDownloadTypes.PreferAndUpgrade);

            if (comparison.Result <= 0)
            {
                _logger.Trace("Existing item is equal or better by profile preference ({0}), skipping", comparison.Factor);
                return false;
            }

            _logger.Trace("New item improves profile preference by {0}", comparison.Factor);
            return true;
        }

        public bool QualityCutoffNotMet(QualityProfile profile, QualityModel currentQuality, QualityModel newQuality = null)
        {
            currentQuality = NormalizeQuality(currentQuality);
            newQuality = newQuality == null ? null : NormalizeQuality(newQuality);

            var cutoff = profile.UpgradeAllowed ? profile.Cutoff : profile.FirstAllowedQuality().Id;
            var cutoffCompare = new QualityModelComparer(profile).Compare(currentQuality.Quality.Id, cutoff);

            if (cutoffCompare < 0)
            {
                return true;
            }

            if (newQuality != null && IsRevisionUpgrade(currentQuality, newQuality))
            {
                return true;
            }

            return false;
        }

        private bool CustomFormatCutoffNotMet(QualityProfile profile, List<CustomFormat> currentFormats)
        {
            currentFormats ??= new List<CustomFormat>();
            var score = profile.CalculateCustomFormatScore(currentFormats);
            return score < profile.CutoffFormatScore;
        }

        public bool CutoffNotMet(QualityProfile profile, List<QualityModel> currentQualities, List<CustomFormat> currentFormats, QualityModel newQuality = null)
        {
            currentQualities ??= new List<QualityModel>();

            foreach (var quality in currentQualities)
            {
                if (QualityCutoffNotMet(profile, quality, newQuality))
                {
                    return true;
                }
            }

            if (CustomFormatCutoffNotMet(profile, currentFormats))
            {
                return true;
            }

            _logger.Trace("Existing item meets cut-off. skipping.");

            return false;
        }

        public bool IsRevisionUpgrade(QualityModel currentQuality, QualityModel newQuality)
        {
            currentQuality = NormalizeQuality(currentQuality);
            newQuality = NormalizeQuality(newQuality);

            var compare = newQuality.Revision.CompareTo(currentQuality.Revision);

            // Comparing the quality directly because we don't want to upgrade to a proper for a webrip from a webdl or vice versa
            if (currentQuality.Quality == newQuality.Quality && compare > 0)
            {
                _logger.Trace("New quality is a better revision for existing quality");
                return true;
            }

            return false;
        }

        public bool IsUpgradeAllowed(QualityProfile qualityProfile, QualityModel currentQuality, List<CustomFormat> currentCustomFormats, QualityModel newQuality, List<CustomFormat> newCustomFormats)
        {
            return IsUpgradeAllowed(qualityProfile, currentQuality, currentCustomFormats, newQuality, newCustomFormats, currentIsIncomingRelease: false);
        }

        public bool IsReleaseUpgradeAllowed(QualityProfile qualityProfile, QualityModel currentQuality, List<CustomFormat> currentCustomFormats, QualityModel newQuality, List<CustomFormat> newCustomFormats)
        {
            return IsUpgradeAllowed(qualityProfile, currentQuality, currentCustomFormats, newQuality, newCustomFormats, currentIsIncomingRelease: true);
        }

        private bool IsUpgradeAllowed(QualityProfile qualityProfile, QualityModel currentQuality, List<CustomFormat> currentCustomFormats, QualityModel newQuality, List<CustomFormat> newCustomFormats, bool currentIsIncomingRelease)
        {
            currentQuality = NormalizeQuality(currentQuality);
            newQuality = NormalizeQuality(newQuality);
            currentCustomFormats ??= new List<CustomFormat>();
            newCustomFormats ??= new List<CustomFormat>();

            var comparison = currentIsIncomingRelease
                ? QualityProfilePreferenceComparer.CompareReleases(
                    qualityProfile,
                    newQuality,
                    qualityProfile.CalculateCustomFormatScore(newCustomFormats),
                    currentQuality,
                    qualityProfile.CalculateCustomFormatScore(currentCustomFormats),
                    _configService?.DownloadPropersAndRepacks ?? ProperDownloadTypes.PreferAndUpgrade)
                : QualityProfilePreferenceComparer.CompareCandidateToStored(
                    qualityProfile,
                    newQuality,
                    qualityProfile.CalculateCustomFormatScore(newCustomFormats),
                    currentQuality,
                    qualityProfile.CalculateCustomFormatScore(currentCustomFormats),
                    _configService?.DownloadPropersAndRepacks ?? ProperDownloadTypes.PreferAndUpgrade);

            if (comparison.Result <= 0)
            {
                // This specification only enforces whether a real improvement may replace
                // an existing item. Equal and worse candidates are rejected by the shared
                // upgradable specification.
                return true;
            }

            if (!qualityProfile.UpgradeAllowed)
            {
                _logger.Trace("Quality profile does not allow upgrades, skipping");
                return false;
            }

            _logger.Trace("Quality profile allows upgrading by {0}", comparison.Factor);
            return true;
        }
    }
}
