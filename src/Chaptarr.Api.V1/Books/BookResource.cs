using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Chaptarr.Api.V1.Author;
using Chaptarr.Api.V1.BookFiles;
using Chaptarr.Api.V1.MediaTypes;
using Chaptarr.Http.Middleware;
using Chaptarr.Http.REST;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.AuthorStats;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Parser;

namespace Chaptarr.Api.V1.Books
{
    public class BookResource : RestResource
    {
        public string Title { get; set; }
        public string AuthorTitle { get; set; }
        public string SeriesTitle { get; set; }
        public string Disambiguation { get; set; }
        public string Overview { get; set; }
        public int AuthorId { get; set; }
        public string ForeignBookId { get; set; }  // Chaptarr-native IDs are prefixed; Readarr facades emit bare IDs in the requested dialect.
        public string LocalBookId { get; set; }
        public string ForeignEditionId { get; set; }
        public string BaseBookId { get; set; }

        // Individual provider IDs for frontend compatibility
        public string HardcoverBookId { get; set; }
        public string GoodreadsBookId { get; set; }
        public string GoodreadsWorkId { get; set; }
        public string OpenLibraryWorkId { get; set; }
        public string GoogleBooksId { get; set; }
        public string ASIN { get; set; }
        public string AudibleASIN { get; set; }
        public string TitleSlug { get; set; }
        public bool Monitored { get; set; }
        public bool AudiobookMonitored { get; set; }
        public bool EbookMonitored { get; set; }
        public bool AnyEditionOk { get; set; }
        public Ratings Ratings { get; set; }
        public DateTime? ReleaseDate { get; set; }
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
        public string Narrator { get; set; }
        // For client-side search/filtering: all narrator names available across editions (even when Narrator display is hidden).
        public List<string> AvailableNarrators { get; set; }
        // Narrator names for the monitored edition (used for "Full Cast" tooltips and per-edition display).
        public List<string> NarratorNames { get; set; }
        public TimeSpan? Duration { get; set; }
        public double? DurationMinutes { get; set; }

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
            var title = (selectedEdition?.Title ?? model.Title) ?? string.Empty;

            var seriesTitle = BookSeriesLabel.Build(model.SeriesLinks);

            // Lookup and search results are not persisted and therefore never carry links, so the
            // denormalized pair from the metadata server is all they have. For a library book the
            // links are the truth, including when there are none.
            if (seriesTitle.IsNullOrWhiteSpace() && model.Id <= 0)
            {
                seriesTitle = BookSeriesLabel.Format(model.SeriesName, model.SeriesPosition);
            }

            title = StripDuplicatedSeriesSuffix(title, BuildSuffixCandidates(model, seriesTitle));

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
                Disambiguation = selectedEdition?.Disambiguation,
                Overview = options.IncludeOverview && !string.IsNullOrWhiteSpace(selectedEdition?.Overview)
                    ? selectedEdition.Overview
                    : (options.IncludeOverview ? (model.Overview ?? string.Empty) : null),
                Images = selectedEdition != null
                    ? (selectedEdition.Images ?? new List<MediaCover>()).Where(i => !string.IsNullOrWhiteSpace(i?.Url)).ToList()
                    : (model.Images ?? new List<MediaCover>()).Where(i => !string.IsNullOrWhiteSpace(i?.Url)).ToList(),
                Links = links,
                Statistics = options.Statistics?.ToResource(),
                AddOptions = model.AddOptions,
                RemoteCover = string.Empty,
                Grabbed = false,
                Narrator = GetNarratorDisplay(model, selectedEdition),
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
                resource.Narrator = null;
                resource.NarratorNames = new List<string>();
            }

            return resource;
        }

            private static List<string> BuildSuffixCandidates(Book model, string seriesTitle)
            {
                var candidates = new List<string>();

                if (seriesTitle.IsNotNullOrWhiteSpace())
                {
                    candidates.Add(seriesTitle);
                }

                candidates.AddRange(BookSeriesLabel.BuildAll(model.SeriesLinks));

                var denormalized = BookSeriesLabel.Format(model.SeriesName, model.SeriesPosition);
                if (denormalized.IsNotNullOrWhiteSpace())
                {
                    candidates.Add(denormalized);
                }

                return candidates;
            }

            private static string StripDuplicatedSeriesSuffix(string title, IEnumerable<string> seriesTitles)
            {
                title ??= string.Empty;
                title = title.Trim();

                var candidates = (seriesTitles ?? Enumerable.Empty<string>())
                    .Where(x => x.IsNotNullOrWhiteSpace())
                    .ToList();

                if (title.Length == 0 || candidates.Count == 0)
                {
                    return title;
                }

                var match = Regex.Match(title, @"^(?<base>.*?)(?:\s*\((?<suffix>[^()]*)\))$");
                if (!match.Success)
                {
                    return title;
                }

                var suffix = match.Groups["suffix"].Value;
                if (!candidates.Any(candidate => SuffixMatchesSeriesTitle(suffix, candidate)))
                {
                    return title;
                }

                return match.Groups["base"].Value.TrimEnd();
            }

            private static bool SuffixMatchesSeriesTitle(string suffix, string seriesTitle)
            {
                if (suffix.IsNullOrWhiteSpace() || seriesTitle.IsNullOrWhiteSpace())
                {
                    return false;
                }

                if (!suffix.Contains('#'))
                {
                    return false;
                }

                return NormalizeSeriesSuffix(suffix) == NormalizeSeriesSuffix(seriesTitle);
            }

            private static string NormalizeSeriesSuffix(string value)
            {
                var normalized = value.ToLowerInvariant();
                normalized = Regex.Replace(normalized, @"[^\p{L}\p{N}#]+", " ");
                normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
                normalized = Regex.Replace(normalized, @"\s+#", " #");
                return normalized;
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

                private static string GetNarratorDisplay(Book book, Edition edition)
                {
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

            var book = new Book
            {
                Id = resource.Id,
                // LocalBookId removed - using database ID directly
                TitleSlug = resource.TitleSlug,
                Title = resource.Title,
                // CleanTitle is used for matching and must use the same normalization as Parser/BookRepository lookups.
                CleanTitle = (resource.Title ?? string.Empty).CleanBookTitle().CleanAuthorName(),
                ReleaseDate = resource.ReleaseDate,
                Ratings = resource.Ratings ?? new Ratings(),
                AudiobookMonitored = resource.AudiobookMonitored || (mediaType == BookMediaType.Audiobook && resource.Monitored),
                EbookMonitored = resource.EbookMonitored || (mediaType == BookMediaType.Ebook && resource.Monitored),
                MediaType = mediaType,
                AnyEditionOk = resource.AnyEditionOk,
                AddOptions = resource.AddOptions,
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

                // ForeignBookId is deprecated
                book.TitleSlug = resource.TitleSlug;
                // The book title is owned by metadata refresh, not user edits/monitor toggles.
                // Do not overwrite it from the API resource (which may carry an edition-specific title).
                book.ReleaseDate = resource.ReleaseDate;
                book.Ratings = resource.Ratings ?? new Ratings();
            // Set monitoring based on book's MediaType
            if (book.MediaType == BookMediaType.Audiobook)
            {
                book.AudiobookMonitored = resource.Monitored;
            }
            else if (book.MediaType == BookMediaType.Ebook)
            {
                book.EbookMonitored = resource.Monitored;
            }
            book.AnyEditionOk = resource.AnyEditionOk;
            book.AddOptions = resource.AddOptions;
            book.Narrator = resource.Narrator;
            book.DurationMinutes = resource.DurationMinutes.HasValue ? (int?)resource.DurationMinutes.Value : null;

                // Readarr/Seerr compatibility: Readarr sends editions in PUT /book to update monitored edition.
                if (resource.Editions != null && resource.Editions.Any())
                {
                    var storedEditionsById = book.Editions?
                        .Where(edition => edition != null && edition.Id > 0)
                        .ToDictionary(edition => edition.Id) ?? new Dictionary<int, Edition>();

                    book.Editions = resource.Editions
                        .Select(resourceEdition =>
                        {
                            var mappedEdition = resourceEdition.ToModel(facadeContext);
                            if (mappedEdition?.Id > 0 &&
                                storedEditionsById.TryGetValue(mappedEdition.Id, out var storedEdition))
                            {
                                PreserveStoredEditionIdentity(mappedEdition, storedEdition);
                            }

                            return mappedEdition;
                        })
                        .ToList();
                }

            return book;
        }

        private static void PreserveStoredEditionIdentity(Edition target, Edition stored)
        {
            if (target == null || stored == null)
            {
                return;
            }

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
