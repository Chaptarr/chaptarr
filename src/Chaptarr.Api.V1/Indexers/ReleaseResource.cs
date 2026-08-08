using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Chaptarr.Api.V1.CustomFormats;
using Chaptarr.Http.REST;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Api.V1.Indexers
{
    // Enhanced rejection details for filter warning system
    public class RejectionDetail
    {
        public string Reason { get; set; }
        public string Type { get; set; }      // "Permanent", "Temporary"
        public string Category { get; set; }   // "Quality", "Language", "Seeders", "Format", etc.
        public bool CanBypass { get; set; }
        public int Severity { get; set; }      // 1=Info, 2=Warning, 3=Error
    }

    // Filter summary statistics for API responses
    public class FilterSummary
    {
        [JsonPropertyName("totalResults")]
        public int TotalResults { get; set; }

        [JsonPropertyName("filteredCount")]
        public int FilteredCount { get; set; }

        [JsonPropertyName("displayedCount")]
        public int DisplayedCount { get; set; }

        [JsonPropertyName("hardFilteredCount")]
        public int HardFilteredCount { get; set; }

        [JsonPropertyName("softFilteredCount")]
        public int SoftFilteredCount { get; set; }

        [JsonPropertyName("hasSoftFilters")]
        public bool HasSoftFilters { get; set; }

        [JsonPropertyName("hasHardFilters")]
        public bool HasHardFilters { get; set; }

        [JsonPropertyName("bypassMode")]
        public bool BypassMode { get; set; }

        [JsonPropertyName("filteredCategories")]
        public List<string> FilteredCategories { get; set; } = new List<string>();

        [JsonPropertyName("filterBreakdown")]
        public Dictionary<string, int> FilterBreakdown { get; set; } = new Dictionary<string, int>();

        [JsonPropertyName("summaryText")]
        public string SummaryText { get; set; }

        [JsonPropertyName("filterWarnings")]
        public List<string> FilterWarnings { get; set; } = new List<string>();
    }

    // Enhanced response wrapper for interactive search with filter information
    public class ReleaseSearchResponse
    {
        [JsonPropertyName("releases")]
        public List<ReleaseResource> Releases { get; set; } = new List<ReleaseResource>();

        [JsonPropertyName("hiddenReleases")]
        public List<ReleaseResource> HiddenReleases { get; set; } = new List<ReleaseResource>();

        [JsonPropertyName("filterSummary")]
        public FilterSummary FilterSummary { get; set; } = new FilterSummary();

        [JsonPropertyName("siblingBookId")]
        public int? SiblingBookId { get; set; }

        [JsonPropertyName("siblingMediaType")]
        public string SiblingMediaType { get; set; }

        [JsonPropertyName("siblingToggleEnabled")]
        public bool SiblingToggleEnabled { get; set; }

        [JsonPropertyName("siblingToggleDisabledReason")]
        public string SiblingToggleDisabledReason { get; set; }
    }

    public class ReleaseResource : RestResource
    {
        public string Guid { get; set; }
        public QualityModel Quality { get; set; }
        public int QualityWeight { get; set; }

        // Multi-format quality information for GUI display
        [JsonPropertyName("isMultiFormat")]
        public bool IsMultiFormat { get; set; }

        [JsonPropertyName("allDetectedFormats")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string AllDetectedFormats { get; set; }
        public int Age { get; set; }
        public double AgeHours { get; set; }
        public double AgeMinutes { get; set; }
        public long Size { get; set; }
        public int IndexerId { get; set; }
        public string Indexer { get; set; }
        public string ReleaseGroup { get; set; }
        public string SubGroup { get; set; }
        public string ReleaseHash { get; set; }
        public string Title { get; set; }

        // Clean display title with redundant elements removed
        [JsonPropertyName("displayTitle")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string DisplayTitle { get; set; }

        public bool Discography { get; set; }
        public bool SceneSource { get; set; }
        public string AirDate { get; set; }
        public string AuthorName { get; set; }
        public string BookTitle { get; set; }

        [JsonPropertyName("matchedTitle")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string MatchedTitle { get; set; }

        [JsonPropertyName("matchedTitleCharStart")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? MatchedTitleCharStart { get; set; }

        [JsonPropertyName("matchedTitleCharEnd")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? MatchedTitleCharEnd { get; set; }

        public bool Approved { get; set; }
        public bool TemporarilyRejected { get; set; }
        public bool Rejected { get; set; }

        // Legacy rejection support (backward compatibility)
        public IEnumerable<string> Rejections { get; set; }

        // Enhanced rejection details for filter warning system
        public List<RejectionDetail> RejectionDetails { get; set; } = new List<RejectionDetail>();
        public bool HasSoftFilters { get; set; }
        public bool HasHardFilters { get; set; }
        public bool CanBypassFilters { get; set; }

        public DateTime PublishDate { get; set; }
        public string CommentUrl { get; set; }
        public string DownloadUrl { get; set; }
        public string InfoUrl { get; set; }
        public bool DownloadAllowed { get; set; }
        public int ReleaseWeight { get; set; }
        public int Rank { get; set; }
        public List<CustomFormatResource> CustomFormats { get; set; }
        public int CustomFormatScore { get; set; }
        public bool IsPreferredChoice { get; set; }
        public List<string> AutoPickReasons { get; set; }

        public string MagnetUrl { get; set; }
        public string InfoHash { get; set; }
        public int? Seeders { get; set; }
        public int? Leechers { get; set; }
        public DownloadProtocol Protocol { get; set; }
        public int IndexerFlags { get; set; }

        // MAM Enhanced Metadata
        [JsonPropertyName("duration")]
        public string Duration { get; set; }

        [JsonPropertyName("narrator")]
        public string Narrator { get; set; }

        [JsonPropertyName("isGraphicAudio")]
        public bool IsGraphicAudio { get; set; }

        // Sent when queuing an unknown release
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int? AuthorId { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int? BookId { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int? DownloadClientId { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string DownloadClient { get; set; }

        // Format type for grouping in UI (audiobook or ebook)
        [JsonPropertyName("formatType")]
        public string FormatType { get; set; }
    }

    public static class ReleaseResourceMapper
    {
        private static readonly Regex EbookHintRegex = new Regex(@"\b(epub|mobi|azw3|azw|pdf|djvu|cbz|cbr|fb2|ebook)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex AudiobookHintRegex = new Regex(@"\b(audiobook|audible|mp3|m4b|m4a|flac|aac|opus|ogg|wav)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	        public static ReleaseResource ToResource(this DownloadDecision model)
	        {
	            if (model == null)
	            {
	                return null;
	            }

	            var remoteBook = model.RemoteBook ?? new RemoteBook();
	            var releaseInfo = remoteBook.Release ?? new ReleaseInfo();
	            var torrentInfo = (releaseInfo as TorrentInfo) ?? new TorrentInfo();
	            var indexerFlags = releaseInfo.IndexerFlags;
	            var rejections = (model.Rejections ?? Enumerable.Empty<Rejection>()).Where(r => r != null).ToList();
	            var rejectionReasons = rejections
	                .Select(r => r.Reason)
	                .Where(r => !string.IsNullOrWhiteSpace(r))
	                .Distinct(StringComparer.OrdinalIgnoreCase)
	                .ToList();
	            var customFormats = (remoteBook.CustomFormats ?? new List<CustomFormat>()).Where(cf => cf != null).ToList();
	            var resolvedAuthorId = remoteBook.Author?.Id > 0
	                ? remoteBook.Author.Id
	                : remoteBook.Books?.Select(book => book?.AuthorId ?? 0).FirstOrDefault(id => id > 0);
	            var resolvedBookId = remoteBook.Books?.Count == 1 && remoteBook.Books[0]?.Id > 0
	                ? remoteBook.Books[0].Id
	                : (int?)null;

            var parsedBookInfo = remoteBook.ParsedBookInfo ?? new ParsedBookInfo
            {
                AuthorName = releaseInfo.Author,
                BookTitle = releaseInfo.Title,
                ReleaseTitle = releaseInfo.Title
            };

            parsedBookInfo.Quality ??= new QualityModel { Quality = Quality.Unknown };
            var displayTitle = CleanDisplayTitle(releaseInfo, parsedBookInfo.Quality);
            var matchedTitleCharSpan = GetMatchedTitleCharSpan(remoteBook, releaseInfo.Title, displayTitle ?? releaseInfo.Title);

            // TODO: Clean this mess up. don't mix data from multiple classes, use sub-resources instead? (Got a huge Deja Vu, didn't we talk about this already once?)
            return new ReleaseResource
            {
                Guid = releaseInfo.Guid,
                Quality = parsedBookInfo.Quality,

                // Multi-format quality information
                IsMultiFormat = parsedBookInfo.Quality.IsMultiFormat,
                AllDetectedFormats = parsedBookInfo.Quality.IsMultiFormat ? parsedBookInfo.Quality.AllDetectedFormats : null,

                //QualityWeight
                Age = releaseInfo.Age,
                AgeHours = releaseInfo.AgeHours,
                AgeMinutes = releaseInfo.AgeMinutes,
                Size = releaseInfo.Size,
                IndexerId = releaseInfo.IndexerId,
                Indexer = releaseInfo.Indexer,
                ReleaseGroup = parsedBookInfo.ReleaseGroup,
                ReleaseHash = parsedBookInfo.ReleaseHash,
                Title = releaseInfo.Title,
                DisplayTitle = displayTitle,
                AuthorName = parsedBookInfo.AuthorName ?? releaseInfo.Author,
                BookTitle = parsedBookInfo.BookTitle,
                MatchedTitle = GetMatchedTitle(remoteBook),
                MatchedTitleCharStart = matchedTitleCharSpan.Start,
                MatchedTitleCharEnd = matchedTitleCharSpan.End,
                Discography = parsedBookInfo.Discography,
                Approved = model.Approved,
                TemporarilyRejected = model.TemporarilyRejected,
	                Rejected = model.Rejected,

	                // Legacy rejection support (backward compatibility)
	                Rejections = rejectionReasons,

	                // Enhanced rejection details for filter warning system
	                RejectionDetails = rejections.Select(r => new RejectionDetail
	                {
	                    Reason = r.Reason,
	                    Type = r.Type.ToString(),
	                    Category = r.Category,
	                    CanBypass = r.CanBypass,
	                    Severity = r.Severity
	                }).ToList(),

	                HasSoftFilters = rejections.Any(r => r.IsSoftFilter),
	                HasHardFilters = rejections.Any(r => r.IsHardFilter),
	                CanBypassFilters = rejections.Any() && rejections.All(r => r.CanBypass),
	                PublishDate = releaseInfo.PublishDate,
	                CommentUrl = releaseInfo.CommentUrl,
	                DownloadUrl = releaseInfo.DownloadUrl,
	                InfoUrl = releaseInfo.InfoUrl,
	                DownloadAllowed = remoteBook.DownloadAllowed,

	                // ReleaseWeight
	                CustomFormatScore = remoteBook.CustomFormatScore,
	                CustomFormats = customFormats.ToResource(false),
	                AutoPickReasons = model.AutoPickReasons?.Select(r => r.ToString()).ToList() ?? new List<string>(),

                MagnetUrl = torrentInfo.MagnetUrl,
                InfoHash = torrentInfo.InfoHash,
                Seeders = torrentInfo.Seeders,
                Leechers = (torrentInfo.Peers.HasValue && torrentInfo.Seeders.HasValue) ? (torrentInfo.Peers.Value - torrentInfo.Seeders.Value) : (int?)null,
                Protocol = releaseInfo.DownloadProtocol,
                IndexerFlags = (int)indexerFlags,

                // Enhanced metadata from MAM and other indexers
                Duration = releaseInfo.Duration,
                Narrator = releaseInfo.Narrator,
                IsGraphicAudio = releaseInfo.IsGraphicAudio,

                AuthorId = resolvedAuthorId > 0 ? resolvedAuthorId : null,
                BookId = resolvedBookId,

                // Format type for UI grouping
                FormatType = GetFormatType(parsedBookInfo?.Quality?.Quality, releaseInfo, torrentInfo)
            };
        }

        private static string GetMatchedTitle(RemoteBook remoteBook)
        {
            var match = remoteBook?.SearchCriteriaMatch;
            if (match?.IsMatch != true)
            {
                return null;
            }

            return string.IsNullOrWhiteSpace(match.PrimaryTitle) ? null : match.PrimaryTitle.Trim();
        }

        private static (int? Start, int? End) GetMatchedTitleCharSpan(RemoteBook remoteBook, string releaseTitle, string displayTitle)
        {
            var match = remoteBook?.SearchCriteriaMatch;
            if (match?.IsMatch != true || string.IsNullOrWhiteSpace(releaseTitle) || string.IsNullOrWhiteSpace(displayTitle))
            {
                return (null, null);
            }

            var releaseTokens = ReleaseTitleMatchScorer.Tokenize(NzbDrone.Core.Parser.Parser.CleanReleaseTitleForParsing(releaseTitle));
            if (match.MatchedStart < 0 ||
                match.MatchedEnd < match.MatchedStart ||
                match.MatchedEnd >= releaseTokens.Count)
            {
                return (null, null);
            }

            var matchedTokens = releaseTokens
                .Skip(match.MatchedStart)
                .Take(match.MatchedEnd - match.MatchedStart + 1)
                .ToList();

            var occurrenceRank = GetMatchedOccurrenceRank(releaseTokens, matchedTokens, match.MatchedStart, match.MatchedEnd);
            if (occurrenceRank <= 0)
            {
                return (null, null);
            }

            var displayTokens = ReleaseTitleMatchScorer.TokenizeWithSpans(displayTitle);
            var displayTokenValues = displayTokens.Select(token => token.Value).ToList();
            var occurrence = 0;

            foreach (var span in ReleaseTitleMatchScorer.FindExactTitleSpans(displayTokenValues, matchedTokens))
            {
                occurrence++;
                if (occurrence == occurrenceRank)
                {
                    return (displayTokens[span.Start].Start, displayTokens[span.End].End);
                }
            }

            return (null, null);
        }

        private static int GetMatchedOccurrenceRank(IReadOnlyList<string> releaseTokens, IReadOnlyList<string> matchedTokens, int matchedStart, int matchedEnd)
        {
            var occurrence = 0;

            foreach (var span in ReleaseTitleMatchScorer.FindExactTitleSpans(releaseTokens, matchedTokens))
            {
                occurrence++;
                if (span.Start == matchedStart && span.End == matchedEnd)
                {
                    return occurrence;
                }
            }

            return 0;
        }

        public static ReleaseInfo ToModel(this ReleaseResource resource)
        {
            ReleaseInfo model;

            if (resource.Protocol == DownloadProtocol.Torrent)
            {
                model = new TorrentInfo
                {
                    MagnetUrl = resource.MagnetUrl,
                    InfoHash = resource.InfoHash,
                    Seeders = resource.Seeders,
                    Peers = (resource.Seeders.HasValue && resource.Leechers.HasValue) ? (resource.Seeders + resource.Leechers) : null,
                    IndexerFlags = (IndexerFlags)resource.IndexerFlags
                };
            }
            else
            {
                model = new ReleaseInfo();
            }

            model.Guid = resource.Guid;
            model.Title = resource.Title;
            model.Size = resource.Size;
            model.DownloadUrl = resource.DownloadUrl;
            model.InfoUrl = resource.InfoUrl;
            model.CommentUrl = resource.CommentUrl;
            model.IndexerId = resource.IndexerId;
            model.Indexer = resource.Indexer;
            model.DownloadProtocol = resource.Protocol;
            model.PublishDate = resource.PublishDate.ToUniversalTime();

            return model;
        }

        private static string CleanDisplayTitle(ReleaseInfo releaseInfo, QualityModel quality)
        {
            var originalTitle = releaseInfo?.Title;
            if (string.IsNullOrWhiteSpace(originalTitle))
            {
                return originalTitle;
            }

            var cleanTitle = originalTitle;

            // Only remove metadata already shown in other interactive-search columns.
            cleanTitle = RemoveBracketedOrLabelledValue(cleanTitle, releaseInfo.Narrator);
            cleanTitle = RemoveDisplayedToken(cleanTitle, releaseInfo.Duration);

            foreach (var token in GetDisplayedQualityTokens(quality))
            {
                cleanTitle = RemoveDisplayedToken(cleanTitle, token);
            }

            foreach (var token in GetDisplayedFlagTokens(releaseInfo.IndexerFlags))
            {
                cleanTitle = RemoveBracketedOrLabelledValue(cleanTitle, token);
            }

            if (releaseInfo.IsGraphicAudio)
            {
                cleanTitle = RemoveBracketedOrLabelledValue(cleanTitle, "GraphicAudio");
                cleanTitle = RemoveBracketedOrLabelledValue(cleanTitle, "Graphic Audio");
            }

            cleanTitle = NormalizeDisplayTitle(cleanTitle);

            return string.IsNullOrWhiteSpace(cleanTitle) || cleanTitle.Length < 3 ? originalTitle : cleanTitle;
        }

        private static IEnumerable<string> GetDisplayedQualityTokens(QualityModel quality)
        {
            var qualities = (quality?.DetectedQualities?.Any() == true ? quality.DetectedQualities : new List<Quality> { quality?.Quality })
                .Where(q => q != null && q != Quality.Unknown && q != Quality.UnknownAudio)
                .Select(q => q.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            return qualities;
        }

        private static IEnumerable<string> GetDisplayedFlagTokens(IndexerFlags flags)
        {
            if (flags.HasFlag(IndexerFlags.Freeleech))
            {
                yield return "Freeleech";
                yield return "FL";
            }

            if (flags.HasFlag(IndexerFlags.Halfleech))
            {
                yield return "Halfleech";
            }

            if (flags.HasFlag(IndexerFlags.DoubleUpload))
            {
                yield return "Double Upload";
                yield return "DoubleUpload";
            }

            if (flags.HasFlag(IndexerFlags.Internal))
            {
                yield return "Internal";
            }

            if (flags.HasFlag(IndexerFlags.Scene))
            {
                yield return "Scene";
            }

            if (flags.HasFlag(IndexerFlags.Freeleech75))
            {
                yield return "75% Freeleech";
                yield return "Freeleech75";
            }

            if (flags.HasFlag(IndexerFlags.Freeleech25))
            {
                yield return "25% Freeleech";
                yield return "Freeleech25";
            }

            if (flags.HasFlag(IndexerFlags.VipExclusive))
            {
                yield return "VIP";
                yield return "VIP Exclusive";
            }

            if (flags.HasFlag(IndexerFlags.VipFreeleech))
            {
                yield return "VIP Freeleech";
            }
        }

        private static string RemoveBracketedOrLabelledValue(string title, string value)
        {
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(value))
            {
                return title;
            }

            var escaped = Regex.Escape(value.Trim());
            var result = Regex.Replace(title, $@"\s*[\[\(]\s*{escaped}\s*[\]\)]\s*", " ", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, $@"\b(?:narrator|narrated\s+by|read\s+by)\s*[:\-]?\s*{escaped}\b", " ", RegexOptions.IgnoreCase);

            return result;
        }

        private static string RemoveDisplayedToken(string title, string token)
        {
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(token))
            {
                return title;
            }

            var result = RemoveBracketedOrLabelledValue(title, token);
            var escaped = Regex.Escape(token.Trim());

            return Regex.Replace(
                result,
                $@"(^|[\s_\-\(\[]){escaped}($|[\s_\-\)\]])",
                match => $"{match.Groups[1].Value}{match.Groups[2].Value}",
                RegexOptions.IgnoreCase);
        }

        private static string NormalizeDisplayTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return title;
            }

            var result = Regex.Replace(title, @"\s+", " ");
            result = Regex.Replace(result, @"\s*[\[\(]\s*[\]\)]\s*", " ");
            result = Regex.Replace(result, @"(?:\s*[-_:]\s*){2,}", " - ");
            result = Regex.Replace(result, @"^[\s\-_:,]+|[\s\-_:,]+$", "");

            return result.Trim();
        }

        private static string GetFormatType(Quality quality, ReleaseInfo release, TorrentInfo torrent)
        {
            // Prefer explicit file type from indexers that provide it (e.g., MAM).
            // This is the most direct indicator of "ebook vs audiobook" and avoids category mismatches.
            var fileType = torrent?.FileType ?? release?.Container;
            if (!string.IsNullOrWhiteSpace(fileType))
            {
                if (EbookHintRegex.IsMatch(fileType))
                {
                    return "ebook";
                }

                if (AudiobookHintRegex.IsMatch(fileType))
                {
                    return "audiobook";
                }
            }

            // MyAnonaMouse provides explicit format classification in the JSON API
            if (torrent != null)
            {
                if (torrent.MediaType.HasValue)
                {
                    if (torrent.MediaType.Value == 1)
                    {
                        return "audiobook";
                    }

                    if (torrent.MediaType.Value == 2)
                    {
                        return "ebook";
                    }
                }

                if (torrent.MainCategory.HasValue)
                {
                    if (torrent.MainCategory.Value == 13)
                    {
                        return "audiobook";
                    }

                    if (torrent.MainCategory.Value == 14)
                    {
                        return "ebook";
                    }
                }

                var categoryName = torrent.CategoryName;
                if (!string.IsNullOrWhiteSpace(categoryName))
                {
                    if (categoryName.StartsWith("Audiobooks", StringComparison.OrdinalIgnoreCase))
                    {
                        return "audiobook";
                    }

                    if (categoryName.StartsWith("Ebooks", StringComparison.OrdinalIgnoreCase))
                    {
                        return "ebook";
                    }
                }
            }

            // Quality (container) is a strong signal and should override categories when explicit.
            // Some Newznab indexers categorize audiobooks under general Books categories, but the quality parser can still
            // detect MP3/M4B/FLAC/etc from the title. Prefer that so audiobooks don't land in the ebook bucket.
            if (quality != null)
            {
                if (IsAudiobookFormat(quality))
                {
                    return "audiobook";
                }

                // Treat explicit ebook containers as ebook, but do not force Unknown into ebook until after categories.
                if (quality != Quality.Unknown && IsEbookFormat(quality))
                {
                    return "ebook";
                }
            }

            // Indexer categories are the most authoritative source for format type when available
            // (e.g., Newznab/Torznab: 3030=Audiobook, 7000-7999=Books/Ebook/Comics/Magazines).
            var categories = release?.Categories;
            if (categories != null && categories.Count > 0)
            {
                // Prefer audiobook when both are present
                if (categories.Any(c => c == 3030 || (c >= 3000 && c < 4000)))
                {
                    return "audiobook";
                }

                if (categories.Any(c => c >= 7000 && c < 8000))
                {
                    return "ebook";
                }
            }

            if (quality != null)
            {
                // "Unknown Text" should default to ebook for UI grouping unless other signals indicate audio.
                if (quality == Quality.Unknown)
                {
                    return "ebook";
                }

                if (IsEbookFormat(quality))
                {
                    return "ebook";
                }

                if (IsAudiobookFormat(quality))
                {
                    return "audiobook";
                }
            }

            // Indexer-provided audiobook hints (MAM and other torrent sources)
            if (torrent != null && (torrent.IsGraphicAudio || !string.IsNullOrWhiteSpace(torrent.Narrator) || !string.IsNullOrWhiteSpace(torrent.Duration)))
            {
                return "audiobook";
            }

            // Fallback to title inspection
            var title = release?.Title ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(title))
            {
                if (EbookHintRegex.IsMatch(title))
                {
                    return "ebook";
                }

                if (AudiobookHintRegex.IsMatch(title))
                {
                    return "audiobook";
                }
            }

            return "audiobook";
        }

        private static bool IsAudiobookFormat(Quality quality)
        {
            return quality.Id == Quality.MP3.Id ||
                   quality.Id == Quality.M4B.Id ||
                   quality.Id == Quality.FLAC.Id ||
                   quality.Id == Quality.UnknownAudio.Id;
        }

        private static bool IsEbookFormat(Quality quality)
        {
            return quality.Id == Quality.Unknown.Id ||
                   quality.Id == Quality.PDF.Id ||
                   quality.Id == Quality.MOBI.Id ||
                   quality.Id == Quality.EPUB.Id ||
                   quality.Id == Quality.AZW3.Id;
        }
    }
}
