using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Books;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Goodreads;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Tags;
using NzbDrone.Core.ThingiProvider.Status;

namespace Chaptarr.Core.Test.ImportLists.Goodreads
{
    [TestFixture]
    public class GoodreadsBookshelfImportListFixture
    {
        private sealed class StubQualityProfileService : IQualityProfileService
        {
            public QualityProfile Add(QualityProfile profile) => throw new NotImplementedException();
            public void Update(QualityProfile profile) => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
            public List<QualityProfile> All() => new();
            public List<QualityProfile> GetByType(ProfileType type) => new();
            public QualityProfile Get(int id) => throw new NotImplementedException();
            public bool Exists(int id) => false;
            public QualityProfile GetDefaultProfile(string name, Quality cutoff = null, params Quality[] allowed) => throw new NotImplementedException();
        }

        private sealed class StubMetadataProfileService : IMetadataProfileService
        {
            public MetadataProfile Add(MetadataProfile profile) => throw new NotImplementedException();
            public void Update(MetadataProfile profile) => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
            public List<MetadataProfile> All() => new();
            public MetadataProfile Get(int id) => throw new NotImplementedException();
            public bool Exists(int id) => false;
            public List<Book> FilterBooks(Author input, int profileId) => input?.Books ?? new List<Book>();
        }

        private sealed class StubTagService : ITagService
        {
            public Tag GetTag(int tagId) => null;
            public Tag GetTag(string tag) => null;
            public TagDetails Details(int tagId) => null;
            public List<TagDetails> Details() => new();
            public List<Tag> All() => new();
            public Tag Add(Tag tag) => throw new NotImplementedException();
            public Tag Update(Tag tag) => throw new NotImplementedException();
            public void Delete(int tagId) => throw new NotImplementedException();
        }

        private sealed class StubRootFolderService : IRootFolderService
        {
            public List<RootFolder> All() => new();
            public List<RootFolder> AllWithSpaceStats() => new();
            public RootFolder Add(RootFolder rootFolder) => throw new NotImplementedException();
            public RootFolder Update(RootFolder rootFolder) => throw new NotImplementedException();
            public void Remove(int id) => throw new NotImplementedException();
            public RootFolder Get(int id) => null;
            public List<RootFolder> AllForTag(int tagId) => new();
            public RootFolder GetBestRootFolder(string path) => null;
            public RootFolder GetBestRootFolder(string path, List<RootFolder> allRootFolders) => null;
            public string GetBestRootFolderPath(string path) => path;
            public string GetBestRootFolderPath(string path, List<RootFolder> allRootFolders) => path;
        }

        private sealed class StubRootFolderSettingsResolver : IRootFolderSettingsResolver
        {
            public ResolvedRootFolderSettings ResolveSettings(int rootFolderId, BookMediaType mediaType) =>
                new() { IsConfigured = false, Source = "Unconfigured" };

            public ResolvedRootFolderSettings ResolveSettings(RootFolder rootFolder, BookMediaType mediaType) =>
                new() { IsConfigured = false, Source = "Unconfigured" };
        }

        private class StubImportListStatusService : IImportListStatusService
        {
            public List<ImportListStatus> GetBlockedProviders() => new();
            public void RecordSuccess(int providerId) { }
            public void RecordFailure(int providerId, TimeSpan minimumBackOff = default) { }
            public void RecordConnectionFailure(int providerId) { }
            public DateTime? GetLastSyncListInfo(int importListId) => null;
            public void UpdateListSyncStatus(int importListId) { }
        }

        private class StubHttpClient : IHttpClient
        {
            private readonly string _rssPage1Xml;
            private readonly string _rssPage2Xml;
            private readonly string _shelfListXml;
            private readonly string _shelfListPage2Xml;
            private readonly string _reviewListPage1Xml;
            private readonly string _reviewListPage2Xml;

            public StubHttpClient(string rssPage1Xml = null, string rssPage2Xml = null, string shelfListXml = null, string shelfListPage2Xml = null, string reviewListPage1Xml = null, string reviewListPage2Xml = null)
            {
                _rssPage1Xml = rssPage1Xml;
                _rssPage2Xml = rssPage2Xml;
                _shelfListXml = shelfListXml;
                _shelfListPage2Xml = shelfListPage2Xml;
                _reviewListPage1Xml = reviewListPage1Xml;
                _reviewListPage2Xml = reviewListPage2Xml;
            }

            public List<HttpRequest> Requests { get; } = new();

            public HttpResponse Get(HttpRequest request)
            {
                Requests.Add(request);

                var url = request.Url.FullUri;

                if (url.Contains("/shelf/list.xml", StringComparison.OrdinalIgnoreCase) &&
                    url.Contains("page=2", StringComparison.OrdinalIgnoreCase) &&
                    _shelfListPage2Xml != null)
                {
                    return new HttpResponse(request, new HttpHeader { ContentType = "application/xml" }, _shelfListPage2Xml);
                }

                if (url.Contains("/shelf/list.xml", StringComparison.OrdinalIgnoreCase) &&
                    url.Contains("page=1", StringComparison.OrdinalIgnoreCase) &&
                    _shelfListXml != null)
                {
                    return new HttpResponse(request, new HttpHeader { ContentType = "application/xml" }, _shelfListXml);
                }

                if (url.Contains("/review/list.xml", StringComparison.OrdinalIgnoreCase) &&
                    url.Contains("page=1", StringComparison.OrdinalIgnoreCase) &&
                    _reviewListPage1Xml != null)
                {
                    return new HttpResponse(request, new HttpHeader { ContentType = "application/xml" }, _reviewListPage1Xml);
                }

                if (url.Contains("/review/list.xml", StringComparison.OrdinalIgnoreCase) &&
                    url.Contains("page=2", StringComparison.OrdinalIgnoreCase) &&
                    _reviewListPage2Xml != null)
                {
                    return new HttpResponse(request, new HttpHeader { ContentType = "application/xml" }, _reviewListPage2Xml);
                }

                if (url.Contains("/review/list_rss/12345678", StringComparison.OrdinalIgnoreCase) &&
                    url.Contains("page=1", StringComparison.OrdinalIgnoreCase) &&
                    _rssPage1Xml != null)
                {
                    return new HttpResponse(request, new HttpHeader { ContentType = "application/xml" }, _rssPage1Xml);
                }

                if (url.Contains("/review/list_rss/12345678", StringComparison.OrdinalIgnoreCase) &&
                    url.Contains("page=2", StringComparison.OrdinalIgnoreCase) &&
                    _rssPage2Xml != null)
                {
                    return new HttpResponse(request, new HttpHeader { ContentType = "application/xml" }, _rssPage2Xml);
                }

                throw new InvalidOperationException($"Unexpected URL: {url}");
            }

            public HttpResponse Execute(HttpRequest request) => throw new NotImplementedException();
            public void DownloadFile(string url, string fileName, string userAgent = null) => throw new NotImplementedException();
            public HttpResponse<T> Get<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
            public HttpResponse Head(HttpRequest request) => throw new NotImplementedException();
            public HttpResponse Post(HttpRequest request) => throw new NotImplementedException();
            public HttpResponse<T> Post<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
            public System.Threading.Tasks.Task<HttpResponse> ExecuteAsync(HttpRequest request) => throw new NotImplementedException();
            public System.Threading.Tasks.Task DownloadFileAsync(string url, string fileName, string userAgent = null) => throw new NotImplementedException();
            public System.Threading.Tasks.Task<HttpResponse> GetAsync(HttpRequest request) => throw new NotImplementedException();
            public System.Threading.Tasks.Task<HttpResponse<T>> GetAsync<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
            public System.Threading.Tasks.Task<HttpResponse> HeadAsync(HttpRequest request) => throw new NotImplementedException();
            public System.Threading.Tasks.Task<HttpResponse> PostAsync(HttpRequest request) => throw new NotImplementedException();
            public System.Threading.Tasks.Task<HttpResponse<T>> PostAsync<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
        }

        private sealed class StubHttpClientForShelves : IHttpClient
        {
            private readonly string _profileHtml;

            public StubHttpClientForShelves(string profileHtml)
            {
                _profileHtml = profileHtml;
            }

            public List<HttpRequest> Requests { get; } = new();

            public HttpResponse Get(HttpRequest request)
            {
                Requests.Add(request);

                var url = request.Url.FullUri;
                if (url.Contains("/user/show/12345678", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponse(request, new HttpHeader { ContentType = "text/html" }, _profileHtml);
                }

                throw new InvalidOperationException($"Unexpected URL: {url}");
            }

            public HttpResponse Execute(HttpRequest request) => throw new NotImplementedException();
            public void DownloadFile(string url, string fileName, string userAgent = null) => throw new NotImplementedException();
            public HttpResponse<T> Get<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
            public HttpResponse Head(HttpRequest request) => throw new NotImplementedException();
            public HttpResponse Post(HttpRequest request) => throw new NotImplementedException();
            public HttpResponse<T> Post<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
            public System.Threading.Tasks.Task<HttpResponse> ExecuteAsync(HttpRequest request) => throw new NotImplementedException();
            public System.Threading.Tasks.Task DownloadFileAsync(string url, string fileName, string userAgent = null) => throw new NotImplementedException();
            public System.Threading.Tasks.Task<HttpResponse> GetAsync(HttpRequest request) => throw new NotImplementedException();
            public System.Threading.Tasks.Task<HttpResponse<T>> GetAsync<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
            public System.Threading.Tasks.Task<HttpResponse> HeadAsync(HttpRequest request) => throw new NotImplementedException();
            public System.Threading.Tasks.Task<HttpResponse> PostAsync(HttpRequest request) => throw new NotImplementedException();
            public System.Threading.Tasks.Task<HttpResponse<T>> PostAsync<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
        }

        private static string BuildReviewListXml(int startId, int count, int total, int endAttribute)
        {
            var reviews = string.Join("\n", Enumerable.Range(startId, count).Select(id => $@"
    <review>
      <book>
        <id>{id}</id>
        <title>Book {id}</title>
        <title_without_series>Book {id}</title_without_series>
        <work><id>{100000 + id}</id></work>
        <authors>
          <author><id>{200000 + id}</id><name>Author {id}</name></author>
        </authors>
      </book>
      <date_added>Mon, 01 Jan 2024 00:00:00 +0000</date_added>
    </review>"));

            return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<GoodreadsResponse>
  <reviews start=""1"" end=""{endAttribute}"" total=""{total}"">
{reviews}
  </reviews>
</GoodreadsResponse>";
        }

        private static string BuildShelfListXml(IEnumerable<string> names)
        {
            var shelves = string.Join("\n", names.Select(name => $"    <user_shelf><name>{name}</name></user_shelf>"));
            return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<GoodreadsResponse>
  <shelves>
{shelves}
  </shelves>
</GoodreadsResponse>";
        }

        [Test]
        public void should_fetch_books_from_public_shelf()
        {
            const string shelfListXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<GoodreadsResponse>
  <shelves>
    <user_shelf><name>to-read</name></user_shelf>
  </shelves>
</GoodreadsResponse>";

            const string reviewListXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<GoodreadsResponse>
  <reviews start=""1"" end=""1"" total=""1"">
    <review>
      <book>
        <id>456</id>
        <title> Test Book </title>
        <title_without_series> Test Book </title_without_series>
        <isbn13>9780000000001</isbn13>
        <kindle_asin>B00TEST123</kindle_asin>
        <work><id>123</id></work>
        <authors>
          <author><id>789</id><name> Test Author </name></author>
        </authors>
      </book>
      <date_added>Mon, 01 Jan 2024 00:00:00 +0000</date_added>
    </review>
  </reviews>
</GoodreadsResponse>";

            var httpClient = new StubHttpClient(shelfListXml: shelfListXml, reviewListPage1Xml: reviewListXml);
            var settings = new GoodreadsBookshelfImportListSettings
            {
                UserId = "https://www.goodreads.com/user/show/12345678-test-user",
                BookshelfIds = new[] { "to-read" }
            };

            var definition = new ImportListDefinition
            {
                Id = 1,
                Name = "Goodreads Bookshelves",
                Settings = settings
            };

            var rootFolderService = new StubRootFolderService();

            var importList = new GoodreadsBookshelf(
                importListStatusService: new StubImportListStatusService(),
                configService: null,
                parsingService: null,
                httpClient: httpClient,
                qualityProfileService: new Lazy<IQualityProfileService>(() => new StubQualityProfileService()),
                metadataProfileService: new Lazy<IMetadataProfileService>(() => new StubMetadataProfileService()),
                tagService: new Lazy<ITagService>(() => new StubTagService()),
                rootFolderService: rootFolderService,
                rootFolderSettingsResolver: new StubRootFolderSettingsResolver(),
                logger: LogManager.GetCurrentClassLogger())
            {
                Definition = definition
            };

            var items = importList.Fetch();

            Assert.That(items, Has.Count.EqualTo(1));

            var item = items.Single();
            Assert.That(item.ImportListId, Is.EqualTo(1));
            Assert.That(item.ImportList, Is.EqualTo("Goodreads Bookshelves"));

            Assert.That(item.Author, Is.EqualTo("Test Author"));
            Assert.That(item.AuthorGoodreadsId, Is.EqualTo("gr:789"));
            Assert.That(item.Book, Is.EqualTo("Test Book"));
            Assert.That(item.BookGoodreadsId, Is.EqualTo("gr:123"));
            Assert.That(item.EditionGoodreadsId, Is.EqualTo("gr:456"));
            Assert.That(item.Isbn13, Is.EqualTo("9780000000001"));
            Assert.That(item.Asin, Is.EqualTo("B00TEST123"));
            Assert.That(item.ReleaseDate, Is.Not.EqualTo(default(DateTime)));

            var requestedUrls = httpClient.Requests.Select(r => r.Url.FullUri).ToList();
            Assert.That(requestedUrls.Any(u => u.Contains("/shelf/list.xml", StringComparison.OrdinalIgnoreCase)), Is.True);
            Assert.That(requestedUrls.Any(u => u.Contains("/review/list.xml", StringComparison.OrdinalIgnoreCase)), Is.True);
            Assert.That(requestedUrls.Any(u => u.Contains("shelf=to-read", StringComparison.OrdinalIgnoreCase)), Is.True);
            Assert.That(requestedUrls.Any(u => u.Contains("key=", StringComparison.OrdinalIgnoreCase)), Is.True);
            Assert.That(requestedUrls.Any(u => u.Contains("/review/list_rss/12345678", StringComparison.OrdinalIgnoreCase)), Is.False);

            // Verify no OAuth Authorization headers are sent (public API key only).
            Assert.That(httpClient.Requests.All(r => !r.Headers.ContainsKey("Authorization")), Is.True);
        }

        [Test]
        public void should_continue_review_list_pagination_by_counting_reviews_not_end_attribute()
        {
            const string shelfListXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<GoodreadsResponse>
  <shelves>
    <user_shelf><name>read</name></user_shelf>
  </shelves>
</GoodreadsResponse>";

            var page1Xml = BuildReviewListXml(startId: 1, count: 200, total: 201, endAttribute: 100);
            var page2Xml = BuildReviewListXml(startId: 201, count: 1, total: 201, endAttribute: 201);
            var httpClient = new StubHttpClient(shelfListXml: shelfListXml, reviewListPage1Xml: page1Xml, reviewListPage2Xml: page2Xml);
            var settings = new GoodreadsBookshelfImportListSettings
            {
                UserId = "12345678",
                BookshelfIds = new[] { "read" }
            };

            var importList = new GoodreadsBookshelf(
                importListStatusService: new StubImportListStatusService(),
                configService: null,
                parsingService: null,
                httpClient: httpClient,
                qualityProfileService: new Lazy<IQualityProfileService>(() => new StubQualityProfileService()),
                metadataProfileService: new Lazy<IMetadataProfileService>(() => new StubMetadataProfileService()),
                tagService: new Lazy<ITagService>(() => new StubTagService()),
                rootFolderService: new StubRootFolderService(),
                rootFolderSettingsResolver: new StubRootFolderSettingsResolver(),
                logger: LogManager.GetCurrentClassLogger())
            {
                Definition = new ImportListDefinition
                {
                    Id = 1,
                    Name = "Goodreads Bookshelves",
                    Settings = settings
                }
            };

            var items = importList.Fetch();

            Assert.That(items, Has.Count.EqualTo(201));
            Assert.That(items.Last().EditionGoodreadsId, Is.EqualTo("gr:201"));
            Assert.That(httpClient.Requests.Any(r => r.Url.FullUri.Contains("/review/list.xml", StringComparison.OrdinalIgnoreCase) &&
                                                     r.Url.FullUri.Contains("page=2", StringComparison.OrdinalIgnoreCase)), Is.True);
        }

        [Test]
        public void should_keep_fetched_review_list_page_when_later_page_fails()
        {
            const string shelfListXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<GoodreadsResponse>
  <shelves>
    <user_shelf><name>read</name></user_shelf>
  </shelves>
</GoodreadsResponse>";

            var page1Xml = BuildReviewListXml(startId: 1, count: 200, total: 201, endAttribute: 200);
            var httpClient = new StubHttpClient(
                shelfListXml: shelfListXml,
                reviewListPage1Xml: page1Xml,
                rssPage1Xml: @"<?xml version=""1.0"" encoding=""UTF-8""?><rss version=""2.0""><channel><item><title>RSS Fallback</title><author_name>RSS Author</author_name><book_id>999</book_id></item></channel></rss>");
            var settings = new GoodreadsBookshelfImportListSettings
            {
                UserId = "12345678",
                BookshelfIds = new[] { "read" }
            };

            var importList = new GoodreadsBookshelf(
                importListStatusService: new StubImportListStatusService(),
                configService: null,
                parsingService: null,
                httpClient: httpClient,
                qualityProfileService: new Lazy<IQualityProfileService>(() => new StubQualityProfileService()),
                metadataProfileService: new Lazy<IMetadataProfileService>(() => new StubMetadataProfileService()),
                tagService: new Lazy<ITagService>(() => new StubTagService()),
                rootFolderService: new StubRootFolderService(),
                rootFolderSettingsResolver: new StubRootFolderSettingsResolver(),
                logger: LogManager.GetCurrentClassLogger())
            {
                Definition = new ImportListDefinition
                {
                    Id = 1,
                    Name = "Goodreads Bookshelves",
                    Settings = settings
                }
            };

            var items = importList.Fetch();

            Assert.That(items, Has.Count.EqualTo(200));
            Assert.That(items.All(item => item.AuthorGoodreadsId?.StartsWith("gr:", StringComparison.Ordinal) == true), Is.True);
            Assert.That(items.Any(item => item.Book == "RSS Fallback"), Is.False);
            Assert.That(httpClient.Requests.Any(r => r.Url.FullUri.Contains("/review/list.xml", StringComparison.OrdinalIgnoreCase) &&
                                                     r.Url.FullUri.Contains("page=2", StringComparison.OrdinalIgnoreCase)), Is.True);
            Assert.That(httpClient.Requests.Any(r => r.Url.FullUri.Contains("/review/list_rss/", StringComparison.OrdinalIgnoreCase)), Is.False);
        }

        [Test]
        public void should_fall_back_to_rss_when_review_list_page_one_fails()
        {
            const string shelfListXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<GoodreadsResponse>
  <shelves>
    <user_shelf><name>read</name></user_shelf>
  </shelves>
</GoodreadsResponse>";

            const string page1Rss = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<rss version=""2.0"">
  <channel>
    <title>Test User: read</title>
    <item>
      <title> RSS Fallback </title>
      <author_name> RSS Author </author_name>
      <book_id>999</book_id>
    </item>
  </channel>
</rss>";

            var httpClient = new StubHttpClient(rssPage1Xml: page1Rss, shelfListXml: shelfListXml);
            var settings = new GoodreadsBookshelfImportListSettings
            {
                UserId = "12345678",
                BookshelfIds = new[] { "read" },
                ImportLimit = 1
            };

            var importList = new GoodreadsBookshelf(
                importListStatusService: new StubImportListStatusService(),
                configService: null,
                parsingService: null,
                httpClient: httpClient,
                qualityProfileService: new Lazy<IQualityProfileService>(() => new StubQualityProfileService()),
                metadataProfileService: new Lazy<IMetadataProfileService>(() => new StubMetadataProfileService()),
                tagService: new Lazy<ITagService>(() => new StubTagService()),
                rootFolderService: new StubRootFolderService(),
                rootFolderSettingsResolver: new StubRootFolderSettingsResolver(),
                logger: LogManager.GetCurrentClassLogger())
            {
                Definition = new ImportListDefinition
                {
                    Id = 1,
                    Name = "Goodreads Bookshelves",
                    Settings = settings
                }
            };

            var items = importList.Fetch();

            Assert.That(items, Has.Count.EqualTo(1));
            Assert.That(items.Single().Book, Is.EqualTo("RSS Fallback"));
            Assert.That(items.Single().AuthorGoodreadsId, Is.Null);
            Assert.That(httpClient.Requests.Any(r => r.Url.FullUri.Contains("/review/list.xml", StringComparison.OrdinalIgnoreCase)), Is.True);
            Assert.That(httpClient.Requests.Any(r => r.Url.FullUri.Contains("/review/list_rss/", StringComparison.OrdinalIgnoreCase)), Is.True);
        }

        [Test]
        public void should_stop_fetching_bookshelf_when_import_limit_is_reached()
        {
            const string page1Rss = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<rss version=""2.0"">
  <channel>
    <title>Test User: to-read</title>
    <item>
      <title> First Book </title>
      <author_name> First Author </author_name>
      <book_id>101</book_id>
    </item>
    <item>
      <title> Second Book </title>
      <author_name> Second Author </author_name>
      <book_id>102</book_id>
    </item>
  </channel>
</rss>";

            const string page2Rss = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<rss version=""2.0"">
  <channel>
    <title>Test User: to-read</title>
    <item>
      <title> Third Book </title>
      <author_name> Third Author </author_name>
      <book_id>103</book_id>
    </item>
  </channel>
</rss>";

            var httpClient = new StubHttpClient(page1Rss, page2Rss);
            var settings = new GoodreadsBookshelfImportListSettings
            {
                UserId = "12345678",
                BookshelfIds = new[] { "to-read" },
                ImportLimit = 1
            };

            var importList = new GoodreadsBookshelf(
                importListStatusService: new StubImportListStatusService(),
                configService: null,
                parsingService: null,
                httpClient: httpClient,
                qualityProfileService: new Lazy<IQualityProfileService>(() => new StubQualityProfileService()),
                metadataProfileService: new Lazy<IMetadataProfileService>(() => new StubMetadataProfileService()),
                tagService: new Lazy<ITagService>(() => new StubTagService()),
                rootFolderService: new StubRootFolderService(),
                rootFolderSettingsResolver: new StubRootFolderSettingsResolver(),
                logger: LogManager.GetCurrentClassLogger())
            {
                Definition = new ImportListDefinition
                {
                    Id = 1,
                    Name = "Goodreads Bookshelves",
                    Settings = settings
                }
            };

            var items = importList.Fetch();

            Assert.That(items, Has.Count.EqualTo(1));
            Assert.That(items.Single().EditionGoodreadsId, Is.EqualTo("gr:101"));
            Assert.That(httpClient.Requests.Any(r => r.Url.FullUri.Contains("&page=2", StringComparison.OrdinalIgnoreCase)), Is.False);
        }

        [Test]
        public void should_accept_shelf_name_with_spaces_when_rss_returns_slug()
        {
            const string page1Rss = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<rss version=""2.0"">
  <channel>
    <title>Test User: to-read</title>
    <item>
      <title> Test Book </title>
      <author_name> Test Author </author_name>
      <book_id>456</book_id>
      <pubDate>Mon, 01 Jan 2024 00:00:00 +0000</pubDate>
    </item>
  </channel>
</rss>";

            const string page2Rss = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<rss version=""2.0"">
  <channel>
    <title>Test User: to-read</title>
  </channel>
</rss>";

            var httpClient = new StubHttpClient(page1Rss, page2Rss);
            var settings = new GoodreadsBookshelfImportListSettings
            {
                UserId = "12345678",
                BookshelfIds = new[] { "to read" }
            };

            var definition = new ImportListDefinition
            {
                Id = 1,
                Name = "Goodreads Bookshelves",
                Settings = settings
            };

            var importList = new GoodreadsBookshelf(
                importListStatusService: new StubImportListStatusService(),
                configService: null,
                parsingService: null,
                httpClient: httpClient,
                qualityProfileService: new Lazy<IQualityProfileService>(() => new StubQualityProfileService()),
                metadataProfileService: new Lazy<IMetadataProfileService>(() => new StubMetadataProfileService()),
                tagService: new Lazy<ITagService>(() => new StubTagService()),
                rootFolderService: new StubRootFolderService(),
                rootFolderSettingsResolver: new StubRootFolderSettingsResolver(),
                logger: LogManager.GetCurrentClassLogger())
            {
                Definition = definition
            };

            var items = importList.Fetch();
            Assert.That(items, Has.Count.EqualTo(1));

            var requestedUrls = httpClient.Requests.Select(r => r.Url.FullUri).ToList();
            Assert.That(requestedUrls.Any(u => u.Contains("shelf=to%20read", StringComparison.OrdinalIgnoreCase)), Is.True);
        }

        [Test]
        public void should_refuse_unknown_shelf_when_api_validation_succeeds()
        {
            const string shelfListXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<GoodreadsResponse>
  <shelves>
    <user_shelf><name>to-read</name></user_shelf>
    <user_shelf><name>read</name></user_shelf>
  </shelves>
</GoodreadsResponse>";

            var httpClient = new StubHttpClient(shelfListXml: shelfListXml);
            var settings = new GoodreadsBookshelfImportListSettings
            {
                UserId = "12345678",
                BookshelfIds = new[] { "definitely-not-a-real-shelf" }
            };

            var importList = new GoodreadsBookshelf(
                importListStatusService: new StubImportListStatusService(),
                configService: null,
                parsingService: null,
                httpClient: httpClient,
                qualityProfileService: new Lazy<IQualityProfileService>(() => new StubQualityProfileService()),
                metadataProfileService: new Lazy<IMetadataProfileService>(() => new StubMetadataProfileService()),
                tagService: new Lazy<ITagService>(() => new StubTagService()),
                rootFolderService: new StubRootFolderService(),
                rootFolderSettingsResolver: new StubRootFolderSettingsResolver(),
                logger: LogManager.GetCurrentClassLogger())
            {
                Definition = new ImportListDefinition
                {
                    Id = 1,
                    Name = "Goodreads Bookshelves",
                    Settings = settings
                }
            };

            var items = importList.Fetch();

            Assert.That(items, Is.Empty);
            Assert.That(httpClient.Requests.Any(r => r.Url.FullUri.Contains("/review/list.xml", StringComparison.OrdinalIgnoreCase)), Is.False);
            Assert.That(httpClient.Requests.Any(r => r.Url.FullUri.Contains("/review/list_rss/", StringComparison.OrdinalIgnoreCase)), Is.False);
        }

        [Test]
        public void should_decode_plus_as_space_when_extracting_bookshelves()
        {
            const string profileHtml = @"<html>
	<body>
	  <a href=""/review/list/12345678?utf8=%E2%9C%93&shelf=to+read"">to read</a>
	  <a href=""/review/list/12345678?shelf=currently-reading"">currently-reading</a>
	</body>
	</html>";

            var httpClient = new StubHttpClientForShelves(profileHtml);
            var settings = new GoodreadsBookshelfImportListSettings
            {
                UserId = "12345678",
                BookshelfIds = new[] { "to-read" }
            };

            var definition = new ImportListDefinition
            {
                Id = 1,
                Name = "Goodreads Bookshelves",
                Settings = settings
            };

            var importList = new GoodreadsBookshelf(
                importListStatusService: new StubImportListStatusService(),
                configService: null,
                parsingService: null,
                httpClient: httpClient,
                qualityProfileService: new Lazy<IQualityProfileService>(() => new StubQualityProfileService()),
                metadataProfileService: new Lazy<IMetadataProfileService>(() => new StubMetadataProfileService()),
                tagService: new Lazy<ITagService>(() => new StubTagService()),
                rootFolderService: new StubRootFolderService(),
                rootFolderSettingsResolver: new StubRootFolderSettingsResolver(),
                logger: LogManager.GetCurrentClassLogger())
            {
                Definition = definition
            };

            var actionResult = importList.RequestAction("getBookshelves", new Dictionary<string, string> { { "name", "bookshelfIds" } });
            var json = actionResult.ToJson();
            var parsed = JObject.Parse(json);

            var shelves = parsed["options"]?["shelves"]?.Select(s => (string)s["id"]).ToList() ?? new List<string>();

            Assert.That(shelves, Does.Contain("to read"));
            Assert.That(shelves, Does.Contain("currently-reading"));
            Assert.That(shelves, Does.Not.Contain("to+read"));
        }

        [Test]
        public void should_fetch_second_shelf_list_page_when_first_page_is_full()
        {
            var page1 = BuildShelfListXml(Enumerable.Range(1, 100).Select(i => $"shelf-{i:D3}"));
            var page2 = BuildShelfListXml(new[] { "shelf-101" });
            var httpClient = new StubHttpClient(shelfListXml: page1, shelfListPage2Xml: page2);
            var settings = new GoodreadsBookshelfImportListSettings
            {
                UserId = "12345678",
                BookshelfIds = new[] { "shelf-001" }
            };

            var definition = new ImportListDefinition
            {
                Id = 1,
                Name = "Goodreads Bookshelves",
                Settings = settings
            };

            var importList = new GoodreadsBookshelf(
                importListStatusService: new StubImportListStatusService(),
                configService: null,
                parsingService: null,
                httpClient: httpClient,
                qualityProfileService: new Lazy<IQualityProfileService>(() => new StubQualityProfileService()),
                metadataProfileService: new Lazy<IMetadataProfileService>(() => new StubMetadataProfileService()),
                tagService: new Lazy<ITagService>(() => new StubTagService()),
                rootFolderService: new StubRootFolderService(),
                rootFolderSettingsResolver: new StubRootFolderSettingsResolver(),
                logger: LogManager.GetCurrentClassLogger())
            {
                Definition = definition
            };

            var actionResult = importList.RequestAction("getBookshelves", new Dictionary<string, string> { { "name", "bookshelfIds" } });
            var json = actionResult.ToJson();
            var parsed = JObject.Parse(json);

            var shelves = parsed["options"]?["shelves"]?.Select(s => (string)s["id"]).ToList() ?? new List<string>();

            Assert.That(shelves, Has.Count.EqualTo(101));
            Assert.That(shelves, Does.Contain("shelf-101"));
            Assert.That(httpClient.Requests.Any(r => r.Url.FullUri.Contains("/shelf/list.xml", StringComparison.OrdinalIgnoreCase) &&
                                                     r.Url.FullUri.Contains("page=2", StringComparison.OrdinalIgnoreCase)), Is.True);
            Assert.That(httpClient.Requests.All(r => !r.Url.FullUri.Contains("/shelf/list.xml", StringComparison.OrdinalIgnoreCase) ||
                                                    r.Url.FullUri.Contains("per_page=100", StringComparison.OrdinalIgnoreCase)), Is.True);
        }

        [Test]
        public void should_ignore_non_shelf_links_when_profile_contains_shelf_list_items()
        {
            const string profileHtml = @"<html>
<body>
  <a class=""actionLinkLite userShowPageShelfListItem"" href=""/review/list/12345678?shelf=to-read"">to-read</a>
  <a class=""actionLinkLite userShowPageShelfListItem"" href=""/review/list/12345678?shelf=currently-reading"">currently-reading</a>
  <a class=""actionLinkLite userShowPageShelfListItem"" href=""/review/list/12345678?shelf=did-not-finish"">did-not-finish</a>
  <a href=""/some/other/page?shelf=book"">not a shelf</a>
</body>
</html>";

            var httpClient = new StubHttpClientForShelves(profileHtml);
            var settings = new GoodreadsBookshelfImportListSettings
            {
                UserId = "12345678",
                BookshelfIds = new[] { "to-read" }
            };

            var definition = new ImportListDefinition
            {
                Id = 1,
                Name = "Goodreads Bookshelves",
                Settings = settings
            };

            var importList = new GoodreadsBookshelf(
                importListStatusService: new StubImportListStatusService(),
                configService: null,
                parsingService: null,
                httpClient: httpClient,
                qualityProfileService: new Lazy<IQualityProfileService>(() => new StubQualityProfileService()),
                metadataProfileService: new Lazy<IMetadataProfileService>(() => new StubMetadataProfileService()),
                tagService: new Lazy<ITagService>(() => new StubTagService()),
                rootFolderService: new StubRootFolderService(),
                rootFolderSettingsResolver: new StubRootFolderSettingsResolver(),
                logger: LogManager.GetCurrentClassLogger())
            {
                Definition = definition
            };

            var actionResult = importList.RequestAction("getBookshelves", new Dictionary<string, string> { { "name", "bookshelfIds" } });
            var json = actionResult.ToJson();
            var parsed = JObject.Parse(json);

            var shelves = parsed["options"]?["shelves"]?.Select(s => (string)s["id"]).ToList() ?? new List<string>();

            Assert.That(shelves, Does.Contain("to-read"));
            Assert.That(shelves, Does.Contain("currently-reading"));
            Assert.That(shelves, Does.Contain("did-not-finish"));
            Assert.That(shelves, Does.Not.Contain("book"));
            Assert.That(shelves, Does.Not.Contain("did-not-fini"));
        }

        [Test]
        public void should_not_truncate_s_in_shelf_slug_when_profile_has_no_shelf_list_items()
        {
            const string profileHtml = @"<html>
\t<body>
\t  <a href=""/review/list/12345678?shelf=did-not-finish"">did-not-finish</a>
\t  <a href=""/review/list/12345678?shelf=currently-reading"">currently-reading</a>
\t</body>
\t</html>";

            var httpClient = new StubHttpClientForShelves(profileHtml);
            var settings = new GoodreadsBookshelfImportListSettings
            {
                UserId = "12345678",
                BookshelfIds = new[] { "to-read" }
            };

            var definition = new ImportListDefinition
            {
                Id = 1,
                Name = "Goodreads Bookshelves",
                Settings = settings
            };

            var importList = new GoodreadsBookshelf(
                importListStatusService: new StubImportListStatusService(),
                configService: null,
                parsingService: null,
                httpClient: httpClient,
                qualityProfileService: new Lazy<IQualityProfileService>(() => new StubQualityProfileService()),
                metadataProfileService: new Lazy<IMetadataProfileService>(() => new StubMetadataProfileService()),
                tagService: new Lazy<ITagService>(() => new StubTagService()),
                rootFolderService: new StubRootFolderService(),
                rootFolderSettingsResolver: new StubRootFolderSettingsResolver(),
                logger: LogManager.GetCurrentClassLogger())
            {
                Definition = definition
            };

            var actionResult = importList.RequestAction("getBookshelves", new Dictionary<string, string> { { "name", "bookshelfIds" } });
            var json = actionResult.ToJson();
            var parsed = JObject.Parse(json);

            var shelves = parsed["options"]?["shelves"]?.Select(s => (string)s["id"]).ToList() ?? new List<string>();

            Assert.That(shelves, Does.Contain("did-not-finish"));
            Assert.That(shelves, Does.Not.Contain("did-not-fini"));
        }

        [Test]
        public void should_return_empty_for_shelf_with_no_books()
        {
            const string emptyRssPage1 = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<rss version=""2.0"">
  <channel>
    <title>Test User: to-read</title>
  </channel>
</rss>";

            const string emptyRssPage2 = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<rss version=""2.0"">
  <channel>
    <title>Test User: to-read</title>
  </channel>
</rss>";

            var httpClient = new StubHttpClient(emptyRssPage1, emptyRssPage2);
            var settings = new GoodreadsBookshelfImportListSettings
            {
                UserId = "12345678",
                BookshelfIds = new[] { "to-read" }
            };

            var definition = new ImportListDefinition
            {
                Id = 2,
                Name = "Goodreads Bookshelves",
                Settings = settings
            };

            var rootFolderService = new StubRootFolderService();

            var importList = new GoodreadsBookshelf(
                importListStatusService: new StubImportListStatusService(),
                configService: null,
                parsingService: null,
                httpClient: httpClient,
                qualityProfileService: new Lazy<IQualityProfileService>(() => new StubQualityProfileService()),
                metadataProfileService: new Lazy<IMetadataProfileService>(() => new StubMetadataProfileService()),
                tagService: new Lazy<ITagService>(() => new StubTagService()),
                rootFolderService: rootFolderService,
                rootFolderSettingsResolver: new StubRootFolderSettingsResolver(),
                logger: LogManager.GetCurrentClassLogger())
            {
                Definition = definition
            };

            var items = importList.Fetch();

            Assert.That(items, Has.Count.EqualTo(0));
        }

        [Test]
        public void settings_should_default_to_monitor_both_media_types()
        {
            var settings = new GoodreadsBookshelfImportListSettings();

            Assert.That(settings.MonitorAudiobooks, Is.True);
            Assert.That(settings.MonitorEbooks, Is.True);
            Assert.That(settings.ImportLimit, Is.EqualTo(0));
        }

        [Test]
        public void settings_should_require_at_least_one_media_type()
        {
            var settings = new GoodreadsBookshelfImportListSettings
            {
                UserId = "12345678",
                BookshelfIds = new[] { "to-read" },
                MonitorAudiobooks = false,
                MonitorEbooks = false
            };

            var result = settings.Validate();

            Assert.That(result.IsValid, Is.False);
        }

        [Test]
        public void settings_should_reject_negative_import_limit()
        {
            var settings = new GoodreadsBookshelfImportListSettings
            {
                UserId = "12345678",
                BookshelfIds = new[] { "to-read" },
                ImportLimit = -1
            };

            var result = settings.Validate();

            Assert.That(result.IsValid, Is.False);
        }
    }
}
