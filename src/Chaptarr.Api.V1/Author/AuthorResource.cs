using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.Books;
using Chaptarr.Http.Middleware;
using Chaptarr.Http.REST;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Validation;

namespace Chaptarr.Api.V1.Author
{
    public class AuthorResource : RestResource
    {
        //Todo: Sorters should be done completely on the client
        //Todo: Is there an easy way to keep IgnoreArticlesWhenSorting in sync between, Series, History, Missing?
        //Todo: We should get the entire Profile instead of ID and Name separately
        public AuthorStatusType Status { get; set; }

        public bool Ended => Status == AuthorStatusType.Ended;

        public string AuthorName { get; set; }
        public string AuthorNameLastFirst { get; set; }
        public string ForeignAuthorId { get; set; }
        public string TitleSlug { get; set; }
        public string Overview { get; set; }
        public string Disambiguation { get; set; }
        public List<Links> Links { get; set; }

        public BookResource NextBook { get; set; }
        public BookResource LastBook { get; set; }

        public List<MediaCover> Images { get; set; }

        public string RemotePoster { get; set; }

        //View & Edit
        public string Path { get; set; }
        // Readarr/Seerr compatibility (single profile + root folder)
        public int? QualityProfileId { get; set; }
        public int? AudiobookQualityProfileId { get; set; }
        public int? EbookQualityProfileId { get; set; }
        public int? MetadataProfileId { get; set; }  // DEPRECATED: Made nullable to handle legacy data gracefully
        
        // Per-type metadata profiles
        public int? AudiobookMetadataProfileId { get; set; }
        public int? EbookMetadataProfileId { get; set; }

        //Editing Only
        public bool Monitored { get; set; }
        // TRI-STATE MONITORING SYSTEM - Integer per media type
        // Values: 0 = None (monitor nothing), 1 = All (monitor everything), 2 = Selected (monitor specific books only)
        // NULL = not configured for this media type yet (treated as unmonitored until root-folder discovery or user config)
        public int? AudiobookMonitorExisting { get; set; } // 0=None, 1=All, 2=Selected, NULL=unconfigured
        public bool? AudiobookMonitorFuture { get; set; } // true=monitor, false=don't monitor, NULL=unconfigured
        public int? EbookMonitorExisting { get; set; } // 0=None, 1=All, 2=Selected, NULL=unconfigured
        public bool? EbookMonitorFuture { get; set; } // true=monitor, false=don't monitor, NULL=unconfigured
        public bool? SyncMonitoredAcrossFormats { get; set; }

        public string AudiobookRootFolderPath { get; set; }
        public string EbookRootFolderPath { get; set; }
        // Readarr/Seerr compatibility (single root folder)
        public string RootFolderPath { get; set; }
        // Readarr/Seerr compatibility (monitor mode string, e.g. "none")
        public string MonitorNewItems { get; set; }
        public string Folder { get; set; }
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

            // Do not expose a provider placeholder while an existing row waits for the
            // startup repair or its next metadata refresh to scrub persistence.
            var displayImages = MediaCoverRendition.SelectCandidates(model.Images).ToList();

            var resource = new AuthorResource
            {
                Id = model.Id,

                AuthorName = model.Name,
                AuthorNameLastFirst = model.NameLastFirst,

                //AlternateTitles
                SortName = model.SortName,
                SortNameLastFirst = model.SortNameLastFirst,

                Status = model.Status,
                Overview = model.Overview,
                Disambiguation = model.Disambiguation,

                Images = displayImages.JsonClone(),

                Path = model.Path,
                AudiobookQualityProfileId = model.AudiobookQualityProfileId,
                EbookQualityProfileId = model.EbookQualityProfileId,
                MetadataProfileId = model.MetadataProfileId,
                AudiobookMetadataProfileId = model.AudiobookMetadataProfileId,
                EbookMetadataProfileId = model.EbookMetadataProfileId,
                Links = CloneLinks(model.Links),

                Monitored = model.Monitored,
                // TRI-STATE MONITORING SYSTEM
                AudiobookMonitorExisting = model.AudiobookMonitorExisting,
                AudiobookMonitorFuture = model.AudiobookMonitorFuture,
                EbookMonitorExisting = model.EbookMonitorExisting,
                EbookMonitorFuture = model.EbookMonitorFuture,
                SyncMonitoredAcrossFormats = model.SyncMonitoredAcrossFormats,

                CleanName = model.CleanName,
                ForeignAuthorId = BuildForeignAuthorId(model, facadeContext),
                TitleSlug = model.TitleSlug,

                AudiobookRootFolderPath = model.AudiobookRootFolderPath?.GetCleanPath(),
                EbookRootFolderPath = model.EbookRootFolderPath?.GetCleanPath(),
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
                resource.Monitored = (model.AudiobookMonitorExisting ?? 0) > 0 || (model.AudiobookMonitorFuture ?? false);
                resource.MonitorNewItems = ToMonitorNewItems(model.AudiobookMonitorFuture);
            }
            else if (facadeContext?.MediaType == "ebook")
            {
                resource.QualityProfileId = resource.EbookQualityProfileId;
                resource.MetadataProfileId = resource.EbookMetadataProfileId ?? resource.MetadataProfileId;
                resource.RootFolderPath = resource.EbookRootFolderPath;
                resource.Tags = resource.EbookTags ?? new HashSet<int>();
                resource.Monitored = (model.EbookMonitorExisting ?? 0) > 0 || (model.EbookMonitorFuture ?? false);
                resource.MonitorNewItems = ToMonitorNewItems(model.EbookMonitorFuture);
            }
        }

        private static string ToMonitorNewItems(bool? monitorFuture)
        {
            return monitorFuture == false ? "none" : "all";
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

            // Parse provider IDs from ForeignAuthorId - keep the full ID with prefix
            string hardcoverAuthorId = null;
            string goodreadsAuthorId = null;
            string openLibraryAuthorId = null;
            string googleBooksAuthorId = null;
            string audnexusAuthorId = null;
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
                    }
                }
            }

            // Readarr/Seerr compatibility: when only the legacy single fields are provided,
            // native/bare compatibility keeps the old both-media behavior. Dialect facades project
            // a single fake Readarr instance, so those fields write only the active media side.
            var audiobookQualityProfileId = resource.AudiobookQualityProfileId;
            var ebookQualityProfileId = resource.EbookQualityProfileId;
            var audiobookRootFolderPath = resource.AudiobookRootFolderPath;
            var ebookRootFolderPath = resource.EbookRootFolderPath;

            if (facadeContext?.MediaType == "audiobook")
            {
                audiobookQualityProfileId ??= resource.QualityProfileId;
                audiobookRootFolderPath ??= resource.RootFolderPath;
            }
            else if (facadeContext?.MediaType == "ebook")
            {
                ebookQualityProfileId ??= resource.QualityProfileId;
                ebookRootFolderPath ??= resource.RootFolderPath;
            }
            else
            {
                audiobookQualityProfileId ??= resource.QualityProfileId;
                ebookQualityProfileId ??= resource.QualityProfileId;
                audiobookRootFolderPath ??= resource.RootFolderPath;
                ebookRootFolderPath ??= resource.RootFolderPath;
            }

            // Seerr sends monitorNewItems="none" + addOptions.booksToMonitor=[...]
            // Map that to our explicit SpecificBook monitor mode.
            var addOptions = resource.AddOptions;
            NormalizeFacadeBooksToMonitor(addOptions, facadeContext);
            if (addOptions != null &&
                string.Equals(resource.MonitorNewItems, "none", StringComparison.OrdinalIgnoreCase) &&
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

            var audiobookMonitorExisting = resource.AudiobookMonitorExisting;
            var audiobookMonitorFuture = resource.AudiobookMonitorFuture;
            var ebookMonitorExisting = resource.EbookMonitorExisting;
            var ebookMonitorFuture = resource.EbookMonitorFuture;

            if (facadeContext?.MediaType == "audiobook")
            {
                audiobookMonitorExisting ??= resource.Monitored ? 1 : 0;
                audiobookMonitorFuture ??= resource.Monitored;
            }
            else if (facadeContext?.MediaType == "ebook")
            {
                ebookMonitorExisting ??= resource.Monitored ? 1 : 0;
                ebookMonitorFuture ??= resource.Monitored;
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
                Status = resource.Status,
                Overview = resource.Overview,
                Links = resource.Links ?? new List<Links>(),
                Images = resource.Images ?? new List<MediaCover>(),
                Genres = resource.Genres ?? new List<string>(),
                Ratings = resource.Ratings ?? new Ratings(),

                //AlternateTitles
                Path = resource.Path,
                AudiobookQualityProfileId = audiobookQualityProfileId,
                EbookQualityProfileId = ebookQualityProfileId,
                MetadataProfileId = resource.MetadataProfileId,
                AudiobookMetadataProfileId = resource.AudiobookMetadataProfileId,
                EbookMetadataProfileId = resource.EbookMetadataProfileId,

                Monitored = resource.Monitored,
                // TRI-STATE MONITORING SYSTEM
                AudiobookMonitorExisting = audiobookMonitorExisting,
                AudiobookMonitorFuture = audiobookMonitorFuture,
                EbookMonitorExisting = ebookMonitorExisting,
                EbookMonitorFuture = ebookMonitorFuture,
                SyncMonitoredAcrossFormats = resource.SyncMonitoredAcrossFormats,

                CleanName = resource.CleanName,
                AudiobookRootFolderPath = audiobookRootFolderPath?.GetCleanPath(),
                EbookRootFolderPath = ebookRootFolderPath?.GetCleanPath(),

                AudiobookTags = audiobookTags,
                EbookTags = ebookTags,
                Tags = combinedTags,
                Added = resource.Added,
                AddOptions = addOptions,
                LastSelectedMediaType = resource.LastSelectedMediaType,
                // Set provider IDs from ForeignAuthorId
                HardcoverAuthorId = hardcoverAuthorId,
                GoodreadsAuthorId = goodreadsAuthorId,
                OpenLibraryAuthorId = openLibraryAuthorId,
                GoogleBooksAuthorId = googleBooksAuthorId,
                AudnexusAuthorId = audnexusAuthorId
                // LocalAuthorId removed - using database ID directly
            };
        }

        private static void NormalizeFacadeBooksToMonitor(AddAuthorOptions addOptions, ReadarrFacadeContext facadeContext)
        {
            if (addOptions?.BooksToMonitor == null || facadeContext == null)
            {
                return;
            }

            addOptions.BooksToMonitor = addOptions.BooksToMonitor
                .Select(bookId => NormalizeFacadeBookProviderId(bookId, facadeContext))
                .ToList();
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
            updatedAuthor.AudiobookMonitorExisting = storedAuthor.AudiobookMonitorExisting;
            updatedAuthor.AudiobookMonitorFuture = storedAuthor.AudiobookMonitorFuture;
            updatedAuthor.AudiobookTags = CloneTagsOrNull(storedAuthor.AudiobookTags);
        }

        private static void PreserveEbookAuthorState(NzbDrone.Core.Books.Author storedAuthor, NzbDrone.Core.Books.Author updatedAuthor)
        {
            updatedAuthor.EbookQualityProfileId = storedAuthor.EbookQualityProfileId;
            updatedAuthor.EbookMetadataProfileId = storedAuthor.EbookMetadataProfileId;
            updatedAuthor.EbookRootFolderPath = storedAuthor.EbookRootFolderPath;
            updatedAuthor.EbookMonitorExisting = storedAuthor.EbookMonitorExisting;
            updatedAuthor.EbookMonitorFuture = storedAuthor.EbookMonitorFuture;
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
    }
}
