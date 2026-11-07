using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Http;
using NzbDrone.Core.MediaCover;

namespace NzbDrone.Core.MetadataSource.BookInfo
{
    public interface IOpenLibraryMetadataProxy
    {
        List<Book> Search(string query);
        bool HandlesBookId(string foreignBookId);
        bool HandlesAuthorId(string foreignAuthorId);
        Tuple<string, Book, List<Author>> GetBook(string foreignBookId);
        Author GetAuthor(string foreignAuthorId);
    }

    /// <summary>
    /// Open Library's public Search and Work/Edition APIs as an optional
    /// ChaptarrNG runtime catalog. Source-qualified IDs keep Open Library
    /// records separate from Goodreads-compatible and Hardcover records.
    /// </summary>
    public class OpenLibraryMetadataProxy : IOpenLibraryMetadataProxy
    {
        private sealed class SearchHint
        {
            public string AuthorId { get; init; }
            public string AuthorName { get; init; }
            public DateTime? ReleaseDate { get; init; }
            public string[] Genres { get; init; }
        }

        private const string WorkPrefix = "openlibrary:";
        private const string AuthorPrefix = "openlibrary-author:";
        private const string BaseUrl = "https://openlibrary.org";
        private static readonly TimeSpan CacheDuration = TimeSpan.FromDays(1);
        private static readonly Regex OpenLibraryIdRegex = new Regex("^OL[0-9]+[WMA]$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly string SearchFields = string.Join(",", new[]
        {
            "key", "title", "author_name", "author_key", "first_publish_year",
            "cover_i", "cover_edition_key", "edition_count", "editions",
            "editions.key", "editions.title", "editions.isbn_10", "editions.isbn_13",
            "editions.publish_date", "editions.number_of_pages", "editions.publishers",
            "editions.languages", "editions.covers", "subject"
        });

        private readonly ICachedHttpResponseService _cachedHttpClient;
        private readonly ICached<SearchHint> _searchHintCache;
        private readonly IConfigService _configService;
        private readonly Logger _logger;

        public OpenLibraryMetadataProxy(
            ICachedHttpResponseService cachedHttpClient,
            ICacheManager cacheManager,
            IConfigService configService,
            Logger logger)
        {
            _cachedHttpClient = cachedHttpClient;
            _searchHintCache = cacheManager.GetCache<SearchHint>(GetType(), "search-hints");
            _configService = configService;
            _logger = logger;
        }

        public List<Book> Search(string query)
        {
            if (query.IsNullOrWhiteSpace())
            {
                return new List<Book>();
            }

            var request = CreateRequest("/search.json")
                .AddQueryParam("q", query.Trim())
                .AddQueryParam("fields", SearchFields)
                .AddQueryParam("limit", "25")
                .Build();
            var response = _cachedHttpClient.Get<JObject>(request, true, CacheDuration).Resource;
            var books = new List<Book>();

            foreach (var document in response?["docs"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                try
                {
                    var book = MapSearchResult(document);
                    if (book != null)
                    {
                        books.Add(book);
                    }
                }
                catch (Exception e)
                {
                    _logger.Warn(e, "Could not map Open Library search result");
                }
            }

            return books;
        }

        public bool HandlesBookId(string foreignBookId) =>
            TryGetId(foreignBookId, WorkPrefix, 'W', out _) ||
            TryGetId(foreignBookId, WorkPrefix, 'M', out _);

        public bool HandlesAuthorId(string foreignAuthorId) =>
            HasPrefix(foreignAuthorId, AuthorPrefix) &&
            (TryGetId(foreignAuthorId, AuthorPrefix, 'A', out _) || GetAuthorNameId(foreignAuthorId) != null);

        public Tuple<string, Book, List<Author>> GetBook(string foreignBookId)
        {
            string workId;
            string requestedEditionId = null;
            if (!TryGetId(foreignBookId, WorkPrefix, 'W', out workId))
            {
                if (!TryGetId(foreignBookId, WorkPrefix, 'M', out var editionId))
                {
                    throw new BookNotFoundException(foreignBookId);
                }

                requestedEditionId = editionId;
                var edition = GetJson("/books/{id}.json", editionId);
                workId = GetOpenLibraryId(edition?["works"]?.OfType<JObject>().FirstOrDefault()?["key"], 'W');
                if (workId == null)
                {
                    throw new BookNotFoundException(foreignBookId);
                }
            }

            var workRequest = CreateRequest("/works/{id}.json")
                .SetSegment("id", workId)
                .Build();
            var work = _cachedHttpClient.Get<JObject>(workRequest, true, CacheDuration).Resource;
            if (work == null)
            {
                throw new BookNotFoundException(foreignBookId);
            }

            var editionsRequest = CreateRequest("/works/{id}/editions.json")
                .SetSegment("id", workId)
                .AddQueryParam("limit", "100")
                .Build();
            var editions = _cachedHttpClient.Get<JObject>(editionsRequest, true, CacheDuration).Resource;
            var book = MapWork(workId, work, editions);
            if (requestedEditionId != null)
            {
                var selectedEdition = book.Editions.FirstOrDefault(x =>
                    string.Equals(x.OpenLibraryEditionId, requestedEditionId, StringComparison.OrdinalIgnoreCase));
                if (selectedEdition == null)
                {
                    var editionResource = GetJson("/books/{id}.json", requestedEditionId);
                    selectedEdition = MapEdition(editionResource);
                    if (selectedEdition != null)
                    {
                        book.Editions.Add(selectedEdition);
                    }
                }

                if (selectedEdition == null)
                {
                    throw new BookNotFoundException(foreignBookId);
                }

                foreach (var edition in book.Editions)
                {
                    edition.Monitored = ReferenceEquals(edition, selectedEdition);
                }
            }

            var authorMetadata = book.Author;
            return Tuple.Create(authorMetadata.AdditionalMetadataProviderId, book, new List<Author> { authorMetadata });
        }

        public Author GetAuthor(string foreignAuthorId)
        {
            var authorId = GetAuthorNameId(foreignAuthorId);
            var cacheKey = authorId ?? (TryGetId(foreignAuthorId, AuthorPrefix, 'A', out var parsedId) ? parsedId : null);
            // Reject invalid provider IDs before calling Open Library; this validation does not grant or bypass authorization.

            // lgtm[cs/user-controlled-bypass]
            if (cacheKey == null)
            {
                throw new AuthorNotFoundException(foreignAuthorId);
            }

            JObject resource;
            // The parsed ID selects a fixed Open Library endpoint (name search or author key lookup); neither branch changes authorization.

            // lgtm[cs/user-controlled-bypass]
            if (authorId != null)
            {
                resource = SearchAuthorByName(Uri.UnescapeDataString(authorId));
                cacheKey = GetOpenLibraryId(resource?["key"], 'A');
                if (cacheKey == null)
                {
                    throw new AuthorNotFoundException(foreignAuthorId);
                }
            }
            else
            {
                resource = GetJson("/authors/{id}.json", cacheKey);
            }

            var works = GetJson("/authors/{id}/works.json", cacheKey, "limit", "100");
            var author = MapAuthor(cacheKey, resource, works);
            return author;
        }

        private JObject SearchAuthorByName(string name)
        {
            var request = CreateRequest("/search/authors.json")
                .AddQueryParam("q", name)
                .AddQueryParam("limit", "10")
                .Build();
            var response = _cachedHttpClient.Get<JObject>(request, true, CacheDuration).Resource;
            return response?["docs"]?.OfType<JObject>()
                .FirstOrDefault(x => NormalizeName((string)x["name"]) == NormalizeName(name));
        }

        private Book MapSearchResult(JObject document)
        {
            var workId = GetOpenLibraryId(document["key"], 'W');
            var title = ((string)document["title"]).CleanSpaces();
            if (workId == null || title.IsNullOrWhiteSpace())
            {
                return null;
            }

            var authorName = (string)document["author_name"]?.FirstOrDefault();
            var authorId = GetOpenLibraryId(document["author_key"]?.FirstOrDefault(), 'A');
            var authorMetadata = MapAuthor(authorId, authorName, null);
            var editionDocuments = GetEditionDocuments(document);
            var providerId = AdditionalMetadataSources.CreateProviderId(AdditionalMetadataSources.OpenLibrary, workId);
            var book = new Book
            {
                AdditionalMetadataProviderId = providerId,
                Title = title,
                TitleSlug = providerId,
                CleanTitle = Parser.Parser.CleanAuthorName(title),
                ReleaseDate = GetYear(document["first_publish_year"]),
                Genres = document["subject"]?.OfType<JValue>().Select(x => (string)x).Where(x => !x.IsNullOrWhiteSpace()).Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToList() ?? new List<string>(),
                Editions = editionDocuments.Select(MapEdition).Where(x => x != null).ToList(),
                Author = CreateAuthor(authorMetadata)
            };

            _searchHintCache.Set(workId, new SearchHint
            {
                AuthorId = authorId,
                AuthorName = authorName,
                ReleaseDate = book.ReleaseDate,
                Genres = book.Genres.ToArray()
            }, CacheDuration);

            AddWorkLink(book, workId);
            ApplySearchCover(book, document, editionDocuments.FirstOrDefault());
            EnsureMonitoredEdition(book);
            return book;
        }

        private Book MapWork(string workId, JObject work, JObject editions)
        {
            var authorReference = work["authors"]?.OfType<JObject>().FirstOrDefault()?["author"] as JObject;
            var authorId = GetOpenLibraryId(authorReference?["key"], 'A');
            var searchHint = _searchHintCache.Find(workId);
            authorId ??= searchHint?.AuthorId;
            Author authorMetadata;

            if (authorId != null)
            {
                var author = GetAuthor(AuthorPrefix + authorId);
                authorMetadata = author;
            }
            else if (!string.IsNullOrWhiteSpace(searchHint?.AuthorName))
            {
                authorMetadata = MapAuthor(null, searchHint.AuthorName, null);
            }
            else
            {
                throw new BookInfoException("Open Library work did not include an author identifier.");
            }

            var editionEntries = editions?["entries"]?.OfType<JObject>().ToList() ?? new List<JObject>();
            var providerId = AdditionalMetadataSources.CreateProviderId(AdditionalMetadataSources.OpenLibrary, workId);
            var book = new Book
            {
                AdditionalMetadataProviderId = providerId,
                Title = ((string)work["title"]).CleanSpaces(),
                TitleSlug = providerId,
                CleanTitle = Parser.Parser.CleanAuthorName((string)work["title"]),
                ReleaseDate = ParseDate((string)work["first_publish_date"]) ?? searchHint?.ReleaseDate,
                Genres = work["subjects"]?.OfType<JValue>().Select(x => (string)x).Where(x => !x.IsNullOrWhiteSpace()).Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToList() ?? searchHint?.Genres?.ToList() ?? new List<string>(),
                Editions = editionEntries.Select(MapEdition).Where(x => x != null).ToList(),
                Author = CreateAuthor(authorMetadata),
                AnyEditionOk = true
            };

            var description = GetText(work["description"]);
            foreach (var edition in book.Editions)
            {
                if (edition.Overview.IsNullOrWhiteSpace())
                {
                    edition.Overview = description;
                }
            }

            AddWorkLink(book, workId);
            ApplyWorkCover(book, work);
            EnsureMonitoredEdition(book);
            return book;
        }

        private Author MapAuthor(string authorId, JObject resource, JObject works)
        {
            var name = (string)resource?["name"];
            if (name.IsNullOrWhiteSpace())
            {
                throw new AuthorNotFoundException(authorId);
            }

            var metadata = MapAuthor(authorId, name, resource);
            var books = works?["entries"]?.OfType<JObject>()
                .Select(MapAuthorWork)
                .Where(x => x != null)
                .ToList() ?? new List<Book>();
            books.ForEach(x =>
            {
                x.Author = metadata;
            });

            metadata.CleanName = Parser.Parser.CleanAuthorName(name);
            metadata.Books = books;
            metadata.Series = new List<Series>();
            return metadata;
        }

        private static Book MapAuthorWork(JObject work)
        {
            var workId = GetOpenLibraryId(work["key"], 'W');
            var title = (string)work["title"];
            if (workId == null || title.IsNullOrWhiteSpace())
            {
                return null;
            }

            var providerId = AdditionalMetadataSources.CreateProviderId(AdditionalMetadataSources.OpenLibrary, workId);
            var book = new Book
            {
                AdditionalMetadataProviderId = providerId,
                Title = title.CleanSpaces(),
                TitleSlug = providerId,
                CleanTitle = Parser.Parser.CleanAuthorName(title),
                ReleaseDate = ParseDate((string)work["first_publish_date"]),
                Genres = work["subjects"]?.OfType<JValue>().Select(x => (string)x).Where(x => !x.IsNullOrWhiteSpace()).Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToList() ?? new List<string>(),
                Editions = new List<Edition>(),
                AnyEditionOk = true
            };

            AddWorkLink(book, workId);
            ApplyWorkCover(book, work);
            return book;
        }

        private static Author MapAuthor(string authorId, string name, JObject resource)
        {
            var metadata = new Author
            {
                AdditionalMetadataProviderId = AdditionalMetadataSources.CreateProviderId(AdditionalMetadataSources.OpenLibrary + "-author", authorId ?? "name=" + Uri.EscapeDataString(name ?? string.Empty)),
                TitleSlug = AdditionalMetadataSources.CreateProviderId(AdditionalMetadataSources.OpenLibrary + "-author", authorId ?? "name=" + Uri.EscapeDataString(name ?? string.Empty)),
                Name = name.CleanSpaces(),
                Overview = GetText(resource?["bio"]),
                Links = new List<Links>()
            };

            if (authorId != null)
            {
                metadata.Links.Add(new Links { Url = BaseUrl + "/authors/" + Uri.EscapeDataString(authorId), Name = "Open Library" });
            }

            foreach (var photo in resource?["photos"]?.OfType<JValue>() ?? Enumerable.Empty<JValue>())
            {
                var photoId = (long?)photo;
                if (photoId > 0)
                {
                    metadata.Images.Add(new MediaCover.MediaCover
                    {
                        Url = $"https://covers.openlibrary.org/a/id/{photoId}-M.jpg?default=false",
                        CoverType = MediaCoverTypes.Poster
                    });
                    break;
                }
            }

            return metadata;
        }

        private static Author CreateAuthor(Author metadata)
        {
            metadata.CleanName = Parser.Parser.CleanAuthorName(metadata.Name);
            metadata.Books ??= new List<Book>();
            metadata.Series ??= new List<Series>();
            return metadata;
        }

        private static List<JObject> GetEditionDocuments(JObject document)
        {
            var editions = document["editions"];
            return (editions is JObject editionCollection ? editionCollection["docs"] : editions)?
                .OfType<JObject>().ToList() ?? new List<JObject>();
        }

        private static Edition MapEdition(JObject resource)
        {
            var editionId = GetOpenLibraryId(resource["key"], 'M');
            if (editionId == null)
            {
                return null;
            }

            var isbn = NormalizeIsbn13(resource["isbn_13"]?.FirstOrDefault(), resource["isbn_10"]?.FirstOrDefault());
            var languageKey = (string)resource["languages"]?.OfType<JObject>().FirstOrDefault()?["key"];
            var language = languageKey?.Split('/').LastOrDefault();
            var edition = new Edition
            {
                ForeignEditionId = AdditionalMetadataSources.CreateProviderId(AdditionalMetadataSources.OpenLibrary, editionId),
                OpenLibraryEditionId = editionId,
                TitleSlug = AdditionalMetadataSources.CreateProviderId(AdditionalMetadataSources.OpenLibrary, editionId),
                Title = ((string)resource["title"]).CleanSpaces(),
                Isbn13 = isbn,
                Language = language,
                Publisher = (string)resource["publishers"]?.FirstOrDefault(),
                PageCount = (int?)resource["number_of_pages"] ?? 0,
                ReleaseDate = ParseDate((string)resource["publish_date"]),
                Overview = string.Empty,
                Ratings = new Ratings { Votes = 0, Value = 0 }
            };

            ApplyEditionCover(edition, resource);
            edition.Links.Add(new Links { Url = BaseUrl + "/books/" + editionId, Name = "Open Library" });
            return edition;
        }

        private JObject GetJson(string path, string id, string queryName = null, string queryValue = null)
        {
            var request = CreateRequest(path).SetSegment("id", id);
            if (!queryName.IsNullOrWhiteSpace())
            {
                request.AddQueryParam(queryName, queryValue);
            }

            return _cachedHttpClient.Get<JObject>(request.Build(), true, CacheDuration).Resource;
        }

        private HttpRequestBuilder CreateRequest(string path)
        {
            var contactEmail = GetConfiguredValue("OPEN_LIBRARY_CONTACT_EMAIL", _configService.OpenLibraryContactEmail);
            var userAgent = contactEmail.IsNullOrWhiteSpace()
                ? "ChaptarrNG (https://github.com/snapetech-org/chaptarrng)"
                : $"ChaptarrNG ({contactEmail.Trim()})";

            return new HttpRequestBuilder(BaseUrl + path)
                .SetHeader("User-Agent", userAgent)
                .WithRateLimit(1);
        }

        private static string GetConfiguredValue(string environmentName, string savedValue)
        {
            var environmentValue = Environment.GetEnvironmentVariable(environmentName);
            return environmentValue.IsNullOrWhiteSpace() ? savedValue ?? string.Empty : environmentValue;
        }

        private static void AddWorkLink(Book book, string workId)
        {
            book.Links.Add(new Links { Url = BaseUrl + "/works/" + workId, Name = "Open Library" });
        }

        private static void ApplySearchCover(Book book, JObject document, JObject edition)
        {
            var coverId = (long?)edition?["covers"]?.FirstOrDefault() ?? (long?)document["cover_i"];
            var coverEdition = GetOpenLibraryId(document["cover_edition_key"], 'M');
            var coverUrl = coverId > 0
                ? $"https://covers.openlibrary.org/b/id/{coverId}-L.jpg?default=false"
                : coverEdition != null
                    ? $"https://covers.openlibrary.org/b/olid/{coverEdition}-L.jpg?default=false"
                    : null;

            if (coverUrl != null && book.Editions.Count > 0)
            {
                AddCover(book.Editions[0], coverUrl);
            }
        }

        private static void ApplyWorkCover(Book book, JObject work)
        {
            var coverId = (long?)work["covers"]?.FirstOrDefault();
            if (coverId > 0 && book.Editions.Count > 0)
            {
                AddCover(book.Editions[0], $"https://covers.openlibrary.org/b/id/{coverId}-L.jpg?default=false");
            }
        }

        private static void ApplyEditionCover(Edition edition, JObject resource)
        {
            var coverId = (long?)resource["covers"]?.FirstOrDefault();
            if (coverId > 0)
            {
                AddCover(edition, $"https://covers.openlibrary.org/b/id/{coverId}-L.jpg?default=false");
            }
        }

        private static void AddCover(Edition edition, string url)
        {
            if (edition.Images.All(x => !string.Equals(x.Url, url, StringComparison.OrdinalIgnoreCase)))
            {
                edition.Images.Add(new MediaCover.MediaCover { Url = url, CoverType = MediaCoverTypes.Cover });
            }
        }

        private static void EnsureMonitoredEdition(Book book)
        {
            var editions = book.Editions;
            if (editions == null || editions.Count == 0)
            {
                return;
            }

            editions[0].Monitored = true;
            book.AnyEditionOk = true;
        }

        private static DateTime? GetYear(JToken token)
        {
            var year = (int?)token;
            return year > 0 && year < 10000 ? new DateTime(year.Value, 1, 1) : null;
        }

        private static DateTime? ParseDate(string value)
        {
            if (value.IsNullOrWhiteSpace())
            {
                return null;
            }

            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date))
            {
                return date;
            }

            var yearMatch = Regex.Match(value, "(?:^|\\D)(1[0-9]{3}|20[0-9]{2})(?:\\D|$)");
            return yearMatch.Success && int.TryParse(yearMatch.Groups[1].Value, out var year)
                ? new DateTime(year, 1, 1)
                : null;
        }

        private static string NormalizeIsbn13(JToken isbn13Values, JToken isbn10Values)
        {
            var candidates = GetStringValues(isbn13Values).Concat(GetStringValues(isbn10Values));

            foreach (var candidate in candidates)
            {
                var compact = new string((candidate ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
                if (compact.Length == 13 && IsValidIsbn13(compact))
                {
                    return compact;
                }

                if (compact.Length == 10 && IsValidIsbn10(compact))
                {
                    var body = "978" + compact.Substring(0, 9);
                    var sum = body.Select((character, index) => (character - '0') * (index % 2 == 0 ? 1 : 3)).Sum();
                    return body + ((10 - (sum % 10)) % 10).ToString(CultureInfo.InvariantCulture);
                }
            }

            return null;
        }

        private static bool IsValidIsbn13(string isbn)
        {
            var sum = isbn.Take(12).Select((character, index) => (character - '0') * (index % 2 == 0 ? 1 : 3)).Sum();
            return char.IsDigit(isbn[12]) && isbn[12] - '0' == (10 - (sum % 10)) % 10;
        }

        private static bool IsValidIsbn10(string isbn)
        {
            var sum = isbn.Take(9).Select((character, index) => (character - '0') * (10 - index)).Sum();
            var check = isbn[9] == 'X' ? 10 : char.IsDigit(isbn[9]) ? isbn[9] - '0' : -1;
            return check >= 0 && (sum + check) % 11 == 0;
        }

        private static IEnumerable<string> GetStringValues(JToken token)
        {
            if (token == null)
            {
                return Enumerable.Empty<string>();
            }

            return token is JArray values
                ? values.Values<string>().Where(x => !x.IsNullOrWhiteSpace())
                : new[] { (string)token }.Where(x => !x.IsNullOrWhiteSpace());
        }

        private static string GetText(JToken token) => token == null
            ? null
            : token.Type == JTokenType.Object
                ? (string)token["value"]
                : (string)token;

        private static string GetOpenLibraryId(JToken key, char suffix)
        {
            var value = (string)key;
            if (value.IsNullOrWhiteSpace())
            {
                return null;
            }

            var id = value.Trim('/').Split('/').LastOrDefault();
            return id != null && id.EndsWith(suffix.ToString(), StringComparison.OrdinalIgnoreCase) && OpenLibraryIdRegex.IsMatch(id)
                ? id.ToUpperInvariant()
                : null;
        }

        private static bool TryGetId(string qualifiedId, string prefix, char suffix, out string id)
        {
            id = null;
            if (!HasPrefix(qualifiedId, prefix))
            {
                return false;
            }

            var candidate = qualifiedId.Substring(prefix.Length);
            if (candidate.EndsWith(suffix.ToString(), StringComparison.OrdinalIgnoreCase) && OpenLibraryIdRegex.IsMatch(candidate))
            {
                id = candidate.ToUpperInvariant();
                return true;
            }

            return false;
        }

        private static bool HasPrefix(string value, string prefix) =>
            value?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true;

        private static string GetAuthorNameId(string foreignAuthorId)
        {
            if (!HasPrefix(foreignAuthorId, AuthorPrefix))
            {
                return null;
            }

            var value = foreignAuthorId.Substring(AuthorPrefix.Length);
            return value.StartsWith("name=", StringComparison.OrdinalIgnoreCase) ? value.Substring(5) : null;
        }

        private static string NormalizeName(string name) =>
            string.Join(" ", (name ?? string.Empty).Split((char[])null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
    }
}
