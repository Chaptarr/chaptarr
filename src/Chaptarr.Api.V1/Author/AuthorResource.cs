using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.Books;
using Chaptarr.Api.V1.MediaTypes;
using Chaptarr.Http.Middleware;
using Chaptarr.Http.REST;
using FluentValidation;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Validation;

namespace Chaptarr.Api.V1.Author
{
    public class AuthorResource : RestResource
    {
        //Todo: Sorters should be done completely on the client
        //Todo: Is there an easy way to keep IgnoreArticlesWhenSorting in sync between, Series, History, Missing?
        //Todo: We should get the entire Profile instead of ID and Name separately
        public AuthorStatusType Status { get; set; }

        public bool Ended => AuthorExtensions.GetLifeStatus(Died) == AuthorStatusType.Ended;
        public int AuthorMetadataId { get; set; }

        public string AuthorName { get; set; }
        public string AuthorNameLastFirst { get; set; }
        public string ForeignAuthorId { get; set; }
        public string TitleSlug { get; set; }
        public string Overview { get; set; }
        public string Disambiguation { get; set; }
        public DateTime? Born { get; set; }
        public DateTime? Died { get; set; }
        public List<Links> Links { get; set; }

        // BookshelfNG serialized its core Book model here. Keep that response shape for
        // existing clients while projecting fields from ChaptarrNG's unified Book model.
        public AuthorIndexBookResource NextBook { get; set; }
        public AuthorIndexBookResource LastBook { get; set; }

        public List<MediaCover> Images { get; set; }

        public string RemotePoster { get; set; }

        //View & Edit
        public string Path { get; set; }
        // Readarr/Seerr compatibility (single profile + root folder)
        public int? QualityProfileId { get; set; }
        public int? AudiobookQualityProfileId { get; set; }
        public int? EbookQualityProfileId { get; set; }
        [Newtonsoft.Json.JsonIgnore]
        [global::System.Text.Json.Serialization.JsonIgnore]
        internal int? OriginalBookshelfNGQualityProfileId { get; set; }
        [Newtonsoft.Json.JsonIgnore]
        [global::System.Text.Json.Serialization.JsonIgnore]
        internal int? OriginalBookshelfNGAudiobookQualityProfileId { get; set; }
        [Newtonsoft.Json.JsonIgnore]
        [global::System.Text.Json.Serialization.JsonIgnore]
        internal int? OriginalBookshelfNGEbookQualityProfileId { get; set; }
        [Newtonsoft.Json.JsonIgnore]
        [global::System.Text.Json.Serialization.JsonIgnore]
        internal bool OriginalBookshelfQualityProfileIdsCaptured { get; set; }
        public int? MetadataProfileId { get; set; }  // DEPRECATED: Made nullable to handle legacy data gracefully
        
        // Per-type metadata profiles
        public int? AudiobookMetadataProfileId { get; set; }
        public int? EbookMetadataProfileId { get; set; }

        // Editing only. Responses always populate this legacy aggregate; nullable
        // input distinguishes an explicit monitor choice from an omitted field.
        public bool? Monitored { get; set; }
        // Author-level monitoring gates. NULL means unconfigured; false is an explicit pause.
        public bool? AudiobookMonitored { get; set; }
        // One-time action for the catalog in an add/import request; not persisted on Author.
        public MonitorTypes? AudiobookMonitorExistingMode { get; set; }
        public bool? EbookMonitored { get; set; }
        // One-time action for the catalog in an add/import request; not persisted on Author.
        public MonitorTypes? EbookMonitorExistingMode { get; set; }
        public NewItemMonitorTypes? AudiobookMonitorNewItems { get; set; }
        public NewItemMonitorTypes? EbookMonitorNewItems { get; set; }
        // Deprecated wire aliases for pre-binary Chaptarr clients. These are
        // translated at the resource boundary and are never persisted.
        public int? AudiobookMonitorExisting { get; set; }
        public bool? AudiobookMonitorFuture { get; set; }
        public int? EbookMonitorExisting { get; set; }
        public bool? EbookMonitorFuture { get; set; }
        public bool? SyncMonitoredAcrossFormats { get; set; }

        public string AudiobookRootFolderPath { get; set; }
        public string EbookRootFolderPath { get; set; }
        // Readarr/Seerr compatibility (single root folder)
        public string RootFolderPath { get; set; }
        // BookshelfNG and Readarr compatibility (single monitor enum)
        public NewItemMonitorTypes? MonitorNewItems { get; set; }
        public string Folder { get; set; }
        // Writable per-media author paths. The edit UI submits these; AudiobookFolder/
        // EbookFolder below remain the read-only display values set by the controller.
        public string AudiobookPath { get; set; }
        public string EbookPath { get; set; }
        public string AudiobookFolder { get; set; }
        public string EbookFolder { get; set; }
        public List<string> Genres { get; set; }
        public string CleanName { get; set; }
        public string SortName { get; set; }
        public string SortNameLastFirst { get; set; }

        public HashSet<int> Tags { get; set; }
        public HashSet<int> AudiobookTags { get; set; }
        public HashSet<int> EbookTags { get; set; }
        public DateTime Added { get; set; }
        public AddAuthorOptions AddOptions { get; set; }
        public Ratings Ratings { get; set; }
        public string LastSelectedMediaType { get; set; }
        public string SelectedPosterHash { get; set; }

        public AuthorStatisticsResource Statistics { get; set; }
        // Per-media-type statistics for live UI updates without full refetch
        public AuthorStatisticsResource AudiobookStatistics { get; set; }
        public AuthorStatisticsResource EbookStatistics { get; set; }

        // Optional warning returned by certain operations (e.g. v1 author import hydration).
        // When set, the operation succeeded but additional follow-up may be required.
        public string HydrationWarning { get; set; }
    }

    public static class AuthorResourceMapper
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public static AuthorResource ToResource(this NzbDrone.Core.Books.Author model)
        {
            return model.ToResource(null);
        }

        public static AuthorResource ToResource(this NzbDrone.Core.Books.Author model, ReadarrFacadeContext facadeContext)
        {
            if (model == null)
            {
                return null;
            }

            // Native display filters provider placeholders. The compatibility response
            // retains the predecessor's stored image list, including those placeholders.
            var displayImages = facadeContext?.IsBookshelfCompatibility == true
                ? model.Images?.Where(image => image != null).ToList() ?? new List<MediaCover>()
                : MediaCoverRendition.SelectCandidates(model.Images).ToList();

            var resource = new AuthorResource
            {
                Id = model.Id,
                // ChaptarrNG has a unified author identity. The controller replaces
                // this with the imported BookshelfNG metadata identity when available.
                AuthorMetadataId = model.Id,

                AuthorName = model.Name,
                AuthorNameLastFirst = model.NameLastFirst,

                //AlternateTitles
                SortName = model.SortName,
                SortNameLastFirst = model.SortNameLastFirst,

                Status = AuthorExtensions.GetLifeStatus(model.Died),
                Overview = model.Overview,
                Disambiguation = model.Disambiguation,
                Born = model.Born,
                Died = model.Died,

                Images = displayImages.JsonClone(),

                Path = model.Path,
                AudiobookPath = model.AudiobookPath,
                EbookPath = model.EbookPath,
                QualityProfileId = GetBookshelfQualityProfileId(model, facadeContext) ?? 0,
                RootFolderPath = GetBookshelfRootFolderPath(model),
                MonitorNewItems = GetBookshelfMonitorNewItems(model),
                AudiobookQualityProfileId = facadeContext?.IsBookshelfCompatibility == true
                    ? model.BookshelfNGAudiobookQualityProfileId ?? model.AudiobookQualityProfileId
                    : model.AudiobookQualityProfileId,
                EbookQualityProfileId = facadeContext?.IsBookshelfCompatibility == true
                    ? model.BookshelfNGEbookQualityProfileId ?? model.EbookQualityProfileId
                    : model.EbookQualityProfileId,
                MetadataProfileId = GetBookshelfMetadataProfileId(model, facadeContext) ?? 0,
                AudiobookMetadataProfileId = model.AudiobookMetadataProfileId,
                EbookMetadataProfileId = model.EbookMetadataProfileId,
                Links = CloneLinks(model.Links),

                // The legacy aggregate supports BookshelfNG and Readarr-compatible clients;
                // never let a stale stored value override the per-media gates.
                Monitored = model.IsMonitoredFromMediaSettings(),
                AudiobookMonitored = model.AudiobookMonitored,
                AudiobookMonitorNewItems = model.AudiobookMonitorNewItems,
                EbookMonitored = model.EbookMonitored,
                EbookMonitorNewItems = model.EbookMonitorNewItems,
                AudiobookMonitorExisting = ToLegacyMonitorExisting(model.AudiobookMonitored, model.AudiobookMonitorNewItems),
                AudiobookMonitorFuture = ToLegacyMonitorFuture(model.AudiobookMonitorNewItems),
                EbookMonitorExisting = ToLegacyMonitorExisting(model.EbookMonitored, model.EbookMonitorNewItems),
                EbookMonitorFuture = ToLegacyMonitorFuture(model.EbookMonitorNewItems),
                SyncMonitoredAcrossFormats = model.SyncMonitoredAcrossFormats,

                CleanName = model.CleanName,
                ForeignAuthorId = BuildForeignAuthorId(model, facadeContext),
                TitleSlug = model.TitleSlug,

                AudiobookRootFolderPath = model.AudiobookRootFolderPath?.GetCleanPath(),
                EbookRootFolderPath = model.EbookRootFolderPath?.GetCleanPath(),
                AudiobookFolder = model.AudiobookPath?.GetCleanPath(),
                EbookFolder = model.EbookPath?.GetCleanPath(),
                Genres = CloneStringList(model.Genres),
                AudiobookTags = CloneTags(model.AudiobookTags),
                EbookTags = CloneTags(model.EbookTags),
                Added = model.Added,
                AddOptions = model.AddOptions,
                Ratings = CloneRatings(model.Ratings),
                LastSelectedMediaType = model.LastSelectedMediaType,
                SelectedPosterHash = model.SelectedPosterHash,

                Statistics = new AuthorStatisticsResource()
            };

            var useLegacyTags = model.AudiobookTags == null && model.EbookTags == null && model.Tags != null;
            if (useLegacyTags)
            {
                resource.AudiobookTags = new HashSet<int>(model.Tags);
                resource.EbookTags = new HashSet<int>(model.Tags);
            }

            resource.Tags = resource.AudiobookTags.Concat(resource.EbookTags).ToHashSet();
            ApplyFacadeProjection(resource, model, facadeContext);

            // Compute stable hashes for images if not already present
            if (resource.Images != null)
            {
                foreach (var image in resource.Images)
                {
                    image.Hash = AuthorImageHashHelper.ComputeStableImageHash(image.Url, image.CoverType);
                }
            }

            if (!string.IsNullOrWhiteSpace(resource.SelectedPosterHash) &&
                !resource.Images.Any(image => image.Hash == resource.SelectedPosterHash))
            {
                resource.SelectedPosterHash = null;
            }

            return resource;
        }

        private static string BuildForeignAuthorId(NzbDrone.Core.Books.Author model, ReadarrFacadeContext facadeContext)
        {
            if (facadeContext?.IsHardcover == true)
            {
                var hardcoverAuthorId = TryNormalizeProviderId(model?.HardcoverAuthorId, "hc");
                if (!string.IsNullOrWhiteSpace(hardcoverAuthorId))
                {
                    return StripProviderPrefix(hardcoverAuthorId);
                }

                WarnMissingFacadeAuthorId("hc", model);
                return string.Empty;
            }

            if (facadeContext?.IsGoodreads == true)
            {
                var goodreadsAuthorId = TryNormalizeProviderId(model?.GoodreadsAuthorId, "gr");
                if (!string.IsNullOrWhiteSpace(goodreadsAuthorId))
                {
                    return StripProviderPrefix(goodreadsAuthorId);
                }

                WarnMissingFacadeAuthorId("gr", model);
                return string.Empty;
            }

            return AuthorIdentity.GetPreferredProviderId(model) ?? string.Empty;
        }

        private static void ApplyFacadeProjection(AuthorResource resource, NzbDrone.Core.Books.Author model, ReadarrFacadeContext facadeContext)
        {
            if (facadeContext?.MediaType == "audiobook")
            {
                resource.QualityProfileId = resource.AudiobookQualityProfileId;
                resource.MetadataProfileId = resource.AudiobookMetadataProfileId ?? resource.MetadataProfileId;
                resource.RootFolderPath = resource.AudiobookRootFolderPath;
                resource.Tags = resource.AudiobookTags ?? new HashSet<int>();
                resource.Monitored = model.AudiobookMonitored == true;
                resource.MonitorNewItems = model.AudiobookMonitorNewItems;
            }
            else if (facadeContext?.MediaType == "ebook")
            {
                resource.QualityProfileId = resource.EbookQualityProfileId;
                resource.MetadataProfileId = resource.EbookMetadataProfileId ?? resource.MetadataProfileId;
                resource.RootFolderPath = resource.EbookRootFolderPath;
                resource.Tags = resource.EbookTags ?? new HashSet<int>();
                resource.Monitored = model.EbookMonitored == true;
                resource.MonitorNewItems = model.EbookMonitorNewItems;
            }
        }

        private static int? GetBookshelfQualityProfileId(NzbDrone.Core.Books.Author model, ReadarrFacadeContext facadeContext)
        {
            if (facadeContext?.IsBookshelfCompatibility == true)
            {
                return model.BookshelfNGQualityProfileId ?? model.QualityProfileId;
            }

            return string.Equals(model.LastSelectedMediaType, "ebook", StringComparison.OrdinalIgnoreCase)
                ? model.EbookQualityProfileId ?? model.AudiobookQualityProfileId
                : model.AudiobookQualityProfileId ?? model.EbookQualityProfileId;
        }

        private static string GetBookshelfRootFolderPath(NzbDrone.Core.Books.Author model)
        {
            return string.Equals(model.LastSelectedMediaType, "ebook", StringComparison.OrdinalIgnoreCase)
                ? model.EbookRootFolderPath ?? model.AudiobookRootFolderPath
                : model.AudiobookRootFolderPath ?? model.EbookRootFolderPath;
        }

        private static int? GetBookshelfMetadataProfileId(NzbDrone.Core.Books.Author model, ReadarrFacadeContext facadeContext)
        {
            if (facadeContext?.IsBookshelfCompatibility == true)
            {
                return model.MetadataProfileId;
            }

            return string.Equals(model.LastSelectedMediaType, "ebook", StringComparison.OrdinalIgnoreCase)
                ? model.EbookMetadataProfileId ?? model.MetadataProfileId ?? model.AudiobookMetadataProfileId
                : model.AudiobookMetadataProfileId ?? model.MetadataProfileId ?? model.EbookMetadataProfileId;
        }

        private static NewItemMonitorTypes GetBookshelfMonitorNewItems(NzbDrone.Core.Books.Author model)
        {
            var selected = string.Equals(model.LastSelectedMediaType, "ebook", StringComparison.OrdinalIgnoreCase)
                ? model.EbookMonitorNewItems ?? model.AudiobookMonitorNewItems
                : model.AudiobookMonitorNewItems ?? model.EbookMonitorNewItems;

            return selected ?? NewItemMonitorTypes.All;
        }

        private static int? ToLegacyMonitorExisting(bool? monitored, NewItemMonitorTypes? monitorNewItems)
        {
            return monitored switch
            {
                false => 0,
                true when monitorNewItems == NewItemMonitorTypes.All => 1,
                true => 2,
                _ => null
            };
        }

        private static bool? ToLegacyMonitorFuture(NewItemMonitorTypes? monitorNewItems)
        {
            return monitorNewItems switch
            {
                NewItemMonitorTypes.All or NewItemMonitorTypes.New => true,
                NewItemMonitorTypes.None => false,
                _ => null
            };
        }

        private static string TryNormalizeProviderId(string value, string prefix)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            try
            {
                return ProviderIdHelper.Normalize(value, prefix);
            }
            catch
            {
                return null;
            }
        }

        private static string StripProviderPrefix(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            var idx = value.IndexOf(':');
            return idx > 0 ? value.Substring(idx + 1) : value;
        }

        private static void WarnMissingFacadeAuthorId(string dialect, NzbDrone.Core.Books.Author model)
        {
            Logger.Debug("[ReadarrFacade] Cannot emit author identity in {0} dialect for localAuthorId={1} name='{2}'. Omitting compatibility ID instead of falling back across providers.",
                dialect,
                model?.Id ?? 0,
                model?.Name ?? string.Empty);
        }

        private static List<Links> CloneLinks(List<Links> links)
        {
            return links?.Select(link => link == null ? null : new Links
            {
                Name = link.Name,
                Url = link.Url
            }).ToList() ?? new List<Links>();
        }

        private static List<string> CloneStringList(List<string> values)
        {
            return values?.ToList() ?? new List<string>();
        }

        private static HashSet<int> CloneTags(HashSet<int> tags)
        {
            return tags != null ? new HashSet<int>(tags) : new HashSet<int>();
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

        public static NzbDrone.Core.Books.Author ToModel(this AuthorResource resource)
        {
            return resource.ToModel((ReadarrFacadeContext)null);
        }

        public static NzbDrone.Core.Books.Author ToModel(this AuthorResource resource, ReadarrFacadeContext facadeContext)
        {
            if (resource == null)
            {
                return null;
            }

            NormalizeLegacySingleFields(resource, facadeContext);

            // Parse provider IDs from ForeignAuthorId - keep the full ID with prefix
            string hardcoverAuthorId = null;
            string goodreadsAuthorId = null;
            string openLibraryAuthorId = null;
            string googleBooksAuthorId = null;
            string audnexusAuthorId = null;
            string ranobeDbAuthorId = null;
            string additionalMetadataProviderId = null;
            if (!string.IsNullOrWhiteSpace(resource.ForeignAuthorId))
            {
                if (long.TryParse(resource.ForeignAuthorId, out _) && facadeContext != null)
                {
                    if (facadeContext.IsHardcover)
                    {
                        hardcoverAuthorId = ProviderIdHelper.Normalize("hc:" + resource.ForeignAuthorId, "hc");
                    }
                    else if (facadeContext.IsGoodreads)
                    {
                        goodreadsAuthorId = ProviderIdHelper.Normalize("gr:" + resource.ForeignAuthorId, "gr");
                    }
                }
                else if (ProviderIdValidator.TryNormalize(resource.ForeignAuthorId, out var normalizedProviderId, out var prefix, out _, out _))
                {
                    switch (prefix)
                    {
                        case "hc":
                            hardcoverAuthorId = ProviderIdHelper.Normalize(normalizedProviderId, "hc");
                            break;
                        case "gr":
                            goodreadsAuthorId = ProviderIdHelper.Normalize(normalizedProviderId, "gr");
                            break;
                        case "ol":
                            openLibraryAuthorId = ProviderIdHelper.Normalize(normalizedProviderId, "ol");
                            break;
                        case "gb":
                            googleBooksAuthorId = ProviderIdHelper.Normalize(normalizedProviderId, "gb");
                            break;
                        case "az":
                            audnexusAuthorId = ProviderIdHelper.Normalize(normalizedProviderId, "az");
                            break;
                        case "rn":
                            ranobeDbAuthorId = ProviderIdHelper.Normalize(normalizedProviderId, "rn");
                            break;
                        case "meta":
                            additionalMetadataProviderId = ProviderIdHelper.Normalize(normalizedProviderId, "meta");
                            break;
                    }
                }
            }

            var audiobookQualityProfileId = resource.AudiobookQualityProfileId;
            var ebookQualityProfileId = resource.EbookQualityProfileId;
            var audiobookRootFolderPath = resource.AudiobookRootFolderPath;
            var ebookRootFolderPath = resource.EbookRootFolderPath;

            // Seerr sends monitorNewItems="none" + addOptions.booksToMonitor=[...]
            // Map that to our explicit SpecificBook monitor mode.
            var addOptions = resource.AddOptions;
            NormalizeFacadeBooksToMonitor(addOptions, facadeContext);
            if (addOptions != null &&
                resource.MonitorNewItems == NewItemMonitorTypes.None &&
                addOptions.BooksToMonitor?.Any() == true)
            {
                addOptions.Monitor = MonitorTypes.SpecificBook;
            }

            var useLegacyTags = facadeContext == null && resource.AudiobookTags == null && resource.EbookTags == null && resource.Tags != null;
            var audiobookTags = useLegacyTags ? new HashSet<int>(resource.Tags) : resource.AudiobookTags;
            var ebookTags = useLegacyTags ? new HashSet<int>(resource.Tags) : resource.EbookTags;
            if (facadeContext?.MediaType == "audiobook")
            {
                audiobookTags ??= resource.Tags;
            }
            else if (facadeContext?.MediaType == "ebook")
            {
                ebookTags ??= resource.Tags;
            }

            var audiobookMonitored = resource.AudiobookMonitored;
            var audiobookMonitorNewItems = resource.AudiobookMonitorNewItems;
            var ebookMonitored = resource.EbookMonitored;
            var ebookMonitorNewItems = resource.EbookMonitorNewItems;

            if (facadeContext?.MediaType == "audiobook")
            {
                audiobookMonitored = resource.Monitored ?? false;
                audiobookMonitorNewItems = resource.MonitorNewItems ?? audiobookMonitorNewItems;
            }
            else if (facadeContext?.MediaType == "ebook")
            {
                ebookMonitored = resource.Monitored ?? false;
                ebookMonitorNewItems = resource.MonitorNewItems ?? ebookMonitorNewItems;
            }

            var hasTagInput = resource.Tags != null || resource.AudiobookTags != null || resource.EbookTags != null;
            var combinedTags = facadeContext != null && !hasTagInput
                ? null
                : useLegacyTags
                    ? new HashSet<int>(resource.Tags)
                    : (audiobookTags ?? new HashSet<int>())
                        .Concat(ebookTags ?? new HashSet<int>())
                        .ToHashSet();

            return new NzbDrone.Core.Books.Author
            {
                Id = resource.Id,
                // ForeignAuthorId is mapped to LocalAuthorId for backward compatibility
                TitleSlug = resource.TitleSlug,
                Name = resource.AuthorName,
                NameLastFirst = resource.AuthorNameLastFirst,
                SortName = resource.SortName,
                SortNameLastFirst = resource.SortNameLastFirst,
                Status = AuthorExtensions.GetLifeStatus(resource.Died),
                Overview = resource.Overview,
                Born = resource.Born,
                Died = resource.Died,
                Links = resource.Links ?? new List<Links>(),
                Images = resource.Images ?? new List<MediaCover>(),
                Genres = resource.Genres ?? new List<string>(),
                Ratings = resource.Ratings ?? new Ratings(),

                //AlternateTitles
                Path = resource.Path,
                AudiobookPath = resource.AudiobookPath,
                EbookPath = resource.EbookPath,
                QualityProfileId = resource.QualityProfileId ?? audiobookQualityProfileId ?? ebookQualityProfileId ?? 1,
                BookshelfNGQualityProfileId = resource.OriginalBookshelfQualityProfileIdsCaptured
                    ? resource.OriginalBookshelfNGQualityProfileId
                    : resource.QualityProfileId,
                BookshelfNGAudiobookQualityProfileId = resource.OriginalBookshelfQualityProfileIdsCaptured
                    ? resource.OriginalBookshelfNGAudiobookQualityProfileId
                    : resource.AudiobookQualityProfileId,
                BookshelfNGEbookQualityProfileId = resource.OriginalBookshelfQualityProfileIdsCaptured
                    ? resource.OriginalBookshelfNGEbookQualityProfileId
                    : resource.EbookQualityProfileId,
                AudiobookQualityProfileId = audiobookQualityProfileId,
                EbookQualityProfileId = ebookQualityProfileId,
                MetadataProfileId = resource.MetadataProfileId,
                AudiobookMetadataProfileId = resource.AudiobookMetadataProfileId,
                EbookMetadataProfileId = resource.EbookMetadataProfileId,

                // The persisted legacy field is non-nullable. The service recomputes
                // it from media-specific gates on save; retain the old default here.
                Monitored = resource.Monitored ?? false,
                AudiobookMonitored = audiobookMonitored,
                AudiobookMonitorNewItems = audiobookMonitorNewItems,
                EbookMonitored = ebookMonitored,
                EbookMonitorNewItems = ebookMonitorNewItems,
                SyncMonitoredAcrossFormats = resource.SyncMonitoredAcrossFormats,

                CleanName = resource.CleanName,
                AudiobookRootFolderPath = audiobookRootFolderPath?.GetCleanPath(),
                EbookRootFolderPath = ebookRootFolderPath?.GetCleanPath(),

                AudiobookTags = audiobookTags,
                EbookTags = ebookTags,
                Tags = combinedTags,
                Added = resource.Added,
                AddOptions = addOptions,
                LastSelectedMediaType = resource.LastSelectedMediaType == null
                    ? null
                    : MediaTypeParameterParser.NormalizeOptional(resource.LastSelectedMediaType, allowAll: false),
                // Set provider IDs from ForeignAuthorId
                HardcoverAuthorId = hardcoverAuthorId,
                GoodreadsAuthorId = goodreadsAuthorId,
                OpenLibraryAuthorId = openLibraryAuthorId,
                GoogleBooksAuthorId = googleBooksAuthorId,
                AudnexusAuthorId = audnexusAuthorId,
                RanobeDbAuthorId = ranobeDbAuthorId,
                AdditionalMetadataProviderId = additionalMetadataProviderId
                // LocalAuthorId removed - using database ID directly
            };
        }

        internal static void NormalizeFacadeBooksToMonitor(AddAuthorOptions addOptions, ReadarrFacadeContext facadeContext)
        {
            if (addOptions?.BooksToMonitor == null || facadeContext == null)
            {
                return;
            }

            addOptions.BooksToMonitor = addOptions.BooksToMonitor
                .Select(bookId => NormalizeFacadeBookProviderId(bookId, facadeContext))
                .ToList();
        }

        internal static void NormalizeLegacySingleFields(AuthorResource resource, ReadarrFacadeContext facadeContext, RootFolder legacyRootFolder = null)
        {
            if (resource == null)
            {
                return;
            }

            var audiobookLegacy = TranslateLegacyMediaMonitoring(
                resource.AudiobookMonitorExisting,
                resource.AudiobookMonitorFuture);
            resource.AudiobookMonitored ??= audiobookLegacy.Monitored;
            resource.AudiobookMonitorNewItems ??= audiobookLegacy.MonitorNewItems;
            resource.AudiobookMonitorExistingMode ??= audiobookLegacy.MonitorExistingMode;

            var ebookLegacy = TranslateLegacyMediaMonitoring(
                resource.EbookMonitorExisting,
                resource.EbookMonitorFuture);
            resource.EbookMonitored ??= ebookLegacy.Monitored;
            resource.EbookMonitorNewItems ??= ebookLegacy.MonitorNewItems;
            resource.EbookMonitorExistingMode ??= ebookLegacy.MonitorExistingMode;

            if (facadeContext != null)
            {
                var facadeMonitorNewItems = resource.MonitorNewItems;
                if (facadeContext.MediaType == "audiobook")
                {
                    resource.AudiobookQualityProfileId ??= resource.QualityProfileId;
                    resource.AudiobookMetadataProfileId ??= resource.MetadataProfileId;
                    resource.AudiobookRootFolderPath ??= resource.RootFolderPath;
                    resource.AudiobookTags ??= resource.Tags;
                    resource.AudiobookMonitored = resource.Monitored ?? false;
                    resource.AudiobookMonitorNewItems = facadeMonitorNewItems ?? resource.AudiobookMonitorNewItems;
                }
                else if (facadeContext.MediaType == "ebook")
                {
                    resource.EbookQualityProfileId ??= resource.QualityProfileId;
                    resource.EbookMetadataProfileId ??= resource.MetadataProfileId;
                    resource.EbookRootFolderPath ??= resource.RootFolderPath;
                    resource.EbookTags ??= resource.Tags;
                    resource.EbookMonitored = resource.Monitored ?? false;
                    resource.EbookMonitorNewItems = facadeMonitorNewItems ?? resource.EbookMonitorNewItems;
                }

                NormalizeFacadeBooksToMonitor(resource.AddOptions, facadeContext);
                return;
            }

            var hasFormatSpecificSettings = HasAudiobookSpecificSettings(resource) || HasEbookSpecificSettings(resource);
            var legacyMonitored = hasFormatSpecificSettings ? null : resource.Monitored;
            var legacyMonitorNewItems = hasFormatSpecificSettings ? null : resource.MonitorNewItems;
            var legacyTags = hasFormatSpecificSettings ? null : resource.Tags;
            var hasLegacySingleSettings = legacyMonitored.HasValue ||
                                          legacyMonitorNewItems.HasValue ||
                                          legacyTags != null ||
                                          resource.QualityProfileId.HasValue ||
                                          resource.MetadataProfileId.HasValue ||
                                          !string.IsNullOrWhiteSpace(resource.RootFolderPath);
            if (!hasLegacySingleSettings)
            {
                return;
            }

            var targetAudiobook = legacyRootFolder == null
                ? HasAudiobookSpecificSettings(resource) || !HasEbookSpecificSettings(resource)
                : RootFolderDefaultResolver.IsCompatibleRootFolder(legacyRootFolder, FolderType.Audiobook);
            var targetEbook = legacyRootFolder == null
                ? HasEbookSpecificSettings(resource) || !HasAudiobookSpecificSettings(resource)
                : RootFolderDefaultResolver.IsCompatibleRootFolder(legacyRootFolder, FolderType.Ebook);
            var monitorNewItems = legacyMonitorNewItems;

            if (targetAudiobook)
            {
                resource.AudiobookQualityProfileId ??= resource.QualityProfileId;
                resource.AudiobookMetadataProfileId ??= resource.MetadataProfileId;
                resource.AudiobookRootFolderPath ??= resource.RootFolderPath;
                resource.AudiobookMonitored ??= legacyMonitored ?? false;
                resource.AudiobookMonitorNewItems ??= monitorNewItems;
                if (legacyTags != null)
                {
                    resource.AudiobookTags ??= new HashSet<int>(legacyTags);
                }
            }

            if (targetEbook)
            {
                resource.EbookQualityProfileId ??= resource.QualityProfileId;
                resource.EbookMetadataProfileId ??= resource.MetadataProfileId;
                resource.EbookRootFolderPath ??= resource.RootFolderPath;
                resource.EbookMonitored ??= legacyMonitored ?? false;
                resource.EbookMonitorNewItems ??= monitorNewItems;
                if (legacyTags != null)
                {
                    resource.EbookTags ??= new HashSet<int>(legacyTags);
                }
            }
        }

        private static bool HasAudiobookSpecificSettings(AuthorResource resource)
        {
            return resource.AudiobookQualityProfileId.HasValue ||
                   resource.AudiobookMetadataProfileId.HasValue ||
                   resource.AudiobookRootFolderPath.IsNotNullOrWhiteSpace() ||
                   resource.AudiobookMonitored.HasValue ||
                   resource.AudiobookMonitorExistingMode.HasValue ||
                   resource.AudiobookMonitorNewItems.HasValue ||
                   resource.AudiobookTags != null;
        }

        private static bool HasEbookSpecificSettings(AuthorResource resource)
        {
            return resource.EbookQualityProfileId.HasValue ||
                   resource.EbookMetadataProfileId.HasValue ||
                   resource.EbookRootFolderPath.IsNotNullOrWhiteSpace() ||
                   resource.EbookMonitored.HasValue ||
                   resource.EbookMonitorExistingMode.HasValue ||
                   resource.EbookMonitorNewItems.HasValue ||
                   resource.EbookTags != null;
        }

        private static (bool? Monitored, NewItemMonitorTypes? MonitorNewItems, MonitorTypes? MonitorExistingMode) TranslateLegacyMediaMonitoring(
            int? monitorExisting,
            bool? monitorFuture)
        {
            if (!monitorExisting.HasValue && !monitorFuture.HasValue)
            {
                return (null, null, null);
            }

            bool? monitored = monitorExisting is 1 or 2 || monitorFuture == true
                ? true
                : monitorExisting == 0 ? false : null;
            NewItemMonitorTypes? monitorNewItems = monitorExisting == 1
                ? NewItemMonitorTypes.All
                : monitorFuture == true
                    ? NewItemMonitorTypes.New
                    : NewItemMonitorTypes.None;
            MonitorTypes? monitorExistingMode = monitorExisting switch
            {
                1 => MonitorTypes.All,
                0 or 2 => MonitorTypes.None,
                _ => null
            };

            return (monitored, monitorNewItems, monitorExistingMode);
        }

        private static string NormalizeFacadeBookProviderId(string bookId, ReadarrFacadeContext facadeContext)
        {
            if (string.IsNullOrWhiteSpace(bookId))
            {
                return bookId;
            }

            var trimmed = bookId.Trim();
            if (!long.TryParse(trimmed, out _))
            {
                return bookId;
            }

            if (facadeContext.IsHardcover)
            {
                return "hc:" + trimmed;
            }

            if (facadeContext.IsGoodreads)
            {
                return "gr:" + trimmed;
            }

            return bookId;
        }

        public static NzbDrone.Core.Books.Author ToModel(this AuthorResource resource, NzbDrone.Core.Books.Author author)
        {
            return resource.ToModel(author, null);
        }

        public static NzbDrone.Core.Books.Author ToModel(this AuthorResource resource, NzbDrone.Core.Books.Author author, ReadarrFacadeContext facadeContext)
        {
            var updatedAuthor = resource.ToModel(facadeContext);
            if (facadeContext != null)
            {
                PreserveStoredAuthorStateForFacadeUpdate(resource, author, updatedAuthor, facadeContext);
            }

            if (!resource.SyncMonitoredAcrossFormats.HasValue)
            {
                updatedAuthor.SyncMonitoredAcrossFormats = author.SyncMonitoredAcrossFormats;
            }
            author.ApplyChanges(updatedAuthor);
            return author;
        }

        private static void PreserveStoredAuthorStateForFacadeUpdate(AuthorResource resource, NzbDrone.Core.Books.Author storedAuthor, NzbDrone.Core.Books.Author updatedAuthor, ReadarrFacadeContext facadeContext)
        {
            if (resource == null || storedAuthor == null || updatedAuthor == null || facadeContext == null)
            {
                return;
            }

            if (resource.Path == null)
            {
                updatedAuthor.Path = storedAuthor.Path;
            }

            if (resource.AudiobookPath == null)
            {
                updatedAuthor.AudiobookPath = storedAuthor.AudiobookPath;
            }

            if (resource.EbookPath == null)
            {
                updatedAuthor.EbookPath = storedAuthor.EbookPath;
            }

            if (resource.AddOptions == null)
            {
                updatedAuthor.AddOptions = storedAuthor.AddOptions;
            }

            if (resource.LastSelectedMediaType == null)
            {
                updatedAuthor.LastSelectedMediaType = storedAuthor.LastSelectedMediaType;
            }

            if (facadeContext.MediaType == "audiobook")
            {
                PreserveEbookAuthorState(storedAuthor, updatedAuthor);

                if (!resource.QualityProfileId.HasValue && !resource.AudiobookQualityProfileId.HasValue)
                {
                    updatedAuthor.AudiobookQualityProfileId = storedAuthor.AudiobookQualityProfileId;
                }

                if (!resource.MetadataProfileId.HasValue && !resource.AudiobookMetadataProfileId.HasValue)
                {
                    updatedAuthor.AudiobookMetadataProfileId = storedAuthor.AudiobookMetadataProfileId;
                }

                if (resource.RootFolderPath == null && resource.AudiobookRootFolderPath == null)
                {
                    updatedAuthor.AudiobookRootFolderPath = storedAuthor.AudiobookRootFolderPath;
                }

                if (resource.Tags == null && resource.AudiobookTags == null)
                {
                    updatedAuthor.AudiobookTags = CloneTagsOrNull(storedAuthor.AudiobookTags);
                }

                if (resource.MonitorNewItems == null && !resource.AudiobookMonitorNewItems.HasValue)
                {
                    updatedAuthor.AudiobookMonitorNewItems = storedAuthor.AudiobookMonitorNewItems;
                }
            }
            else if (facadeContext.MediaType == "ebook")
            {
                PreserveAudiobookAuthorState(storedAuthor, updatedAuthor);

                if (!resource.QualityProfileId.HasValue && !resource.EbookQualityProfileId.HasValue)
                {
                    updatedAuthor.EbookQualityProfileId = storedAuthor.EbookQualityProfileId;
                }

                if (!resource.MetadataProfileId.HasValue && !resource.EbookMetadataProfileId.HasValue)
                {
                    updatedAuthor.EbookMetadataProfileId = storedAuthor.EbookMetadataProfileId;
                }

                if (resource.RootFolderPath == null && resource.EbookRootFolderPath == null)
                {
                    updatedAuthor.EbookRootFolderPath = storedAuthor.EbookRootFolderPath;
                }

                if (resource.Tags == null && resource.EbookTags == null)
                {
                    updatedAuthor.EbookTags = CloneTagsOrNull(storedAuthor.EbookTags);
                }

                if (resource.MonitorNewItems == null && !resource.EbookMonitorNewItems.HasValue)
                {
                    updatedAuthor.EbookMonitorNewItems = storedAuthor.EbookMonitorNewItems;
                }
            }

            updatedAuthor.Tags = (updatedAuthor.AudiobookTags ?? new HashSet<int>())
                .Concat(updatedAuthor.EbookTags ?? new HashSet<int>())
                .ToHashSet();
        }

        private static void PreserveAudiobookAuthorState(NzbDrone.Core.Books.Author storedAuthor, NzbDrone.Core.Books.Author updatedAuthor)
        {
            updatedAuthor.AudiobookQualityProfileId = storedAuthor.AudiobookQualityProfileId;
            updatedAuthor.AudiobookMetadataProfileId = storedAuthor.AudiobookMetadataProfileId;
            updatedAuthor.AudiobookRootFolderPath = storedAuthor.AudiobookRootFolderPath;
            updatedAuthor.AudiobookMonitored = storedAuthor.AudiobookMonitored;
            updatedAuthor.AudiobookMonitorNewItems = storedAuthor.AudiobookMonitorNewItems;
            updatedAuthor.AudiobookTags = CloneTagsOrNull(storedAuthor.AudiobookTags);
        }

        private static void PreserveEbookAuthorState(NzbDrone.Core.Books.Author storedAuthor, NzbDrone.Core.Books.Author updatedAuthor)
        {
            updatedAuthor.EbookQualityProfileId = storedAuthor.EbookQualityProfileId;
            updatedAuthor.EbookMetadataProfileId = storedAuthor.EbookMetadataProfileId;
            updatedAuthor.EbookRootFolderPath = storedAuthor.EbookRootFolderPath;
            updatedAuthor.EbookMonitored = storedAuthor.EbookMonitored;
            updatedAuthor.EbookMonitorNewItems = storedAuthor.EbookMonitorNewItems;
            updatedAuthor.EbookTags = CloneTagsOrNull(storedAuthor.EbookTags);
        }

        private static HashSet<int> CloneTagsOrNull(HashSet<int> tags)
        {
            return tags != null ? new HashSet<int>(tags) : null;
        }

        public static List<AuthorResource> ToResource(this IEnumerable<NzbDrone.Core.Books.Author> author)
        {
            return author.Select(ToResource).ToList();
        }

        public static List<AuthorResource> ToResource(this IEnumerable<NzbDrone.Core.Books.Author> author, ReadarrFacadeContext facadeContext)
        {
            var resources = author.Select(model => model.ToResource(facadeContext)).ToList();
            WarnFacadeIdentityGaps(resources, facadeContext, "author response");
            return resources;
        }

        public static void WarnFacadeIdentityGaps(IEnumerable<AuthorResource> resources, ReadarrFacadeContext facadeContext, string source)
        {
            if (facadeContext == null)
            {
                return;
            }

            var missingCount = resources?.Count(resource => resource != null && string.IsNullOrWhiteSpace(resource.ForeignAuthorId)) ?? 0;
            if (missingCount == 0)
            {
                return;
            }

            Logger.Warn("[ReadarrFacade] Emitted {0} author resource(s) without {1} identity from {2}. Compatibility ID left blank instead of falling back across providers.",
                missingCount,
                facadeContext.Dialect,
                source ?? "author response");
        }

        public static List<NzbDrone.Core.Books.Author> ToModel(this IEnumerable<AuthorResource> resources)
        {
            return resources.Select(ToModel).ToList();
        }

        internal static void MapBookshelfQualityProfileIds(
            AuthorResource resource,
            IEnumerable<BookshelfLegacyIdentityMapEntry> mappings,
            BookMediaType? mediaType = null)
        {
            if (resource == null)
            {
                return;
            }

            if (!resource.OriginalBookshelfQualityProfileIdsCaptured)
            {
                resource.OriginalBookshelfNGQualityProfileId = resource.QualityProfileId;
                resource.OriginalBookshelfNGAudiobookQualityProfileId = resource.AudiobookQualityProfileId;
                resource.OriginalBookshelfNGEbookQualityProfileId = resource.EbookQualityProfileId;
                resource.OriginalBookshelfQualityProfileIdsCaptured = true;
            }
            var identityMappings = mappings?.ToList() ?? new List<BookshelfLegacyIdentityMapEntry>();

            int? Map(int? sourceId, int mediaType, string fieldName)
            {
                if (!sourceId.HasValue || sourceId.Value <= 0)
                {
                    return sourceId;
                }

                var sourceMappings = identityMappings.Where(mapping => mapping.SourceId == sourceId.Value).ToList();
                if (sourceMappings.Count == 0)
                {
                    return sourceId;
                }

                var matchingTargets = sourceMappings
                    .Where(mapping => mapping.MediaType == mediaType)
                    .Select(mapping => mapping.TargetId)
                    .Distinct()
                    .ToList();

                if (matchingTargets.Count == 1)
                {
                    return matchingTargets[0];
                }

                throw new ValidationException(new[]
                {
                    new ValidationFailure(fieldName, "The BookshelfNG quality profile mapping is missing or ambiguous for this media type.")
                });
            }

            resource.QualityProfileId = Map(resource.QualityProfileId, (int)(mediaType ?? BookMediaType.Audiobook), "QualityProfileId");
            resource.AudiobookQualityProfileId = Map(
                resource.AudiobookQualityProfileId,
                (int)BookMediaType.Audiobook,
                "AudiobookQualityProfileId");
            resource.EbookQualityProfileId = Map(
                resource.EbookQualityProfileId,
                (int)BookMediaType.Ebook,
                "EbookQualityProfileId");
        }
    }
}
