using System.Linq;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Profiles.Qualities
{
    public enum QualityProfilePreferenceFactor
    {
        None,
        CustomFormatScore,
        EffectiveQuality,
        SourceQuality
    }

    public readonly struct QualityProfilePreferenceComparison
    {
        public QualityProfilePreferenceComparison(int result, QualityProfilePreferenceFactor factor)
        {
            Result = result;
            Factor = factor;
        }

        public int Result { get; }
        public QualityProfilePreferenceFactor Factor { get; }
    }

    /// <summary>
    /// Defines the profile-owned release order used by search, pending, queue, history,
    /// and on-disk upgrades. Search-only tie breakers (protocol, indexer, size, age)
    /// deliberately live outside this comparer and can never trigger an upgrade.
    /// </summary>
    public static class QualityProfilePreferenceComparer
    {
        public static QualityProfilePreferenceComparison CompareReleases(
            QualityProfile profile,
            QualityModel leftSourceQuality,
            int leftCustomFormatScore,
            QualityModel rightSourceQuality,
            int rightCustomFormatScore,
            ProperDownloadTypes properDownloadTypes)
        {
            return Compare(
                profile,
                leftSourceQuality,
                leftCustomFormatScore,
                leftIsIncomingRelease: true,
                rightSourceQuality,
                rightCustomFormatScore,
                rightIsIncomingRelease: true,
                properDownloadTypes);
        }

        public static QualityProfilePreferenceComparison CompareCandidateToStored(
            QualityProfile profile,
            QualityModel candidateSourceQuality,
            int candidateCustomFormatScore,
            QualityModel storedQuality,
            int storedCustomFormatScore,
            ProperDownloadTypes properDownloadTypes)
        {
            return Compare(
                profile,
                candidateSourceQuality,
                candidateCustomFormatScore,
                leftIsIncomingRelease: true,
                storedQuality,
                storedCustomFormatScore,
                rightIsIncomingRelease: false,
                properDownloadTypes);
        }

        private static QualityProfilePreferenceComparison Compare(
            QualityProfile profile,
            QualityModel leftSourceQuality,
            int leftCustomFormatScore,
            bool leftIsIncomingRelease,
            QualityModel rightSourceQuality,
            int rightCustomFormatScore,
            bool rightIsIncomingRelease,
            ProperDownloadTypes properDownloadTypes)
        {
            var leftSource = NormalizeQuality(leftSourceQuality);
            var rightSource = NormalizeQuality(rightSourceQuality);
            var leftEffective = leftIsIncomingRelease
                ? QualityConversionHelper.GetEffectiveQualityAfterPlannedConversion(profile, leftSource)
                : leftSource;
            var rightEffective = rightIsIncomingRelease
                ? QualityConversionHelper.GetEffectiveQualityAfterPlannedConversion(profile, rightSource)
                : rightSource;
            var preferencesFirst = profile?.ProfileType == ProfileType.Audiobook &&
                                   profile.PreferCustomFormatsOverQuality;

            if (preferencesFirst)
            {
                var customFormatComparison = leftCustomFormatScore.CompareTo(rightCustomFormatScore);
                if (customFormatComparison != 0)
                {
                    return new QualityProfilePreferenceComparison(customFormatComparison, QualityProfilePreferenceFactor.CustomFormatScore);
                }
            }

            var effectiveQualityComparison = CompareQuality(
                profile,
                leftEffective,
                rightEffective,
                properDownloadTypes,
                respectGroupOrder: false);
            if (effectiveQualityComparison != 0)
            {
                return new QualityProfilePreferenceComparison(effectiveQualityComparison, QualityProfilePreferenceFactor.EffectiveQuality);
            }

            if (!preferencesFirst)
            {
                var customFormatComparison = leftCustomFormatScore.CompareTo(rightCustomFormatScore);
                if (customFormatComparison != 0)
                {
                    return new QualityProfilePreferenceComparison(customFormatComparison, QualityProfilePreferenceFactor.CustomFormatScore);
                }
            }

            // Conversion can make otherwise different source formats equivalent. Keep the
            // profile's source order as a final profile-level tie breaker so native output
            // wins when scores tie, while conversion-off grouped qualities remain unchanged.
            if (WasConverted(leftSource, leftEffective) || WasConverted(rightSource, rightEffective))
            {
                var sourceQualityComparison = CompareQuality(
                    profile,
                    leftSource,
                    rightSource,
                    properDownloadTypes,
                    respectGroupOrder: true);
                if (sourceQualityComparison != 0)
                {
                    return new QualityProfilePreferenceComparison(sourceQualityComparison, QualityProfilePreferenceFactor.SourceQuality);
                }
            }

            return new QualityProfilePreferenceComparison(0, QualityProfilePreferenceFactor.None);
        }

        private static QualityModel NormalizeQuality(QualityModel quality)
        {
            return new QualityModel(
                quality?.Quality ?? Quality.Unknown,
                quality?.Revision ?? new Revision());
        }

        private static int CompareQuality(
            QualityProfile profile,
            QualityModel left,
            QualityModel right,
            ProperDownloadTypes properDownloadTypes,
            bool respectGroupOrder)
        {
            if (profile?.Items?.Any() != true)
            {
                return 0;
            }

            var leftIndex = profile.GetIndex(left.Quality, respectGroupOrder);
            var rightIndex = profile.GetIndex(right.Quality, respectGroupOrder);
            var qualityComparison = leftIndex.CompareTo(rightIndex, respectGroupOrder);

            if (qualityComparison != 0 || properDownloadTypes == ProperDownloadTypes.DoNotPrefer)
            {
                return qualityComparison;
            }

            return left.Revision.CompareTo(right.Revision);
        }

        private static bool WasConverted(QualityModel source, QualityModel effective)
        {
            return source?.Quality?.Id != effective?.Quality?.Id;
        }
    }
}
