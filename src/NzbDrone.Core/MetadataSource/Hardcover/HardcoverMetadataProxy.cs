using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MetadataSource.BookInfo;
using NzbDrone.Core.MetadataSource.Goodreads;
using AuthorResource = NzbDrone.Core.MetadataSource.BookInfo.AuthorResource;
using BookResource = NzbDrone.Core.MetadataSource.BookInfo.BookResource;
using SeriesResource = NzbDrone.Core.MetadataSource.BookInfo.SeriesResource;
using WorkResource = NzbDrone.Core.MetadataSource.BookInfo.WorkResource;

namespace NzbDrone.Core.MetadataSource.Hardcover
{
    public interface IHardcoverMetadataProxy
    {
        bool IsConfigured { get; }
        bool IsNativeEnabled { get; }
        List<SearchJsonResource> Search(string query, bool interactiveSearch = false);
        JObject ExecuteSearchGraphQl(string operationName, string query, JObject variables, bool interactiveSearch = false);
        AuthorResource GetAuthor(string foreignAuthorId, bool interactiveSearch = false);
        WorkResource GetWork(string foreignWorkId, bool interactiveSearch = false);
        WorkResource GetEdition(string foreignEditionId, bool interactiveSearch = false);
    }

    /// <summary>
    /// Native Hardcover metadata access for ChaptarrNG's explicit Hardcover mode.
    ///
    /// The legacy Readarr metadata API remains available through BookInfoProxy
    /// when native mode is disabled. A native deployment without a token fails
    /// clearly instead of silently switching metadata providers. This class
    /// deliberately returns the existing Bookshelf metadata resource shapes so
    /// native and compatibility mode share the same domain mapping.
    /// </summary>
    public class HardcoverMetadataProxy : IHardcoverMetadataProxy
    {
        private const string DefaultApiUrl = "https://api.hardcover.app";
        private const int CacheLockCount = 32;
        private const int MaxRequestAttempts = 3;
        private const int AuthorPageSize = 50;
        private const int RequestRateLimitSeconds = 1;
        private const int DailyQuotaReservePercent = 10;
        private static readonly object RateLimitPauseLock = new object();
        private static readonly SemaphoreSlim RequestSemaphore = new SemaphoreSlim(1, 1);
        private static readonly Regex AsinRegex = new Regex("^[Bb]0[0-9A-Za-z]{8}$", RegexOptions.Compiled);
        private static readonly Regex Isbn10Regex = new Regex("^[0-9]{9}[0-9Xx]$", RegexOptions.Compiled);
        private static readonly Regex Isbn13Regex = new Regex("^[0-9]{13}$", RegexOptions.Compiled);
        private static readonly object[] CacheLocks = Enumerable.Range(0, CacheLockCount).Select(x => new object()).ToArray();
        private static readonly HashSet<string> NonPrimaryRoles = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase)
        {
            "pseudonym",
            "translator",
            "narrator",
            "reading",
            "adaptation",
            "illustrator",
            "illustrations",
            "ilustrator",
            "contributor & illustrator",
            "writer/illustrator",
            "writer, illustrator",
            "writer, editor",
            "brand",
            "visual art",
            "character design",
            "artist",
            "cover",
            "cover art",
            "cover artist",
            "text",
            "writer",
            "writer, editior",
            "author & editor",
            "penciler",
            "penciller",
            "inker",
            "colourist",
            "letterer",
            "colorist",
            "contributor",
            "contributer",
            "guion",
            "dibujo",
            "foreword",
            "foreward",
            "introduction",
            "introduction/contributor",
            "editor/introduction",
            "editor",
            "editor and contributor",
            "editor/contributor",
            "editor / contributor",
            "editor,contributor"
        };

        private static readonly string SearchQuery = @"
query Search($query: String!) {
  search(
    query: $query
    per_page: 15
    query_type: ""book""
    fields: ""title,isbns,series_names,author_names,alternative_titles""
    weights: ""5,1,3,5,1""
    sort: ""ratings_count:desc,_text_match:desc""
  ) {
    ids
  }
}";

        private static readonly string IdentifierQuery = @"
query GetWorkByASINISBN($identifier: String!) {
  editions(
    where: {
      _or: [
        { isbn_10: { _eq: $identifier } }
        { isbn_13: { _eq: $identifier } }
        { asin: { _eq: $identifier } }
      ]
    }
  ) {
    id
    book_id
    title
    subtitle
    asin
    isbn_13
    edition_format
    reading_format_id
    pages
    language { code3 }
    publisher { name }
    release_date
    physical_format
    physical_information
    edition_information
    book {
      id
      title
      subtitle
      description
      release_date
      cached_tags(path: ""$.Genre"")
      cached_image(path: ""url"")
      slug
      canonical_id
      rating
      ratings_count
      contributions {
        contribution
        author {
          id
          name
          slug
          bio
          cached_image(path: ""url"")
        }
      }
    }
  }
}";

        private static readonly string WorkQuery = @"
query GetWork($bookID: Int!) {
  books_by_pk(id: $bookID) {
    id
    title
    subtitle
    description
    release_date
    cached_tags(path: ""$.Genre"")
    cached_image(path: ""url"")
    slug
    canonical_id
    rating
    ratings_count
    contributions {
      contribution
      author {
        id
        name
        slug
        bio
        cached_image(path: ""url"")
      }
    }
    book_series {
      position
      series {
        id
        name
        description
      }
    }
    editions(order_by: { score: desc_nulls_last }) {
      id
      title
      subtitle
      asin
      isbn_13
      edition_format
      reading_format_id
      pages
      language { code3 }
      publisher { name }
      release_date
      physical_format
      physical_information
      edition_information
    }
  }
}";

        private static readonly string WorksByIdsQuery = @"
query GetWorksByIds($ids: [Int!]!) {
  books(where: { id: { _in: $ids } }, limit: 100) {
    id
    title
    subtitle
    description
    release_date
    cached_tags(path: ""$.Genre"")
    cached_image(path: ""url"")
    slug
    canonical_id
    rating
    ratings_count
    contributions {
      contribution
      author {
        id
        name
        slug
        bio
        cached_image(path: ""url"")
      }
    }
    book_series {
      position
      series {
        id
        name
        description
      }
    }
    editions(order_by: { score: desc_nulls_last }) {
      id
      title
      subtitle
      asin
      isbn_13
      edition_format
      reading_format_id
      pages
      language { code3 }
      publisher { name }
      release_date
      physical_format
      physical_information
      edition_information
    }
  }
}";

        private static readonly string EditionQuery = @"
query GetEdition($editionID: Int!) {
  editions_by_pk(id: $editionID) {
    book {
      id
      title
      subtitle
      description
      release_date
      cached_tags(path: ""$.Genre"")
      cached_image(path: ""url"")
      slug
      canonical_id
      rating
      ratings_count
      contributions {
        contribution
        author {
          id
          name
          slug
          bio
          cached_image(path: ""url"")
        }
      }
      book_series {
        position
        series {
          id
          name
          description
        }
      }
      editions(order_by: { score: desc_nulls_last }) {
        id
        title
        subtitle
        asin
        isbn_13
        edition_format
        reading_format_id
        pages
        language { code3 }
        publisher { name }
        release_date
        physical_format
        physical_information
        edition_information
      }
    }
  }
}";

        private static readonly string AuthorQuery = @"
query GetAuthorEditions($id: Int!, $limit: Int!, $offset: Int!) {
  authors_by_pk(id: $id) {
    id
    name
    slug
    bio
    cached_image(path: ""url"")
    contributions(
      limit: $limit
      offset: $offset
      order_by: { book: { ratings_count: desc } }
      where: {
        contributable_type: { _eq: ""Book"" }
        book: { book_status_id: { _eq: ""1"" } }
      }
    ) {
      contribution
      author {
        id
        name
        slug
        bio
        cached_image(path: ""url"")
      }
      book {
        id
        title
        subtitle
        description
        release_date
        cached_tags(path: ""$.Genre"")
        cached_image(path: ""url"")
        slug
        canonical_id
        rating
        ratings_count
        contributions {
          contribution
          author {
            id
            name
            slug
            bio
            cached_image(path: ""url"")
          }
        }
        book_series {
          position
          series {
            id
            name
            description
          }
        }
        editions(order_by: { score: desc_nulls_last }) {
          id
          title
          subtitle
          asin
          isbn_13
          edition_format
          reading_format_id
          pages
          language { code3 }
          publisher { name }
          release_date
          physical_format
          physical_information
          edition_information
        }
      }
    }
  }
}";

        private static DateTime _rateLimitPauseUntilUtc = DateTime.MinValue;
        private static long _dailyRateLimitLimit = -1;
        private static long _dailyRateLimitRemaining = -1;
        private static DateTime _dailyRateLimitResetAtUtc = DateTime.MinValue;

        private sealed class RateLimitBucket
        {
            public string Name { get; set; }
            public long? Limit { get; set; }
            public long? WindowSeconds { get; set; }
            public long? Remaining { get; set; }
            public long? ResetSeconds { get; set; }
        }

        private readonly IHttpClient _httpClient;
        private readonly IConfigService _configService;
        private readonly Logger _logger;
        private readonly ICached<WorkResource> _workCache;
        private readonly ICached<AuthorResource> _authorCache;
        private readonly ICached<List<SearchJsonResource>> _searchCache;

        public HardcoverMetadataProxy(IHttpClient httpClient,
                                      Logger logger,
                                      ICacheManager cacheManager,
                                      IConfigService configService)
        {
            _httpClient = httpClient;
            _logger = logger;
            _configService = configService;
            _workCache = cacheManager.GetCache<WorkResource>(GetType(), "works");
            _authorCache = cacheManager.GetCache<AuthorResource>(GetType(), "authors");
            _searchCache = cacheManager.GetCache<List<SearchJsonResource>>(GetType(), "search");
        }

        public bool IsConfigured => !GetApiToken().IsNullOrWhiteSpace();

        // Mode selection is intentionally independent from credentials. A
        // native deployment with a missing token must fail clearly at request
        // time instead of silently switching to METADATA_URL.
        public bool IsNativeEnabled =>
            string.Equals(Environment.GetEnvironmentVariable("HARDCOVER"), "true", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Environment.GetEnvironmentVariable("HARDCOVER_NATIVE"), "false", StringComparison.OrdinalIgnoreCase);

        public List<SearchJsonResource> Search(string query, bool interactiveSearch = false)
        {
            if (query.IsNullOrWhiteSpace())
            {
                return new List<SearchJsonResource>();
            }

            var normalized = query.Trim();
            var cacheKey = normalized.ToLowerInvariant();

            return GetCached(_searchCache, cacheKey, () => SearchUncached(normalized, interactiveSearch), TimeSpan.FromMinutes(10));
        }

        public JObject ExecuteSearchGraphQl(string operationName, string query, JObject variables, bool interactiveSearch = false)
        {
            return ExecuteGraphQl(operationName, query, variables ?? new JObject(), interactiveSearch);
        }

        public AuthorResource GetAuthor(string foreignAuthorId, bool interactiveSearch = false)
        {
            var id = ParseId(foreignAuthorId, "author");

            var cacheKey = id.ToString(CultureInfo.InvariantCulture);
            return GetCached(_authorCache, cacheKey, () => LoadAuthor(id, interactiveSearch), TimeSpan.FromMinutes(30));
        }

        public WorkResource GetWork(string foreignWorkId, bool interactiveSearch = false)
        {
            var id = ParseId(foreignWorkId, "work");

            var cacheKey = id.ToString(CultureInfo.InvariantCulture);
            return GetCached(_workCache, cacheKey, () => LoadWork(id, interactiveSearch), TimeSpan.FromHours(6));
        }

        public WorkResource GetEdition(string foreignEditionId, bool interactiveSearch = false)
        {
            var id = ParseId(foreignEditionId, "edition");
            var data = ExecuteGraphQl("GetEdition", EditionQuery, new JObject { ["editionID"] = id }, interactiveSearch);
            var edition = data["editions_by_pk"] as JObject;
            var work = edition?["book"] as JObject;
            var workId = GetInt(work?["id"]);

            if (workId == 0)
            {
                throw new EditionNotFoundException(foreignEditionId);
            }

            var canonicalId = GetInt(work["canonical_id"]);
            if (canonicalId != 0 && canonicalId != workId)
            {
                return GetWork(canonicalId.ToString(CultureInfo.InvariantCulture), interactiveSearch);
            }

            var result = MapWork(work);
            _workCache.Set(workId.ToString(CultureInfo.InvariantCulture), result, TimeSpan.FromHours(6));
            return result;
        }

        private List<SearchJsonResource> SearchUncached(string query, bool interactiveSearch)
        {
            if (LooksLikeIsbnOrAsin(query))
            {
                return SearchByIdentifier(query.Replace("-", string.Empty).Replace(" ", string.Empty), interactiveSearch);
            }

            var data = ExecuteGraphQl("Search", SearchQuery, new JObject { ["query"] = query }, interactiveSearch);
            var ids = AsObject(data["search"])?["ids"] as JArray;
            var results = new List<SearchJsonResource>();

            if (ids == null)
            {
                return results;
            }

            var workIds = ids
                .Select(GetInt)
                .Where(x => x != 0)
                .Distinct()
                .ToList();
            var works = LoadWorks(workIds, interactiveSearch);

            foreach (var workId in workIds)
            {
                if (works.TryGetValue(workId, out var work))
                {
                    var edition = work.Books?.FirstOrDefault();
                    var author = work.Authors?.FirstOrDefault();

                    if (edition == null || author == null)
                    {
                        continue;
                    }

                    results.Add(ToSearchResource(work, edition, author));
                }
            }

            return results;
        }

        private List<SearchJsonResource> SearchByIdentifier(string identifier, bool interactiveSearch)
        {
            var data = ExecuteGraphQl("GetWorkByASINISBN", IdentifierQuery, new JObject { ["identifier"] = identifier }, interactiveSearch);
            var editions = data["editions"] as JArray;
            var results = new List<SearchJsonResource>();

            if (editions == null)
            {
                return results;
            }

            var editionRows = editions.OfType<JObject>().ToList();
            var canonicalIds = editionRows
                .Select(x => x["book"] as JObject)
                .Where(x => GetInt(x?["id"]) > 0)
                .Select(x => new { WorkId = GetInt(x["id"]), CanonicalId = GetInt(x["canonical_id"]) })
                .Where(x => x.CanonicalId != 0 && x.CanonicalId != x.WorkId)
                .Select(x => x.CanonicalId);
            var works = LoadWorks(canonicalIds, interactiveSearch);
            var missingBookIds = editionRows
                .Where(x => GetInt(AsObject(x["book"])?["id"]) == 0)
                .Select(x => GetInt(x["book_id"]))
                .Where(x => x > 0)
                .Distinct();
            foreach (var workId in missingBookIds)
            {
                try
                {
                    works[workId] = GetWork(workId.ToString(CultureInfo.InvariantCulture), interactiveSearch);
                }
                catch (BookInfoException ex)
                {
                    _logger.Warn(ex, "Unable to load Hardcover work {0} for identifier {1}", workId, identifier);
                }
            }

            foreach (var editionToken in editionRows)
            {
                var workId = GetInt(editionToken["book_id"]);
                var editionId = GetInt(editionToken["id"]);
                var book = editionToken["book"] as JObject;
                var canonicalId = GetInt(book?["canonical_id"]);
                var selectedWorkId = canonicalId != 0 && canonicalId != workId ? canonicalId : workId;
                if (workId == 0 || editionId == 0)
                {
                    continue;
                }

                try
                {
                    WorkResource work;
                    BookResource edition;
                    if (GetInt(book?["id"]) > 0 && selectedWorkId == workId)
                    {
                        work = MapWork(book);
                        edition = MapEdition(editionToken, book);
                    }
                    else if (works.TryGetValue(selectedWorkId, out work))
                    {
                        edition = work.Books?.FirstOrDefault(x => x.ForeignId == editionId);
                    }
                    else
                    {
                        continue;
                    }

                    var author = work.Authors?.FirstOrDefault();

                    if (edition != null && author != null)
                    {
                        results.Add(ToSearchResource(work, edition, author));
                    }
                }
                catch (BookInfoException ex)
                {
                    _logger.Warn(ex, "Unable to map Hardcover edition {0} returned for identifier {1}", editionId, identifier);
                }
            }

            return results;
        }

        private Dictionary<int, WorkResource> LoadWorks(IEnumerable<int> requestedIds, bool interactiveSearch = false)
        {
            return LoadWorks(requestedIds, new HashSet<int>(), interactiveSearch);
        }

        private Dictionary<int, WorkResource> LoadWorks(IEnumerable<int> requestedIds, HashSet<int> visitedIds, bool interactiveSearch)
        {
            var workIds = requestedIds
                .Where(x => x > 0)
                .Distinct()
                .Where(visitedIds.Add)
                .ToList();
            var works = new Dictionary<int, WorkResource>();
            var missingIds = new List<int>();
            var canonicalIds = new Dictionary<int, int>();

            foreach (var workId in workIds)
            {
                var key = workId.ToString(CultureInfo.InvariantCulture);
                var cached = _workCache.Find(key);
                if (cached != null)
                {
                    works[workId] = cached;
                }
                else
                {
                    missingIds.Add(workId);
                }
            }

            for (var offset = 0; offset < missingIds.Count; offset += 100)
            {
                var batch = missingIds.Skip(offset).Take(100).ToList();
                var data = ExecuteGraphQl(
                    "GetWorksByIds",
                    WorksByIdsQuery,
                    new JObject
                    {
                        ["ids"] = new JArray(batch.Select(x => new JValue(x)))
                    },
                    interactiveSearch);
                var rows = data["books"] as JArray;

                foreach (var row in rows?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                {
                    var workId = GetInt(row["id"]);
                    if (workId == 0)
                    {
                        continue;
                    }

                    var canonicalId = GetInt(row["canonical_id"]);
                    if (canonicalId != 0 && canonicalId != workId)
                    {
                        canonicalIds[workId] = canonicalId;
                        continue;
                    }

                    try
                    {
                        var work = MapWork(row);
                        _workCache.Set(workId.ToString(CultureInfo.InvariantCulture), work, TimeSpan.FromHours(6));
                        works[workId] = work;
                    }
                    catch (BookInfoException ex)
                    {
                        _logger.Warn(ex, "Unable to map Hardcover work {0} returned in a batch lookup", workId);
                    }
                }
            }

            if (canonicalIds.Count > 0)
            {
                var unresolvedCanonicalIds = canonicalIds
                    .Where(x => !works.ContainsKey(x.Value))
                    .Select(x => x.Value)
                    .ToList();
                var canonicalWorks = LoadWorks(unresolvedCanonicalIds, visitedIds, interactiveSearch);
                foreach (var canonical in canonicalIds)
                {
                    if (works.TryGetValue(canonical.Value, out var work) ||
                        canonicalWorks.TryGetValue(canonical.Value, out work))
                    {
                        works[canonical.Key] = work;
                        _workCache.Set(canonical.Key.ToString(CultureInfo.InvariantCulture), work, TimeSpan.FromHours(6));
                    }
                }
            }

            return works;
        }

        private AuthorResource LoadAuthor(int id, bool interactiveSearch)
        {
            JObject author = null;
            var works = new List<WorkResource>();
            var offset = 0;

            while (true)
            {
                var data = ExecuteGraphQl(
                    "GetAuthorEditions",
                    AuthorQuery,
                    new JObject
                    {
                        ["id"] = id,
                        ["limit"] = AuthorPageSize,
                        ["offset"] = offset
                    },
                    interactiveSearch);

                author = author ?? data["authors_by_pk"] as JObject;
                var pageAuthor = data["authors_by_pk"] as JObject;
                var contributions = pageAuthor?["contributions"] as JArray ?? new JArray();

                foreach (var contribution in contributions.OfType<JObject>())
                {
                    var book = contribution["book"] as JObject;
                    var workId = GetInt(book?["id"]);
                    if (workId == 0)
                    {
                        continue;
                    }

                    try
                    {
                        var work = book?["editions"] is JArray ? MapWork(book) : GetWork(workId.ToString(CultureInfo.InvariantCulture), interactiveSearch);

                        // The contribution query is scoped to this author. Keep
                        // its work even when canonicalization or role mapping
                        // omits the author from the nested contributor list.
                        works.Add(work);
                        _workCache.Set(workId.ToString(CultureInfo.InvariantCulture), work, TimeSpan.FromHours(6));
                    }
                    catch (BookInfoException ex)
                    {
                        _logger.Warn(ex, "Unable to load Hardcover work {0} for author {1}", workId, id);
                    }
                }

                if (contributions.Count < AuthorPageSize)
                {
                    break;
                }

                offset += AuthorPageSize;
            }

            if (author == null)
            {
                throw new AuthorNotFoundException(id.ToString(CultureInfo.InvariantCulture));
            }

            var result = MapAuthor(author);

            result.Works = works
                .GroupBy(x => x.ForeignId)
                .Select(x => x.First())
                .ToList();
            result.Series = result.Works
                .SelectMany(x => x.Series ?? new List<SeriesResource>())
                .GroupBy(x => x.ForeignId)
                .Select(x => x.First())
                .ToList();

            return result;
        }

        private WorkResource LoadWork(int id, bool interactiveSearch)
        {
            var data = ExecuteGraphQl("GetWork", WorkQuery, new JObject { ["bookID"] = id }, interactiveSearch);
            var work = data["books_by_pk"] as JObject;

            if (work == null)
            {
                throw new BookNotFoundException(id.ToString(CultureInfo.InvariantCulture));
            }

            var canonicalId = GetInt(work["canonical_id"]);
            if (canonicalId != 0 && canonicalId != id)
            {
                return GetWork(canonicalId.ToString(CultureInfo.InvariantCulture), interactiveSearch);
            }

            return MapWork(work);
        }

        private WorkResource MapWork(JObject work)
        {
            var workId = GetInt(work["id"]);
            var authors = SelectAuthors(work["contributions"] as JArray);

            if (workId == 0 || !authors.Any())
            {
                throw new BookInfoException("Hardcover returned a work without an id or author");
            }

            var series = (work["book_series"] as JArray ?? new JArray())
                .OfType<JObject>()
                .Select(x => MapSeries(x, workId))
                .Where(x => x != null)
                .ToList();

            var books = (work["editions"] as JArray ?? new JArray())
                .OfType<JObject>()
                .Select(x => MapEdition(x, work))
                .Where(x => x != null)
                .ToList();

            var authorResources = authors.Select(MapAuthor).ToList();
            foreach (var authorResource in authorResources)
            {
                authorResource.Series = series;
            }

            return new WorkResource
            {
                ForeignId = workId,
                Title = CleanSubtitle(GetString(work["title"]), GetString(work["subtitle"])),
                Url = HardcoverUrl("books", GetString(work["slug"]), workId),
                ReleaseDate = ParseDate(GetString(work["release_date"])),
                Genres = GetGenres(work["cached_tags"]),
                RelatedWorks = new List<int>(),
                Books = books,
                Series = series,
                Authors = authorResources
            };
        }

        private static BookResource MapEdition(JObject edition, JObject work)
        {
            var editionId = GetInt(edition["id"]);
            if (editionId == 0)
            {
                return null;
            }

            var title = GetString(edition["title"]);
            var subtitle = GetString(edition["subtitle"]);
            var workId = GetInt(work["id"]);
            var format = GetString(edition["edition_format"]);
            var slug = GetString(work["slug"]);
            var narratorNames = (work["contributions"] as JArray ?? new JArray())
                .OfType<JObject>()
                .Where(x => IsNarratorRole(GetString(x["contribution"])))
                .Select(x => GetString(AsObject(x["author"])?["name"]))
                .Where(x => x.IsNotNullOrWhiteSpace())
                .Distinct(StringComparer.InvariantCultureIgnoreCase)
                .ToList();
            var contributors = SelectAuthorContributions(work["contributions"] as JArray)
                .Select(x => new ContributorResource
                {
                    ForeignId = GetInt(AsObject(x["author"])?["id"]),
                    Role = WithFallback(GetString(x["contribution"]), "Author")
                })
                .ToList();

            return new BookResource
            {
                ForeignId = editionId,
                Asin = GetString(edition["asin"]),
                Description = WithFallback(GetString(work["description"]), "N/A"),
                Isbn13 = GetString(edition["isbn_13"]),
                Title = CleanSubtitle(title, subtitle),
                Language = GetString(AsObject(edition["language"])?["code3"]),
                Format = format.IsNullOrWhiteSpace() ? GetString(edition["physical_format"]) : format,
                ReadingFormatId = GetInt(edition["reading_format_id"]),
                NarratorNames = narratorNames,
                EditionInformation = WithFallback(GetString(edition["edition_information"]), GetString(edition["physical_information"])),
                Publisher = GetString(AsObject(edition["publisher"])?["name"]),
                ImageUrl = GetString(work["cached_image"]),
                IsEbook = string.Equals(format, "ebook", StringComparison.OrdinalIgnoreCase) || string.Equals(format, "kindle edition", StringComparison.OrdinalIgnoreCase),
                NumPages = GetNullableInt(edition["pages"]),
                RatingCount = GetInt(work["ratings_count"]),
                AverageRating = GetDouble(work["rating"]),
                Url = HardcoverUrl("books", slug, workId),
                ReleaseDate = ParseDate(GetString(edition["release_date"])),
                Contributors = contributors
            };
        }

        private static AuthorResource MapAuthor(JObject author)
        {
            var id = GetInt(author["id"]);
            var name = GetString(author["name"]);

            return new AuthorResource
            {
                ForeignId = id,
                Name = name,
                Description = WithFallback(GetString(author["bio"]), "N/A"),
                ImageUrl = GetString(author["cached_image"]),
                Url = HardcoverUrl("authors", GetString(author["slug"]), id),
                RatingCount = 0,
                AverageRating = 0,
                Works = new List<WorkResource>(),
                Series = new List<SeriesResource>()
            };
        }

        private static SeriesResource MapSeries(JObject bookSeries, int workId)
        {
            var series = bookSeries["series"] as JObject;
            var seriesId = GetInt(series?["id"]);
            if (seriesId == 0)
            {
                return null;
            }

            var position = GetDouble(bookSeries["position"]);

            return new SeriesResource
            {
                ForeignId = seriesId,
                Title = GetString(series["name"]),
                Description = GetString(series["description"]),
                LinkItems = new List<SeriesWorkLinkResource>
                {
                    new SeriesWorkLinkResource
                    {
                        ForeignWorkId = workId,
                        PositionInSeries = position.ToString(CultureInfo.InvariantCulture),
                        SeriesPosition = (int)position,
                        Primary = false
                    }
                }
            };
        }

        private static SearchJsonResource ToSearchResource(WorkResource work, BookResource edition, AuthorResource author)
        {
            return new SearchJsonResource
            {
                BookId = edition.ForeignId,
                WorkId = work.ForeignId,
                BookUrl = edition.Url,
                ImageUrl = edition.ImageUrl,
                Title = work.Title,
                BookTitleBare = edition.Title,
                PageCount = edition.NumPages ?? 0,
                AverageRating = (decimal)edition.AverageRating,
                RatingsCount = edition.RatingCount,
                Author = new AuthorJsonResource
                {
                    Id = author.ForeignId,
                    Name = author.Name,
                    ProfileUrl = author.Url,
                    IsGoodreadsAuthor = false
                }
            };
        }

        private JObject ExecuteGraphQl(string operationName, string query, JObject variables, bool interactiveSearch = false)
        {
            if (!IsConfigured)
            {
                throw new BookInfoException("Native Hardcover metadata requires HARDCOVER_AUTH, HARDCOVER_API_KEY, or the saved HardcoverApiToken setting");
            }

            var payload = new JObject
            {
                ["operationName"] = operationName,
                ["query"] = query,
                ["variables"] = variables
            }.ToString(Formatting.None);

            for (var attempt = 0; attempt < MaxRequestAttempts; attempt++)
            {
                var request = BuildGraphQlRequest(payload);
                HttpResponse response;
                DateTime? rateLimitPauseUntil = null;

                try
                {
                    RequestSemaphore.Wait();
                    try
                    {
                        ThrowIfRateLimitCooldownActive(operationName);
                        ThrowIfDailyQuotaReserveActive(operationName, interactiveSearch);
                        response = _httpClient.Execute(request);

                        UpdateDailyRateLimit(response);
                        rateLimitPauseUntil = GetRateLimitPauseUntil(response);
                        if (response.StatusCode == HttpStatusCode.TooManyRequests && !rateLimitPauseUntil.HasValue)
                        {
                            rateLimitPauseUntil = DateTime.UtcNow.AddMinutes(1);
                        }

                        if (rateLimitPauseUntil.HasValue && rateLimitPauseUntil.Value > DateTime.UtcNow)
                        {
                            var reason = response.StatusCode == HttpStatusCode.TooManyRequests
                                ? "returned HTTP 429"
                                : "rate-limit headers show an exhausted bucket";
                            PauseRequestsUntil(rateLimitPauseUntil.Value, reason);
                        }
                    }
                    finally
                    {
                        RequestSemaphore.Release();
                    }
                }
                catch (WebException ex)
                {
                    if (attempt < MaxRequestAttempts - 1 && IsTransientNetworkError(ex))
                    {
                        WaitUntilRetry(attempt, ex.Status.ToString());
                        continue;
                    }

                    throw new BookInfoException("Hardcover {0} network request failed", ex, operationName);
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    throw new BookInfoException("Hardcover rate limit reached for {0}; no new requests will be sent before {1:yyyy-MM-dd HH:mm:ss} UTC", operationName, rateLimitPauseUntil.Value);
                }

                if (IsTransientStatus(response.StatusCode))
                {
                    if (attempt < MaxRequestAttempts - 1)
                    {
                        WaitUntilRetry(attempt, response.StatusCode.ToString());
                        continue;
                    }

                    throw new BookInfoException("Hardcover returned HTTP status {0} for {1} after {2} attempts", response.StatusCode, operationName, MaxRequestAttempts);
                }

                if (response.HasHttpError)
                {
                    throw new BookInfoException("Hardcover returned HTTP status {0} for {1}", response.StatusCode, operationName);
                }

                if (response.Content.IsNullOrWhiteSpace())
                {
                    throw new BookInfoException("Hardcover {0} returned an empty response", operationName);
                }

                JObject root;
                try
                {
                    root = JObject.Parse(response.Content);
                }
                catch (JsonException ex)
                {
                    throw new BookInfoException("Hardcover {0} returned invalid JSON", ex, operationName);
                }

                var errors = root["errors"] as JArray;
                if (errors != null && errors.Count > 0)
                {
                    var firstError = errors.FirstOrDefault() as JObject;
                    var message = firstError?["message"]?.Value<string>() ?? "GraphQL request failed";
                    throw new BookInfoException("Hardcover {0} failed: {1}", operationName, message);
                }

                return root["data"] as JObject ?? throw new BookInfoException("Hardcover {0} returned no data", operationName);
            }

            throw new BookInfoException("Hardcover {0} request failed", operationName);
        }

        private HttpRequest BuildGraphQlRequest(string payload)
        {
            var request = new HttpRequestBuilder($"{GetApiUrl().TrimEnd('/')}/v1/graphql")
                .Post()
                .WithRateLimit(RequestRateLimitSeconds)
                .Accept(HttpAccept.Json)
                .SetHeader("Authorization", $"Bearer {GetApiToken()}")
                .SetHeader("X-Api-Key", GetApiToken())
                .SetHeader("User-Agent", "ChaptarrNG (Hardcover metadata)")
                .SetHeader("Content-Type", "application/json")
                .KeepAlive()
                .Build();

            request.SuppressHttpError = true;
            request.SetContent(payload);
            request.ContentSummary = "Hardcover GraphQL request";
            return request;
        }

        private void WaitUntilRetry(int attempt, string reason)
        {
            var seconds = Math.Min(1 << attempt, 4);
            _logger.Info("Hardcover returned {0}, retrying in {1}s (attempt {2}/{3})", reason, seconds, attempt + 1, MaxRequestAttempts);
            System.Threading.Thread.Sleep(TimeSpan.FromSeconds(seconds));
        }

        private void ThrowIfRateLimitCooldownActive(string operationName)
        {
            lock (RateLimitPauseLock)
            {
                if (_rateLimitPauseUntilUtc > DateTime.UtcNow)
                {
                    throw new BookInfoException("Hardcover rate limit pause is active for {0}; no request was sent. Retry after {1:yyyy-MM-dd HH:mm:ss} UTC", operationName, _rateLimitPauseUntilUtc);
                }
            }
        }

        private void ThrowIfDailyQuotaReserveActive(string operationName, bool interactiveSearch)
        {
            if (interactiveSearch)
            {
                return;
            }

            long remaining;
            long limit;
            long reserved;
            DateTime resetAt;

            lock (RateLimitPauseLock)
            {
                if (_dailyRateLimitResetAtUtc <= DateTime.UtcNow)
                {
                    _dailyRateLimitLimit = -1;
                    _dailyRateLimitRemaining = -1;
                    _dailyRateLimitResetAtUtc = DateTime.MinValue;
                }

                if (_dailyRateLimitLimit <= 0 || _dailyRateLimitRemaining < 0)
                {
                    return;
                }

                remaining = _dailyRateLimitRemaining;
                limit = _dailyRateLimitLimit;
                resetAt = _dailyRateLimitResetAtUtc;
                reserved = GetDailyQuotaReserve(limit);

                if (remaining > reserved)
                {
                    return;
                }
            }

            throw new BookInfoException(
                "Hardcover daily quota reserve is active for {0}; no request was sent. {1} of {2} daily requests remain for interactive searches until {3:yyyy-MM-dd HH:mm:ss} UTC",
                operationName,
                remaining,
                limit,
                resetAt);
        }

        private static long GetDailyQuotaReserve(long dailyLimit)
        {
            var reserveDivisor = 100 / DailyQuotaReservePercent;
            return (dailyLimit / reserveDivisor) + (dailyLimit % reserveDivisor == 0 ? 0 : 1);
        }

        private void PauseRequestsUntil(DateTime pauseUntilUtc, string reason)
        {
            var pauseExtended = false;
            lock (RateLimitPauseLock)
            {
                if (pauseUntilUtc > _rateLimitPauseUntilUtc)
                {
                    _rateLimitPauseUntilUtc = pauseUntilUtc;
                    pauseExtended = true;
                }
            }

            if (pauseExtended)
            {
                _logger.Warn("Hardcover {0}; pausing all requests until {1:yyyy-MM-dd HH:mm:ss} UTC", reason, pauseUntilUtc);
            }
        }

        private static DateTime? GetRateLimitPauseUntil(HttpResponse response)
        {
            var retryAfter = GetRetryAfterUntil(response);
            var exhaustedBucketReset = GetExhaustedRateLimitBucketReset(response);

            if (retryAfter.HasValue && exhaustedBucketReset.HasValue)
            {
                return retryAfter.Value > exhaustedBucketReset.Value ? retryAfter : exhaustedBucketReset;
            }

            return retryAfter ?? exhaustedBucketReset;
        }

        private static void UpdateDailyRateLimit(HttpResponse response)
        {
            var snapshot = GetDailyRateLimitSnapshot(response);
            if (!snapshot.Remaining.HasValue)
            {
                return;
            }

            lock (RateLimitPauseLock)
            {
                var limit = snapshot.Limit ?? _dailyRateLimitLimit;
                var resetAt = snapshot.ResetAtUtc ?? _dailyRateLimitResetAtUtc;

                if (limit <= 0 || resetAt <= DateTime.UtcNow)
                {
                    return;
                }

                _dailyRateLimitLimit = limit;
                _dailyRateLimitRemaining = snapshot.Remaining.Value;
                _dailyRateLimitResetAtUtc = resetAt;
            }
        }

        private static (long? Limit, long? Remaining, DateTime? ResetAtUtc) GetDailyRateLimitSnapshot(HttpResponse response)
        {
            var policies = ParseRateLimitBuckets(response?.Headers["RateLimit-Policy"], true);
            var dailyPolicy = policies.FirstOrDefault(x =>
                string.Equals(x.Name, "daily", StringComparison.OrdinalIgnoreCase) ||
                x.WindowSeconds.GetValueOrDefault() >= 86400);

            if (dailyPolicy != null)
            {
                var status = ParseRateLimitBuckets(response?.Headers["RateLimit"], false)
                    .FirstOrDefault(x => string.Equals(x.Name, dailyPolicy.Name, StringComparison.OrdinalIgnoreCase));

                if (status?.Remaining.HasValue == true)
                {
                    var resetSeconds = status.ResetSeconds ?? dailyPolicy.WindowSeconds;
                    var resetAt = resetSeconds.HasValue
                        ? DateTime.UtcNow.AddSeconds(Math.Max(resetSeconds.Value, 1))
                        : (DateTime?)null;

                    return (dailyPolicy.Limit, status.Remaining, resetAt);
                }
            }

            var legacyLimit = GetHeaderLong(response, "X-RateLimit-Daily-Limit");
            var legacyRemaining = GetHeaderLong(response, "X-RateLimit-Daily-Remaining");
            var legacyReset = GetHeaderLong(response, "X-RateLimit-Daily-Reset");

            if (legacyRemaining.HasValue)
            {
                DateTime? resetAt = null;
                if (legacyReset.HasValue)
                {
                    try
                    {
                        resetAt = DateTimeOffset.FromUnixTimeSeconds(legacyReset.Value).UtcDateTime;
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        // An invalid legacy reset value should not block background work.
                    }
                }

                return (legacyLimit, legacyRemaining, resetAt);
            }

            return (null, null, null);
        }

        private static List<RateLimitBucket> ParseRateLimitBuckets(string header, bool policy)
        {
            if (header.IsNullOrWhiteSpace())
            {
                return new List<RateLimitBucket>();
            }

            var buckets = new List<RateLimitBucket>();

            foreach (var rawBucket in header.Split(','))
            {
                var parts = rawBucket.Split(';').Select(x => x.Trim()).ToList();
                if (parts.Count == 0 || parts[0].IsNullOrWhiteSpace())
                {
                    continue;
                }

                var bucket = new RateLimitBucket { Name = parts[0].Trim().Trim('"') };

                foreach (var part in parts.Skip(1))
                {
                    var separator = part.IndexOf('=');
                    if (separator <= 0 || !long.TryParse(part.Substring(separator + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                    {
                        continue;
                    }

                    var parameter = part.Substring(0, separator).Trim();
                    if (policy && parameter.Equals("q", StringComparison.OrdinalIgnoreCase))
                    {
                        bucket.Limit = value;
                    }
                    else if (policy && parameter.Equals("w", StringComparison.OrdinalIgnoreCase))
                    {
                        bucket.WindowSeconds = value;
                    }
                    else if (!policy && parameter.Equals("r", StringComparison.OrdinalIgnoreCase))
                    {
                        bucket.Remaining = value;
                    }
                    else if (!policy && parameter.Equals("t", StringComparison.OrdinalIgnoreCase))
                    {
                        bucket.ResetSeconds = value;
                    }
                }

                buckets.Add(bucket);
            }

            return buckets;
        }

        private static long? GetHeaderLong(HttpResponse response, string name)
        {
            return long.TryParse(response?.Headers[name], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : (long?)null;
        }

        private static DateTime? GetRetryAfterUntil(HttpResponse response)
        {
            var value = response?.Headers["Retry-After"];
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
            {
                var remaining = (DateTime.MaxValue - DateTime.UtcNow).TotalSeconds;
                return DateTime.UtcNow.AddSeconds(Math.Min(Math.Max(seconds, 1), remaining));
            }

            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var retryAt))
            {
                var utcRetryAt = retryAt.UtcDateTime;
                return utcRetryAt > DateTime.UtcNow ? utcRetryAt : DateTime.UtcNow.AddSeconds(1);
            }

            return null;
        }

        private static DateTime? GetExhaustedRateLimitBucketReset(HttpResponse response)
        {
            var rateLimit = response?.Headers["RateLimit"];
            var resets = new List<DateTime>();

            if (!rateLimit.IsNullOrWhiteSpace())
            {
                foreach (var bucket in rateLimit.Split(','))
                {
                    var parts = bucket.Split(';').Select(x => x.Trim()).ToList();
                    if (parts.Count == 0)
                    {
                        continue;
                    }

                    var remaining = GetRateLimitParameter(parts, "r");
                    var resetSeconds = GetRateLimitParameter(parts, "t");
                    if (remaining == 0 && resetSeconds >= 0)
                    {
                        resets.Add(DateTime.UtcNow.AddSeconds(Math.Max(resetSeconds, 1)));
                    }
                }
            }

            var legacyRemaining = response?.Headers["X-RateLimit-Remaining"];
            var legacyReset = response?.Headers["X-RateLimit-Reset"];
            if (long.TryParse(legacyRemaining, NumberStyles.Integer, CultureInfo.InvariantCulture, out var requestsRemaining) &&
                requestsRemaining == 0 &&
                long.TryParse(legacyReset, NumberStyles.Integer, CultureInfo.InvariantCulture, out var resetEpoch))
            {
                AddFutureReset(resets, resetEpoch);
            }

            var legacyDailyRemaining = response?.Headers["X-RateLimit-Daily-Remaining"];
            var legacyDailyReset = response?.Headers["X-RateLimit-Daily-Reset"];
            if (long.TryParse(legacyDailyRemaining, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dailyRequestsRemaining) &&
                dailyRequestsRemaining == 0 &&
                long.TryParse(legacyDailyReset, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dailyResetEpoch))
            {
                AddFutureReset(resets, dailyResetEpoch);
            }

            return resets.Count == 0 ? (DateTime?)null : resets.Max();
        }

        private static void AddFutureReset(ICollection<DateTime> resets, long resetEpoch)
        {
            try
            {
                var resetAt = DateTimeOffset.FromUnixTimeSeconds(resetEpoch).UtcDateTime;
                if (resetAt > DateTime.UtcNow)
                {
                    resets.Add(resetAt);
                }
            }
            catch (ArgumentOutOfRangeException)
            {
                // Ignore invalid reset timestamps and continue with other headers.
            }
        }

        private static int GetRateLimitParameter(IEnumerable<string> parts, string name)
        {
            var prefix = name + "=";
            var value = parts
                .Select(x => x.Trim())
                .FirstOrDefault(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

            return value != null && int.TryParse(value.Substring(prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : -1;
        }

        private static bool IsTransientStatus(HttpStatusCode statusCode)
        {
            return statusCode == HttpStatusCode.RequestTimeout ||
                   statusCode == HttpStatusCode.InternalServerError ||
                   statusCode == HttpStatusCode.BadGateway ||
                   statusCode == HttpStatusCode.ServiceUnavailable ||
                   statusCode == HttpStatusCode.GatewayTimeout;
        }

        private static bool IsTransientNetworkError(WebException exception)
        {
            return exception.Status == WebExceptionStatus.ConnectFailure ||
                   exception.Status == WebExceptionStatus.ConnectionClosed ||
                   exception.Status == WebExceptionStatus.ReceiveFailure ||
                   exception.Status == WebExceptionStatus.SendFailure ||
                   exception.Status == WebExceptionStatus.Timeout ||
                   exception.Status == WebExceptionStatus.NameResolutionFailure;
        }

        private static List<JObject> SelectAuthorContributions(JArray contributions)
        {
            var candidates = (contributions ?? new JArray())
                .OfType<JObject>()
                .Where(x => x["author"] is JObject)
                .Where(x => !IsNarratorOnlyRole(GetString(x["contribution"])))
                .ToList();

            var primary = candidates.Where(x => IsPrimaryAuthorRole(GetString(x["contribution"]))).ToList();
            var fallback = candidates.Where(x => !NonPrimaryRoles.Contains(GetString(x["contribution"]))).ToList();
            var selected = primary.Any() ? primary : (fallback.Any() ? fallback : candidates);

            return selected
                .GroupBy(x => GetInt(AsObject(x["author"])?["id"]))
                .Where(x => x.Key != 0)
                .Select(x => x.First())
                .ToList();
        }

        private static List<JObject> SelectAuthors(JArray contributions)
        {
            return SelectAuthorContributions(contributions)
                .Select(x => x["author"] as JObject)
                .Where(x => x != null)
                .ToList();
        }

        private static bool IsPrimaryAuthorRole(string role)
        {
            return role.IsNullOrWhiteSpace() ||
                   role.Equals("author", StringComparison.OrdinalIgnoreCase) ||
                   role.Equals("author/narrator", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNarratorOnlyRole(string role)
        {
            return string.Equals(role, "narrator", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(role, "reading", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(role, "reader", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNarratorRole(string role)
        {
            return IsNarratorOnlyRole(role) ||
                   string.Equals(role, "author/narrator", StringComparison.OrdinalIgnoreCase);
        }

        private static object GetCacheLock(string key)
        {
            var hash = StringComparer.Ordinal.GetHashCode(key) & int.MaxValue;
            return CacheLocks[hash % CacheLocks.Length];
        }

        private static T GetCached<T>(ICached<T> cache, string key, Func<T> factory, TimeSpan lifetime)
        {
            lock (GetCacheLock(key))
            {
                return cache.Get(key, factory, lifetime);
            }
        }

        private static bool LooksLikeIsbnOrAsin(string value)
        {
            var normalized = value.Replace("-", string.Empty).Replace(" ", string.Empty);
            return AsinRegex.IsMatch(normalized) || Isbn10Regex.IsMatch(normalized) || Isbn13Regex.IsMatch(normalized);
        }

        private static int ParseId(string value, string type)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
            {
                throw new BadRequestException($"Invalid Hardcover {type} id: {value}");
            }

            return id;
        }

        // A JSON null deserialises to a JValue whose Type is Null -- NOT a C#
        // null -- so `token?["child"]` does not short-circuit and instead throws
        // "Cannot access child value on Newtonsoft.Json.Linq.JValue". Normalise
        // JSON nulls to real nulls before indexing into them.
        private static JToken AsObject(JToken token)
        {
            return token == null || token.Type == JTokenType.Null ? null : token;
        }

        private static string GetString(JToken token)
        {
            return token?.Type == JTokenType.Null ? null : token?.Value<string>();
        }

        private static int GetInt(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return 0;
            }

            return int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
        }

        private static int? GetNullableInt(JToken token)
        {
            var value = GetInt(token);
            return value == 0 ? null : value;
        }

        private static double GetDouble(JToken token)
        {
            return double.TryParse(token?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
        }

        private static List<string> GetGenres(JToken token)
        {
            var values = new List<string>();

            if (token == null || token.Type == JTokenType.Null)
            {
                return new List<string> { "none" };
            }

            if (token.Type == JTokenType.String)
            {
                var raw = token.Value<string>();
                if (!raw.IsNullOrWhiteSpace())
                {
                    try
                    {
                        token = JToken.Parse(raw);
                    }
                    catch (JsonException)
                    {
                        values.Add(raw);
                    }
                }
            }

            if (token is JArray array)
            {
                foreach (var item in array)
                {
                    if (item.Type == JTokenType.String)
                    {
                        values.Add(item.Value<string>());
                    }
                    else if (item is JObject itemObject)
                    {
                        values.Add(GetString(itemObject["tag"]));
                        values.Add(GetString(itemObject["name"]));
                    }
                }
            }
            else if (token.Type == JTokenType.Object)
            {
                values.Add(GetString(token["tag"]));
                values.Add(GetString(token["name"]));
                values.AddRange(token.Values<string>());
            }

            return values.Where(x => !x.IsNullOrWhiteSpace()).Distinct(StringComparer.InvariantCultureIgnoreCase).ToList() is { Count: > 0 } genres
                ? genres
                : new List<string> { "none" };
        }

        private static DateTime? ParseDate(string value)
        {
            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
            {
                return date;
            }

            return null;
        }

        private static string CleanSubtitle(string title, string subtitle)
        {
            if (title.IsNullOrWhiteSpace() || subtitle.IsNullOrWhiteSpace())
            {
                return title;
            }

            var suffix = $": {subtitle}";
            return title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? title.Substring(0, title.Length - suffix.Length) : title;
        }

        private static string HardcoverUrl(string resource, string slug, int id)
        {
            return $"https://hardcover.app/{resource}/{(slug.IsNullOrWhiteSpace() ? id.ToString(CultureInfo.InvariantCulture) : slug)}";
        }

        private static string GetApiUrl()
        {
            return WithFallback(Environment.GetEnvironmentVariable("HARDCOVER_API_URL"), DefaultApiUrl);
        }

        private string GetApiToken()
        {
            var value = WithFallback(
                Environment.GetEnvironmentVariable("HARDCOVER_AUTH"),
                WithFallback(Environment.GetEnvironmentVariable("HARDCOVER_API_KEY"), _configService.HardcoverApiToken));
            if (value.IsNullOrWhiteSpace())
            {
                return string.Empty;
            }

            const string bearerPrefix = "Bearer ";
            return value.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase) ? value.Substring(bearerPrefix.Length).Trim() : value.Trim();
        }

        private static string WithFallback(string value, string fallback)
        {
            return value.IsNullOrWhiteSpace() ? fallback : value;
        }
    }
}
