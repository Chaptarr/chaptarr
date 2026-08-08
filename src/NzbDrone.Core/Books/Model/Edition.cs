using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Equ;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MetadataSource;

namespace NzbDrone.Core.Books
{
    public class Edition : Entity<Edition>
    {
        public Edition()
        {
            Overview = string.Empty;
            Images = new List<MediaCover.MediaCover>();
            Links = new List<Links>();
            Ratings = new Ratings();
            Chapters = new List<EditionChapter>();
            NarratorNames = new List<string>();
            NarratorCredits = new List<NarratorCredit>();
            ProviderUrls = new ProviderUrlMap();
        }

        // These correspond to columns in the Books table
        // These are metadata entries
        public int BookId { get; set; }
        public string ForeignEditionId { get; set; }
        public string TitleSlug { get; set; }
        public string Isbn13 { get; set; }
        public string Isbn10 { get; set; }
        public string Asin { get; set; }
        public List<string> Asins { get; set; } = new List<string>(); // All ASINs including regional variants
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public string MatchingTitle { get; set; } // Pre-normalized for FTS: lowercase, possessives merged, diacritics stripped
        public string Language { get; set; }
        public string Overview { get; set; }
        public string Format { get; set; }
        public bool IsEbook { get; set; }
        public string Disambiguation { get; set; }
        public string Publisher { get; set; }
        public int PageCount { get; set; }
        public DateTime? ReleaseDate { get; set; }
        public List<MediaCover.MediaCover> Images { get; set; }
        public List<Links> Links { get; set; }
        public Ratings Ratings { get; set; }
        public int? ReviewCount { get; set; }

        // Provider IDs
        public long? GoodreadsEditionId { get; set; }
        public string HardcoverEditionId { get; set; }
        public string OpenLibraryEditionId { get; set; }

        // Format identification
        public int? ReadingFormatId { get; set; } // 1=physical, 2=audio, 3=ebook
        public string EditionFormat { get; set; } // Specific format like "Paperback", "Hardcover", "Kindle Edition"
        public string EditionInfo { get; set; }

        // Audiobook specific
        public int? DurationSeconds { get; set; }
        public int? ChapterCount { get; set; }
        public bool HasChapters { get; set; }
        public List<EditionChapter> Chapters { get; set; }

        // GraphicAudio classification fields
        public bool IsGraphicAudio { get; set; }
        public string AudioProductionType { get; set; }

        // Narrator information
        public string Narrator { get; set; }

        // New provider and metadata fields
        public string AudibleASIN { get; set; }
        public string GoogleBooksEditionId { get; set; }
        public List<string> NarratorNames { get; set; }
        [MemberwiseEqualityIgnore]
        [JsonIgnore]
        public List<NarratorCredit> NarratorCredits { get; set; }
        public ProviderUrlMap ProviderUrls { get; set; }
        [MemberwiseEqualityIgnore]
        public DateTime? LastUpdated { get; set; }

        // These are Chaptarr generated/config
        public bool Monitored { get; set; }
        public bool ManualAdd { get; set; }
        public bool IsFallbackEdition { get; set; }

        // These are dynamically queried from other tables
        [MemberwiseEqualityIgnore]
        [JsonIgnore]
        public LazyLoaded<Book> LazyBook { get; set; }
        [MemberwiseEqualityIgnore]
        public Book Book
        {
            get => LazyBook?.Value;
            set => LazyBook = value;
        }
        [MemberwiseEqualityIgnore]
        public List<BookFile> BookFiles { get; set; }

        public override string ToString()
        {
            return string.Format("[{0}][{1}]", ForeignEditionId, Title.NullSafe());
        }

		        public override void UseMetadataFrom(Edition other)
		        {
			            ForeignEditionId = CleanProviderIdForCopy(other.ForeignEditionId, ForeignEditionId);

		            // Edition-level identifiers
		            Isbn13 = other.Isbn13;
		            Isbn10 = other.Isbn10;
			            Asin = CleanProviderIdForCopy(other.Asin, Asin);
			            Asins = CleanProviderIdsForCopy(other.Asins, Asins).ToList();

		            // Core metadata
		            // Non-destructive metadata refresh: never overwrite a non-empty Title with null/empty.
		            if (!string.IsNullOrWhiteSpace(other.Title))
		            {
		                Title = other.Title;
		            }

		            Subtitle = other.Subtitle;
		            MatchingTitle = other.MatchingTitle;
		            Language = other.Language;
		            Overview = other.Overview;
		            Format = other.Format;
		            IsEbook = other.IsEbook;
		            Disambiguation = other.Disambiguation;
		            Publisher = other.Publisher;
		            PageCount = other.PageCount;
		            if (other.ReleaseDate.HasValue)
		            {
		                ReleaseDate = other.ReleaseDate;
		            }

		            // Provider IDs
		            GoodreadsEditionId = other.GoodreadsEditionId;
			            HardcoverEditionId = CleanProviderIdForCopy(other.HardcoverEditionId, HardcoverEditionId);
			            OpenLibraryEditionId = CleanProviderIdForCopy(other.OpenLibraryEditionId, OpenLibraryEditionId);

		            // Format identification
		            ReadingFormatId = other.ReadingFormatId;
		            EditionFormat = other.EditionFormat;
		            EditionInfo = other.EditionInfo;

			            // Audiobook specific
			            DurationSeconds = other.DurationSeconds;
			            ChapterCount = other.ChapterCount;
			            HasChapters = other.HasChapters;
			            Chapters = other.Chapters?.Any() == true
			                ? other.Chapters.Select(c => new EditionChapter
			                {
			                    Title = c?.Title,
			                    StartOffsetMs = c?.StartOffsetMs ?? 0,
			                    StartOffsetSec = c?.StartOffsetSec ?? 0,
			                    LengthMs = c?.LengthMs ?? 0
			                }).ToList()
			                : Chapters ?? new List<EditionChapter>();

			            // Only keep safe absolute HTTP(S) image URLs (prevents poisoned/invalid values from persisting in the DB).
		            var otherImages = other.Images?
		                .Where(i => i?.Url.IsValidHttpUrl() == true)
		                .ToList();

		            var currentImages = Images?
		                .Where(i => i?.Url.IsValidHttpUrl() == true)
		                .ToList();

		            Images = otherImages?.Any() == true ? otherImages : currentImages ?? new List<MediaCover.MediaCover>();
			            Links = other.Links?.Any() == true ? other.Links : Links;
			            Ratings = other.Ratings;
			            ReviewCount = other.ReviewCount ?? ReviewCount;
			            IsGraphicAudio = other.IsGraphicAudio;
			            if (!string.IsNullOrWhiteSpace(other.AudioProductionType))
			            {
			                AudioProductionType = other.AudioProductionType;
			            }
			            Narrator = other.Narrator;

		            // New metadata fields
			            AudibleASIN = CleanProviderIdForCopy(other.AudibleASIN, AudibleASIN);
			            GoogleBooksEditionId = CleanProviderIdForCopy(other.GoogleBooksEditionId, GoogleBooksEditionId);
		            NarratorNames = other.NarratorNames ?? new List<string>();
			            NarratorCredits = other.NarratorCredits?.Any() == true
			                ? other.NarratorCredits.Select(c => new NarratorCredit
			                {
			                    Name = c?.Name,
			                    GoodreadsNarratorId = c?.GoodreadsNarratorId,
			                    HardcoverNarratorId = c?.HardcoverNarratorId,
			                    Order = c?.Order ?? 0,
			                    IsPrimary = c?.IsPrimary ?? false,
			                    Role = c?.Role ?? "Narrator"
			                }).ToList()
			                : NarratorCredits ?? new List<NarratorCredit>();
		            ProviderUrls = other.ProviderUrls?.Count > 0 ? new ProviderUrlMap(other.ProviderUrls) : ProviderUrls;
		            LastUpdated = other.LastUpdated ?? LastUpdated;
		        }

        private static IEnumerable<string> CleanProviderIds(IEnumerable<string> incoming)
        {
            return incoming?
                .Where(id => id.IsNotNullOrWhiteSpace() && !ProviderIdHelper.ContainsProviderIdArtifact(id))
                .Select(id => id.Trim()) ?? Enumerable.Empty<string>();
        }

        private static IEnumerable<string> CleanProviderIdsForCopy(IEnumerable<string> incoming, IEnumerable<string> current)
        {
            var cleanIncoming = CleanProviderIds(incoming).ToList();
            if (cleanIncoming.Count > 0)
            {
                return cleanIncoming;
            }

            var hadIncomingValues = incoming?.Any(id => id.IsNotNullOrWhiteSpace()) == true;
            return hadIncomingValues ? CleanProviderIds(current) : Enumerable.Empty<string>();
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

        public override void UseDbFieldsFrom(Edition other)
        {
            Id = other.Id;
            BookId = other.BookId;
            Book = other.Book;
            TitleSlug = other.TitleSlug;
            Monitored = other.Monitored;
            ManualAdd = other.ManualAdd;
            IsGraphicAudio = other.IsGraphicAudio;
            AudioProductionType = other.AudioProductionType;
            Narrator = other.Narrator;
            IsFallbackEdition = other.IsFallbackEdition;
            MatchingTitle = other.MatchingTitle;
        }

        public override void ApplyChanges(Edition other)
        {
            ForeignEditionId = other.ForeignEditionId;
            Monitored = other.Monitored;
        }
    }
}
