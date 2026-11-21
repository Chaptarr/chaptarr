using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Delay;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.DecisionEngine
{
    public class DownloadDecisionComparer : IComparer<DownloadDecision>
    {
        private readonly IConfigService _configService;
        private readonly IDelayProfileService _delayProfileService;
        private readonly IQualityProfileService _qualityProfileService;
        private readonly Logger _logger;

        private delegate int CompareDelegate(DownloadDecision x, DownloadDecision y);
        private readonly (string Name, CompareDelegate Compare)[] _comparers;

        public DownloadDecisionComparer(IConfigService configService, IDelayProfileService delayProfileService, IQualityProfileService qualityProfileService, Logger logger)
        {
            _configService = configService;
            _delayProfileService = delayProfileService;
            _qualityProfileService = qualityProfileService;
            _logger = logger;
            _comparers = new (string, CompareDelegate)[]
            {
                ("Title Match", CompareTitleMatch),
                ("Native Category", CompareNativeCategory),
                ("Format Type", CompareFormatType),
                ("Profile Preference", CompareProfilePreference),
                ("Protocol Preference", CompareProtocol),
                ("Indexer Priority", CompareIndexerPriority),
                ("Torrent Health", ComparePeersIfTorrent),
                ("Release Scope", CompareBookCount),
                ("Usenet Age", CompareAgeIfUsenet),
                ("File Size", CompareSize)
            };
        }

        public int Compare(DownloadDecision x, DownloadDecision y)
        {
            return Compare(x, y, out _);
        }

        private int Compare(DownloadDecision x, DownloadDecision y, out string decidingFactor)
        {
            _logger.Debug("Comparing {0} ({1}) with {2} ({3})",
                x.RemoteBook.Release.Title,
                x.RemoteBook.Release.Size.SizeSuffix(),
                y.RemoteBook.Release.Title,
                y.RemoteBook.Release.Size.SizeSuffix());

            foreach (var (name, comparer) in _comparers)
            {
                var result = comparer(x, y);
                _logger.Debug("Comparison {0}: {1}", name, result);
                if (result != 0)
                {
                    decidingFactor = name;
                    return result;
                }
            }

            decidingFactor = null;
            return 0;
        }

        public DownloadDecisionComparisonResult CompareWithReasons(DownloadDecision x, DownloadDecision y)
        {
            var result = Compare(x, y, out var decidingFactor);
            var reason = result == 0
                ? "Download allowed and meets all requirements"
                : GetPrimaryReason(decidingFactor, x, y, result > 0 ? x : y);

            return new DownloadDecisionComparisonResult(result, new DownloadDecisionReason("Primary", reason));
        }

        private string GetPrimaryReason(string comparisonName, DownloadDecision x, DownloadDecision y, DownloadDecision winner)
        {
            switch (comparisonName)
            {
                case "Title Match":
                    return MatchesPrimaryTitle(winner.RemoteBook)
                        ? "Matches monitored edition title"
                        : "Matches sibling edition title";

                case "Native Category":
                    return "Preferred indexer category";

                case "Format Type":
                    var isEbook = winner.RemoteBook.GetMediaType() == BookMediaType.Ebook;
                    return isEbook ? "Ebook format" : "Audiobook format preferred";

                case "Profile Preference":
                    var preference = CompareProfilePreferenceDetailed(x, y);
                    switch (preference.Factor)
                    {
                        case QualityProfilePreferenceFactor.CustomFormatScore:
                            return "Higher Custom Format score";
                        case QualityProfilePreferenceFactor.EffectiveQuality:
                            return $"Preferred final file format for '{GetQualityProfileName(winner)}' profile";
                        case QualityProfilePreferenceFactor.SourceQuality:
                            return "Preferred source format; avoids unnecessary conversion";
                        default:
                            return "Higher profile preference";
                    }

                case "Protocol Preference":
                    var protocol = winner.RemoteBook.Release.DownloadProtocol.ToString();
                    return $"Better protocol preference ({protocol})";

                case "Indexer Priority":
                    var indexerName = winner.RemoteBook.Release.Indexer;
                    return $"Higher indexer priority ({indexerName})";

                case "Torrent Health":
                    return "Better torrent health";

                case "Release Scope":
                    if (winner.RemoteBook.ParsedBookInfo.Discography)
                    {
                        return "Discography preferred over single book";
                    }

                    return "More books in release";

                case "Usenet Age":
                    return "Newer release";

                case "File Size":
                    return "Larger file size";

                default:
                    return "Higher priority";
            }
        }

        private string GetQualityProfileName(DownloadDecision decision)
        {
            try
            {
                var qualityProfile = decision.RemoteBook.GetQualityProfile();
                return qualityProfile?.Name ?? "Unknown";
            }
            catch
            {
                return "Unknown";
            }
        }

        private int CompareBy<TSubject, TValue>(TSubject left, TSubject right, Func<TSubject, TValue> funcValue)
            where TValue : IComparable<TValue>
        {
            var leftValue = funcValue(left);
            var rightValue = funcValue(right);

            return leftValue.CompareTo(rightValue);
        }

        private int CompareByReverse<TSubject, TValue>(TSubject left, TSubject right, Func<TSubject, TValue> funcValue)
            where TValue : IComparable<TValue>
        {
            return CompareBy(left, right, funcValue) * -1;
        }

        private int CompareAll(params int[] comparers)
        {
            return comparers.Select(comparer => comparer).FirstOrDefault(result => result != 0);
        }

        private int CompareFormatType(DownloadDecision x, DownloadDecision y)
        {
            return CompareBy(x.RemoteBook, y.RemoteBook, remoteBook => remoteBook.GetMediaType() switch
            {
                BookMediaType.Audiobook => 2,
                BookMediaType.Ebook => 1,
                _ => 0
            });
        }

        // MAM-specific ranking is handled through quality profiles and custom formats.

        private int CompareIndexerPriority(DownloadDecision x, DownloadDecision y)
        {
            // Lower priority number should win; reverse the compare so smaller values sort ahead in OrderByDescending
            return CompareByReverse(x.RemoteBook.Release, y.RemoteBook.Release, release => release.IndexerPriority);
        }

        private int CompareNativeCategory(DownloadDecision x, DownloadDecision y)
        {
            return CompareBy(x.RemoteBook, y.RemoteBook, GetNativeCategoryScore);
        }

        private static int GetNativeCategoryScore(RemoteBook remoteBook)
        {
            var categories = remoteBook?.Release?.Categories;
            if (categories == null || categories.Count == 0)
            {
                return 0;
            }

            var mediaType = remoteBook.GetMediaType();
            if (!mediaType.HasValue)
            {
                return 0;
            }

            var normalized = categories.Select(NormalizeCategory).ToList();

            if (mediaType == BookMediaType.Ebook)
            {
                if (normalized.Contains(7020))
                {
                    return 2;
                }

                return normalized.Any(category => category >= 7000 && category < 8000) ? 1 : 0;
            }

            if (normalized.Contains(3030))
            {
                return 2;
            }

            return normalized.Any(category => category >= 3000 && category < 4000) ? 1 : 0;
        }

        private static int NormalizeCategory(int category)
        {
            return category >= 100000 ? category - 100000 : category;
        }

        private int CompareProfilePreference(DownloadDecision x, DownloadDecision y)
        {
            return CompareProfilePreferenceDetailed(x, y).Result;
        }

        private QualityProfilePreferenceComparison CompareProfilePreferenceDetailed(DownloadDecision x, DownloadDecision y)
        {
            var xQuality = x.RemoteBook?.ParsedBookInfo?.Quality;
            var yQuality = y.RemoteBook?.ParsedBookInfo?.Quality;

            if (xQuality == null || yQuality == null)
            {
                return new QualityProfilePreferenceComparison(0, QualityProfilePreferenceFactor.None);
            }

            // Per-author sorting uses the target's media profile for both sides of this comparison.
            var qualityProfile = x.RemoteBook.GetQualityProfile();
            var otherProfile = y.RemoteBook.GetQualityProfile();
            if (qualityProfile == null || otherProfile == null || qualityProfile.Id != otherProfile.Id)
            {
                return new QualityProfilePreferenceComparison(0, QualityProfilePreferenceFactor.None);
            }

            return QualityProfilePreferenceComparer.CompareReleases(
                qualityProfile,
                xQuality,
                x.RemoteBook.CustomFormatScore,
                yQuality,
                y.RemoteBook.CustomFormatScore,
                _configService?.DownloadPropersAndRepacks ?? ProperDownloadTypes.PreferAndUpgrade);
        }

        private int CompareProtocol(DownloadDecision x, DownloadDecision y)
        {
            var result = CompareBy(x.RemoteBook, y.RemoteBook, remoteBook =>
            {
                var mediaType = remoteBook.GetPreferredMediaType();
                var tags = remoteBook.Author.GetTagsForMediaType(mediaType);
                var delayProfile = _delayProfileService.BestForTags(tags);
                var downloadProtocol = remoteBook.Release.DownloadProtocol;
                return downloadProtocol == delayProfile.PreferredProtocol;
            });

            return result;
        }

        private int CompareBookCount(DownloadDecision x, DownloadDecision y)
        {
            var discographyCompare = CompareBy(x.RemoteBook,
                y.RemoteBook,
                remoteBook => remoteBook.ParsedBookInfo.Discography);

            if (discographyCompare != 0)
            {
                return discographyCompare;
            }

            return CompareByReverse(x.RemoteBook, y.RemoteBook, remoteBook => remoteBook.Books.Count);
        }

        private int ComparePeersIfTorrent(DownloadDecision x, DownloadDecision y)
        {
            // Different protocols should get caught when checking the preferred protocol,
            // since we're dealing with the same series in our comparisions
            if (x.RemoteBook.Release.DownloadProtocol != DownloadProtocol.Torrent ||
                y.RemoteBook.Release.DownloadProtocol != DownloadProtocol.Torrent)
            {
                return 0;
            }

            return CompareAll(
                CompareBy(x.RemoteBook, y.RemoteBook, remoteBook =>
                {
                    var seeders = TorrentInfo.GetSeeders(remoteBook.Release);

                    return seeders.HasValue && seeders.Value > 0 ? Math.Round(Math.Log10(seeders.Value)) : 0;
                }),
                CompareBy(x.RemoteBook, y.RemoteBook, remoteBook =>
                {
                    var peers = TorrentInfo.GetPeers(remoteBook.Release);

                    return peers.HasValue && peers.Value > 0 ? Math.Round(Math.Log10(peers.Value)) : 0;
                }));
        }

        private int CompareAgeIfUsenet(DownloadDecision x, DownloadDecision y)
        {
            if (x.RemoteBook.Release.DownloadProtocol != DownloadProtocol.Usenet ||
                y.RemoteBook.Release.DownloadProtocol != DownloadProtocol.Usenet)
            {
                return 0;
            }

            return CompareBy(x.RemoteBook, y.RemoteBook, remoteBook =>
            {
                var ageHours = remoteBook.Release.AgeHours;
                var age = remoteBook.Release.Age;

                if (ageHours < 1)
                {
                    return 1000;
                }

                if (ageHours <= 24)
                {
                    return 100;
                }

                if (age <= 7)
                {
                    return 10;
                }

                return 1;
            });
        }

        private int CompareSize(DownloadDecision x, DownloadDecision y)
        {
            // Size only breaks ties after preferences, availability and release scope.
            return CompareBy(x.RemoteBook, y.RemoteBook, remoteBook => remoteBook.Release.Size.Round(
                (remoteBook.GetPreferredMediaType() == BookMediaType.Ebook ? 1 : 200).Megabytes()));
        }

        private int CompareTitleMatch(DownloadDecision x, DownloadDecision y)
        {
            return CompareBy(x.RemoteBook, y.RemoteBook, MatchesPrimaryTitle);
        }

        private bool MatchesPrimaryTitle(RemoteBook remoteBook)
        {
            var match = remoteBook?.SearchCriteriaMatch;
            return match?.IsMatch == true &&
                   !string.IsNullOrWhiteSpace(match.PrimaryTitle) &&
                   string.Equals(match.PrimaryTitle, match.MatchedVariant, StringComparison.OrdinalIgnoreCase);
        }

    }
}
