using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Equ;

using NzbDrone.Common.Extensions;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Books
{
    public class Author : Entity<Author>
    {
        public Author()
        {
            Tags = new HashSet<int>();
            AudiobookTags = new HashSet<int>();
            EbookTags = new HashSet<int>();
            Images = new List<MediaCover.MediaCover>();
            Genres = new List<string>();
            Links = new List<Links>();
            Aliases = new List<string>();
            Pseudonyms = new List<string>();
            ProviderUrls = new ProviderUrlMap();
            Ratings = new Ratings();
            AudiobookQualityProfile = new LazyLoaded<QualityProfile>();
            EbookQualityProfile = new LazyLoaded<QualityProfile>();
            MetadataProfile = new LazyLoaded<MetadataProfile>();
            AudiobookMetadataProfile = new LazyLoaded<MetadataProfile>();
            EbookMetadataProfile = new LazyLoaded<MetadataProfile>();
        }

        // These correspond to columns in the Authors table
        public string CleanName { get; set; }
        public bool Monitored { get; set; } // Legacy compatibility field

        // Author-level monitoring gates. NULL means the media type has not been configured;
        // false is an explicit pause and true enables automatic monitoring for that media type.
        public bool? AudiobookMonitored { get; set; }
        public bool? EbookMonitored { get; set; }
        public NewItemMonitorTypes? AudiobookMonitorNewItems { get; set; }
        public NewItemMonitorTypes? EbookMonitorNewItems { get; set; }
        public bool? SyncMonitoredAcrossFormats { get; set; }
        public bool AudiobookSettingsManuallyOverridden { get; set; }
        public bool EbookSettingsManuallyOverridden { get; set; }

        public DateTime? LastInfoSync { get; set; }
        public string Path { get; set; }
        public string AudiobookRootFolderPath { get; set; }
        public string EbookRootFolderPath { get; set; }

        // Discovered folder paths for each media type
        public string AudiobookPath { get; set; }
        public string EbookPath { get; set; }
        public DateTime Added { get; set; }
        public int? AudiobookQualityProfileId { get; set; }
        public int? EbookQualityProfileId { get; set; }
        public int? MetadataProfileId { get; set; } // DEPRECATED: Legacy field, being phased out in favor of media-specific profiles

        // Per-type metadata profiles
        public int? AudiobookMetadataProfileId { get; set; }
        public int? EbookMetadataProfileId { get; set; }

        public HashSet<int> Tags { get; set; }
        public HashSet<int> AudiobookTags { get; set; }
        public HashSet<int> EbookTags { get; set; }
        [MemberwiseEqualityIgnore]
        public AddAuthorOptions AddOptions { get; set; }
        public string LastSelectedMediaType { get; set; } = "audiobook";

        // Metadata fields (previously in AuthorMetadata)
        public string TitleSlug { get; set; }
        public string Name { get; set; }
        public string SortName { get; set; }
        public string NameLastFirst { get; set; }
        public string SortNameLastFirst { get; set; }
        public List<string> Aliases { get; set; }
        public string Overview { get; set; }
        public string Disambiguation { get; set; }
        public string Gender { get; set; }
        public string Hometown { get; set; }
        public DateTime? Born { get; set; }
        public DateTime? Died { get; set; }
        public AuthorStatusType Status { get; set; }
        public List<MediaCover.MediaCover> Images { get; set; }
        public List<Links> Links { get; set; }
        public List<string> Genres { get; set; }
        public Ratings Ratings { get; set; }

        // Provider IDs
        public string GoodreadsAuthorId { get; set; }
        public string HardcoverAuthorId { get; set; }
        public string AudnexusAuthorId { get; set; }
        public string OpenLibraryAuthorId { get; set; }
        public string GoogleBooksAuthorId { get; set; }

        // Optional in-memory provider ID sets from upstream metadata payloads (not persisted).
        // Used for robust matching when an entity gains new provider IDs upstream.
        [MemberwiseEqualityIgnore]
        [JsonIgnore]
        public HashSet<string> RemoteProviderIds { get; set; }
        [MemberwiseEqualityIgnore]
        [JsonIgnore]
        public string RemoteMetadataETag { get; set; }

        // Standard identifiers
        public string ISNI { get; set; }
        public string VIAF { get; set; }

        // New metadata fields
        public List<string> Pseudonyms { get; set; }
        public ProviderUrlMap ProviderUrls { get; set; }
        public DateTime? LastUpdated { get; set; }
        
        // Image selection
        public string SelectedPosterHash { get; set; }
        [MemberwiseEqualityIgnore]
        public LazyLoaded<QualityProfile> AudiobookQualityProfile { get; set; }
        [MemberwiseEqualityIgnore]
        public LazyLoaded<QualityProfile> EbookQualityProfile { get; set; }
        [MemberwiseEqualityIgnore]
        public LazyLoaded<MetadataProfile> MetadataProfile { get; set; }
        [MemberwiseEqualityIgnore]
        public LazyLoaded<MetadataProfile> AudiobookMetadataProfile { get; set; }
        [MemberwiseEqualityIgnore]
        public LazyLoaded<MetadataProfile> EbookMetadataProfile { get; set; }
        [MemberwiseEqualityIgnore]
        public List<Book> Books { get; set; }
        [MemberwiseEqualityIgnore]
        public List<Series> Series { get; set; }

        public override string ToString()
        {
            return string.Format("[{0}][{1}]", Id, Name.NullSafe());
        }

        public override void UseMetadataFrom(Author other)
        {
            CleanName = other.CleanName;
            TitleSlug = other.TitleSlug;
            Name = other.Name;
            SortName = other.SortName;
            NameLastFirst = other.NameLastFirst;
            SortNameLastFirst = other.SortNameLastFirst;
            Aliases = CloneStringList(other.Aliases, Aliases);
            Overview = other.Overview.IsNullOrWhiteSpace() ? Overview : other.Overview;
            Disambiguation = other.Disambiguation;
            Gender = other.Gender;
            Hometown = other.Hometown;
            Born = other.Born;
            Died = other.Died;
            Status = other.Status;

            // Only keep safe absolute HTTP(S) image URLs (prevents poisoned/invalid values from persisting in the DB).
            var otherImages = other.Images?
                .Where(i => i?.Url.IsValidHttpUrl() == true &&
                            !MediaCover.MediaCoverRendition.IsKnownPlaceholderImageUrl(i.Url))
                .ToList();

            var currentImages = Images?
                .Where(i => i?.Url.IsValidHttpUrl() == true &&
                            !MediaCover.MediaCoverRendition.IsKnownPlaceholderImageUrl(i.Url))
                .ToList();

            Images = CloneMediaCovers(otherImages?.Any() == true ? otherImages : currentImages);
            Links = CloneLinks(other.Links, Links);
            Genres = CloneStringList(other.Genres, Genres);
            Ratings = other.Ratings?.Votes > 0 ? CloneRatings(other.Ratings) : Ratings;

            // Provider IDs
            GoodreadsAuthorId = other.GoodreadsAuthorId;
            HardcoverAuthorId = other.HardcoverAuthorId;
            AudnexusAuthorId = other.AudnexusAuthorId;
            OpenLibraryAuthorId = other.OpenLibraryAuthorId;
            GoogleBooksAuthorId = other.GoogleBooksAuthorId;
            RemoteProviderIds = CloneProviderIds(other.RemoteProviderIds);

            // Standard identifiers
            ISNI = other.ISNI ?? ISNI;
            VIAF = other.VIAF ?? VIAF;

            // New metadata fields
            Pseudonyms = CloneStringList(other.Pseudonyms, Pseudonyms);
            ProviderUrls = other.ProviderUrls?.Count > 0 ? new ProviderUrlMap(other.ProviderUrls) : ProviderUrls;
            LastUpdated = other.LastUpdated ?? LastUpdated;
        }

        private static List<string> CloneStringList(List<string> source, List<string> fallback)
        {
            var chosen = source?.Any() == true ? source : fallback;
            return chosen?.ToList() ?? new List<string>();
        }

        private static List<MediaCover.MediaCover> CloneMediaCovers(List<MediaCover.MediaCover> source)
        {
            return source?.Select(image => image == null ? null : new MediaCover.MediaCover
            {
                CoverType = image.CoverType,
                Url = image.Url,
                Hash = image.Hash
            }).ToList() ?? new List<MediaCover.MediaCover>();
        }

        private static List<Links> CloneLinks(List<Links> source, List<Links> fallback)
        {
            var chosen = source?.Any() == true ? source : fallback;
            return chosen?.Select(link => link == null ? null : new Links
            {
                Name = link.Name,
                Url = link.Url
            }).ToList() ?? new List<Links>();
        }

        private static HashSet<string> CloneProviderIds(HashSet<string> source)
        {
            var values = source?.Where(id => !id.IsNullOrWhiteSpace()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return values?.Count > 0 ? values : null;
        }

        private static Ratings CloneRatings(Ratings ratings)
        {
            if (ratings == null)
            {
                return new Ratings();
            }

            return new Ratings
            {
                Votes = ratings.Votes,
                Value = ratings.Value
            };
        }

        public override void UseDbFieldsFrom(Author other)
        {
            Id = other.Id;
            Monitored = other.Monitored;
            AudiobookMonitored = other.AudiobookMonitored;
            EbookMonitored = other.EbookMonitored;
            AudiobookMonitorNewItems = other.AudiobookMonitorNewItems;
            EbookMonitorNewItems = other.EbookMonitorNewItems;
            AudiobookSettingsManuallyOverridden = other.AudiobookSettingsManuallyOverridden;
            EbookSettingsManuallyOverridden = other.EbookSettingsManuallyOverridden;
            SyncMonitoredAcrossFormats = other.SyncMonitoredAcrossFormats;
            LastInfoSync = other.LastInfoSync;
            Path = other.Path;
            AudiobookRootFolderPath = other.AudiobookRootFolderPath;
            EbookRootFolderPath = other.EbookRootFolderPath;
            Added = other.Added;
            AudiobookQualityProfileId = other.AudiobookQualityProfileId;
            AudiobookQualityProfile = other.AudiobookQualityProfile;
            EbookQualityProfileId = other.EbookQualityProfileId;
            EbookQualityProfile = other.EbookQualityProfile;
            MetadataProfileId = other.MetadataProfileId;
            AudiobookMetadataProfileId = other.AudiobookMetadataProfileId;
            EbookMetadataProfileId = other.EbookMetadataProfileId;
            MetadataProfile = other.MetadataProfile;
            AudiobookMetadataProfile = other.AudiobookMetadataProfile;
            EbookMetadataProfile = other.EbookMetadataProfile;
            Tags = other.Tags;
            AudiobookTags = other.AudiobookTags;
            EbookTags = other.EbookTags;
            AddOptions = other.AddOptions;
            LastSelectedMediaType = other.LastSelectedMediaType;
        }

        public override void ApplyChanges(Author other)
        {
            Path = other.Path;

            // Don't overwrite quality profiles if they're already set
            // Quality profiles from metadata sources are always null/0
            if (other.AudiobookQualityProfileId.HasValue && other.AudiobookQualityProfileId.Value > 0)
            {
                AudiobookQualityProfileId = other.AudiobookQualityProfileId;
                AudiobookQualityProfile = other.AudiobookQualityProfile;
            }

            if (other.EbookQualityProfileId.HasValue && other.EbookQualityProfileId.Value > 0)
            {
                EbookQualityProfileId = other.EbookQualityProfileId;
                EbookQualityProfile = other.EbookQualityProfile;
            }

            // Update metadata profile if provided (legacy field, can be null)
            if (other.MetadataProfileId.HasValue)
            {
                MetadataProfileId = other.MetadataProfileId;
                MetadataProfile = other.MetadataProfile;
            }

            // Update per-type metadata profiles if provided
            if (other.AudiobookMetadataProfileId.HasValue && other.AudiobookMetadataProfileId.Value > 0)
            {
                AudiobookMetadataProfileId = other.AudiobookMetadataProfileId;
                AudiobookMetadataProfile = other.AudiobookMetadataProfile;
            }

            if (other.EbookMetadataProfileId.HasValue && other.EbookMetadataProfileId.Value > 0)
            {
                EbookMetadataProfileId = other.EbookMetadataProfileId;
                EbookMetadataProfile = other.EbookMetadataProfile;
            }

            Books = other.Books;
            if (other.AudiobookTags == null && other.EbookTags == null && other.Tags != null)
            {
                // Legacy behavior: a single Tags field applies to both media types.
                AudiobookTags = other.Tags;
                EbookTags = other.Tags;
            }
            else if (other.AudiobookTags != null)
            {
                AudiobookTags = other.AudiobookTags;
            }

            if (other.EbookTags != null)
            {
                EbookTags = other.EbookTags;
            }

            Tags = (AudiobookTags ?? new HashSet<int>()).Concat(EbookTags ?? new HashSet<int>()).ToHashSet();
            AddOptions = other.AddOptions;
            AudiobookRootFolderPath = other.AudiobookRootFolderPath;
            EbookRootFolderPath = other.EbookRootFolderPath;
            Monitored = other.Monitored;
            // Copy optional monitoring values only when explicitly provided. This keeps
            // partial metadata updates from wiping a user's monitoring choices.
            if (other.AudiobookMonitored.HasValue)
            {
                AudiobookMonitored = other.AudiobookMonitored;
                AudiobookSettingsManuallyOverridden = true;
            }
            if (other.AudiobookMonitorNewItems.HasValue)
            {
                AudiobookMonitorNewItems = other.AudiobookMonitorNewItems;
                AudiobookSettingsManuallyOverridden = true;
            }
            if (other.EbookMonitored.HasValue)
            {
                EbookMonitored = other.EbookMonitored;
                EbookSettingsManuallyOverridden = true;
            }
            if (other.EbookMonitorNewItems.HasValue)
            {
                EbookMonitorNewItems = other.EbookMonitorNewItems;
                EbookSettingsManuallyOverridden = true;
            }
            if (other.SyncMonitoredAcrossFormats.HasValue)
            {
                SyncMonitoredAcrossFormats = other.SyncMonitoredAcrossFormats;
            }
            LastSelectedMediaType = other.LastSelectedMediaType;
        }

        public HashSet<int> GetTagsForMediaType(BookMediaType mediaType)
        {
            if (AudiobookTags == null && EbookTags == null)
            {
                return Tags ?? new HashSet<int>();
            }

            return mediaType == BookMediaType.Ebook
                ? EbookTags ?? new HashSet<int>()
                : AudiobookTags ?? new HashSet<int>();
        }

        public string GetRootFolderForQuality(Quality quality)
        {
            var mediaType = GetEffectiveMediaTypeForQuality(quality);

            if (mediaType == BookMediaType.Ebook && !string.IsNullOrWhiteSpace(EbookRootFolderPath))
            {
                return EbookRootFolderPath;
            }
            else if (mediaType == BookMediaType.Audiobook && !string.IsNullOrWhiteSpace(AudiobookRootFolderPath))
            {
                return AudiobookRootFolderPath;
            }

            // If the user doesn't want this media type, return null
            return null;
        }

        public QualityProfile GetQualityProfileForQuality(Quality quality)
        {
            if (GetEffectiveMediaTypeForQuality(quality) == BookMediaType.Ebook)
            {
                return EbookQualityProfileId.HasValue ? EbookQualityProfile?.Value : null;
            }

            return AudiobookQualityProfileId.HasValue ? AudiobookQualityProfile?.Value : null;
        }

        private static BookMediaType GetEffectiveMediaTypeForQuality(Quality quality)
        {
            if (quality == null || quality == Quality.Unknown)
            {
                return BookMediaType.Ebook;
            }

            return QualityMediaTypeHelper.GetKnownMediaType(quality) ?? BookMediaType.Audiobook;
        }
    }
}
