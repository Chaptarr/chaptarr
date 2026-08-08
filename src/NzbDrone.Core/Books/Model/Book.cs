using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Serialization;
using Equ;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MetadataSource;

namespace NzbDrone.Core.Books
{
    public enum BookMediaType
    {
        Audiobook = 0,
        Ebook = 1
    }

    [DebuggerDisplay("{GetType().FullName} ID = {Id}[{Title}]")]
    public class Book : Entity<Book>
    {
        public Book()
        {
            Links = new List<Links>();
            Genres = new List<string>();
            RelatedBooks = new List<int>();
            Ratings = new Ratings();
            Author = new Author();
            AddOptions = new AddBookOptions();
            NarratorEntity = new Narrator();
            ProviderUrls = new ProviderUrlMap();
            Images = new List<MediaCover.MediaCover>();
        }

        // These correspond to columns in the Books table
        // These are metadata entries
        [MemberwiseEqualityIgnore]
        public string ForeignEditionId { get; set; }
        public string TitleSlug { get; set; }
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public string OriginalTitle { get; set; }
        public string Overview { get; set; }
        public DateTime? ReleaseDate { get; set; }
        public List<Links> Links { get; set; }
        public List<string> Genres { get; set; }
        public List<int> RelatedBooks { get; set; }
        public Ratings Ratings { get; set; }
        public DateTime? LastSearchTime { get; set; }
        public List<MediaCover.MediaCover> Images { get; set; }

        // Provider IDs
        public string GoodreadsBookId { get; set; }
        public string GoodreadsWorkId { get; set; }
        public string HardcoverBookId { get; set; }
        public string ISBN10 { get; set; }
        public string ISBN13 { get; set; }
        public string OpenLibraryEditionId { get; set; }
        public string OpenLibraryWorkId { get; set; }
        public string GoogleBooksId { get; set; }
        public string ASIN { get; set; }
        public string AudibleASIN { get; set; }

        // Book grouping
        public string BaseBookId { get; set; }
        public string UnitKeyHash { get; set; }

        // Additional metadata
        public string LanguageCode { get; set; }
        public string LanguageName { get; set; }
        public int? PublicationYear { get; set; }
        public string Publisher { get; set; }
        public int? PageCount { get; set; }

        // Series information (denormalized)
        public int? SeriesId { get; set; }
        public string SeriesName { get; set; }
        public string SeriesPosition { get; set; }

        // GraphicAudio classification fields
        public bool IsGraphicAudio { get; set; }
        public string AudioProductionType { get; set; }

        // Omnibus/Collection flag - true for box sets, anthologies, complete series collections
        public bool IsOmnibus { get; set; }

        // Narrator information
        public int? NarratorId { get; set; }
        public string Narrator { get; set; }

        // Instance management for multiple copies
        // Legacy local narrator-monitoring field. Narrator identity is no longer a public/API contract.
        public int? WantedNarratorId { get; set; }

        // Duration tracking
        public int? DurationMinutes { get; set; }  // Total duration in minutes for this physical copy

        // Media type for dual-instance architecture
        public BookMediaType MediaType { get; set; } = BookMediaType.Audiobook;

        // These are Chaptarr generated/config
        public string CleanTitle { get; set; }
        // Legacy Monitored field - kept for database compatibility but not used
        // Always set to false for new books. Use AudiobookMonitored/EbookMonitored instead
        [MemberwiseEqualityIgnore]
        public bool Monitored { get; set; }
        public bool AudiobookMonitored { get; set; }
        public bool EbookMonitored { get; set; }
        public bool AnyEditionOk { get; set; }
        public DateTime? LastInfoSync { get; set; }
        public DateTime Added { get; set; }
        [MemberwiseEqualityIgnore]
        public AddBookOptions AddOptions { get; set; }

        // New metadata fields
        public ProviderUrlMap ProviderUrls { get; set; }
        [MemberwiseEqualityIgnore]
        public DateTime? LastUpdated { get; set; }

        // These are dynamically queried from other tables
        [MemberwiseEqualityIgnore]
        [JsonIgnore]
        public LazyLoaded<Author> LazyAuthor { get; set; }
        [MemberwiseEqualityIgnore]
        [JsonIgnore]
        public LazyLoaded<List<Edition>> LazyEditions { get; set; }
        [MemberwiseEqualityIgnore]
        [JsonIgnore]
        public LazyLoaded<List<BookFile>> LazyBookFiles { get; set; }
        [MemberwiseEqualityIgnore]
        [JsonIgnore]
        public LazyLoaded<List<SeriesBookLink>> LazySeriesLinks { get; set; }
        private int _authorId;

        [MemberwiseEqualityIgnore]
        public Author Author
        {
            get => LazyAuthor?.Value;
            set
            {
                LazyAuthor = value;
                _authorId = value?.Id ?? 0;
            }
        }
        [MemberwiseEqualityIgnore]
        public List<Edition> Editions
        {
            get => LazyEditions?.Value;
            set => LazyEditions = value;
        }
        [MemberwiseEqualityIgnore]
        public List<BookFile> BookFiles
        {
            get => LazyBookFiles?.Value;
            set => LazyBookFiles = value;
        }
        [MemberwiseEqualityIgnore]
        public List<SeriesBookLink> SeriesLinks
        {
            get => LazySeriesLinks?.Value;
            set => LazySeriesLinks = value;
        }
        [MemberwiseEqualityIgnore]
        public Narrator NarratorEntity { get; set; }
        [MemberwiseEqualityIgnore]
        public Narrator WantedNarrator { get; set; }

        // Optional in-memory provider ID sets from upstream metadata payloads (not persisted).
        // Used for identity matching (provider ID intersection) without relying on local row IDs or titles.
        [MemberwiseEqualityIgnore]
        [JsonIgnore]
        public HashSet<string> RemoteProviderIds { get; set; }

        //compatibility properties with old version of Book
        [MemberwiseEqualityIgnore]
        [JsonIgnore]
        public int AuthorId
        {
            get
            {
                if (LazyAuthor?.IsLoaded == true && LazyAuthor.Value != null)
                {
                    return LazyAuthor.Value.Id;
                }

                return _authorId;
            }
            set
            {
                _authorId = value;

                if (LazyAuthor?.IsLoaded == true && LazyAuthor.Value != null)
                {
                    LazyAuthor.Value.Id = value;
                }
            }
        }

        [MemberwiseEqualityIgnore]
        [JsonIgnore]
        public string NarratorName
        {
            get
            {
                return NarratorEntity?.Name ?? Narrator;
            }

            set
            {
                if (NarratorEntity != null)
                {
                    NarratorEntity.Name = value;
                }
                else
                {
                    Narrator = value;
                }
            }
        }

        // Computed properties for instance management
        [MemberwiseEqualityIgnore]
        [JsonIgnore]
        public bool HasFiles => BookFiles?.Any() ?? false;

        public override string ToString()
        {
            return string.Format("[{0}][{1}]", Id, Title.NullSafe());
        }

	        public override void UseMetadataFrom(Book other)
	        {
		            ForeignEditionId = CleanProviderIdForCopy(other.ForeignEditionId, ForeignEditionId);
	            if (!string.IsNullOrWhiteSpace(other.TitleSlug))
	            {
	                TitleSlug = other.TitleSlug;
	            }

	            // Non-destructive metadata refresh: never overwrite a non-empty Title/ReleaseDate with null/empty.
	            if (!string.IsNullOrWhiteSpace(other.Title))
	            {
	                Title = other.Title;
	            }

	            if (other.ReleaseDate.HasValue)
	            {
	                ReleaseDate = other.ReleaseDate;
	            }
	            Subtitle = other.Subtitle;
	            OriginalTitle = other.OriginalTitle;
	            Overview = string.IsNullOrWhiteSpace(other.Overview) ? Overview : other.Overview;
	            Links = other.Links;
	            Genres = other.Genres;
	            RelatedBooks = other.RelatedBooks;
	            Ratings = other.Ratings;
	            // Images: prefer remote if it has valid images; keep local otherwise
	            if (other.Images?.Any(i => !string.IsNullOrWhiteSpace(i?.Url)) == true)
	            {
	                Images = other.Images;
	            }
	            CleanTitle = other.CleanTitle;
            IsGraphicAudio = other.IsGraphicAudio;
            AudioProductionType = other.AudioProductionType;
            IsOmnibus = other.IsOmnibus;
            NarratorId = other.NarratorId;
            Narrator = other.Narrator;
            SeriesLinks = other.SeriesLinks;

            // Additional metadata
            LanguageCode = other.LanguageCode ?? LanguageCode;
            LanguageName = other.LanguageName ?? LanguageName;
            PublicationYear = other.PublicationYear ?? PublicationYear;
            Publisher = other.Publisher ?? Publisher;
            SeriesId = other.SeriesId ?? SeriesId;
            SeriesName = other.SeriesName ?? SeriesName;
            SeriesPosition = other.SeriesPosition ?? SeriesPosition;

		            // Provider IDs and book identifiers.
		            // NOTE: ISBN10/ISBN13 are metadata identifiers, NOT provider IDs, and are never used for remote matching.
		            // IMPORTANT: These fields participate in equality and refresh change detection.
		            // If we refuse to overwrite a populated local value, refresh can enter a non-converging state
		            // where a book is always classified as "Updating" but never becomes "Up to date".
		            // Treat upstream as authoritative so refresh converges in one pass.
		            BaseBookId = CleanProviderIdForCopy(other.BaseBookId, BaseBookId);
		            HardcoverBookId = CleanProviderIdForCopy(other.HardcoverBookId, HardcoverBookId);
		            GoodreadsBookId = CleanProviderIdForCopy(other.GoodreadsBookId, GoodreadsBookId);
		            GoodreadsWorkId = CleanProviderIdForCopy(other.GoodreadsWorkId, GoodreadsWorkId);
		            ISBN10 = other.ISBN10;
		            ISBN13 = other.ISBN13;
		            OpenLibraryEditionId = CleanProviderIdForCopy(other.OpenLibraryEditionId, OpenLibraryEditionId);
		            OpenLibraryWorkId = CleanProviderIdForCopy(other.OpenLibraryWorkId, OpenLibraryWorkId);
		            GoogleBooksId = CleanProviderIdForCopy(other.GoogleBooksId, GoogleBooksId);
		            ASIN = CleanProviderIdForCopy(other.ASIN, ASIN);
		            AudibleASIN = CleanProviderIdForCopy(other.AudibleASIN, AudibleASIN);
		            RemoteProviderIds = CloneProviderIds(other.RemoteProviderIds);

	            // New metadata fields (upstream authoritative; apply even when empty to ensure convergence)
	            ProviderUrls = other.ProviderUrls == null ? new ProviderUrlMap() : new ProviderUrlMap(other.ProviderUrls);
	            LastUpdated = other.LastUpdated ?? LastUpdated;
	        }

        private static HashSet<string> CloneProviderIds(HashSet<string> source)
        {
            return CleanProviderIds(source);
        }

        private static HashSet<string> CleanProviderIds(IEnumerable<string> source)
        {
            var values = source?
                .Where(id => id.IsNotNullOrWhiteSpace() && !ProviderIdHelper.ContainsProviderIdArtifact(id))
                .Select(id => id.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return values?.Count > 0 ? values : null;
        }

        private static string CleanProviderIdForCopy(string incoming, string current)
        {
            if (ProviderIdHelper.ContainsProviderIdArtifact(incoming))
            {
                return ProviderIdHelper.ContainsProviderIdArtifact(current) ? null : current;
            }

            if (incoming.IsNullOrWhiteSpace() && ProviderIdHelper.ContainsProviderIdArtifact(current))
            {
                return null;
            }

            return incoming;
        }

        public override void UseDbFieldsFrom(Book other)
        {
            Id = other.Id;
            AuthorId = other.AuthorId;
            AudiobookMonitored = other.AudiobookMonitored;
            EbookMonitored = other.EbookMonitored;
            AnyEditionOk = other.AnyEditionOk;
            LastInfoSync = other.LastInfoSync;
            LastSearchTime = other.LastSearchTime;
            Added = other.Added;
            AddOptions = other.AddOptions;
            IsGraphicAudio = other.IsGraphicAudio;
            AudioProductionType = other.AudioProductionType;
            NarratorId = other.NarratorId;
            Narrator = other.Narrator;
            PageCount = other.PageCount; // CRITICAL: Copy PageCount to prevent NULL constraint violations
            TitleSlug = other.TitleSlug;
            UnitKeyHash = other.UnitKeyHash;
            DurationMinutes = other.DurationMinutes;
            MediaType = other.MediaType;
            RelatedBooks = other.RelatedBooks;
        }

        public override void ApplyChanges(Book other)
        {
            ForeignEditionId = other.ForeignEditionId;
            AddOptions = other.AddOptions;
            // Monitored field removed - use media-type specific fields
            AudiobookMonitored = other.AudiobookMonitored;
            EbookMonitored = other.EbookMonitored;
            AnyEditionOk = other.AnyEditionOk;
        }

        public bool IsMonitoredForMediaType(string mediaType)
        {
            if (string.Equals(mediaType, "audiobook", StringComparison.OrdinalIgnoreCase))
            {
                return AudiobookMonitored;
            }

            if (string.Equals(mediaType, "ebook", StringComparison.OrdinalIgnoreCase))
            {
                return EbookMonitored;
            }

            return false;
        }

        public void SetMonitoredForMediaType(string mediaType, bool monitored)
        {
            if (string.Equals(mediaType, "audiobook", StringComparison.OrdinalIgnoreCase))
            {
                AudiobookMonitored = monitored;
                if (MediaType == BookMediaType.Audiobook)
                {
                    EbookMonitored = false; // Enforce invariant
                }

                return;
            }

            if (string.Equals(mediaType, "ebook", StringComparison.OrdinalIgnoreCase))
            {
                EbookMonitored = monitored;
                if (MediaType == BookMediaType.Ebook)
                {
                    AudiobookMonitored = false; // Enforce invariant
                }

                return;
            }

            throw new ArgumentException($"Unknown media type '{mediaType}'", nameof(mediaType));
        }
        // Helper methods for MediaType-aware monitoring
        public bool IsMonitored()
        {
            return MediaType == BookMediaType.Audiobook
                ? AudiobookMonitored
                : EbookMonitored;
        }

        public void SetMonitored(bool value)
        {
            if (MediaType == BookMediaType.Audiobook)
            {
                AudiobookMonitored = value;
                EbookMonitored = false; // Enforce invariant
            }
            else
            {
                EbookMonitored = value;
                AudiobookMonitored = false; // Enforce invariant
            }
        }

        // Helper to check monitoring for specific media type
        public bool IsMonitoredForMediaType(BookMediaType mediaType)
        {
            return mediaType == BookMediaType.Audiobook
                ? AudiobookMonitored
                : EbookMonitored;
        }

        // Helper to set monitoring for specific media type
        public void SetMonitoredForMediaType(BookMediaType mediaType, bool value)
        {
            if (mediaType == BookMediaType.Audiobook)
            {
                AudiobookMonitored = value;
                if (MediaType == BookMediaType.Audiobook)
                {
                    EbookMonitored = false; // Enforce invariant
                }
            }
            else
            {
                EbookMonitored = value;
                if (MediaType == BookMediaType.Ebook)
                {
                    AudiobookMonitored = false; // Enforce invariant
                }
            }
        }
    }
}
