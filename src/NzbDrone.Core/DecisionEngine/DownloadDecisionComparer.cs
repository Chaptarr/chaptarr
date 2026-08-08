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

        public delegate int CompareDelegate(DownloadDecision x, DownloadDecision y);
        public delegate int CompareDelegate<TSubject, TValue>(DownloadDecision x, DownloadDecision y);

        public DownloadDecisionComparer(IConfigService configService, IDelayProfileService delayProfileService, IQualityProfileService qualityProfileService, Logger logger)
        {
            _configService = configService;
            _delayProfileService = delayProfileService;
            _qualityProfileService = qualityProfileService;
            _logger = logger;
        }

        public int Compare(DownloadDecision x, DownloadDecision y)
        {
            _logger.Debug("Compare called - X: {0} ({1}), Y: {2} ({3})",
                x.RemoteBook.Release.Title,
                x.RemoteBook.Release.Size.SizeSuffix(),
                y.RemoteBook.Release.Title,
                y.RemoteBook.Release.Size.SizeSuffix());

            var comparers = new List<(string name, CompareDelegate comparer)>
            {
                ("Title Match", CompareTitleMatch),
                ("Native Category", CompareNativeCategory),
                ("Format Type", CompareFormatType),
                ("Profile Preference", CompareProfilePreference),
                ("Protocol", CompareProtocol),
                ("Indexer Priority", CompareIndexerPriority),
                ("Size", CompareSize),
                ("Peers", ComparePeersIfTorrent),
                ("Book Count", CompareBookCount),
                ("Age", CompareAgeIfUsenet)
            };

            foreach (var (name, comparer) in comparers)
            {
                var result = comparer(x, y);
                _logger.Debug("Comparison {0}: {1}", name, result);
                if (result != 0)
                {
                    _logger.Debug("Comparison {0} decided winner (result: {1}) - FLIPPED SIGNS: positive=X wins, negative=Y wins", name, result);
                    return result;
                }
            }

            _logger.Debug("All comparisons tied, returning 0");
            return 0;
        }

        public DownloadDecisionComparisonResult CompareWithReasons(DownloadDecision x, DownloadDecision y)
        {
            _logger.Debug("CompareWithReasons called - X: {0} ({1}), Y: {2} ({3})",
                x.RemoteBook.Release.Title,
                x.RemoteBook.Release.Size.SizeSuffix(),
                y.RemoteBook.Release.Title,
                y.RemoteBook.Release.Size.SizeSuffix());

            var reasons = new List<DownloadDecisionReason>();

            var comparers = new List<(string name, CompareDelegate comparer)>
            {
                ("Title Match", CompareTitleMatch),
                ("Native Category", CompareNativeCategory),
                ("Format Type", CompareFormatType),
                ("Profile Preference", CompareProfilePreference),
                ("Protocol Preference", CompareProtocol),
                ("Indexer Priority", CompareIndexerPriority),
                ("File Size", CompareSize),
                ("Torrent Health", ComparePeersIfTorrent),
                ("Release Scope", CompareBookCount),
                ("Usenet Age", CompareAgeIfUsenet)
            };

            foreach (var (name, comparer) in comparers)
            {
                var result = comparer(x, y);
                if (result != 0)
                {
                    var comparisonWinner = result > 0 ? x : y;
                    var reason = GetPrimaryReason(name, x, y, comparisonWinner);
                    reasons.Add(new DownloadDecisionReason("Primary", reason));
                    return new DownloadDecisionComparisonResult(result, reasons);
                }
            }

            // If we get here, all comparisons were tied - show generic fallback
            if (!reasons.Any())
            {
                reasons.Add(new DownloadDecisionReason("Primary", "Download allowed and meets all requirements"));
            }

            return new DownloadDecisionComparisonResult(0, reasons);
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
                    var quality = winner.RemoteBook.ParsedBookInfo.Quality.Quality;
                    var isEbook = IsEbookFormat(quality);
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
                var qualityProfile = decision.RemoteBook.Author.GetQualityProfileForQuality(decision.RemoteBook.ParsedBookInfo.Quality.Quality);
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

        private int CompareByReverse<TValue>(TValue left, TValue right)
            where TValue : IComparable<TValue>
        {
            return right.CompareTo(left);
        }

        private int CompareAll(params int[] comparers)
        {
            return comparers.Select(comparer => comparer).FirstOrDefault(result => result != 0);
        }

        private int CompareFormatType(DownloadDecision x, DownloadDecision y)
        {
            var xQuality = x.RemoteBook.ParsedBookInfo.Quality.Quality;
            var yQuality = y.RemoteBook.ParsedBookInfo.Quality.Quality;

            var xIsEbook = IsEbookFormat(xQuality);
            var yIsEbook = IsEbookFormat(yQuality);

            // If both are the same format type, they're equal in this comparison
            if (xIsEbook == yIsEbook)
            {
                return 0;
            }

            // Audiobooks come before ebooks in the results
            if (!xIsEbook && yIsEbook)
            {
                _logger.Debug("Format Type - X is audiobook, Y is ebook. X wins.");
                return 1; // x (audiobook) is better
            }

            _logger.Debug("Format Type - X is ebook, Y is audiobook. Y wins.");
            return -1; // y (audiobook) is better
        }

        private bool IsEbookFormat(Quality quality)
        {
            return quality.Id == Quality.Unknown.Id ||
                   quality.Id == Quality.PDF.Id ||
                   quality.Id == Quality.MOBI.Id ||
                   quality.Id == Quality.EPUB.Id ||
                   quality.Id == Quality.AZW3.Id;
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

            var mediaType = remoteBook.GetPreferredMediaType();
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

            var author = x.RemoteBook.Author ?? y.RemoteBook.Author;
            if (author == null)
            {
                return new QualityProfilePreferenceComparison(0, QualityProfilePreferenceFactor.None);
            }

            // Use a single, stable profile for both sides to keep comparisons symmetric.
            var qualityProfile = author.GetQualityProfileForQuality(xQuality.Quality);

            if (qualityProfile == null)
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
            // Larger files are better for audiobooks (higher quality, more complete content)
            var xSize = x.RemoteBook.Release.Size;
            var ySize = y.RemoteBook.Release.Size;

            _logger.Debug("CompareSize - X: {0} ({1}), Y: {2} ({3})",
                x.RemoteBook.Release.Title,
                xSize.SizeSuffix(),
                y.RemoteBook.Release.Title,
                ySize.SizeSuffix());

            // Direct comparison: larger size wins - FLIPPED
            var result = xSize.CompareTo(ySize); // FLIPPED: X compared to Y makes larger X win
            _logger.Debug("CompareSize result: {0} (positive means X wins, negative means Y wins)", result);
            return result;
        }

        private string GetComparisonDetails(string comparisonType, DownloadDecision x, DownloadDecision y, DownloadDecision winner)
        {
            switch (comparisonType)
            {
                case "Quality":
                    var winnerQuality = winner.RemoteBook.ParsedBookInfo.Quality.Quality.Name;
                    return $"Quality profile preference ({winnerQuality})";

                case "Custom Format Score":
                    var score = winner.RemoteBook.CustomFormatScore;
                    return $"Custom format score ({score})";

                case "Protocol Preference":
                    var protocol = winner.RemoteBook.Release.DownloadProtocol.ToString();
                    return $"Preferred protocol ({protocol})";

                case "Indexer Priority":
                    var indexer = winner.RemoteBook.Release.Indexer;
                    var priority = winner.RemoteBook.Release.IndexerPriority;
                    return $"Indexer priority ({indexer}, priority {priority})";

                case "Torrent Health":
                    if (winner.RemoteBook.Release.DownloadProtocol == DownloadProtocol.Torrent)
                    {
                        var seeders = TorrentInfo.GetSeeders(winner.RemoteBook.Release);
                        return $"Better torrent health ({seeders} seeders)";
                    }

                    return "Torrent health";

                case "Release Scope":
                    if (winner.RemoteBook.ParsedBookInfo.Discography)
                    {
                        return "Discography release";
                    }

                    return $"More books ({winner.RemoteBook.Books.Count} books)";

                case "Usenet Age":
                    var age = winner.RemoteBook.Release.Age;
                    return $"Newer usenet release ({age} days old)";

                case "File Size":
                    var size = winner.RemoteBook.Release.Size;
                    return $"File size preference ({size.SizeSuffix()})";

                default:
                    return comparisonType;
            }
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
