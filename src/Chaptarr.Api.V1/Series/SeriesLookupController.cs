using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chaptarr.Http;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Common.Http;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MetadataSource;
// using NzbDrone.Core.MetadataSource.Hardcover; // Removed - using V5 API via BookInfoProxy
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Profiles.Metadata;

namespace Chaptarr.Api.V1.Series
{
    [V1ApiController("series/lookup")]
    public class SeriesLookupController : Controller
    {
        // private readonly IHardcoverSearchProxy _hardcoverSearchProxy; // Removed - using V5 API via BookInfoProxy
        private readonly IHttpClient _httpClient;
        private readonly IConfigService _configService;
        private readonly IMediaCoverProxy _mediaCoverProxy;
        private readonly IMetadataProfileService _metadataProfileService;
        private readonly Logger _logger;

        public SeriesLookupController(
            // IHardcoverSearchProxy hardcoverSearchProxy, // Removed - using V5 API via BookInfoProxy
            IHttpClient httpClient,
            IConfigService configService,
            IMediaCoverProxy mediaCoverProxy,
            IMetadataProfileService metadataProfileService,
            Logger logger)
        {
            // _hardcoverSearchProxy = hardcoverSearchProxy; // Removed - using V5 API via BookInfoProxy
            _httpClient = httpClient;
            _configService = configService;
            _mediaCoverProxy = mediaCoverProxy;
            _metadataProfileService = metadataProfileService;
            _logger = logger;
        }

        [HttpGet]
        public ActionResult<SeriesLookupResource> GetSeriesInfo([FromQuery] string foreignSeriesId, [FromQuery] string provider = "hardcover", [FromQuery] int? metadataProfileId = null, [FromQuery] int? primaryWorkCount = null)
        {
            if (string.IsNullOrWhiteSpace(foreignSeriesId))
            {
                return BadRequest("Foreign series ID is required");
            }

            try
            {
                _logger.Debug($"Looking up series details for: {foreignSeriesId} using provider: {provider}, metadataProfileId: {metadataProfileId}");

                if (!provider.Equals("hardcover", StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest("Only Hardcover provider is currently supported for series lookup");
                }

                if (!_configService.HardcoverEnabled || string.IsNullOrWhiteSpace(_configService.HardcoverApiToken))
                {
                    return BadRequest("Hardcover is not enabled or API token is missing");
                }

                var (seriesProvider, rawSeriesId) = SplitProviderId(foreignSeriesId);
                if (!seriesProvider.Equals("hc", StringComparison.OrdinalIgnoreCase) && !seriesProvider.Equals("hardcover", StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest("Only Hardcover series IDs are currently supported (expected hc:{id})");
                }

                if (!int.TryParse(rawSeriesId, out var seriesId))
                {
                    return BadRequest("Invalid Hardcover series ID");
                }

                // Get allowed languages from metadata profile
                List<string> allowedLanguages = null;
                if (metadataProfileId.HasValue)
                {
                    try
                    {
                        var profile = _metadataProfileService.Get(metadataProfileId.Value);
                        if (profile?.AllowedLanguages.IsNotNullOrWhiteSpace() ?? false)
                        {
                            allowedLanguages = profile.AllowedLanguages
                                .Trim(',')
                                .Split(',')
                                .Select(x => x.Trim())
                                .Where(x => x.IsNotNullOrWhiteSpace())
                                .ToList();
                            _logger.Debug($"Using allowed languages: {string.Join(", ", allowedLanguages)}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn(ex, $"Failed to get metadata profile {metadataProfileId}");
                    }
                }

                var series = GetHardcoverSeriesDetails(seriesId, primaryWorkCount);
                if (series == null)
                {
                    return NotFound();
                }

                return Ok(series);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Error looking up series {foreignSeriesId}");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        private static (string provider, string id) SplitProviderId(string prefixedId)
        {
            if (string.IsNullOrWhiteSpace(prefixedId))
            {
                return (null, null);
            }

            var trimmed = prefixedId.Trim().Trim('{', '}');
            var idx = trimmed.IndexOf(':');
            if (idx <= 0 || idx == trimmed.Length - 1)
            {
                return (string.Empty, trimmed);
            }

            return (trimmed.Substring(0, idx), trimmed.Substring(idx + 1));
        }

        private SeriesLookupResource GetHardcoverSeriesDetails(int seriesId, int? primaryWorkCount)
        {
            // Prefer featured (primary) books for series previews.
            const int pageSize = 50;
            var offset = 0;
            var books = new List<SeriesBookResource>();
            var primarySlotsFound = new HashSet<int>();

            HardcoverSeriesMeta meta = null;
            var primaryTargetOverride = primaryWorkCount.HasValue && primaryWorkCount.Value > 0 ? primaryWorkCount.Value : 0;
            if (primaryTargetOverride > 200)
            {
                primaryTargetOverride = 200;
            }

            while (true)
            {
                var page = FetchHardcoverSeriesPage(seriesId, pageSize, offset);
                if (page.Series == null)
                {
                    return null;
                }

                meta ??= page.Series;
                var primaryTarget = primaryTargetOverride > 0 ? primaryTargetOverride : meta.PrimaryBooksCount;

                if (page.PrimaryBooks == null || page.PrimaryBooks.Count == 0)
                {
                    break;
                }

                books.AddRange(page.PrimaryBooks);

                if (primaryTarget > 0)
                {
                    foreach (var b in page.PrimaryBooks)
                    {
                        if (TryGetPrimarySlot(b, primaryTarget, out var slot))
                        {
                            primarySlotsFound.Add(slot);
                        }
                    }

                    // Stop early once we have all primary slots (1..primaryTarget).
                    if (primarySlotsFound.Count >= primaryTarget)
                    {
                        break;
                    }
                }

                if (page.PrimaryBooks.Count < pageSize)
                {
                    break;
                }

                offset += pageSize;
                if (offset > 500) // safety
                {
                    break;
                }
            }

            // If we got no featured books (some series may not have them), fall back to first books.
            if (books.Count == 0)
            {
                var fallback = FetchHardcoverSeriesFallbackBooks(seriesId);
                if (fallback.Series == null)
                {
                    return null;
                }
                meta = fallback.Series;
                books = fallback.FallbackBooks ?? new List<SeriesBookResource>();
            }

            var primaryBooks = primaryTargetOverride > 0
                ? primaryTargetOverride
                : (meta.PrimaryBooksCount > 0 ? meta.PrimaryBooksCount : books.Count);
            var primaryBooksList = FilterToPrimaryBooks(books, primaryBooks);

            return new SeriesLookupResource
            {
                ForeignSeriesId = $"hc:{meta.Id}",
                Title = meta.Name,
                TitleSlug = meta.Slug,
                Description = meta.Description,
                WorkCount = primaryBooksList?.Count ?? 0,
                PrimaryWorkCount = primaryBooksList?.Count ?? 0,
                Books = primaryBooksList
                    .Where(b => b != null && !string.IsNullOrWhiteSpace(b.ForeignBookId))
                    .ToList()
            };
        }

        private static bool TryGetPrimarySlot(SeriesBookResource book, int primaryTarget, out int slot)
        {
            slot = 0;
            if (book == null || primaryTarget <= 0 || string.IsNullOrWhiteSpace(book.Position))
            {
                return false;
            }

            if (!double.TryParse(book.Position, NumberStyles.Any, CultureInfo.InvariantCulture, out var pos))
            {
                return false;
            }

            var rounded = (int)Math.Round(pos);
            if (rounded < 1 || rounded > primaryTarget)
            {
                return false;
            }

            // Primary slots are integer positions (1..N). Ignore side-stories like 0.5.
            if (Math.Abs(pos - rounded) > 0.0001)
            {
                return false;
            }

            slot = rounded;
            return true;
        }

        private static List<SeriesBookResource> FilterToPrimaryBooks(List<SeriesBookResource> books, int primaryCount)
        {
            if (books == null || books.Count == 0 || primaryCount <= 0)
            {
                return books ?? new List<SeriesBookResource>();
            }

            var bySlot = new Dictionary<int, SeriesBookResource>();
            foreach (var book in books)
            {
                if (!TryGetPrimarySlot(book, primaryCount, out var slot))
                {
                    continue;
                }

                if (!bySlot.TryGetValue(slot, out var existing))
                {
                    bySlot[slot] = book;
                    continue;
                }

                // Prefer better candidates if multiple books share the same slot.
                bySlot[slot] = ChooseBetterPrimaryCandidate(existing, book);
            }

            if (bySlot.Count == 0)
            {
                return books.Take(primaryCount).ToList();
            }

            var ordered = new List<SeriesBookResource>();
            for (var i = 1; i <= primaryCount; i++)
            {
                if (bySlot.TryGetValue(i, out var book))
                {
                    ordered.Add(book);
                }
            }

            return ordered;
        }

        private static SeriesBookResource ChooseBetterPrimaryCandidate(SeriesBookResource a, SeriesBookResource b)
        {
            if (a == null) return b;
            if (b == null) return a;

            var aVotes = a.Ratings?.Votes ?? 0;
            var bVotes = b.Ratings?.Votes ?? 0;
            if (aVotes != bVotes)
            {
                return bVotes > aVotes ? b : a;
            }

            var aRating = a.Ratings?.Value ?? 0m;
            var bRating = b.Ratings?.Value ?? 0m;
            if (aRating != bRating)
            {
                return bRating > aRating ? b : a;
            }

            var aHasCover = a.Images?.Any(i => !string.IsNullOrWhiteSpace(i?.Url)) == true;
            var bHasCover = b.Images?.Any(i => !string.IsNullOrWhiteSpace(i?.Url)) == true;
            if (aHasCover != bHasCover)
            {
                return bHasCover ? b : a;
            }

            return a;
        }

        private (HardcoverSeriesMeta Series, List<SeriesBookResource> PrimaryBooks) FetchHardcoverSeriesPage(int seriesId, int limit, int offset)
        {
            var query = @"
	                query SeriesLookup($id: Int!, $limit: Int!, $offset: Int!) {
	                  series_by_pk(id: $id) {
	                    id
	                    name
	                    slug
	                    description
	                    books_count
	                    primary_books_count
	                    book_series(where: { featured: { _eq: true } }, order_by: { position: asc }, limit: $limit, offset: $offset) {
	                      position
	                      book {
	                        id
	                        title
	                        subtitle
	                        description
	                        rating
	                        ratings_count
	                        release_date
	                        image { url }
	                        contributions(where: { _or: [{ contribution: { _eq: ""Author"" } }, { contribution: { _eq: """" } }, { contribution: { _is_null: true } }] }, order_by: { id: asc }, limit: 30) {
	                          author_id
	                          author { id name slug image { url } }
	                        }
	                      }
	                    }
	                  }
	                }";

            var graphqlRequest = new
            {
                query,
                variables = new
                {
                    id = seriesId,
                    limit,
                    offset
                }
            };

            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            var jsonContent = global::System.Text.Json.JsonSerializer.Serialize(graphqlRequest, jsonOptions);

            var request = new HttpRequestBuilder("https://api.hardcover.app/v1/graphql")
                .SetHeader("Content-Type", "application/json")
                .SetHeader("Accept", "application/json")
                .SetHeader("User-Agent", ExternalImageRequestHeaders.GetRandomUserAgent())
                .Build();

            request.Method = HttpMethod.Post;
            request.SetContent(jsonContent);
            request.Headers.Add("Authorization", $"Bearer {_configService.HardcoverApiToken}");

            var response = _httpClient.Execute(request);
            if (response == null || response.HasHttpError || string.IsNullOrWhiteSpace(response.Content))
            {
                _logger.Debug("Hardcover series lookup failed: {0}", response?.StatusCode);
                return (null, new List<SeriesBookResource>());
            }

            using var document = JsonDocument.Parse(response.Content);
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("series_by_pk", out var seriesElement) ||
                seriesElement.ValueKind != JsonValueKind.Object)
            {
                return (null, new List<SeriesBookResource>());
            }

            var meta = new HardcoverSeriesMeta
            {
                Id = seriesElement.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt32() : seriesId,
                Name = seriesElement.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null,
                Slug = seriesElement.TryGetProperty("slug", out var slugEl) ? slugEl.GetString() : null,
                Description = seriesElement.TryGetProperty("description", out var descEl) ? descEl.GetString() : null,
                BooksCount = seriesElement.TryGetProperty("books_count", out var bcEl) && bcEl.ValueKind == JsonValueKind.Number ? bcEl.GetInt32() : 0,
                PrimaryBooksCount = seriesElement.TryGetProperty("primary_books_count", out var pbcEl) && pbcEl.ValueKind == JsonValueKind.Number ? pbcEl.GetInt32() : 0
            };

            var books = new List<SeriesBookResource>();
            if (seriesElement.TryGetProperty("book_series", out var bookSeriesEl) && bookSeriesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in bookSeriesEl.EnumerateArray())
                {
                    if (!item.TryGetProperty("book", out var bookEl) || bookEl.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var position = item.TryGetProperty("position", out var posEl) && posEl.ValueKind == JsonValueKind.Number
                        ? posEl.GetDouble().ToString("0.##", CultureInfo.InvariantCulture)
                        : null;

                    var bookId = bookEl.TryGetProperty("id", out var bookIdEl) && bookIdEl.ValueKind == JsonValueKind.Number
                        ? bookIdEl.GetInt32()
                        : 0;

                    var title = bookEl.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;
                    var overview = bookEl.TryGetProperty("description", out var overviewEl) && overviewEl.ValueKind == JsonValueKind.String
                        ? overviewEl.GetString()
                        : null;
                    var authorName = TryGetFirstAuthorName(bookEl);
                    var (coverUrl, _) = TryGetImageUrl(bookEl);

                    var images = new List<MediaCover>();
                    if (!string.IsNullOrWhiteSpace(coverUrl))
                    {
                        images.Add(new MediaCover
                        {
                            Url = _mediaCoverProxy.RegisterUrl(coverUrl),
                            CoverType = MediaCoverTypes.Cover
                        });
                    }

                    DateTime? releaseDate = null;
                    if (bookEl.TryGetProperty("release_date", out var rdEl) && rdEl.ValueKind == JsonValueKind.String &&
                        DateTime.TryParse(rdEl.GetString(), out var parsed))
                    {
                        releaseDate = parsed;
                    }

                    var rating = bookEl.TryGetProperty("rating", out var ratingEl) && ratingEl.ValueKind == JsonValueKind.Number
                        ? ratingEl.GetDecimal()
                        : 0m;
                    var ratingCount = bookEl.TryGetProperty("ratings_count", out var rcEl) && rcEl.ValueKind == JsonValueKind.Number
                        ? rcEl.GetInt32()
                        : 0;

                    books.Add(new SeriesBookResource
                    {
                        ForeignBookId = $"hc:{bookId}",
                        Title = title,
                        Overview = overview,
                        AuthorName = authorName,
                        ReleaseDate = releaseDate,
                        Position = position,
                        Images = images,
                        Ratings = new Ratings { Value = rating, Votes = ratingCount },
                        ForeignAuthorId = TryGetFirstAuthorId(bookEl)
                    });
                }
            }

            return (meta, books);
        }

        private (HardcoverSeriesMeta Series, List<SeriesBookResource> FallbackBooks) FetchHardcoverSeriesFallbackBooks(int seriesId)
        {
            var query = @"
	                query SeriesLookupFallback($id: Int!) {
	                  series_by_pk(id: $id) {
	                    id
	                    name
	                    slug
	                    description
	                    books_count
	                    primary_books_count
	                    book_series(order_by: { position: asc }, limit: 25) {
	                      position
	                      book {
	                        id
	                        title
	                        description
	                        rating
	                        ratings_count
	                        release_date
	                        image { url }
	                        contributions(where: { _or: [{ contribution: { _eq: ""Author"" } }, { contribution: { _eq: """" } }, { contribution: { _is_null: true } }] }, order_by: { id: asc }, limit: 30) {
	                          author_id
	                          author { id name slug }
	                        }
	                      }
	                    }
	                  }
	                }";

            var graphqlRequest = new
            {
                query,
                variables = new
                {
                    id = seriesId
                }
            };

            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            var jsonContent = global::System.Text.Json.JsonSerializer.Serialize(graphqlRequest, jsonOptions);

            var request = new HttpRequestBuilder("https://api.hardcover.app/v1/graphql")
                .SetHeader("Content-Type", "application/json")
                .SetHeader("Accept", "application/json")
                .SetHeader("User-Agent", ExternalImageRequestHeaders.GetRandomUserAgent())
                .Build();

            request.Method = HttpMethod.Post;
            request.SetContent(jsonContent);
            request.Headers.Add("Authorization", $"Bearer {_configService.HardcoverApiToken}");

            var response = _httpClient.Execute(request);
            if (response == null || response.HasHttpError || string.IsNullOrWhiteSpace(response.Content))
            {
                _logger.Debug("Hardcover series fallback lookup failed: {0}", response?.StatusCode);
                return (null, new List<SeriesBookResource>());
            }

            using var document = JsonDocument.Parse(response.Content);
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("series_by_pk", out var seriesElement) ||
                seriesElement.ValueKind != JsonValueKind.Object)
            {
                return (null, new List<SeriesBookResource>());
            }

            var meta = new HardcoverSeriesMeta
            {
                Id = seriesElement.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt32() : seriesId,
                Name = seriesElement.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null,
                Slug = seriesElement.TryGetProperty("slug", out var slugEl) ? slugEl.GetString() : null,
                Description = seriesElement.TryGetProperty("description", out var descEl) ? descEl.GetString() : null,
                BooksCount = seriesElement.TryGetProperty("books_count", out var bcEl) && bcEl.ValueKind == JsonValueKind.Number ? bcEl.GetInt32() : 0,
                PrimaryBooksCount = seriesElement.TryGetProperty("primary_books_count", out var pbcEl) && pbcEl.ValueKind == JsonValueKind.Number ? pbcEl.GetInt32() : 0
            };

            var books = new List<SeriesBookResource>();
            if (seriesElement.TryGetProperty("book_series", out var bookSeriesEl) && bookSeriesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in bookSeriesEl.EnumerateArray())
                {
                    if (!item.TryGetProperty("book", out var bookEl) || bookEl.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var position = item.TryGetProperty("position", out var posEl) && posEl.ValueKind == JsonValueKind.Number
                        ? posEl.GetDouble().ToString("0.##", CultureInfo.InvariantCulture)
                        : null;

                    var bookId = bookEl.TryGetProperty("id", out var bookIdEl) && bookIdEl.ValueKind == JsonValueKind.Number
                        ? bookIdEl.GetInt32()
                        : 0;

                    var title = bookEl.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;
                    var overview = bookEl.TryGetProperty("description", out var overviewEl) && overviewEl.ValueKind == JsonValueKind.String
                        ? overviewEl.GetString()
                        : null;
                    var authorName = TryGetFirstAuthorName(bookEl);
                    var (coverUrl, _) = TryGetImageUrl(bookEl);

                    var images = new List<MediaCover>();
                    if (!string.IsNullOrWhiteSpace(coverUrl))
                    {
                        images.Add(new MediaCover
                        {
                            Url = _mediaCoverProxy.RegisterUrl(coverUrl),
                            CoverType = MediaCoverTypes.Cover
                        });
                    }

                    DateTime? releaseDate = null;
                    if (bookEl.TryGetProperty("release_date", out var rdEl) && rdEl.ValueKind == JsonValueKind.String &&
                        DateTime.TryParse(rdEl.GetString(), out var parsed))
                    {
                        releaseDate = parsed;
                    }

                    var rating = bookEl.TryGetProperty("rating", out var ratingEl) && ratingEl.ValueKind == JsonValueKind.Number
                        ? ratingEl.GetDecimal()
                        : 0m;
                    var ratingCount = bookEl.TryGetProperty("ratings_count", out var rcEl) && rcEl.ValueKind == JsonValueKind.Number
                        ? rcEl.GetInt32()
                        : 0;

                    books.Add(new SeriesBookResource
                    {
                        ForeignBookId = $"hc:{bookId}",
                        Title = title,
                        Overview = overview,
                        AuthorName = authorName,
                        ReleaseDate = releaseDate,
                        Position = position,
                        Images = images,
                        Ratings = new Ratings { Value = rating, Votes = ratingCount },
                        ForeignAuthorId = TryGetFirstAuthorId(bookEl)
                    });
                }
            }

            return (meta, books);
        }

        private static (string url, string thumbnailUrl) TryGetImageUrl(JsonElement bookElement)
        {
            if (bookElement.TryGetProperty("image", out var imageEl) && imageEl.ValueKind == JsonValueKind.Object)
            {
                if (imageEl.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
                {
                    return (urlEl.GetString(), null);
                }
            }

            // Hardcover sometimes stores cached_image as JSONB
            if (bookElement.TryGetProperty("cached_image", out var cachedImage) && cachedImage.ValueKind == JsonValueKind.Object)
            {
                if (cachedImage.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
                {
                    return (urlEl.GetString(), null);
                }
            }

            return (null, null);
        }

        private static string TryGetFirstAuthorName(JsonElement bookElement)
        {
            if (bookElement.TryGetProperty("contributions", out var contribEl) && contribEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in contribEl.EnumerateArray())
                {
                    if (c.TryGetProperty("author", out var authorEl) && authorEl.ValueKind == JsonValueKind.Object &&
                        authorEl.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                    {
                        return nameEl.GetString();
                    }
                }
            }

            return null;
        }

        private static string TryGetFirstAuthorId(JsonElement bookElement)
        {
            if (bookElement.TryGetProperty("contributions", out var contribEl) && contribEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in contribEl.EnumerateArray())
                {
                    if (c.TryGetProperty("author_id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
                    {
                        return $"hc:{idEl.GetInt32()}";
                    }
                }
            }

            return null;
        }

    }

    public class SeriesLookupResource
    {
        [JsonPropertyName("foreignSeriesId")]
        public string ForeignSeriesId { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; }

        [JsonPropertyName("titleSlug")]
        public string TitleSlug { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("workCount")]
        public int WorkCount { get; set; }

        [JsonPropertyName("primaryWorkCount")]
        public int PrimaryWorkCount { get; set; }

        [JsonPropertyName("books")]
        public List<SeriesBookResource> Books { get; set; }
    }

    public class SeriesBookResource
    {
        [JsonPropertyName("foreignBookId")]
        public string ForeignBookId { get; set; }

        [JsonPropertyName("foreignAuthorId")]
        public string ForeignAuthorId { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; }

        [JsonPropertyName("overview")]
        public string Overview { get; set; }

        [JsonPropertyName("authorName")]
        public string AuthorName { get; set; }

        [JsonPropertyName("releaseDate")]
        public DateTime? ReleaseDate { get; set; }

        [JsonPropertyName("position")]
        public string Position { get; set; }

        [JsonPropertyName("images")]
        public List<MediaCover> Images { get; set; }

        [JsonPropertyName("ratings")]
        public Ratings Ratings { get; set; }
    }

    internal class HardcoverSeriesMeta
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Slug { get; set; }
        public string Description { get; set; }
        public int BooksCount { get; set; }
        public int PrimaryBooksCount { get; set; }
    }
}
