using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Books;
using NzbDrone.Core.Http;
using NzbDrone.Core.Parser;

namespace NzbDrone.Core.MetadataSource.RanobeDb
{
    internal sealed class RanobeDbProxy
    {
        private const string ApiBaseUrl = "https://ranobedb.org/api/v0";
        private const int SearchResultLimit = 3;
        private const int BooksPageSize = 100;
        private const int MaximumAuthorBookPages = 20;
        private const int RequestBurstCapacity = 5;
        private const double MinimumRequestSpacingSeconds = 1.1;
        private static readonly TimeSpan SearchCacheTtl = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan DetailCacheTtl = TimeSpan.FromHours(12);
        private static readonly object ApiRequestRateLimitLock = new object();
        private static double _apiRequestTokens = RequestBurstCapacity;
        private static long _lastApiRequestTokenRefill = Stopwatch.GetTimestamp();

        private readonly ICachedHttpResponseService _cachedHttpClient;
        private readonly Logger _logger;

        public RanobeDbProxy(ICachedHttpResponseService cachedHttpClient, Logger logger)
        {
            _cachedHttpClient = cachedHttpClient;
            _logger = logger;
        }

        public List<Book> SearchBooks(string query)
        {
            var cleanedQuery = (query ?? string.Empty).Trim();
            if (cleanedQuery.Length == 0)
            {
                return new List<Book>();
            }

            if (cleanedQuery.Length > 200)
            {
                cleanedQuery = cleanedQuery.Substring(0, 200);
            }

            var response = GetBooksPage(cleanedQuery, page: 1, SearchResultLimit, useCache: true, SearchCacheTtl);
            var books = new List<Book>();

            foreach (var summary in response?.Books?.Where(book => book != null && book.Id > 0).Take(SearchResultLimit) ?? Enumerable.Empty<RanobeDbBookSummary>())
            {
                try
                {
                    var detail = GetBook(summary.Id, useCache: true);
                    var book = detail == null ? null : MapBook(detail);
                    if (book != null && book.Author != null && !string.IsNullOrWhiteSpace(book.Author.RanobeDbAuthorId))
                    {
                        books.Add(book);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "RanobeDB detail lookup failed for book {0}; omitting this search result", summary.Id);
                }
            }

            return books;
        }

        public Tuple<string, Book, List<Author>> GetBookInfo(string providerBookId, bool useCache = true)
        {
            var normalizedId = ProviderIdHelper.Normalize(providerBookId, "rn");
            var rawId = ProviderIdHelper.StripPrefix(normalizedId);
            if (!int.TryParse(rawId, NumberStyles.None, CultureInfo.InvariantCulture, out var bookId) || bookId <= 0)
            {
                return null;
            }

            var detail = GetBook(bookId, useCache);
            var book = detail == null ? null : MapBook(detail);
            if (book?.Author == null || string.IsNullOrWhiteSpace(book.Author.RanobeDbAuthorId))
            {
                return null;
            }

            return Tuple.Create(book.Author.RanobeDbAuthorId, book, new List<Author> { book.Author });
        }

        public Tuple<string, Book, List<Author>> GetEditionInfo(string providerEditionId, bool useCache = true)
        {
            var normalizedId = ProviderIdHelper.Normalize(providerEditionId, "rnr");
            var rawId = ProviderIdHelper.StripPrefix(normalizedId);
            if (!int.TryParse(rawId, NumberStyles.None, CultureInfo.InvariantCulture, out var releaseId) || releaseId <= 0)
            {
                return null;
            }

            var release = GetRelease(releaseId, useCache);
            if (release == null || release.Hidden || !string.Equals(release.Format, "digital", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var bookIds = release.Books?
                .Where(book => book != null && book.Id > 0)
                .Select(book => book.Id)
                .Distinct()
                .ToList() ?? new List<int>();
            if (bookIds.Count != 1)
            {
                return null;
            }

            var bookId = bookIds[0];
            var detail = GetBook(bookId, useCache);
            var book = detail == null ? null : MapBook(detail);
            if (book?.Author == null || string.IsNullOrWhiteSpace(book.Author.RanobeDbAuthorId))
            {
                return null;
            }

            var editionId = ProviderIdHelper.WithPrefix("rnr", releaseId.ToString(CultureInfo.InvariantCulture));
            var edition = book.Editions.FirstOrDefault(item => string.Equals(item.ForeignEditionId, editionId, StringComparison.OrdinalIgnoreCase));
            if (edition == null)
            {
                edition = MapRelease(release);
                book.Editions.Add(edition);
            }

            edition.Monitored = true;
            return Tuple.Create(book.Author.RanobeDbAuthorId, book, new List<Author> { book.Author });
        }

        public Author GetAuthorInfo(string providerAuthorId, bool useCache = true)
        {
            var staff = GetStaff(providerAuthorId, useCache);
            return staff == null ? null : MapAuthor(staff);
        }

        public Author GetAuthorWithBooks(string providerAuthorId, bool useCache, out string etag)
        {
            etag = null;
            var staff = GetStaff(providerAuthorId, useCache);
            if (staff == null)
            {
                return null;
            }

            var author = MapAuthor(staff);
            var books = new List<Book>();
            var page = GetBooksByStaff(staff.Id, page: 1, useCache);
            if (page?.Books == null)
            {
                throw new InvalidOperationException($"RanobeDB did not return the first book page for staff {staff.Id}.");
            }

            ValidateAuthorBookPage(page, staff.Id, 1);

            var totalPages = Math.Min(Math.Max(page.TotalPages, 1), MaximumAuthorBookPages);
            if (page.TotalPages > MaximumAuthorBookPages)
            {
                throw new InvalidOperationException($"RanobeDB staff {staff.Id} has {page.TotalPages} book pages, above the safe refresh limit of {MaximumAuthorBookPages}.");
            }

            AddAuthorBookSummaries(books, page.Books, author);

            for (var pageNumber = 2; pageNumber <= totalPages; pageNumber++)
            {
                var nextPage = GetBooksByStaff(staff.Id, pageNumber, useCache);
                if (nextPage?.Books == null || nextPage.Books.Count == 0)
                {
                    throw new InvalidOperationException($"RanobeDB returned an incomplete book page {pageNumber} for staff {staff.Id}.");
                }

                ValidateAuthorBookPage(nextPage, staff.Id, pageNumber);
                AddAuthorBookSummaries(books, nextPage.Books, author);
            }

            author.Books = books
                .GroupBy(book => book.RanobeDbBookId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            etag = CreateAuthorEtag(staff, author.Books);

            return author;
        }

        private static void ValidateAuthorBookPage(RanobeDbBooksResponse page, int staffId, int pageNumber)
        {
            if (page == null || page.Books == null || page.TotalPages < 0)
            {
                throw new InvalidOperationException($"RanobeDB returned an invalid book page {pageNumber} for staff {staffId}.");
            }

            var countIsKnownAndNonZero = long.TryParse(page.Count, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count > 0;
            if (page.Books.Count == 0 && (countIsKnownAndNonZero || page.TotalPages > 1))
            {
                throw new InvalidOperationException($"RanobeDB returned an empty book page {pageNumber} for staff {staffId} despite reporting results.");
            }

            if (page.Books.Any(book => book == null || book.Id <= 0 ||
                string.IsNullOrWhiteSpace(FirstText(book.Title, book.Romaji, book.OriginalRomaji, book.OriginalTitle))))
            {
                throw new InvalidOperationException($"RanobeDB returned an incomplete book summary on page {pageNumber} for staff {staffId}.");
            }
        }

        private RanobeDbBooksResponse GetBooksPage(string query, int page, int limit, bool useCache, TimeSpan ttl)
        {
            var request = new HttpRequestBuilder(ApiBaseUrl + "/books")
                .AddQueryParam("q", query)
                .AddQueryParam("page", page.ToString(CultureInfo.InvariantCulture))
                .AddQueryParam("limit", limit.ToString(CultureInfo.InvariantCulture))
                .AddQueryParam("rf", "digital")
                .Accept(HttpAccept.Json)
                .SetHeader("User-Agent", "ChaptarrNG/1.0 (+https://github.com/snapetech-org/chaptarrng)")
                .Build();

            return GetResponse<RanobeDbBooksResponse>(request, "books search", useCache, ttl);
        }

        private RanobeDbBooksResponse GetBooksByStaff(int staffId, int page, bool useCache)
        {
            var request = new HttpRequestBuilder(ApiBaseUrl + "/books")
                .AddQueryParam("staff", staffId.ToString(CultureInfo.InvariantCulture))
                .AddQueryParam("page", page.ToString(CultureInfo.InvariantCulture))
                .AddQueryParam("limit", BooksPageSize.ToString(CultureInfo.InvariantCulture))
                .AddQueryParam("rf", "digital")
                .Accept(HttpAccept.Json)
                .SetHeader("User-Agent", "ChaptarrNG/1.0 (+https://github.com/snapetech-org/chaptarrng)")
                .Build();

            return GetResponse<RanobeDbBooksResponse>(request, $"books for staff {staffId}", useCache, DetailCacheTtl);
        }

        private RanobeDbBookDetail GetBook(int bookId, bool useCache)
        {
            var request = new HttpRequestBuilder($"{ApiBaseUrl}/book/{bookId}")
                .Accept(HttpAccept.Json)
                .SetHeader("User-Agent", "ChaptarrNG/1.0 (+https://github.com/snapetech-org/chaptarrng)")
                .Build();

            return GetResponse<RanobeDbBookResponse>(request, $"book {bookId}", useCache, DetailCacheTtl)?.Book;
        }

        private RanobeDbRelease GetRelease(int releaseId, bool useCache)
        {
            var request = new HttpRequestBuilder($"{ApiBaseUrl}/release/{releaseId}")
                .Accept(HttpAccept.Json)
                .SetHeader("User-Agent", "ChaptarrNG/1.0 (+https://github.com/snapetech-org/chaptarrng)")
                .Build();

            return GetResponse<RanobeDbReleaseResponse>(request, $"release {releaseId}", useCache, DetailCacheTtl)?.Release;
        }

        private RanobeDbStaffDetail GetStaff(string providerAuthorId, bool useCache)
        {
            var normalizedId = ProviderIdHelper.Normalize(providerAuthorId, "rn");
            var rawId = ProviderIdHelper.StripPrefix(normalizedId);
            if (!int.TryParse(rawId, NumberStyles.None, CultureInfo.InvariantCulture, out var staffId) || staffId <= 0)
            {
                return null;
            }

            var request = new HttpRequestBuilder($"{ApiBaseUrl}/staff/{staffId}")
                .Accept(HttpAccept.Json)
                .SetHeader("User-Agent", "ChaptarrNG/1.0 (+https://github.com/snapetech-org/chaptarrng)")
                .Build();

            return GetResponse<RanobeDbStaffResponse>(request, $"staff {staffId}", useCache, DetailCacheTtl)?.Staff;
        }

        private T GetResponse<T>(HttpRequest request, string operation, bool useCache, TimeSpan ttl)
            where T : class
        {
            WaitForApiRequestSlot();
            var response = _cachedHttpClient.Get(request, useCache, ttl);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (response.HasHttpError)
            {
                throw new InvalidOperationException($"RanobeDB {operation} failed with HTTP {(int)response.StatusCode}.");
            }

            try
            {
                var root = JObject.Parse(response.Content);
                var propertyName = typeof(T) == typeof(RanobeDbBookResponse) ? "book" :
                    typeof(T) == typeof(RanobeDbReleaseResponse) ? "release" :
                    typeof(T) == typeof(RanobeDbStaffResponse) ? "staff" : null;

                if (propertyName != null && root[propertyName] != null)
                {
                    return new JObject(new JProperty(propertyName, root[propertyName])).ToObject<T>();
                }

                if (typeof(T) == typeof(RanobeDbBookResponse))
                {
                    return (T)(object)new RanobeDbBookResponse { Book = root.ToObject<RanobeDbBookDetail>() };
                }

                if (typeof(T) == typeof(RanobeDbReleaseResponse))
                {
                    return (T)(object)new RanobeDbReleaseResponse { Release = root.ToObject<RanobeDbRelease>() };
                }

                if (typeof(T) == typeof(RanobeDbStaffResponse))
                {
                    return (T)(object)new RanobeDbStaffResponse { Staff = root.ToObject<RanobeDbStaffDetail>() };
                }

                return root.ToObject<T>();
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"RanobeDB {operation} returned invalid JSON.", ex);
            }
        }

        private static void WaitForApiRequestSlot()
        {
            while (true)
            {
                TimeSpan wait;
                lock (ApiRequestRateLimitLock)
                {
                    var now = Stopwatch.GetTimestamp();
                    var elapsedSeconds = (now - _lastApiRequestTokenRefill) / (double)Stopwatch.Frequency;
                    _apiRequestTokens = Math.Min(RequestBurstCapacity, _apiRequestTokens + elapsedSeconds / MinimumRequestSpacingSeconds);
                    _lastApiRequestTokenRefill = now;

                    if (_apiRequestTokens >= 1)
                    {
                        _apiRequestTokens -= 1;
                        return;
                    }

                    wait = TimeSpan.FromSeconds((1 - _apiRequestTokens) * MinimumRequestSpacingSeconds);
                }

                if (wait > TimeSpan.Zero)
                {
                    Thread.Sleep(wait);
                }
            }
        }

        private static Book MapBook(RanobeDbBookDetail source)
        {
            if (source == null || source.Id <= 0 || source.Hidden)
            {
                return null;
            }

            var digitalReleases = (source.Releases ?? new List<RanobeDbRelease>())
                .Where(item => item != null && item.Id > 0 && !item.Hidden && string.Equals(item.Format, "digital", StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.ReleaseDate)
                .ToList();

            // Do not invent an eBook edition for a known print/audio-only work.
            // Works with no release records can still be added ahead of publication.
            if ((source.Releases?.Any() == true) && digitalReleases.Count == 0)
            {
                return null;
            }

            var authorCredit = source.Editions?
                .Where(edition => edition?.Staff != null)
                .SelectMany(edition => edition.Staff)
                .FirstOrDefault(staff => string.Equals(staff?.Role, "author", StringComparison.OrdinalIgnoreCase) && staff.StaffId > 0);

            var author = authorCredit == null
                ? null
                : MapAuthor(new RanobeDbStaffDetail
                {
                    Id = authorCredit.StaffId,
                    Name = authorCredit.Name,
                    Romaji = authorCredit.Romaji
                });

            var title = FirstText(source.Title, source.Romaji, source.OriginalRomaji, source.OriginalTitle);
            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            var providerId = ProviderIdHelper.WithPrefix("rn", source.Id.ToString(CultureInfo.InvariantCulture));
            var book = new Book
            {
                RanobeDbBookId = providerId,
                RemoteProviderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { providerId },
                Title = title,
                OriginalTitle = FirstText(source.OriginalTitle, source.OriginalRomaji),
                TitleSlug = providerId,
                ReleaseDate = ParseDate(source.CurrentReleaseDate),
                PublicationYear = ParseDate(source.CurrentReleaseDate)?.Year,
                Editions = new List<Edition>(),
                MediaType = BookMediaType.Ebook,
                AnyEditionOk = true,
                Author = author ?? new Author(),
                SeriesName = source.Series?.Title,
                SeriesPosition = source.Series?.Books?
                    .FirstOrDefault(item => item.Id == source.Id)?.SortOrder?.ToString(CultureInfo.InvariantCulture),
                Genres = source.Series?.Tags?
                    .Where(tag => string.Equals(tag?.Type, "genre", StringComparison.OrdinalIgnoreCase))
                    .Select(tag => tag.Name)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList() ?? new List<string>(),
                Links = new List<Links> { new Links { Name = "RanobeDB", Url = $"https://ranobedb.org/book/{source.Id}" } },
                Ratings = new Ratings()
            };

            var publisher = source.Publishers?.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item?.Name))?.Name;
            foreach (var release in digitalReleases)
            {
                var edition = MapRelease(release, title, publisher);

                book.Editions.Add(edition);
            }

            if (book.Editions.Count == 0)
            {
                book.Editions.Add(new Edition
                {
                    Title = title,
                    MatchingTitle = title.ToLowerInvariant(),
                    Format = "eBook",
                    EditionFormat = "eBook",
                    IsEbook = true,
                    ReadingFormatId = 3,
                    Monitored = false
                });
            }

            book.Editions[0].Monitored = true;
            return book;
        }

        private static Edition MapRelease(RanobeDbRelease release, string fallbackTitle = null, string publisher = null)
        {
            var title = FirstText(release?.Title, fallbackTitle);
            return new Edition
            {
                ForeignEditionId = release?.Id > 0
                    ? ProviderIdHelper.WithPrefix("rnr", release.Id.ToString(CultureInfo.InvariantCulture))
                    : null,
                Title = title,
                MatchingTitle = title?.ToLowerInvariant(),
                Language = release?.Language,
                Format = "eBook",
                EditionFormat = "eBook",
                IsEbook = true,
                ReadingFormatId = 3,
                Publisher = publisher,
                Isbn13 = release?.Isbn13,
                PageCount = release?.Pages ?? 0,
                ReleaseDate = ParseDate(release?.ReleaseDateParsed) ?? ParseDate(release?.ReleaseDate ?? 0),
                Monitored = false,
                Links = BuildReleaseLinks(release)
            };
        }

        private static List<Links> BuildReleaseLinks(RanobeDbRelease release)
        {
            var links = new List<Links>();
            AddLink(links, "Publisher", release.Website);
            AddLink(links, "Amazon", release.Amazon);
            AddLink(links, "BookWalker", release.BookWalker);
            AddLink(links, "Rakuten", release.Rakuten);
            return links;
        }

        private static void AddLink(ICollection<Links> links, string name, string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                links.Add(new Links { Name = name, Url = uri.ToString() });
            }
        }

        private static Author MapAuthor(RanobeDbStaffDetail source)
        {
            if (source == null || source.Id <= 0 || source.Hidden)
            {
                return null;
            }

            var name = FirstText(source.Romaji, source.Name);
            var providerId = ProviderIdHelper.WithPrefix("rn", source.Id.ToString(CultureInfo.InvariantCulture));
            var author = new Author
            {
                RanobeDbAuthorId = providerId,
                RemoteProviderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { providerId },
                Name = name,
                SortName = name,
                TitleSlug = providerId,
                CleanName = name.CleanAuthorName(),
                Links = new List<Links> { new Links { Name = "RanobeDB", Url = $"https://ranobedb.org/staff/{source.Id}" } },
                Books = new List<Book>(),
                Series = new List<Series>()
            };
            author.NameLastFirst = name.ToLastFirst();
            author.SortNameLastFirst = author.NameLastFirst?.ToLowerInvariant();
            return author;
        }

        private static void AddAuthorBookSummaries(ICollection<Book> target, IEnumerable<RanobeDbBookSummary> summaries, Author author)
        {
            foreach (var summary in summaries ?? Enumerable.Empty<RanobeDbBookSummary>())
            {
                if (summary == null || summary.Id <= 0)
                {
                    continue;
                }

                var title = FirstText(summary.Title, summary.Romaji, summary.OriginalRomaji, summary.OriginalTitle);
                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                var providerId = ProviderIdHelper.WithPrefix("rn", summary.Id.ToString(CultureInfo.InvariantCulture));
                target.Add(new Book
                {
                    RanobeDbBookId = providerId,
                    RemoteProviderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { providerId },
                    Title = title,
                    OriginalTitle = FirstText(summary.OriginalTitle, summary.OriginalRomaji),
                    TitleSlug = providerId,
                    ReleaseDate = ParseDate(summary.CurrentReleaseDate),
                    PublicationYear = ParseDate(summary.CurrentReleaseDate)?.Year,
                    MediaType = BookMediaType.Ebook,
                    AnyEditionOk = true,
                    Author = author,
                    Editions = new List<Edition>
                    {
                        new Edition
                        {
                            Title = title,
                            MatchingTitle = title.ToLowerInvariant(),
                            Format = "eBook",
                            EditionFormat = "eBook",
                            IsEbook = true,
                            ReadingFormatId = 3,
                            Monitored = false
                        }
                    },
                    Links = new List<Links> { new Links { Name = "RanobeDB", Url = $"https://ranobedb.org/book/{summary.Id}" } }
                });
            }
        }

        private static string CreateAuthorEtag(RanobeDbStaffDetail staff, IEnumerable<Book> books)
        {
            var content = $"{staff.Id}|{staff.Name}|{staff.Romaji}|" + string.Join("|", (books ?? Enumerable.Empty<Book>())
                .OrderBy(book => book.RanobeDbBookId, StringComparer.OrdinalIgnoreCase)
                .Select(book => $"{book.RanobeDbBookId}:{book.Title}:{book.ReleaseDate:yyyyMMdd}"));
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
            return "\"" + Convert.ToHexString(hash).ToLowerInvariant() + "\"";
        }

        private static DateTime? ParseDate(int value)
        {
            return value > 0 && DateTime.TryParseExact(value.ToString(CultureInfo.InvariantCulture), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed
                : null;
        }

        private static DateTime? ParseDate(string value)
        {
            return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed.Date
                : null;
        }

        private static string FirstText(params string[] values)
        {
            return values?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
        }
    }
}
