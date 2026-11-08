using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Chaptarr.Api.V1.Author;
using Chaptarr.Api.V1.BookFiles;
using Chaptarr.Api.V1.MediaTypes;
using Chaptarr.Http.Middleware;
using Chaptarr.Http.REST;
using FluentValidation;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.AuthorStats;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Parser;
using Swashbuckle.AspNetCore.Annotations;

namespace Chaptarr.Api.V1.Books
{
    public class BookResource : RestResource
    {
        public string Title { get; set; }
        public string AuthorTitle { get; set; }
        public string SeriesTitle { get; set; }
        [SwaggerSchema("Ordered labels for all same-media Goodreads series memberships; submitted changes are ignored.", ReadOnly = true)]
        public List<string> SeriesLabels { get; set; }
        public string Disambiguation { get; set; }
        public string Overview { get; set; }
        public int AuthorId { get; set; }
        public string ForeignBookId { get; set; }  // Chaptarr-native IDs are prefixed; Readarr facades emit bare IDs in the requested dialect.
        public string LocalBookId { get; set; }
        public string ForeignEditionId { get; set; }
        public string BaseBookId { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [SwaggerSchema("An existing copy of this provider edition (on any audiobook row of the same work) was returned unchanged; nothing was changed or searched.", ReadOnly = true)]
        public bool? ExistingCopy { get; set; }

        // Individual provider IDs for frontend compatibility
        public string HardcoverBookId { get; set; }
        public string GoodreadsBookId { get; set; }
        public string GoodreadsWorkId { get; set; }
        public string OpenLibraryWorkId { get; set; }
        public string GoogleBooksId { get; set; }
        public string ASIN { get; set; }
        public string AudibleASIN { get; set; }
        public string TitleSlug { get; set; }
        private bool? _monitored;
        public bool Monitored
        {
            get => _monitored.GetValueOrDefault();
            set => _monitored = value;
        }

        public bool? AudiobookMonitored { get; set; }
        public bool? EbookMonitored { get; set; }
        private bool? _anyEditionOk;
        public bool AnyEditionOk
        {
            get => _anyEditionOk.GetValueOrDefault();
            set => _anyEditionOk = value;
        }

        internal bool AnyEditionOkSpecified => _anyEditionOk.HasValue;
        [SwaggerSchema("Provider metadata; changes submitted to PUT are ignored.", ReadOnly = true)]
        public Ratings Ratings
        {
            get => _ratings;
            set { _ratings = value; RatingsSpecified = true; }
        }
        private Ratings _ratings;
        internal bool RatingsSpecified { get; private set; }
        [SwaggerSchema("Provider metadata; changes submitted to PUT are ignored.", ReadOnly = true)]
        public DateTime? ReleaseDate
        {
            get => _releaseDate;
            set { _releaseDate = value; ReleaseDateSpecified = true; }
        }
        private DateTime? _releaseDate;
        internal bool ReleaseDateSpecified { get; private set; }
        public DateTime Added { get; set; }
        public int PageCount { get; set; }
        public List<string> Genres { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public AuthorResource Author { get; set; }
        public List<MediaCover> Images { get; set; }
        public List<Links> Links { get; set; }
        public BookStatisticsResource Statistics { get; set; }
        public AddBookOptions AddOptions { get; set; }
        public string RemoteCover { get; set; }
        public List<BookFileResource> BookFiles { get; set; }
        public List<EditionResource> Editions { get; set; }
        public List<BookLocalInstanceResource> LocalAudiobookBooks { get; set; }
        public List<BookLocalInstanceResource> LocalEbookBooks { get; set; }

        // Narrator info
        public string MediaType { get; set; }
        public string InstanceCombinedId { get; set; }
        private string _narrator;
        public string Narrator
        {
            get => _narrator;
            set
            {
                _narrator = value;
                NarratorSpecified = true;
            }
        }

        // An omitted PUT field is not an explicit request to clear the stored narrator.
        internal bool NarratorSpecified { get; private set; }
        // For client-side search/filtering: all narrator names available across editions (even when Narrator display is hidden).
        public List<string> AvailableNarrators { get; set; }
        [SwaggerSchema(ReadOnly = true)]
        public List<NarratorResource> AvailableNarratorCredits => NarratorResource.FromNames(AvailableNarrators);
        // Narrator names for the monitored edition (used for "Full Cast" tooltips and per-edition display).
        public List<string> NarratorNames { get; set; }
        [SwaggerSchema(ReadOnly = true)]
        public List<NarratorResource> Narrators => NarratorResource.FromNames(NarratorNames, Narrator);
        public TimeSpan? Duration { get; set; }
        [SwaggerSchema("Provider metadata; changes submitted to PUT are ignored.", ReadOnly = true)]
        public double? DurationMinutes
        {
            get => _durationMinutes;
            set { _durationMinutes = value; DurationMinutesSpecified = true; }
        }
        private double? _durationMinutes;
        internal bool DurationMinutesSpecified { get; private set; }

        // Indicates if this book has any physical files on disk
        public bool HasFiles { get; set; }

        // Omnibus/Collection flag - true for box sets, anthologies, complete series collections
        public bool IsOmnibus { get; set; }

        // For multi-edition display
        public List<string> Formats { get; set; }
        public string FileSizeOnDisk { get; set; }

        //Hiding this so people don't think its usable (only used to set the initial state)
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Grabbed { get; set; }
    }

    public class BookLocalInstanceResource
    {
        public int Id { get; set; }
        public string Title { get; set; }
        public string TitleSlug { get; set; }
        public string MediaType { get; set; }
        public string Narrator { get; set; }
        public string Disambiguation { get; set; }
        public bool Monitored { get; set; }
        public bool HasFiles { get; set; }
    }

    public sealed class BookResourceMappingOptions
    {
        public bool IncludeAuthor { get; set; } = true;
        public bool IncludeOverview { get; set; } = true;
        public bool IncludeLinks { get; set; } = true;
        public BookStatistics Statistics { get; set; }
        public ReadarrFacadeContext FacadeContext { get; set; }

        public static BookResourceMappingOptions Rich(bool includeAuthor = true)
        {
            return new BookResourceMappingOptions
            {
                IncludeAuthor = includeAuthor,
                IncludeOverview = true,
                IncludeLinks = true
            };
        }

        public static BookResourceMappingOptions Lean(BookStatistics statistics = null, bool includeAuthor = false, bool includeOverview = false, bool includeLinks = false)
        {
            return new BookResourceMappingOptions
            {
                IncludeAuthor = includeAuthor,
                IncludeOverview = includeOverview,
                IncludeLinks = includeLinks,
                Statistics = statistics
            };
        }
    }

    public static class BookResourceMapper
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private static List<MediaCover> CloneImages(IEnumerable<MediaCover> images)
        {
            return (images ?? Enumerable.Empty<MediaCover>())
                .Where(image => image != null && !string.IsNullOrWhiteSpace(image.Url))
                .Select(image => new MediaCover(image.CoverType, image.Url)
                {
                    RemoteUrl = image.RemoteUrl,
                    TemporaryUrl = image.TemporaryUrl,
                    Hash = image.Hash
                })
                .ToList();
        }

        public static BookResource ToResource(this Book model)
        {
            return ToResource(model, BookResourceMappingOptions.Rich());
        }

        public static BookResource ToResource(this Book model, BookResourceMappingOptions options)
        {
            if (model == null)
            {
                return null;
            }

            options ??= BookResourceMappingOptions.Rich();

            var selectedEdition = SelectMonitoredEdition(model.Editions);

            var seriesLabels = BookSeries.GetLinks(model).Select(BookSeries.Label).ToList();
            var headerSeries = BookSeries.GetHeaderLink(model);
            var seriesTitle = headerSeries != null ? BookSeries.Label(headerSeries) : seriesLabels.ConcatToString("; ");

            var title = BookTitleDisplay.GetTitle(model.Title, selectedEdition?.Title, seriesLabels);

            var authorTitle = $"{model.Author?.SortNameLastFirst} {title}";
            var hasFiles = options.Statistics != null
                ? options.Statistics.BookFileCount > 0
                : model.HasFiles;

            // Readarr facade compatibility: foreignBookId is bare only inside a path-declared
            // provider dialect. Native Chaptarr responses keep provider prefixes.
            string foreignBookId = BuildForeignBookId(model, options.FacadeContext);

            var links = options.IncludeLinks
                ? ((model.Links != null && model.Links.Any())
                    ? model.Links
                    : BuildFallbackLinks(model, selectedEdition))
                : null;

            if (options.IncludeLinks)
            {
                links = NormalizeLinks(model, selectedEdition, title, links);
            }

            var resource = new BookResource
            {
                Id = model.Id,
                AuthorId = model.AuthorId,
                Author = options.IncludeAuthor ? model.Author?.ToResource(options.FacadeContext) : null,
                ForeignBookId = foreignBookId,
                LocalBookId = model.Id.ToString(),
                ForeignEditionId = ReadarrFacadeEditionIdentity.BuildForeignEditionId(selectedEdition, options.FacadeContext),
                BaseBookId = model.BaseBookId,

                // Map individual provider IDs from the model
                HardcoverBookId = model.HardcoverBookId,
                GoodreadsBookId = BookEditionIdentity.GetGoodreadsEditionProviderId(model),
                GoodreadsWorkId = model.GoodreadsWorkId,
                OpenLibraryWorkId = model.OpenLibraryWorkId,
                GoogleBooksId = BookEditionIdentity.GetGoogleBooksEditionId(model),
                ASIN = BookEditionIdentity.GetAsin(model),
                AudibleASIN = BookEditionIdentity.GetAudibleAsin(model),

                TitleSlug = model.TitleSlug,
                Monitored = model.IsMonitored(),
                AudiobookMonitored = model.AudiobookMonitored,
                EbookMonitored = model.EbookMonitored,
                AnyEditionOk = model.AnyEditionOk,
                Ratings = selectedEdition != null
                    ? (selectedEdition.Ratings ?? new Ratings())
                    : (model.Ratings ?? new Ratings()),
                ReleaseDate = selectedEdition?.ReleaseDate ?? model.ReleaseDate,
                Added = model.Added,
                // Don't show page count for audiobooks
                PageCount = model.MediaType == BookMediaType.Audiobook
                    ? 0
                    : (selectedEdition?.PageCount ?? 0),
                Genres = (model.Genres ?? new List<string>()).ToList(),
                Title = title,
                AuthorTitle = authorTitle,
                SeriesTitle = seriesTitle,
                SeriesLabels = seriesLabels,
                Disambiguation = selectedEdition?.Disambiguation,
                Overview = options.IncludeOverview && !string.IsNullOrWhiteSpace(selectedEdition?.Overview)
                    ? selectedEdition.Overview
                    : (options.IncludeOverview ? (model.Overview ?? string.Empty) : null),
                Images = CloneImages(selectedEdition != null ? selectedEdition.Images : model.Images),
                Links = links,
                Statistics = options.Statistics?.ToResource(),
                AddOptions = model.AddOptions,
                RemoteCover = string.Empty,
                Grabbed = false,
                Narrator = GetNarratorDisplay(model, selectedEdition, hasFiles),
                AvailableNarrators = GetAvailableNarrators(model),
                NarratorNames = GetNarratorNamesForDisplay(model, selectedEdition),
                Duration = GetDuration(model, selectedEdition),
                DurationMinutes = GetDurationMinutes(model, selectedEdition),
                MediaType = model.MediaType == BookMediaType.Audiobook ? "audiobook" : "ebook",
                HasFiles = hasFiles,
                IsOmnibus = model.IsOmnibus
            };

            // Gate narrator display for V1 (audiobooks only): show names only when they come from files
            // or an explicitly pinned edition selection. Narrator is edition metadata, not an API identity.
            var hasPinnedEdition = !model.AnyEditionOk;

            if (model.MediaType == BookMediaType.Audiobook && !(hasFiles || hasPinnedEdition))
            {
                // Hide narrator fields when not eligible
                resource.NarratorNames = new List<string>();
            }

            return resource;
        }

            private static List<Links> BuildFallbackLinks(Book book, Edition selectedEdition)
            {
                // No client-side URL fabrication. All provider links must come from
                // the metadata server via Book.ProviderUrls / Edition.ProviderUrls.
                return new List<Links>();
            }

            private static List<Links> NormalizeLinks(Book book, Edition selectedEdition, string displayTitle, List<Links> links)
            {
                var normalized = new List<Links>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                void AddIfNew(string name, string url)
                {
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        return;
                    }

                    var key = $"{(name ?? string.Empty).Trim()}|{url.Trim()}";
                    if (seen.Add(key))
                    {
                        normalized.Add(new Links { Name = name, Url = url.Trim() });
                    }
                }

                var providerUrlMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (book?.ProviderUrls != null)
                {
                    foreach (var kvp in book.ProviderUrls)
                    {
                        if (kvp.Key.Equals("_metadata", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var name = kvp.Key?.Trim();
                        var url = kvp.Value?.ToString()?.Trim();
                        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
                        {
                            continue;
                        }

                        providerUrlMap[name] = url;
                    }
                }

                // Merge selected edition's provider URLs (edition-level links from upstream).
                // Edition URLs are more specific (e.g. GR edition ID vs work ID) so they
                // override any work-level entry for the same provider.
                if (selectedEdition?.ProviderUrls != null)
                {
                    foreach (var kvp in selectedEdition.ProviderUrls)
                    {
                        if (kvp.Key.Equals("_metadata", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var name = kvp.Key?.Trim();
                        var url = kvp.Value?.ToString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(url))
                        {
                            providerUrlMap[name] = url;
                        }
                    }
                }

                foreach (var link in links ?? new List<Links>())
                {
                    var name = link?.Name?.Trim();
                    var url = link?.Url?.Trim();
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        continue;
                    }

                    // Prefer providerUrls (e.g. Hardcover slug URLs) over legacy/constructed Links.
                    if (!string.IsNullOrWhiteSpace(name) && providerUrlMap.ContainsKey(name))
                    {
                        continue;
                    }

                    AddIfNew(name, url);
                }

                foreach (var kvp in providerUrlMap)
                {
                    AddIfNew(kvp.Key, kvp.Value);
                }

                // Ensure we still have some links if everything was filtered out.
                if (normalized.Count == 0 && links != null)
                {
                    foreach (var link in links)
                    {
                        AddIfNew(link?.Name, link?.Url);
                    }
                }

                return normalized;
            }

                private static string GetNarratorDisplay(Book book, Edition edition, bool? hasFiles = null)
                {
                    if (hasFiles == false && book.MediaType == BookMediaType.Audiobook && book.AnyEditionOk)
                    {
                        return null;
                    }

                    var narrators = GetNarratorNamesForDisplay(book, edition);
                    if (narrators.Count > 0)
                    {
                        // Do not fabricate "Full Cast". If we have real narrator names, join them for display.
                        return string.Join(", ", narrators);
                    }

                    // Second: fall back to edition narrator string if names list is empty
                    if (!string.IsNullOrWhiteSpace(edition?.Narrator))
                    {
                        return edition.Narrator;
                }

                    return null;
                }

                    private static List<string> GetNarratorNamesForDisplay(Book book, Edition edition)
                    {
                        if (edition?.NarratorNames?.Any() == true)
                        {
                            return edition.NarratorNames
                                .Where(x => x.IsNotNullOrWhiteSpace())
                                .Select(x => x.Trim())
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToList();
                        }

                    // Back-compat / resilience: some DBs may only have the narrator string populated (or have it
                    // populated with a multi-narrator list). Try to split it into individual narrators.
                    var parsed = SplitNarratorList(edition?.Narrator);
                        if (parsed.Any())
                        {
                            return parsed;
                        }

                        return new List<string>();
                    }

                private static List<string> SplitNarratorList(string narrator)
                {
                    if (string.IsNullOrWhiteSpace(narrator))
                    {
                        return new List<string>();
                    }

                    // Start with separators that are unlikely to be part of a single person's name.
                    // Avoid splitting on ',' unless it clearly looks like a list (multiple commas), to prevent
                    // breaking "Last, First" style names.
                    var separators = new[] { "+", "&", " and ", " with ", " featuring ", "/", ";" };

                    var parts = new List<string> { narrator };

                    foreach (var separator in separators)
                    {
                        var newParts = new List<string>();
                        foreach (var part in parts)
                        {
                            if (part.IndexOf(separator, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                newParts.AddRange(part
                                    .Split(new[] { separator }, StringSplitOptions.RemoveEmptyEntries)
                                    .Select(p => p.Trim())
                                    .Where(p => !string.IsNullOrWhiteSpace(p)));
                            }
                            else
                            {
                                newParts.Add(part);
                            }
                        }

                        parts = newParts;
                    }

                    // Comma as a last resort: only split when there are multiple commas, indicating a list.
                    if (parts.Count == 1)
                    {
                        var commaCount = narrator.Count(c => c == ',');
                        if (commaCount >= 2)
                        {
                            parts = narrator
                                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                .Select(p => p.Trim())
                                .Where(p => !string.IsNullOrWhiteSpace(p))
                                .ToList();
                        }
                    }

                    return parts
                        .Select(p => p.Trim())
                        .Where(p => !string.IsNullOrWhiteSpace(p))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }

                private static List<string> GetAvailableNarrators(Book book)
                {
                    try
                    {
                    var narrators = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    var editions = book?.Editions;
                    if (editions != null)
                    {
                        foreach (var edition in editions)
                        {
                            if (edition == null) continue;

                            if (edition.NarratorNames != null)
                            {
                                foreach (var name in edition.NarratorNames)
                                {
                                    if (!string.IsNullOrWhiteSpace(name))
                                    {
                                        narrators.Add(name.Trim());
                                    }
                                }
                            }

                            if (!string.IsNullOrWhiteSpace(edition.Narrator))
                            {
                                narrators.Add(edition.Narrator.Trim());
                            }
                        }
                    }

                    return narrators.Count > 0
                        ? narrators.OrderBy(x => x).ToList()
                        : new List<string>();
                }
                catch
                {
                    return new List<string>();
                }
            }

        private static TimeSpan? GetDuration(Book book, Edition edition)
        {
            // File measurements only — no fallback to server/edition metadata
            if (edition?.BookFiles?.Any() != true)
            {
                return book?.DurationMinutes.HasValue == true
                    ? TimeSpan.FromMinutes(book.DurationMinutes.Value)
                    : null;
            }

            var totalSeconds = edition.BookFiles
                .Where(f => f?.DurationSeconds.HasValue == true && f.DurationSeconds.Value > 0)
                .Sum(f => (long)f.DurationSeconds.Value);

            return totalSeconds > 0 ? TimeSpan.FromSeconds(totalSeconds) : null;
        }

        private static double? GetDurationMinutes(Book book, Edition edition)
        {
            // File measurements only — no fallback to server/edition metadata
            if (edition?.BookFiles?.Any() != true)
            {
                return book?.DurationMinutes;
            }

            var totalSeconds = edition.BookFiles
                .Where(f => f?.DurationSeconds.HasValue == true && f.DurationSeconds.Value > 0)
                .Sum(f => (long)f.DurationSeconds.Value);

            return totalSeconds > 0 ? totalSeconds / 60.0 : null;
        }

            public static List<BookResource> ToResource(this IEnumerable<Book> models)
            {
                return models?.Select(ToResource).ToList();
            }

            public static void WarnFacadeIdentityGaps(IEnumerable<BookResource> resources, ReadarrFacadeContext facadeContext, string source)
            {
                if (facadeContext == null)
                {
                    return;
                }

                var missingCount = resources?.Count(resource => resource != null && string.IsNullOrWhiteSpace(resource.ForeignBookId)) ?? 0;
                if (missingCount == 0)
                {
                    return;
                }

                Logger.Warn("[ReadarrFacade] Emitted {0} book resource(s) without {1} identity from {2}. Compatibility ID left blank instead of falling back across providers.",
                    missingCount,
                    facadeContext.Dialect,
                    source ?? "book response");
            }

            public static BookLocalInstanceResource ToLocalInstanceResource(this Book model)
            {
                if (model == null)
                {
                    return null;
                }

                var selectedEdition = SelectMonitoredEdition(model.Editions);

                return new BookLocalInstanceResource
                {
                    Id = model.Id,
                    Title = selectedEdition?.Title ?? model.Title,
                    TitleSlug = model.TitleSlug,
                    MediaType = model.MediaType == BookMediaType.Audiobook ? "audiobook" : "ebook",
                    Narrator = GetNarratorDisplay(model, selectedEdition),
                    Disambiguation = selectedEdition?.Disambiguation,
                    Monitored = model.IsMonitored(),
                    HasFiles = model.HasFiles
                };
            }

            private static Edition SelectMonitoredEdition(IEnumerable<Edition> editions)
            {
                return editions?
                    .Where(e => e != null && e.Monitored)
                    .OrderBy(e => e.Id)
                    .FirstOrDefault();
            }

            public static List<Book> ToModel(this IEnumerable<BookResource> resources)
            {
                return resources.Select(ToModel).ToList();
            }

            public static List<Book> ToModel(this IEnumerable<BookResource> resources, ReadarrFacadeContext facadeContext)
            {
                return resources.Select(resource => resource.ToModel(facadeContext)).ToList();
            }

        public static Book ToModel(this BookResource resource)
        {
            return resource.ToModel((ReadarrFacadeContext)null);
        }

        public static Book ToModel(this BookResource resource, ReadarrFacadeContext facadeContext)
        {
            if (resource == null)
            {
                return null;
            }

            var author = resource.Author?.ToModel(facadeContext) ?? new NzbDrone.Core.Books.Author();

            // Use individual provider ID properties if available, fallback to parsing ForeignBookId
            string hardcoverBookId = resource.HardcoverBookId;
            string goodreadsBookId = resource.GoodreadsBookId;
            string goodreadsWorkId = resource.GoodreadsWorkId;
            string openLibraryWorkId = resource.OpenLibraryWorkId;
            string googleBooksId = resource.GoogleBooksId;
            string audibleAsin = null;

            static string StripProviderPrefix(string value)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }

                var idx = value.IndexOf(':');
                return idx > 0 ? value.Substring(idx + 1) : value;
            }

                    // Fallback to parsing ForeignBookId if individual properties are not set
                    if (string.IsNullOrWhiteSpace(hardcoverBookId) && string.IsNullOrWhiteSpace(goodreadsBookId) &&
                        !string.IsNullOrWhiteSpace(resource.ForeignBookId))
                        {
                            var foreignBookId = resource.ForeignBookId.Trim();
                            if (foreignBookId.StartsWith("hc:", StringComparison.OrdinalIgnoreCase))
                            {
                                hardcoverBookId = TryCanonicalizeWorkId(foreignBookId, "hc");
                            }
                            else if (foreignBookId.StartsWith("gr:", StringComparison.OrdinalIgnoreCase))
                            {
                                goodreadsWorkId = TryCanonicalizeWorkId(foreignBookId, "gr");  // foreignBookId is work-level only
                            }
                            else if (foreignBookId.StartsWith("ol:", StringComparison.OrdinalIgnoreCase))
                            {
                                openLibraryWorkId = TryCanonicalizeWorkId(foreignBookId, "ol");
                            }
                            else if (foreignBookId.StartsWith("az:", StringComparison.OrdinalIgnoreCase))
                            {
                                // Store raw ASIN in ASIN field (without az: prefix)
                                audibleAsin = foreignBookId.Substring(3);
                            }
                            else if (long.TryParse(foreignBookId, out _) && facadeContext != null)
                            {
                                if (facadeContext.IsHardcover)
                                {
                                    hardcoverBookId = "hc:" + foreignBookId;
                                }
                                else if (facadeContext.IsGoodreads)
                                {
                                    goodreadsWorkId = "gr:" + foreignBookId;
                                }
                            }
                        }

            var mediaType = MediaTypeParameterParser.ParseRequired(resource.MediaType);
            var addOptions = resource.AddOptions ?? new AddBookOptions();
            addOptions.MonitorBook = (mediaType == BookMediaType.Audiobook ? resource.AudiobookMonitored : resource.EbookMonitored)
                ?? resource.Monitored;

            var book = new Book
            {
                Id = resource.Id,
                // LocalBookId removed - using database ID directly
                TitleSlug = resource.TitleSlug,
                Title = resource.Title,
                // CleanTitle is used for matching and must use the same normalization as Parser/BookRepository lookups.
                CleanTitle = NzbDrone.Core.Books.Extensions.StringSuperNormalizer.ComputeBookComparisonKey(resource.Title),
                ReleaseDate = resource.ReleaseDate,
                Ratings = resource.Ratings ?? new Ratings(),
                AudiobookMonitored = resource.AudiobookMonitored ?? (mediaType == BookMediaType.Audiobook && (addOptions.MonitorBook ?? false)),
                EbookMonitored = resource.EbookMonitored ?? (mediaType == BookMediaType.Ebook && (addOptions.MonitorBook ?? false)),
                MediaType = mediaType,
                AnyEditionOk = resource.AnyEditionOk,
                AddOptions = addOptions,
                Author = author,
                AuthorId = author.Id,
                Narrator = resource.Narrator,
                DurationMinutes = resource.DurationMinutes.HasValue ? (int?)resource.DurationMinutes.Value : null,
                // Map editions from resource - critical for AddBookService
                Editions = resource.Editions?.Select(e => e.ToModel(facadeContext)).ToList() ?? new List<Edition>(),
                // Set all provider IDs
                HardcoverBookId = hardcoverBookId,
                GoodreadsWorkId = goodreadsWorkId,
                OpenLibraryWorkId = openLibraryWorkId,
                GoogleBooksId = null,
                ASIN = null,
                RemoteProviderIds = BuildRemoteProviderIds(hardcoverBookId, goodreadsWorkId, openLibraryWorkId)
            };

            if (goodreadsBookId.IsNotNullOrWhiteSpace() || googleBooksId.IsNotNullOrWhiteSpace() || audibleAsin.IsNotNullOrWhiteSpace())
            {
                var edition = book.Editions.FirstOrDefault();
                if (edition == null)
                {
                    edition = new Edition
                    {
                        Monitored = true
                    };

                    book.Editions.Add(edition);
                }

                if (goodreadsBookId.IsNotNullOrWhiteSpace())
                {
                    var goodreadsRawId = StripProviderPrefix(goodreadsBookId);
                    if (long.TryParse(goodreadsRawId, out var goodreadsEditionId))
                    {
                        edition.GoodreadsEditionId = goodreadsEditionId;
                    }
                }

                if (googleBooksId.IsNotNullOrWhiteSpace())
                {
                    edition.GoogleBooksEditionId = StripProviderPrefix(googleBooksId);
                }

                if (audibleAsin.IsNotNullOrWhiteSpace())
                {
                    edition.Asin = audibleAsin;
                    edition.AudibleASIN = audibleAsin;
                }
            }

            BookEditionIdentity.ClearBookLevelEditionIdentity(book);

            book.Added = DateTime.UtcNow;

            return book;
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

        private static string BuildForeignBookId(Book model, ReadarrFacadeContext facadeContext)
        {
            if (facadeContext?.IsHardcover == true)
            {
                var facadeHardcoverBookId = GetCanonicalHardcoverBookId(model);
                if (!string.IsNullOrWhiteSpace(facadeHardcoverBookId))
                {
                    return StripProviderPrefix(facadeHardcoverBookId);
                }

                WarnMissingFacadeId("book", "hc", model?.Id, model?.Title);
                return string.Empty;
            }

            if (facadeContext?.IsGoodreads == true)
            {
                var facadeGoodreadsWorkId = TryCanonicalizeWorkId(model?.GoodreadsWorkId, "gr") ?? GetCanonicalRemoteWorkId(model, "gr");
                if (!string.IsNullOrWhiteSpace(facadeGoodreadsWorkId))
                {
                    return StripProviderPrefix(facadeGoodreadsWorkId);
                }

                WarnMissingFacadeId("book", "gr", model?.Id, model?.Title);
                return string.Empty;
            }

            var hardcoverBookId = GetCanonicalHardcoverBookId(model);
            if (!string.IsNullOrWhiteSpace(hardcoverBookId))
            {
                return hardcoverBookId;
            }

            var goodreadsWorkId = TryCanonicalizeWorkId(model?.GoodreadsWorkId, "gr");
            if (!string.IsNullOrWhiteSpace(goodreadsWorkId))
            {
                return goodreadsWorkId;
            }

            var remoteGoodreadsWorkId = GetCanonicalRemoteWorkId(model, "gr");
            if (!string.IsNullOrWhiteSpace(remoteGoodreadsWorkId))
            {
                return remoteGoodreadsWorkId;
            }

            var openLibraryWorkId = TryCanonicalizeWorkId(model?.OpenLibraryWorkId, "ol");
            if (!string.IsNullOrWhiteSpace(openLibraryWorkId))
            {
                return openLibraryWorkId;
            }

            var remoteOpenLibraryWorkId = GetCanonicalRemoteWorkId(model, "ol");
            if (!string.IsNullOrWhiteSpace(remoteOpenLibraryWorkId))
            {
                return remoteOpenLibraryWorkId;
            }

            if (BookIdentity.GetStableWorkProviderIdentityTokens(model).Count > 0)
            {
                return string.Empty;
            }

            var asin = BookEditionIdentity.GetAsin(model);
            if (!string.IsNullOrWhiteSpace(asin))
            {
                return $"az:{asin}";
            }

            var audibleAsin = BookEditionIdentity.GetAudibleAsin(model);
            if (!string.IsNullOrWhiteSpace(audibleAsin))
            {
                return $"az:{audibleAsin}";
            }

            return string.Empty;
        }

        private static void WarnMissingFacadeId(string entityType, string dialect, int? localId, string title)
        {
            Logger.Debug("[ReadarrFacade] Cannot emit {0} identity in {1} dialect for localId={2} title='{3}'. Omitting compatibility ID instead of falling back across providers.",
                entityType,
                dialect,
                localId?.ToString() ?? "none",
                title ?? string.Empty);
        }

        private static string GetCanonicalRemoteWorkId(Book model, string expectedPrefix)
        {
            foreach (var providerId in model?.RemoteProviderIds ?? Enumerable.Empty<string>())
            {
                var alias = TryCanonicalizeWorkId(providerId, expectedPrefix);
                if (!string.IsNullOrWhiteSpace(alias))
                {
                    return alias;
                }
            }

            return null;
        }

        private static string GetCanonicalHardcoverBookId(Book model)
        {
            var direct = TryCanonicalizeWorkId(model?.HardcoverBookId, "hc");
            if (!string.IsNullOrWhiteSpace(direct))
            {
                return direct;
            }

            foreach (var providerId in model?.RemoteProviderIds ?? Enumerable.Empty<string>())
            {
                var alias = TryCanonicalizeWorkId(providerId, "hc");
                if (!string.IsNullOrWhiteSpace(alias))
                {
                    return alias;
                }
            }

            return null;
        }

        private static HashSet<string> BuildRemoteProviderIds(string hardcoverBookId, string goodreadsWorkId, string openLibraryWorkId)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string providerId, string expectedPrefix)
            {
                var canonical = TryCanonicalizeWorkId(providerId, expectedPrefix);
                if (!string.IsNullOrWhiteSpace(canonical))
                {
                    ids.Add(canonical);
                }
            }

            Add(hardcoverBookId, "hc");
            Add(goodreadsWorkId, "gr");
            Add(openLibraryWorkId, "ol");

            return ids.Count > 0 ? ids : null;
        }

        private static string TryCanonicalizeWorkId(string providerId, string expectedPrefix)
        {
            if (string.IsNullOrWhiteSpace(providerId))
            {
                return null;
            }

            try
            {
                var canonical = ProviderIdHelper.Canonicalize(providerId.Trim(), expectedPrefix);
                var raw = StripProviderPrefix(canonical);

                // Hardcover book IDs are bare numeric. Reject hc:edition:* and other
                // nested/edition-like forms so they cannot become Seerr foreignBookId.
                if (expectedPrefix.Equals("hc", StringComparison.OrdinalIgnoreCase) &&
                    (!raw.All(char.IsDigit) || raw.Contains(":")))
                {
                    return null;
                }

                return canonical;
            }
            catch
            {
                return null;
            }
        }

            public static Book ToModel(this BookResource resource, Book book)
            {
                return resource.ToModel(book, null);
            }

            public static Book ToModel(this BookResource resource, Book book, ReadarrFacadeContext facadeContext)
            {
            if (resource == null)
            {
                return null;
            }

            List<Edition> updatedEditions = null;
            if (resource.Editions?.Count > 0)
            {
                var storedEditionsById = book.Editions?
                    .Where(edition => edition != null && edition.Id > 0)
                    .ToDictionary(edition => edition.Id) ?? new Dictionary<int, Edition>();

                if (resource.Editions.Any(edition => edition == null ||
                    !storedEditionsById.ContainsKey(edition.Id) ||
                    (edition.BookId != 0 && edition.BookId != book.Id)))
                {
                    throw new ValidationException(new[]
                    {
                        new ValidationFailure(nameof(resource.Editions), "All editions must belong to the book being updated.")
                    });
                }

                if (resource.Editions.Select(edition => edition.Id).Distinct().Count() != resource.Editions.Count ||
                    resource.Editions.Count(edition => edition.Monitored) > 1)
                {
                    throw new ValidationException(new[]
                    {
                        new ValidationFailure(nameof(resource.Editions), "Submit each edition once and select at most one monitored edition.")
                    });
                }

                var submittedById = resource.Editions.ToDictionary(edition => edition.Id, edition =>
                {
                    var mapped = edition.ToModel(facadeContext);
                    PreserveStoredEditionIdentity(mapped, storedEditionsById[edition.Id]);
                    if (!edition.ManualAddSpecified)
                    {
                        mapped.ManualAdd = storedEditionsById[edition.Id].ManualAdd;
                    }

                    return mapped;
                });
                var selectedId = resource.Editions.SingleOrDefault(edition => edition.Monitored)?.Id;
                updatedEditions = storedEditionsById.Values.Select(stored =>
                {
                    var edition = submittedById.GetValueOrDefault(stored.Id, stored);
                    if (selectedId.HasValue && edition.Id != selectedId &&
                        ReferenceEquals(edition, stored) && stored.Monitored)
                    {
                        edition = stored.ToResource().ToModel();
                        PreserveStoredEditionIdentity(edition, stored);
                    }

                    return edition;
                }).ToList();
                if (selectedId.HasValue)
                {
                    EditionPinPolicy.ApplyExplicitSelection(updatedEditions, selectedId.Value,
                        storedEditionsById.Values.Where(edition => edition.Monitored).Select(edition => edition.Id).ToHashSet());
                }
            }

                // Existing metadata and duration writers own the stored book values.
                // The resource may project edition or file values that must not be copied back.
            // Native clients may address each side explicitly. Readarr-compatible clients address
            // only the selected facade side through the legacy Monitored field.
            if (facadeContext?.MediaType == "audiobook")
            {
                book.AudiobookMonitored = resource.Monitored;
            }
            else if (facadeContext?.MediaType == "ebook")
            {
                book.EbookMonitored = resource.Monitored;
            }
            else
            {
                if (resource.AudiobookMonitored.HasValue)
                {
                    book.AudiobookMonitored = resource.AudiobookMonitored.Value;
                }
                else if (book.MediaType == BookMediaType.Audiobook)
                {
                    book.AudiobookMonitored = resource.Monitored;
                }

                if (resource.EbookMonitored.HasValue)
                {
                    book.EbookMonitored = resource.EbookMonitored.Value;
                }
                else if (book.MediaType == BookMediaType.Ebook)
                {
                    book.EbookMonitored = resource.Monitored;
                }
            }
            if (Logger.IsDebugEnabled)
            {
                var selected = SelectMonitoredEdition(book.Editions);
                var ignored = new List<string>();
                if (resource.ReleaseDateSpecified && resource.ReleaseDate != (selected?.ReleaseDate ?? book.ReleaseDate)) ignored.Add("releaseDate");
                if (resource.RatingsSpecified && !Equals(resource.Ratings, selected?.Ratings ?? book.Ratings ?? new Ratings())) ignored.Add("ratings");
                if (resource.DurationMinutesSpecified && resource.DurationMinutes != GetDurationMinutes(book, selected)) ignored.Add("durationMinutes");
                if (ignored.Count > 0) Logger.Debug("Book {0}: ignored changes to provider-owned metadata fields: {1}", book.Id, string.Join(", ", ignored));
            }

            // Round-tripped display metadata does not express a manual narrator choice.
            var shouldUpdateNarrator = resource.NarratorSpecified && !string.Equals(resource.Narrator,
                GetNarratorDisplay(book, SelectMonitoredEdition(book.Editions), book.HasFiles), StringComparison.Ordinal);
            if (resource.AnyEditionOkSpecified)
            {
                book.AnyEditionOk = resource.AnyEditionOk;
            }
            book.AddOptions = resource.AddOptions;
            if (shouldUpdateNarrator)
            {
                book.Narrator = resource.Narrator;
            }

            if (updatedEditions != null)
            {
                book.Editions = updatedEditions;
            }

            return book;
        }

        private static void PreserveStoredEditionIdentity(Edition target, Edition stored)
        {
            if (target == null || stored == null)
            {
                return;
            }

            // Values absent from the editable resource remain owned by their existing writers.
            target.UseImagesFrom(stored);
            target.MatchingTitle = stored.MatchingTitle;
            target.IsGraphicAudio = stored.IsGraphicAudio;
            target.AudioProductionType = stored.AudioProductionType;
            target.NarratorCredits = stored.NarratorCredits;
            target.ProviderUrls = stored.ProviderUrls;
            target.ProviderIds = stored.ProviderIds;
            target.LastUpdated = stored.LastUpdated;
            target.IsFallbackEdition = stored.IsFallbackEdition;
            target.BookId = stored.BookId;
            target.ForeignEditionId = stored.ForeignEditionId;
            target.TitleSlug = stored.TitleSlug;
            target.GoodreadsEditionId = stored.GoodreadsEditionId;
            target.HardcoverEditionId = stored.HardcoverEditionId;
            target.OpenLibraryEditionId = stored.OpenLibraryEditionId;
            target.GoogleBooksEditionId = stored.GoogleBooksEditionId;
            target.Asin = stored.Asin;
            target.AudibleASIN = stored.AudibleASIN;
            target.Asins = stored.Asins?.ToList();
            target.Isbn13 = stored.Isbn13;
            target.Isbn10 = stored.Isbn10;
        }
    }
}
