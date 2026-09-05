using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Books;
using NzbDrone.Core.MetadataSource.Goodreads;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Tags;

namespace NzbDrone.Core.ImportLists.Goodreads
{
    public class GoodreadsBookshelf : ImportListBase<GoodreadsBookshelfImportListSettings>
    {
        private const int ReviewsPerPage = 200;
        private const int ShelvesPerPage = 100;
        private const int MaxPages = 100;
        private const int ReviewListParallelism = 5;
        private static readonly string GoodreadsApiKey = new string("gSuM2Onzl6sjMU25HY1Xcd".Reverse().ToArray());
        private static readonly Regex ShelfListItemRegex = new(
            @"<a(?=[^>]*userShowPageShelfListItem)(?=[^>]*href=""[^""]*[?&]shelf=(?<shelf>[^&""'<>\s]+))[^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ShelfQueryRegex = new(@"[?&]shelf=(?<shelf>[^&""'<>\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly IHttpClient _httpClient;
        private readonly Lazy<IQualityProfileService> _qualityProfileService;
        private readonly Lazy<IMetadataProfileService> _metadataProfileService;
        private readonly Lazy<ITagService> _tagService;
        private readonly IRootFolderService _rootFolderService;
        private readonly IRootFolderSettingsResolver _rootFolderSettingsResolver;
        private DateTime _lastApiFallbackWarning;

        public GoodreadsBookshelf(IImportListStatusService importListStatusService,
                                  IConfigService configService,
                                  IParsingService parsingService,
                                  IHttpClient httpClient,
                                  Lazy<IQualityProfileService> qualityProfileService,
                                  Lazy<IMetadataProfileService> metadataProfileService,
                                  Lazy<ITagService> tagService,
                                  IRootFolderService rootFolderService,
                                  IRootFolderSettingsResolver rootFolderSettingsResolver,
                                  Logger logger)
            : base(importListStatusService, configService, parsingService, logger)
        {
            _httpClient = httpClient;
            _qualityProfileService = qualityProfileService;
            _metadataProfileService = metadataProfileService;
            _tagService = tagService;
            _rootFolderService = rootFolderService;
            _rootFolderSettingsResolver = rootFolderSettingsResolver;
        }

        private const int DefaultRefreshIntervalMinutes = 720;

        public override string Name => "Goodreads Bookshelves";
        public override ImportListType ListType => ImportListType.Goodreads;
        public override TimeSpan MinRefreshInterval
        {
            get
            {
                var minutes = Definition?.Settings is GoodreadsBookshelfImportListSettings settings && settings.RefreshIntervalMinutes > 0
                    ? settings.RefreshIntervalMinutes
                    : DefaultRefreshIntervalMinutes;

                return TimeSpan.FromMinutes(minutes);
            }
        }

        public override IList<ImportListItemInfo> Fetch()
        {
            var result = new List<ImportListItemInfo>();
            var seenSourceBooks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (var shelf in Settings.BookshelfIds ?? Enumerable.Empty<string>())
                {
                    var remainingLimit = Settings.ImportLimit > 0 ? Math.Max(Settings.ImportLimit - result.Count, 0) : 0;
                    if (Settings.ImportLimit > 0 && remainingLimit <= 0)
                    {
                        break;
                    }

                    foreach (var item in FetchShelf(shelf, remainingLimit))
                    {
                        GoodreadsImportListLimit.TryAdd(result, item, seenSourceBooks, Settings.ImportLimit);

                        if (GoodreadsImportListLimit.HasReached(result.Count, Settings.ImportLimit))
                        {
                            _logger.Info("Goodreads Bookshelves import list '{0}' reached import limit of {1}; remaining Goodreads items will be skipped.",
                                Definition?.Name ?? Name, Settings.ImportLimit);
                            break;
                        }
                    }

                    if (GoodreadsImportListLimit.HasReached(result.Count, Settings.ImportLimit))
                    {
                        break;
                    }
                }

                _importListStatusService.RecordSuccess(Definition.Id);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Error fetching Goodreads bookshelves");
                _importListStatusService.RecordFailure(Definition.Id);
            }

            return CleanupListItems(result);
        }

        public override object RequestAction(string action, IDictionary<string, string> query)
        {
            if (action.IsNullOrWhiteSpace())
            {
                return null;
            }

            if (action == "getAudiobookQualityProfiles")
            {
                return new
                {
                    options = BuildSelectOptions(_qualityProfileService.Value.GetByType(ProfileType.Audiobook).Select(p => (p.Id, p.Name)), includeDefault: true)
                };
            }

            if (action == "getEbookQualityProfiles")
            {
                return new
                {
                    options = BuildSelectOptions(_qualityProfileService.Value.GetByType(ProfileType.Ebook).Select(p => (p.Id, p.Name)), includeDefault: true)
                };
            }

            if (action == "getAudiobookMetadataProfiles")
            {
                return new
                {
                    options = BuildSelectOptions(_metadataProfileService.Value.All()
                        .Where(p => p.ProfileType == MetadataProfileType.General || p.ProfileType == MetadataProfileType.Audiobook)
                        .Select(p => (p.Id, p.Name)), includeDefault: true)
                };
            }

            if (action == "getEbookMetadataProfiles")
            {
                return new
                {
                    options = BuildSelectOptions(_metadataProfileService.Value.All()
                        .Where(p => p.ProfileType == MetadataProfileType.General || p.ProfileType == MetadataProfileType.Ebook)
                        .Select(p => (p.Id, p.Name)), includeDefault: true)
                };
            }

            if (action == "getTags")
            {
                return new
                {
                    options = _tagService.Value.All()
                        .OrderBy(t => t.Label)
                        .Select(t => new
                        {
                            Value = t.Id,
                            Name = t.Label
                        })
                        .ToList()
                };
            }

            if (action != "getBookshelves")
            {
                return null;
            }

            var helptextKey = query != null && query.TryGetValue("name", out var name) && name.IsNotNullOrWhiteSpace()
                ? name
                : "bookshelfIds";

            var userId = GetNormalizedUserId();
            if (userId.IsNullOrWhiteSpace())
            {
                return new
                {
                    options = new
                    {
                        helptext = new Dictionary<string, string>(),
                        user = string.Empty,
                        shelves = new List<object>()
                    }
                };
            }

            var shelves = FetchShelves(userId);
            var displayUser = userId;
            var helptext = new Dictionary<string, string>
            {
                { helptextKey, $"Import books from {displayUser}'s shelves (add shelves manually if they don't load):" }
            };

            return new
            {
                options = new
                {
                    helptext,
                    user = displayUser,
                    shelves = shelves
                        .OrderBy(s => s)
                        .Select(s => new { id = s, name = s })
                }
            };
        }

        protected override void Test(List<ValidationFailure> failures)
        {
            failures.AddIfNotNull(TestConnection());
            failures.AddRange(TestRootFolderConfig());
        }

        private ValidationFailure TestConnection()
        {
            try
            {
                var userId = GetNormalizedUserId();
                if (userId.IsNullOrWhiteSpace())
                {
                    return new ValidationFailure(nameof(Settings.UserId), "Goodreads user ID is required");
                }

                var shelf = (Settings.BookshelfIds ?? Enumerable.Empty<string>())
                    .FirstOrDefault(s => s.IsNotNullOrWhiteSpace());
                if (shelf.IsNullOrWhiteSpace())
                {
                    return new ValidationFailure(nameof(Settings.BookshelfIds), "At least one bookshelf is required");
                }

                if (TryResolveShelfFromApi(userId, shelf, out var apiShelf, out var apiAvailable))
                {
                    var apiDocument = GetReviewListXml(userId, apiShelf, page: 1);
                    if (apiDocument?.Root == null)
                    {
                        return new ValidationFailure(nameof(Settings.UserId), "Could not retrieve Goodreads bookshelf. Check the user ID and that shelves are public.");
                    }

                    return null;
                }

                if (apiAvailable)
                {
                    return new ValidationFailure(nameof(Settings.BookshelfIds), $"Goodreads shelf '{shelf}' was not found for this user. Verify the shelf name and that shelves are public.");
                }

                var rssDocument = GetReviewsRss(userId, shelf, page: 1);
                if (rssDocument?.Root == null)
                {
                    return new ValidationFailure(nameof(Settings.UserId), "Could not retrieve Goodreads bookshelf RSS feed. Check the user ID and that shelves are public.");
                }

                var channelTitle = rssDocument.Root.Element("channel")?.Element("title")?.Value;
                var actualShelf = TryExtractShelfName(channelTitle);
                if (actualShelf.IsNotNullOrWhiteSpace() &&
                    !ShelfNamesMatch(actualShelf, shelf))
                {
                    return new ValidationFailure(nameof(Settings.BookshelfIds), $"Goodreads returned shelf '{actualShelf}' for requested shelf '{shelf}'. Verify the shelf name and that shelves are public.");
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Unable to connect to Goodreads");
                return new ValidationFailure(string.Empty, "Unable to connect to import list, check the log for more details");
            }
        }

        private IEnumerable<ValidationFailure> TestRootFolderConfig()
        {
            var failures = new List<ValidationFailure>();

            if (Settings == null)
            {
                return failures;
            }

            if (Settings.MonitorAudiobooks)
            {
                failures.AddIfNotNull(TestRootFolder(
                    nameof(Settings.AudiobookRootFolderPath),
                    Settings.AudiobookRootFolderPath,
                    BookMediaType.Audiobook,
                    Settings.AudiobookQualityProfileId,
                    Settings.AudiobookMetadataProfileId));
            }

            if (Settings.MonitorEbooks)
            {
                failures.AddIfNotNull(TestRootFolder(
                    nameof(Settings.EbookRootFolderPath),
                    Settings.EbookRootFolderPath,
                    BookMediaType.Ebook,
                    Settings.EbookQualityProfileId,
                    Settings.EbookMetadataProfileId));
            }

            return failures;
        }

        private ValidationFailure TestRootFolder(string fieldName,
            string rootFolderPath,
            BookMediaType mediaType,
            int overrideQualityProfileId,
            int overrideMetadataProfileId)
        {
            if (rootFolderPath.IsNullOrWhiteSpace())
            {
                return new ValidationFailure(fieldName, "Root folder is required");
            }

            var rootFolder = _rootFolderService.GetBestRootFolder(rootFolderPath);
            if (rootFolder == null)
            {
                return new ValidationFailure(fieldName, $"Root folder '{rootFolderPath}' is not configured in Chaptarr");
            }

            if (mediaType == BookMediaType.Audiobook && rootFolder.FolderType == FolderType.Ebook)
            {
                return new ValidationFailure(fieldName, "Selected root folder is Ebook-only; choose an Audiobook or Mixed root folder");
            }

            if (mediaType == BookMediaType.Ebook && rootFolder.FolderType == FolderType.Audiobook)
            {
                return new ValidationFailure(fieldName, "Selected root folder is Audiobook-only; choose an Ebook or Mixed root folder");
            }

            var resolved = _rootFolderSettingsResolver.ResolveSettings(rootFolder, mediaType);
            if (resolved == null || !resolved.IsConfigured)
            {
                return new ValidationFailure(fieldName, $"Selected root folder '{rootFolder.Path}' does not have {mediaType} defaults configured");
            }

            if (overrideQualityProfileId <= 0 && (resolved.QualityProfileId ?? 0) <= 0)
            {
                return new ValidationFailure(fieldName, $"Selected root folder '{rootFolder.Path}' is missing a {mediaType} quality profile default");
            }

            if (overrideMetadataProfileId <= 0 && (resolved.MetadataProfileId ?? 0) <= 0)
            {
                return new ValidationFailure(fieldName, $"Selected root folder '{rootFolder.Path}' is missing a {mediaType} metadata profile default");
            }

            return null;
        }

        private enum ShelfFetchStatus
        {
            Success,
            Unavailable,
            InvalidShelf
        }

        private sealed class ReviewListPage
        {
            public int Page { get; set; }
            public XDocument Document { get; set; }
        }

        private IEnumerable<ImportListItemInfo> FetchShelf(string shelf, int remainingLimit)
        {
            var userId = GetNormalizedUserId();
            if (userId.IsNullOrWhiteSpace())
            {
                _logger.Warn("Goodreads Bookshelves import list requires a user ID.");
                yield break;
            }

            var apiStatus = TryFetchShelfFromReviewListApi(userId, shelf, remainingLimit, out var apiItems);
            if (apiStatus == ShelfFetchStatus.Success)
            {
                foreach (var item in apiItems)
                {
                    yield return item;
                }

                yield break;
            }

            if (apiStatus == ShelfFetchStatus.InvalidShelf)
            {
                yield break;
            }

            WarnApiToRssFallback(null, userId, shelf, "review/list.xml or shelf/list.xml unavailable");

            foreach (var item in FetchShelfFromRss(userId, shelf))
            {
                yield return item;
            }
        }

        private ShelfFetchStatus TryFetchShelfFromReviewListApi(string userId, string shelf, int remainingLimit, out List<ImportListItemInfo> items)
        {
            items = new List<ImportListItemInfo>();

            if (!TryResolveShelfFromApi(userId, shelf, out var apiShelf, out var apiAvailable))
            {
                if (apiAvailable)
                {
                    _logger.Warn("Goodreads shelf '{0}' was not found for user {1}. Refusing to fetch review/list.xml because Goodreads returns the entire review list for unknown shelves.", shelf, userId);
                    return ShelfFetchStatus.InvalidShelf;
                }

                return ShelfFetchStatus.Unavailable;
            }

            XDocument page1;
            try
            {
                page1 = GetReviewListXml(userId, apiShelf, page: 1);
            }
            catch (Exception ex)
            {
                WarnApiToRssFallback(ex, userId, shelf, "review/list.xml page 1 failed");
                return ShelfFetchStatus.Unavailable;
            }

            var page1Reviews = GetDirectReviewElements(page1).ToList();
            items.AddRange(ParseReviewListItems(page1Reviews));

            if (page1Reviews.Count == 0 || page1Reviews.Count < ReviewsPerPage || (remainingLimit > 0 && items.Count >= remainingLimit))
            {
                return ShelfFetchStatus.Success;
            }

            var totalPages = MaxPages;
            if (TryGetReviewTotal(page1, out var total) && total > 0)
            {
                totalPages = Math.Min(MaxPages, (int)Math.Ceiling(total / (double)ReviewsPerPage));
            }

            foreach (var page in FetchRemainingReviewListPages(userId, apiShelf, totalPages))
            {
                var reviews = GetDirectReviewElements(page.Document).ToList();
                if (reviews.Count == 0)
                {
                    break;
                }

                items.AddRange(ParseReviewListItems(reviews));

                if (reviews.Count < ReviewsPerPage || (remainingLimit > 0 && items.Count >= remainingLimit))
                {
                    break;
                }
            }

            return ShelfFetchStatus.Success;
        }

        private IEnumerable<ReviewListPage> FetchRemainingReviewListPages(string userId, string shelf, int totalPages)
        {
            if (totalPages <= 1)
            {
                yield break;
            }

            var pages = Enumerable.Range(2, totalPages - 1).ToList();
            for (var i = 0; i < pages.Count; i += ReviewListParallelism)
            {
                var wave = pages.Skip(i).Take(ReviewListParallelism).ToList();
                var tasks = wave
                    .Select(page => Task.Run(() => TryGetReviewListPage(userId, shelf, page)))
                    .ToArray();

                Task.WaitAll(tasks);

                foreach (var result in tasks.Select(t => t.Result).OrderBy(r => r.Page))
                {
                    if (result.Document == null)
                    {
                        _logger.Warn("Goodreads review/list.xml page {0} failed for shelf '{1}'. Keeping previously fetched API pages and stopping this shelf without falling back to RSS.", result.Page, shelf);
                        yield break;
                    }

                    yield return result;
                }
            }
        }

        private ReviewListPage TryGetReviewListPage(string userId, string shelf, int page)
        {
            try
            {
                return new ReviewListPage
                {
                    Page = page,
                    Document = GetReviewListXml(userId, shelf, page)
                };
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Goodreads review/list.xml page {0} request failed for shelf {1}", page, shelf);
                return new ReviewListPage { Page = page };
            }
        }

        private IEnumerable<ImportListItemInfo> FetchShelfFromRss(string userId, string shelf)
        {
            var page = 1;
            while (page <= MaxPages)
            {
                XDocument document;
                try
                {
                    document = GetReviewsRss(userId, shelf, page);
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Goodreads RSS request failed for shelf {0}", shelf);
                    yield break;
                }

                var channelTitle = document?.Root?.Element("channel")?.Element("title")?.Value;
                var actualShelf = TryExtractShelfName(channelTitle);
                if (actualShelf.IsNotNullOrWhiteSpace() &&
                    !ShelfNamesMatch(actualShelf, shelf))
                {
                    _logger.Warn("Goodreads RSS shelf mismatch for user {0}: requested '{1}' but got '{2}'. Refusing to import to avoid importing the wrong shelf.",
                        userId, shelf, actualShelf);
                    yield break;
                }

                var rssItems = document?.Descendants("item").ToList();
                if (rssItems == null || rssItems.Count == 0)
                {
                    yield break;
                }

                foreach (var item in rssItems)
                {
                    var title = item.Element("title")?.Value.CleanSpaces();
                    var authorName = item.Element("author_name")?.Value.CleanSpaces();
                    var bookId = item.Element("book_id")?.Value?.Trim();

                    var pubDate = item.Element("pubDate")?.Value?.Trim();
                    var releaseDate = ParseRssPubDate(pubDate);

                    if (bookId.IsNullOrWhiteSpace() && title.IsNullOrWhiteSpace() && authorName.IsNullOrWhiteSpace())
                    {
                        continue;
                    }

                    // Goodreads RSS does not include work/author IDs; carry edition IDs forward for fallback mapping.
                    yield return new ImportListItemInfo
                    {
                        Author = authorName,
                        Book = title,
                        EditionGoodreadsId = PrefixGoodreadsId(bookId),
                        ReleaseDate = releaseDate
                    };
                }

                page++;
            }
        }

        private List<string> FetchShelves(string userId)
        {
            if (TryFetchShelvesFromApi(userId, out var apiShelves) && apiShelves.Any())
            {
                return apiShelves;
            }

            try
            {
                var html = GetUserProfileHtml(userId);
                if (html.IsNullOrWhiteSpace())
                {
                    return new List<string>();
                }

                return ExtractShelfNamesFromProfileHtml(html);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to fetch Goodreads shelves for user {0}", userId);
                return new List<string>();
            }
        }

        private bool TryResolveShelfFromApi(string userId, string requestedShelf, out string apiShelf, out bool apiAvailable)
        {
            apiShelf = null;
            apiAvailable = TryFetchShelvesFromApi(userId, out var shelves);
            if (!apiAvailable)
            {
                return false;
            }

            apiShelf = shelves.FirstOrDefault(s => ShelfNamesMatch(s, requestedShelf));
            return apiShelf.IsNotNullOrWhiteSpace();
        }

        private bool TryFetchShelvesFromApi(string userId, out List<string> shelves)
        {
            shelves = new List<string>();

            try
            {
                for (var page = 1; page <= MaxPages; page++)
                {
                    var document = GetShelfListXml(userId, page);
                    var pageShelves = document?
                        .Descendants("user_shelf")
                        .Select(s => s.Element("name")?.Value.CleanSpaces())
                        .Where(s => s.IsNotNullOrWhiteSpace())
                        .ToList() ?? new List<string>();

                    if (!pageShelves.Any())
                    {
                        break;
                    }

                    shelves.AddRange(pageShelves);

                    if (pageShelves.Count < ShelvesPerPage)
                    {
                        break;
                    }
                }

                shelves = shelves
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return true;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to fetch Goodreads shelves from shelf/list.xml for user {0}", userId);
                shelves = new List<string>();
                return false;
            }
        }

        private XDocument GetShelfListXml(string userId, int page)
        {
            var baseUrl = GetBaseUrl();

            var builder = new HttpRequestBuilder($"{baseUrl}/shelf/list.xml")
                .AddQueryParam("user_id", userId)
                .AddQueryParam("per_page", ShelvesPerPage)
                .AddQueryParam("page", page)
                .AddQueryParam("key", GoodreadsApiKey)
                .AddQueryParam("_nc", "1");
            builder.SetHeader("User-Agent", GoodreadsAndroidUserAgent.GetRandom());
            builder.KeepAlive();

            var httpResponse = _httpClient.Get(builder.Build());
            if (httpResponse?.Content.IsNullOrWhiteSpace() != false)
            {
                return null;
            }

            return XDocument.Parse(httpResponse.Content);
        }

        private void WarnApiToRssFallback(Exception ex, string userId, string shelf, string reason)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastApiFallbackWarning) < TimeSpan.FromMinutes(30))
            {
                if (ex != null)
                {
                    _logger.Debug(ex, "Goodreads review/list.xml unavailable for user {0} shelf '{1}' ({2}); falling back to RSS", userId, shelf, reason);
                }
                else
                {
                    _logger.Debug("Goodreads review/list.xml unavailable for user {0} shelf '{1}' ({2}); falling back to RSS", userId, shelf, reason);
                }

                return;
            }

            _lastApiFallbackWarning = now;
            if (ex != null)
            {
                _logger.Warn(ex, "Goodreads review/list.xml unavailable for user {0} shelf '{1}' ({2}); falling back to RSS. RSS lacks work and author IDs, so matching may require extra provider mapping.", userId, shelf, reason);
            }
            else
            {
                _logger.Warn("Goodreads review/list.xml unavailable for user {0} shelf '{1}' ({2}); falling back to RSS. RSS lacks work and author IDs, so matching may require extra provider mapping.", userId, shelf, reason);
            }
        }

        private XDocument GetReviewListXml(string userId, string shelf, int page)
        {
            var baseUrl = GetBaseUrl();

            var builder = new HttpRequestBuilder($"{baseUrl}/review/list.xml")
                .AddQueryParam("v", 2)
                .AddQueryParam("id", userId)
                .AddQueryParam("shelf", shelf)
                .AddQueryParam("per_page", ReviewsPerPage)
                .AddQueryParam("page", page)
                .AddQueryParam("key", GoodreadsApiKey)
                .AddQueryParam("_nc", "1");
            builder.SetHeader("User-Agent", GoodreadsAndroidUserAgent.GetRandom());
            builder.KeepAlive();

            var httpResponse = _httpClient.Get(builder.Build());
            if (httpResponse?.Content.IsNullOrWhiteSpace() != false)
            {
                return null;
            }

            return XDocument.Parse(httpResponse.Content);
        }

        private static IEnumerable<XElement> GetDirectReviewElements(XDocument document)
        {
            var reviews = document?.Descendants("reviews").FirstOrDefault();
            return reviews?.Elements("review") ?? Enumerable.Empty<XElement>();
        }

        private static bool TryGetReviewTotal(XDocument document, out int total)
        {
            total = 0;
            var raw = document?.Descendants("reviews").FirstOrDefault()?.Attribute("total")?.Value;
            return raw.IsNotNullOrWhiteSpace() && int.TryParse(raw, out total);
        }

        private static IEnumerable<ImportListItemInfo> ParseReviewListItems(IEnumerable<XElement> reviews)
        {
            foreach (var review in reviews ?? Enumerable.Empty<XElement>())
            {
                var book = review.Element("book");
                if (book == null)
                {
                    continue;
                }

                var author = book.Element("authors")?.Elements("author").FirstOrDefault();
                var work = book.Element("work");

                var editionId = book.Element("id")?.Value?.Trim();
                var workId = work?.Element("id")?.Value?.Trim();
                var authorId = author?.Element("id")?.Value?.Trim();
                var title = FirstNonBlank(
                    book.Element("title_without_series")?.Value,
                    book.Element("title")?.Value)?.CleanSpaces();
                var authorName = author?.Element("name")?.Value.CleanSpaces();
                var isbn13 = book.Element("isbn13")?.Value?.Trim();
                var asin = FirstNonBlank(book.Element("kindle_asin")?.Value, book.Element("asin")?.Value)?.Trim();
                var releaseDate = ParseRssPubDate(FirstNonBlank(review.Element("read_at")?.Value, review.Element("date_added")?.Value));

                if (editionId.IsNullOrWhiteSpace() && workId.IsNullOrWhiteSpace() && title.IsNullOrWhiteSpace() && authorName.IsNullOrWhiteSpace())
                {
                    continue;
                }

                yield return new ImportListItemInfo
                {
                    Author = authorName,
                    AuthorGoodreadsId = PrefixGoodreadsId(authorId),
                    Book = title,
                    BookGoodreadsId = PrefixGoodreadsId(workId),
                    EditionGoodreadsId = PrefixGoodreadsId(editionId),
                    Isbn13 = isbn13,
                    Asin = asin,
                    ReleaseDate = releaseDate
                };
            }
        }

        private static string PrefixGoodreadsId(string raw)
        {
            raw = raw?.Trim();
            if (raw.IsNullOrWhiteSpace())
            {
                return null;
            }

            return raw.StartsWith("gr:", StringComparison.OrdinalIgnoreCase) ? raw : $"gr:{raw}";
        }

        private static string FirstNonBlank(params string[] values)
        {
            return values?.FirstOrDefault(v => v.IsNotNullOrWhiteSpace());
        }

        private string GetNormalizedUserId()
        {
            return GoodreadsUserIdParser.TryParse(Settings.UserId, out var normalized) ? normalized : null;
        }

        private string GetUserProfileHtml(string userId)
        {
            var baseUrl = GetBaseUrl();

            var builder = new HttpRequestBuilder($"{baseUrl}/user/show/{userId}");
            builder.SetHeader("User-Agent", GoodreadsAndroidUserAgent.GetRandom());
            builder.KeepAlive();
            builder.AllowAutoRedirect = true;

            var httpResponse = _httpClient.Get(builder.Build());
            return httpResponse?.Content;
        }

        private string GetBaseUrl()
        {
            var baseUrl = Settings.BaseUrl?.Trim().TrimEnd('/');
            if (baseUrl.IsNullOrWhiteSpace())
            {
                return "https://www.goodreads.com";
            }

            if (!baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return "https://" + baseUrl;
            }

            return baseUrl;
        }

        private XDocument GetReviewsRss(string userId, string shelf, int page)
        {
            var baseUrl = GetBaseUrl();

            var builder = new HttpRequestBuilder($"{baseUrl}/review/list_rss/{userId}")
                .AddQueryParam("shelf", shelf)
                .AddQueryParam("per_page", ReviewsPerPage)
                .AddQueryParam("page", page);
            builder.SetHeader("User-Agent", GoodreadsAndroidUserAgent.GetRandom());
            builder.KeepAlive();

            var httpResponse = _httpClient.Get(builder.Build());
            if (httpResponse?.Content.IsNullOrWhiteSpace() != false)
            {
                return null;
            }

            return XDocument.Parse(httpResponse.Content);
        }

        private static List<string> ExtractShelfNamesFromProfileHtml(string html)
        {
            var shelves = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Prefer the canonical shelf list in the profile sidebar. Other links on the page may contain
            // `shelf=` query params that are not actual shelves (Goodreads RSS will silently fall back to `read`).
            var matches = ShelfListItemRegex.Matches(html);
            if (matches.Count == 0)
            {
                matches = ShelfQueryRegex.Matches(html);
            }

            foreach (Match match in matches)
            {
                var shelf = match.Groups["shelf"].Value;
                if (shelf.IsNullOrWhiteSpace())
                {
                    continue;
                }

                try
                {
                    // Goodreads links may encode spaces as '+' in query strings; treat them as spaces before unescaping.
                    shelf = shelf.Replace('+', ' ');
                    shelf = Uri.UnescapeDataString(shelf);
                }
                catch
                {
                    // Ignore malformed percent-encoding
                }

                if (shelf.IsNullOrWhiteSpace())
                {
                    continue;
                }

                shelves.Add(shelf);
            }

            return shelves.ToList();
        }

        private static bool ShelfNamesMatch(string actual, string requested)
        {
            if (actual.IsNullOrWhiteSpace() || requested.IsNullOrWhiteSpace())
            {
                return false;
            }

            return NormalizeShelfName(actual) == NormalizeShelfName(requested);
        }

        private static string NormalizeShelfName(string shelf)
        {
            if (shelf.IsNullOrWhiteSpace())
            {
                return string.Empty;
            }

            // Goodreads shelf identifiers can appear with different separators across URLs/titles.
            // Normalize spaces/underscores to '-' and lowercase for stable comparisons.
            var normalized = shelf.Trim().ToLowerInvariant();
            normalized = Regex.Replace(normalized, @"[\s_]+", "-");
            normalized = Regex.Replace(normalized, @"-+", "-");
            return normalized;
        }

        private static string TryExtractShelfName(string channelTitle)
        {
            if (channelTitle.IsNullOrWhiteSpace())
            {
                return null;
            }

            var colonIndex = channelTitle.LastIndexOf(':');
            if (colonIndex < 0 || colonIndex >= channelTitle.Length - 1)
            {
                return null;
            }

            return channelTitle.Substring(colonIndex + 1).Trim();
        }

        private static DateTime ParseRssPubDate(string pubDate)
        {
            if (pubDate.IsNullOrWhiteSpace())
            {
                return default;
            }

            if (DateTimeOffset.TryParse(pubDate, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
            {
                return parsed.UtcDateTime;
            }

            return default;
        }

        private static List<object> BuildSelectOptions(IEnumerable<(int id, string name)> items, bool includeDefault)
        {
            var options = new List<object>();

            if (includeDefault)
            {
                options.Add(new
                {
                    Value = 0,
                    Name = "Use root folder defaults",
                    LocalizationKey = "UseRootFolderDefaults"
                });
            }

            options.AddRange(items
                .OrderBy(i => i.name)
                .Select(i => new
                {
                    Value = i.id,
                    Name = i.name
                }));

            return options;
        }
    }
}
