using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json.Serialization;
using Equ;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Books
{
    [DebuggerDisplay("{GetType().FullName} ID = {Id}[{Title}]")]
    public class Series : Entity<Series>
    {
        public Series()
        {
            ProviderUrls = new ProviderUrlMap();
        }

        public string Title { get; set; }
        public string TitleSlug { get; set; }
        public string Description { get; set; }
        public bool Numbered { get; set; }
        public int WorkCount { get; set; }
        public int PrimaryWorkCount { get; set; }

        // Provider IDs
        public string GoodreadsSeriesId { get; set; }
        public string HardcoverSeriesId { get; set; }
        public string OpenLibrarySeriesId { get; set; }
        public string AmazonSeriesAsin { get; set; }

        // Series metadata
        public string SeriesType { get; set; } // main/spin-off/companion
        public int? ParentSeriesId { get; set; }
        public int TotalBooks { get; set; }
        public int PrimaryBooks { get; set; }

        // Narrator field for multi-copy architecture
        // Enables narrator-aware series: "Harry Potter Narrated by Jim Dale"
        public string Narrator { get; set; }

        // Media type for dual-record architecture (audiobook vs ebook)
        public BookMediaType MediaType { get; set; } = BookMediaType.Audiobook;

        // New fields for narrator variants
        public string BaseSeriesId { get; set; }
        public int InstanceNumber { get; set; } = 0;
        public int? PreferredNarratorId { get; set; }
        // New metadata fields
        public ProviderUrlMap ProviderUrls { get; set; }
        [MemberwiseEqualityIgnore]
        public DateTime? LastUpdated { get; set; }

        // External links
        public Dictionary<string, string> Links { get; set; } = new Dictionary<string, string>();

        // Books in this series from V5 API
        [MemberwiseEqualityIgnore]
        public List<SeriesBook> SeriesBooks { get; set; } = new List<SeriesBook>();

        // Computed properties
        public bool IsNarratorVariant => PreferredNarratorId.HasValue || !Narrator.IsNullOrWhiteSpace();
        public bool IsOriginal => !IsNarratorVariant;

        // Display title for UI - combines series title with narrator info
        // Backend uses Title for processing, GUI uses DisplayTitle for user experience
        public string DisplayTitle => IsNarratorVariant && !string.IsNullOrEmpty(Narrator)
            ? $"{Title} (Narrated by {Narrator})"
            : Title;
        [MemberwiseEqualityIgnore]
        [JsonIgnore]
        public LazyLoaded<List<SeriesBookLink>> LazyLinkItems { get; set; }

        [MemberwiseEqualityIgnore]
        [JsonIgnore]
        public LazyLoaded<List<Book>> LazyBooks { get; set; }

        [MemberwiseEqualityIgnore]
        public List<SeriesBookLink> LinkItems
        {
            get => LazyLinkItems?.Value;
            set => LazyLinkItems = value;
        }

        [MemberwiseEqualityIgnore]
        public List<Book> Books
        {
            get => LazyBooks?.Value;
            set => LazyBooks = value;
        }

        public override string ToString()
        {
            return string.Format("[{0}][{1}]", Id, Title.NullSafe());
        }

        public override void UseMetadataFrom(Series other)
        {
            Title = other.Title;
            TitleSlug = other.TitleSlug;
            Description = other.Description;
            Numbered = other.Numbered;
            WorkCount = other.WorkCount;
            PrimaryWorkCount = other.PrimaryWorkCount;

            // Provider IDs
            GoodreadsSeriesId = other.GoodreadsSeriesId ?? GoodreadsSeriesId;
            HardcoverSeriesId = other.HardcoverSeriesId ?? HardcoverSeriesId;
            OpenLibrarySeriesId = other.OpenLibrarySeriesId ?? OpenLibrarySeriesId;
            AmazonSeriesAsin = other.AmazonSeriesAsin ?? AmazonSeriesAsin;

            // Series metadata
            SeriesType = other.SeriesType ?? SeriesType;
            ParentSeriesId = other.ParentSeriesId ?? ParentSeriesId;
            TotalBooks = other.TotalBooks > 0 ? other.TotalBooks : TotalBooks;
            PrimaryBooks = other.PrimaryBooks > 0 ? other.PrimaryBooks : PrimaryBooks;

            Narrator = other.Narrator;
            BaseSeriesId = other.BaseSeriesId;
            InstanceNumber = other.InstanceNumber;
            PreferredNarratorId = other.PreferredNarratorId;

            // New metadata fields
            ProviderUrls = other.ProviderUrls?.Count > 0 ? new ProviderUrlMap(other.ProviderUrls) : ProviderUrls;
            LastUpdated = other.LastUpdated ?? LastUpdated;
        }

        public override void UseDbFieldsFrom(Series other)
        {
            Id = other.Id;
            TitleSlug = other.TitleSlug;
            Narrator = other.Narrator;
            MediaType = other.MediaType;
            BaseSeriesId = other.BaseSeriesId;
            InstanceNumber = other.InstanceNumber;
            PreferredNarratorId = other.PreferredNarratorId;
            ParentSeriesId = other.ParentSeriesId;
            Links = other.Links;
        }
    }
}
