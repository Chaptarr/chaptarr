using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using NzbDrone.Core.Books;
using NzbDrone.Core.MetadataSource.BookInfo.V5Resource;

namespace NzbDrone.Core.MetadataSource.BookInfo.V5
{
    // Main response wrapper
    public class V5AuthorResponse
    {
        [JsonIgnore]
        public string ETag { get; set; }
        public V5AuthorData Author { get; set; }
        public V5Summary Summary { get; set; }
        public V5Pagination Pagination { get; set; }
        public List<V5Book> Books { get; set; } = new List<V5Book>();
        public List<V5Series> Series { get; set; } = new List<V5Series>();
    }

	    // Author data model
	    public class V5AuthorData
	    {
	        public string Id { get; set; }
	        public string Name { get; set; }
	        public string SortName { get; set; }
	        public string Slug { get; set; }
	        public List<string> Pseudonyms { get; set; } = new List<string>();

	        // Provider IDs - all as strings per API
	        public string GoodreadsAuthorId { get; set; }
	        public string HardcoverAuthorId { get; set; }
	        public string AudnexusAuthorId { get; set; }
	        public string OpenLibraryAuthorId { get; set; }
	        public string GoogleBooksAuthorId { get; set; }

	        // Complete provider ID lists (canonical, provider-prefixed) used for robust identity matching.
	        [JsonProperty("providerIds")]
	        public Dictionary<string, object> LegacyProviderIds { get; set; } = new Dictionary<string, object>();

	        [JsonProperty("providerIdsAll")]
	        public Dictionary<string, List<string>> ProviderIdsAllCamel { get; set; } = new Dictionary<string, List<string>>();

	        // Complete provider ID lists (canonical, provider-prefixed) used for robust identity matching.
	        // This is emitted by the metadata server as `provider_ids_all`.
	        [JsonProperty("provider_ids_all")]
	        public Dictionary<string, List<string>> ProviderIdsAllSnake { get; set; } = new Dictionary<string, List<string>>();

	        [JsonIgnore]
	        public Dictionary<string, List<string>> ProviderIds => V5ProviderIdMaps.ToListMap(ProviderIdsAllCamel, ProviderIdsAllSnake, LegacyProviderIds);

	        [JsonIgnore]
	        public Dictionary<string, List<string>> ProviderIdsAll => ProviderIds;

	        // Standard identifiers
	        public string Isni { get; set; }
	        public string Viaf { get; set; }

        // Metadata
        public string Bio { get; set; }
        public List<V5AuthorPhoto> Photos { get; set; } = new List<V5AuthorPhoto>();
        public ProviderUrlMap ProviderUrls { get; set; } = new ProviderUrlMap();
        public string LastUpdated { get; set; }
        public decimal RatingAverage { get; set; }
        public int RatingCount { get; set; }

        // Helper methods for ID conversion
        public long? GetGoodreadsIdAsLong() =>
            long.TryParse(GoodreadsAuthorId, out var id) ? id : null;
    }

    // Summary statistics
    public class V5Summary
    {
        public int TotalBooks { get; set; }
        public int TotalSeries { get; set; }
        public V5FormatsAvailable FormatsAvailable { get; set; }
        public string PrimaryFormat { get; set; }
        public decimal AverageRating { get; set; }
        public List<string> PopularSeries { get; set; } = new List<string>();
    }

    public class V5FormatsAvailable
    {
        public int Audiobook { get; set; }
        public int Ebook { get; set; }
        public int Physical { get; set; }
    }

    // Pagination info
    public class V5Pagination
    {
        public int Page { get; set; }
        public int PerPage { get; set; }
        public int TotalPages { get; set; }
        public int TotalCount { get; set; }
        public bool HasNext { get; set; }
        public bool HasPrevious { get; set; }
    }

    // Book model
    public class V5Book
    {
        // Core identity
        public string Id { get; set; }
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public string OriginalTitle { get; set; }
        public string Slug { get; set; }
        [JsonProperty("base_book_id")]
        public string BaseBookId { get; set; }

        // Legacy single provider ID per provider as emitted by the metadata server as `provider_ids`.
        // These values are canonical provider-prefixed IDs (e.g., hc:123, gr:456, az:B00ABC123).
        [JsonProperty("provider_ids")]
        public Dictionary<string, string> LegacyProviderIds { get; set; } = new Dictionary<string, string>();

        // Complete provider ID lists (canonical, provider-prefixed) used for robust identity matching.
        [JsonProperty("providerIds")]
        public Dictionary<string, object> LegacyProviderIdsCamel { get; set; } = new Dictionary<string, object>();

        [JsonProperty("providerIdsAll")]
        public Dictionary<string, List<string>> ProviderIdsAllCamel { get; set; } = new Dictionary<string, List<string>>();

        // Complete provider ID lists (canonical, provider-prefixed) used for robust identity matching.
        // This is emitted by the metadata server as `provider_ids_all`.
        [JsonProperty("provider_ids_all")]
        public Dictionary<string, List<string>> ProviderIdsAllSnake { get; set; } = new Dictionary<string, List<string>>();

        [JsonIgnore]
        public Dictionary<string, List<string>> ProviderIds => V5ProviderIdMaps.ToListMap(ProviderIdsAllCamel, ProviderIdsAllSnake, LegacyProviderIdsCamel, LegacyProviderIds);

        [JsonIgnore]
        public Dictionary<string, List<string>> ProviderIdsAll => ProviderIds;

        // All provider IDs as strings
        [JsonProperty("goodreadsBookId")]
        public string GoodreadsBookId { get; set; }
        [JsonProperty("goodreadsWorkId")]
        public string GoodreadsWorkId { get; set; }
        [JsonProperty("hardcoverBookId")]
        public string HardcoverBookId { get; set; }
        [JsonProperty("hardcoverWorkId")]
        public string LegacyHardcoverWorkId { get; set; }
        [JsonProperty("isbn10")]
        public string Isbn10 { get; set; }
        [JsonProperty("isbn13")]
        public string Isbn13 { get; set; }
        public string Asin { get; set; }
        [JsonProperty("audibleAsin")]
        public string AudibleAsin { get; set; }
        [JsonProperty("openLibraryEditionId")]
        public string OpenLibraryEditionId { get; set; }
        [JsonProperty("openLibraryWorkId")]
        public string OpenLibraryWorkId { get; set; }
        [JsonProperty("googleBooksId")]
        public string GoogleBooksId { get; set; }

        // Metadata
        public string Description { get; set; }
        public string Publisher { get; set; }
        [JsonConverter(typeof(SafeDateTimeConverter))]
        public DateTime? PublicationDate { get; set; }
        public int? PublicationYear { get; set; }
        public string LanguageCode { get; set; }
        public string LanguageName { get; set; }

        // Series info
        public long? SeriesId { get; set; }
        public string SeriesName { get; set; }
        public string SeriesPosition { get; set; }

        // External links
        public Dictionary<string, string> Links { get; set; } = new Dictionary<string, string>();

        // Ratings
        [JsonProperty("ratingAverage")]
        public decimal RatingAverage { get; set; }
        [JsonProperty("ratingCount")]
        public int RatingCount { get; set; }
        [JsonProperty("reviewCount")]
        public int ReviewCount { get; set; }

        // Physical attributes
        public int? PageCount { get; set; }
        public int? DurationSeconds { get; set; }
        public string CoverUrl { get; set; }

        // Arrays
        public List<string> Genres { get; set; } = new List<string>();
        public List<string> Moods { get; set; } = new List<string>();
        public Dictionary<string, List<string>> CachedTags { get; set; } = new Dictionary<string, List<string>>();

        // Provider URLs
        public ProviderUrlMap ProviderUrls { get; set; } = new ProviderUrlMap();

        // Format availability
        public bool HasAudiobook { get; set; }
        public bool HasEbook { get; set; }
        public bool HasPhysical { get; set; }

        // Omnibus/Collection flag - true for box sets, anthologies, complete series collections
        [JsonProperty("isOmnibus")]
        public bool IsOmnibus { get; set; }

        // Editions
        public List<V5Edition> Editions { get; set; } = new List<V5Edition>();

        // Helper methods
        public long? GetGoodreadsBookIdAsLong() =>
            long.TryParse(GoodreadsBookId, out var id) ? id : null;

        public long? GetGoodreadsWorkIdAsLong() =>
            long.TryParse(GoodreadsWorkId, out var id) ? id : null;
    }

    // Edition model
    public class V5Edition
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        // Provider edition IDs
        [JsonProperty("goodreadsEditionId")]
        public string GoodreadsEditionId { get; set; }
        [JsonProperty("hardcoverEditionId")]
        public string HardcoverEditionId { get; set; }
        [JsonProperty("openLibraryEditionId")]
        public string OpenLibraryEditionId { get; set; }
        [JsonProperty("googleBooksEditionId")]
        public string GoogleBooksEditionId { get; set; }

        [JsonProperty("providerIds")]
        public Dictionary<string, object> LegacyProviderIds { get; set; } = new Dictionary<string, object>();

        [JsonProperty("providerIdsAll")]
        public Dictionary<string, List<string>> ProviderIdsAllCamel { get; set; } = new Dictionary<string, List<string>>();

        [JsonProperty("provider_ids_all")]
        public Dictionary<string, List<string>> ProviderIdsAllSnake { get; set; } = new Dictionary<string, List<string>>();

        [JsonIgnore]
        public Dictionary<string, List<string>> ProviderIds => V5ProviderIdMaps.ToListMap(ProviderIdsAllCamel, ProviderIdsAllSnake, LegacyProviderIds);

        // Format identification
        [JsonProperty("readingFormatId")]
        public int ReadingFormatId { get; set; } // 1=physical, 2=audio, 3=ebook
        [JsonProperty("format")]
        public string Format { get; set; }
        [JsonProperty("formatType")]
        public string LegacyFormatType { get; set; }
        [JsonProperty("editionFormat")]
        public string EditionFormat { get; set; } // Specific format like "Paperback", "Hardcover", "Kindle Edition"
        [JsonProperty("editionInfo")]
        public string EditionInfo { get; set; }

        // Edition-specific metadata
        [JsonProperty("title")]
        public string Title { get; set; }
        [JsonProperty("subtitle")]
        public string Subtitle { get; set; }
        [JsonProperty("language")]
        public string Language { get; set; }
        // V5 author payloads commonly use ISO-639-1 two-letter codes in `languageCode` (e.g. "en", "es").
        // Keep this separate from `language` so we can support both formats and normalize during mapping.
        [JsonProperty("languageCode")]
        public string LanguageCode { get; set; }
        [JsonProperty("isbn10")]
        public string Isbn10 { get; set; }
        [JsonProperty("isbn13")]
        public string Isbn13 { get; set; }
        [JsonProperty("asin")]
        public string Asin { get; set; }
        [JsonProperty("asins")]
        public List<string> Asins { get; set; } = new List<string>();
        [JsonProperty("description")]
        public string Description { get; set; }

        // Audiobook specific
        [JsonProperty("narratorNames")]
        public List<string> NarratorNames { get; set; } = new List<string>();

        // Narrator credits (preferred over NarratorNames when present)
        [JsonProperty("narrators")]
        public List<V5NarratorCredit> Narrators { get; set; } = new List<V5NarratorCredit>();
        [JsonProperty("durationSeconds")]
        public int? DurationSeconds { get; set; }
        [JsonProperty("durationMinutes")]
        public int? LegacyDurationMinutes { get; set; }
        [JsonProperty("chapterCount")]
        public int? ChapterCount { get; set; }
        [JsonProperty("hasChapters")]
        public bool HasChapters { get; set; }
        [JsonProperty("chapters")]
        public List<V5EditionChapter> Chapters { get; set; } = new List<V5EditionChapter>();
        [JsonProperty("audioProductionType")]
        public string AudioProductionType { get; set; }

        // Publishing info
        [JsonProperty("publisher")]
        public string Publisher { get; set; }
        [JsonProperty("publicationDate")]
        [JsonConverter(typeof(SafeDateTimeConverter))]
        public DateTime? PublicationDate { get; set; }
        [JsonProperty("pageCount")]
        public int? PageCount { get; set; }
        [JsonProperty("pages")]
        public int? LegacyPages { get; set; }
        [JsonProperty("coverUrl")]
        public string CoverUrl { get; set; }

        // Ratings (edition-specific if available)
        [JsonProperty("ratingAverage")]
        public decimal RatingAverage { get; set; }
        [JsonProperty("rating")]
        public decimal? LegacyRating { get; set; }
        [JsonProperty("ratingCount")]
        public int RatingCount { get; set; }
        [JsonProperty("ratingsCount")]
        public int? LegacyRatingsCount { get; set; }
        [JsonProperty("reviewCount")]
        public int? ReviewCount { get; set; }

        public ProviderUrlMap ProviderUrls { get; set; } = new ProviderUrlMap();

        // Helper to determine format type
        public bool IsAudiobook => ReadingFormatId == 2;
        public bool IsEbook => ReadingFormatId == 3;
        public bool IsPhysical => ReadingFormatId == 1;
    }

    public class V5EditionChapter
    {
        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("startOffsetMs")]
        public int StartOffsetMs { get; set; }
        [JsonProperty("start_offset_ms")]
        private int StartOffsetMsSnake { set => StartOffsetMs = value; }

        [JsonProperty("startOffsetSec")]
        public int StartOffsetSec { get; set; }
        [JsonProperty("start_offset_sec")]
        private int StartOffsetSecSnake { set => StartOffsetSec = value; }

        [JsonProperty("lengthMs")]
        public int LengthMs { get; set; }
        [JsonProperty("length_ms")]
        private int LengthMsSnake { set => LengthMs = value; }
    }

    public class V5NarratorCredit
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        // Provider IDs (canonical provider-prefixed strings: gr:44554, hc:98234)
        [JsonProperty("goodreadsNarratorId")]
        public string GoodreadsNarratorId { get; set; }

        [JsonProperty("hardcoverNarratorId")]
        public string HardcoverNarratorId { get; set; }

        // Credit ordering (0-based). If omitted, consumer may infer from array order.
        [JsonProperty("order")]
        public int? Order { get; set; }

        // Optional explicit primary flag (consumer may treat order==0 as primary).
        [JsonProperty("isPrimary")]
        public bool? IsPrimary { get; set; }

        [JsonProperty("role")]
        public string Role { get; set; }
    }

    // Series model
    public class V5Series
    {
        public string Id { get; set; }
        public string Name { get; set; }

        // Provider IDs
        public string GoodreadsSeriesId { get; set; }
        public string HardcoverSeriesId { get; set; }
        public string OpenLibrarySeriesId { get; set; }
        public string AmazonSeriesAsin { get; set; }

        public string Description { get; set; }
        public string SeriesType { get; set; } // main/spin-off/companion
        public int TotalBooks { get; set; }
        public int PrimaryBooks { get; set; }
        public bool Numbered { get; set; }

        // Books in this series
        public List<V5SeriesBook> Books { get; set; } = new List<V5SeriesBook>();

        // External links
        public Dictionary<string, string> Links { get; set; } = new Dictionary<string, string>();

        public ProviderUrlMap ProviderUrls { get; set; } = new ProviderUrlMap();

        // Helper method
        public long? GetGoodreadsSeriesIdAsLong() =>
            long.TryParse(GoodreadsSeriesId, out var id) ? id : null;
    }

    // Series book entry
    public class V5SeriesBook
    {
        // Provider ID from API - used ONLY for handshaking/matching, never for local operations
        public string BookId { get; set; }
        public string Title { get; set; }
        public string Position { get; set; }
        public string CoverUrl { get; set; }

        // Authoritative primary-slot flag from Goodreads' getWorksForSeries endpoint.
        // Nullable: null when the V5 API didn't include the field for this book (e.g. pre-2026-06-02
        // stale rows or non-Goodreads-sourced series). Consumers should default null to true to
        // preserve current behavior for not-yet-refreshed data.
        [JsonProperty("isPrimary")]
        public bool? IsPrimary { get; set; }
    }

    // Author photo model for multiple photos support
    public class V5AuthorPhoto
    {
        public int Id { get; set; }
        public string Url { get; set; }
        public string ThumbnailUrl { get; set; }
        public string Provider { get; set; }
        public string ProviderId { get; set; }
        public bool IsPrimary { get; set; }
        public V5PhotoDimensions Dimensions { get; set; }
        public int? FileSize { get; set; }
        public string MimeType { get; set; }
        public DateTime AddedAt { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    public class V5PhotoDimensions
    {
        public int? Width { get; set; }
        public int? Height { get; set; }
    }

    internal static class V5ProviderIdMaps
    {
        public static Dictionary<string, List<string>> ToListMap(
            Dictionary<string, List<string>> camelProviderIdsAll,
            Dictionary<string, List<string>> snakeProviderIdsAll,
            Dictionary<string, object> legacyProviderIds,
            Dictionary<string, string> legacyStringProviderIds = null)
        {
            var providerIdsAll = HasAnyValue(camelProviderIdsAll) ? camelProviderIdsAll : snakeProviderIdsAll;
            var legacy = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            if (legacyStringProviderIds != null)
            {
                foreach (var providerId in legacyStringProviderIds)
                {
                    legacy[providerId.Key] = providerId.Value;
                }
            }

            if (legacyProviderIds != null)
            {
                foreach (var providerId in legacyProviderIds)
                {
                    legacy[providerId.Key] = providerId.Value;
                }
            }

            return ProviderIdMapCoercion.ToListMap(providerIdsAll, legacy);
        }

        private static bool HasAnyValue(Dictionary<string, List<string>> providerIds)
        {
            return providerIds?.Values.Any(values => values?.Any(value => !string.IsNullOrWhiteSpace(value)) == true) == true;
        }
    }
}
