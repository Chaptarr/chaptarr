using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Chaptarr.Http.Authentication;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Http
{
    [TestFixture]
    public class SeerrApiKeyAccessPolicyFixture
    {
        private const string ProxyHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        [TestCase("GET", "/MediaCoverProxy/" + ProxyHash + "/cover.jpg", true)]
        [TestCase("GET", "/MediaCoverProxy/" + ProxyHash + "/poster.webp", true)]
        [TestCase("POST", "/MediaCoverProxy/" + ProxyHash + "/cover.jpg", false)]
        [TestCase("GET", "/MediaCoverProxy/short/cover.jpg", false)]
        [TestCase("GET", "/MediaCoverProxy/" + ProxyHash + "/config.xml", false)]
        [TestCase("GET", "/MediaCoverProxy/" + ProxyHash + "/cover.svg", false)]
        [TestCase("GET", "/MediaCoverProxy/" + ProxyHash + "/../cover.jpg", false)]
        [TestCase("GET", "/MediaCoverProxy/" + ProxyHash + "/cover.jpg/other", false)]
        [TestCase("GET", "/api/v1/system/capabilities", true)]
        [TestCase("GET", "/api/v1/system/health", false)]
        [TestCase("GET", "/api/v1/book/paged", true)]
        [TestCase("GET", "/api/v1/book/21", true)]
        [TestCase("GET", "/api/v1/book/21/editions/catalog", true)]
        [TestCase("GET", "/readarr/hc/ebook/api/v1/book/21/editions/catalog", true)]
        [TestCase("GET", "/api/v1/book/21/editions/catalog/other", false)]
        [TestCase("GET", "/api/v1/command/21", true)]
        [TestCase("GET", "/api/v1/MediaCover/21/cover.jpg", true)]
        [TestCase("GET", "/api/v1/MediaCover/author/21/poster.webp", true)]
        [TestCase("GET", "/MediaCover/21/cover.jpg", true)]
        [TestCase("GET", "/MediaCover/author/21/poster.jpg", true)]
        [TestCase("GET", "/MediaCover/Books/21/cover.jpg", true)]
        [TestCase("GET", "/api/v1/MediaCover/book/21/cover.jpg", true)]
        [TestCase("GET", "/MediaCover/Books/0/cover.jpg", false)]
        [TestCase("GET", "/MediaCover/Books/21/config.xml", false)]
        [TestCase("GET", "/MediaCover/Books/21/../config.xml", false)]
        [TestCase("POST", "/MediaCover/Books/21/cover.jpg", false)]
        [TestCase("GET", "/readarr/hc/ebook/api/v1/book/paged", true)]
        [TestCase("GET", "/readarr/gr/audiobook/api/v1/book/21", true)]
        [TestCase("POST", "/api/v1/book", true)]
        [TestCase("POST", "/readarr/hc/audiobook/api/v1/book", true)]
        [TestCase("PUT", "/api/v1/book/21", true)]
        [TestCase("PUT", "/readarr/gr/ebook/api/v1/book/21", true)]
        [TestCase("DELETE", "/api/v1/pendingauthorimport/21", true)]
        [TestCase("DELETE", "/readarr/hc/audiobook/api/v1/pendingauthorimport/21", true)]
        [TestCase("GET", "/api/v1/config/host", false)]
        [TestCase("GET", "/readarr/hc/ebook/api/v1/config/host", false)]
        [TestCase("GET", "/api/v1/config/hardcover", false)]
        [TestCase("GET", "/api/v1/config/development", false)]
        [TestCase("GET", "/readarr/gr/audiobook/api/v1/config/development", false)]
        [TestCase("GET", "/api/v1/history", false)]
        [TestCase("GET", "/api/v1/bookfile", false)]
        [TestCase("POST", "/api/v1/command", false)]
        [TestCase("PUT", "/api/v1/author/21", false)]
        [TestCase("DELETE", "/api/v1/book/21", false)]
        [TestCase("GET", "/api/v1/book/21/editions", false)]
        [TestCase("GET", "/api/v1/series/narrators/matrix", false)]
        [TestCase("GET", "/api/v1/series/21/narrators/matrix", false)]
        [TestCase("GET", "/api/v1/bookish", false)]
        [TestCase("GET", "/api/v1/command/0", false)]
        [TestCase("POST", "/api/v1/author", false)]
        [TestCase("GET", "/readarr/other/ebook/api/v1/book", false)]
        [TestCase("GET", "/api/v1/MediaCover/../config.xml", false)]
        public async Task should_allow_only_book_service_routes(string method, string path, bool expected)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = method;
            context.Request.Path = path;

            var allowed = await SeerrApiKeyAccessPolicy.IsAllowedAsync(context.Request);

            Assert.That(allowed, Is.EqualTo(expected));
        }

        [TestCase("/history", "?bookId=21", true)]
        [TestCase("/bookfile", "?bookId=21", true)]
        [TestCase("/history", "?bookId=0", false)]
        [TestCase("/bookfile", "?bookId=invalid", false)]
        [TestCase("/history", "?bookId=21&bookId=22", false)]
        [TestCase("/readarr/hc/ebook/api/v1/bookfile", "?bookId=21", true)]
        public async Task should_scope_book_files_and_history_to_one_positive_book_id(
            string route,
            string queryString,
            bool expected)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = "GET";
            context.Request.Path = route.StartsWith("/readarr/", System.StringComparison.OrdinalIgnoreCase)
                ? route
                : "/api/v1" + route;
            context.Request.QueryString = new QueryString(queryString);

            var allowed = await SeerrApiKeyAccessPolicy.IsAllowedAsync(context.Request);

            Assert.That(allowed, Is.EqualTo(expected));
        }

        [TestCase("/api/v1/bookshelf/legacy-identities", "?entityType=Book&sourceIds=30", true)]
        [TestCase("/readarr/hc/ebook/api/v1/bookshelf/legacy-identities", "?entityType=Book&sourceIds=30", true)]
        [TestCase("/api/v1/bookshelf/legacy-identities", "?entityType=Series&sourceIds=30", false)]
        [TestCase("/api/v1/bookshelf/legacy-identities", "?entityType=Book&sourceIds=0", false)]
        [TestCase("/api/v1/bookshelf/legacy-identities", "?entityType=Book&sourceIds=30&unexpected=1", false)]
        public async Task should_allow_only_valid_legacy_identity_lookups_for_the_service_key(
            string route,
            string queryString,
            bool expected)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = "GET";
            context.Request.Path = route;
            context.Request.QueryString = new QueryString(queryString);

            var allowed = await SeerrApiKeyAccessPolicy.IsAllowedAsync(context.Request);

            Assert.That(allowed, Is.EqualTo(expected));
        }

        [Test]
        public async Task should_reject_legacy_identity_lookups_over_500_source_ids()
        {
            var context = new DefaultHttpContext();
            context.Request.Method = "GET";
            context.Request.Path = "/api/v1/bookshelf/legacy-identities";
            context.Request.QueryString = new QueryString(
                "?entityType=Book&sourceIds=" + string.Join("&sourceIds=", System.Linq.Enumerable.Range(1, 501)));

            var allowed = await SeerrApiKeyAccessPolicy.IsAllowedAsync(context.Request);

            Assert.That(allowed, Is.False);
        }

        [Test]
        public async Task should_allow_a_narrator_matrix_query_with_plus_encoded_spaces()
        {
            var context = new DefaultHttpContext();
            context.Request.Method = "GET";
            context.Request.Path = "/readarr/hc/audiobook/api/v1/series/narrators/matrix";
            context.Request.QueryString = new QueryString(
                "?title=The+Long+Series+Name&authorId=21&mediaType=audiobook");

            var allowed = await SeerrApiKeyAccessPolicy.IsAllowedAsync(context.Request);

            Assert.That(allowed, Is.True);
            Assert.That(context.Request.Query["title"].ToString(), Is.EqualTo("The Long Series Name"));
        }

        [TestCase("?authorId=21", false)]
        [TestCase("?title=Series&mediaType=audiobook", true)]
        [TestCase("?title=Series&authorId=0&mediaType=audiobook", false)]
        [TestCase("?title=Series&title=Other&mediaType=audiobook", false)]
        [TestCase("?title=Series&mediaType=ebook", false)]
        [TestCase("?title=Series&mediaType=audiobook&mediaType=ebook", false)]
        [TestCase("?title=Series&unexpected=1&mediaType=audiobook", false)]
        public async Task should_reject_invalid_narrator_matrix_query(
            string queryString,
            bool expected)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = "GET";
            context.Request.Path = "/readarr/hc/audiobook/api/v1/series/narrators/matrix";
            context.Request.QueryString = new QueryString(queryString);

            var allowed = await SeerrApiKeyAccessPolicy.IsAllowedAsync(context.Request);

            Assert.That(allowed, Is.EqualTo(expected));
        }

        [Test]
        public async Task should_allow_only_the_non_overriding_series_narrator_preference()
        {
            Assert.That(await IsAllowedSeriesNarratorPreferenceAsync(
                "/series/21/narrators/preferred",
                "{\"narrator\":\"Jane Doe\",\"applyToAllBooks\":true,\"overrideExisting\":false}"), Is.True);
            Assert.That(await IsAllowedSeriesNarratorPreferenceAsync(
                "/readarr/hc/audiobook/api/v1/series/21/narrators/preferred",
                "{\"narrator\":\"Jane Doe\",\"applyToAllBooks\":true,\"overrideExisting\":false}"), Is.True);
            Assert.That(await IsAllowedSeriesNarratorPreferenceAsync(
                "/series/21/narrators/preferred",
                "{\"narrator\":\"Jane Doe\",\"applyToAllBooks\":true,\"overrideExisting\":true}"), Is.False);
            Assert.That(await IsAllowedSeriesNarratorPreferenceAsync(
                "/series/21/narrators/preferred",
                "{\"narrator\":\"Jane Doe\",\"applyToAllBooks\":true,\"overrideExisting\":false,\"deleteFiles\":true}"), Is.False);
            Assert.That(await IsAllowedSeriesNarratorPreferenceAsync(
                "/series/0/narrators/preferred",
                "{\"narrator\":\"Jane Doe\",\"applyToAllBooks\":true,\"overrideExisting\":false}"), Is.False);
            Assert.That(await IsAllowedSeriesNarratorPreferenceAsync(
                "/series/21/narrators/preferred",
                "{\"narrator\":\"Jane Doe\",\"applyToAllBooks\":true,\"overrideExisting\":false}",
                "?mediaType=ebook"), Is.False);
        }

        [Test]
        public async Task should_allow_only_a_single_book_search_command()
        {
            Assert.That(await IsAllowedCommandAsync("{\"name\":\"BookSearch\",\"bookIds\":[42]}"), Is.True);
            Assert.That(await IsAllowedCommandAsync("{\"name\":\"ManualImport\",\"bookIds\":[42]}"), Is.False);
            Assert.That(await IsAllowedCommandAsync("{\"name\":\"BookSearch\",\"bookIds\":[42,43]}"), Is.False);
            Assert.That(await IsAllowedCommandAsync("{\"name\":\"BookSearch\",\"bookIds\":[0]}"), Is.False);
            Assert.That(await IsAllowedCommandAsync("{\"name\":\"BookSearch\",\"Name\":\"RefreshBook\",\"bookIds\":[42]}"), Is.False);
            Assert.That(await IsAllowedCommandAsync("{\"name\":\"BookSearch\",\"bookIds\":[42],\"BookIds\":[43]}"), Is.False);
        }

        private static async Task<bool> IsAllowedCommandAsync(string json)
        {
            var context = new DefaultHttpContext();
            var bytes = Encoding.UTF8.GetBytes(json);
            context.Request.Method = "POST";
            context.Request.Path = "/api/v1/command";
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = bytes.Length;
            context.Request.Body = new MemoryStream(bytes);

            return await SeerrApiKeyAccessPolicy.IsAllowedAsync(context.Request);
        }

        private static async Task<bool> IsAllowedSeriesNarratorPreferenceAsync(
            string route,
            string json,
            string queryString = "?mediaType=audiobook")
        {
            var context = new DefaultHttpContext();
            var bytes = Encoding.UTF8.GetBytes(json);
            context.Request.Method = "PUT";
            context.Request.Path = route.StartsWith("/readarr/", StringComparison.OrdinalIgnoreCase)
                ? route
                : "/api/v1" + route;
            context.Request.QueryString = new QueryString(queryString);
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = bytes.Length;
            context.Request.Body = new MemoryStream(bytes);

            return await SeerrApiKeyAccessPolicy.IsAllowedAsync(context.Request);
        }
    }
}
