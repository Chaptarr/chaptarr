using System.Collections.Generic;
using System.Linq;
using System.Net;
using Newtonsoft.Json;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.LazyLibrarianImport;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.ImportLists
{
    [TestFixture]
    public class LazyLibrarianImportParserFixture
    {
        [TestCase("123", "https://www.goodreads.com/book/show/123-example", null, "gr-ed:123")]
        [TestCase("123", "https://hardcover.app/books/example", "hc:123", null)]
        [TestCase("OL123W", "https://openlibrary.org/works/OL123W/example", "ol:OL123W", null)]
        [TestCase("OL123W", "/works/OL123W", "ol:OL123W", null)]
        [TestCase("OL123M", "https://openlibrary.org/books/OL123M/example", null, "ol:OL123M")]
        [TestCase("volume-id", "https://books.google.com/books?id=volume-id", null, "gb:volume-id")]
        public void should_preserve_provider_identity_without_narrator_intent(string id, string link, string workId, string editionId)
        {
            var item = Parse(id, link).Single();

            Assert.Multiple(() =>
            {
                Assert.That(item.BookProviderId, Is.EqualTo(workId));
                Assert.That(item.EditionProviderId, Is.EqualTo(editionId));
                Assert.That(item.EditionHasNarration, Is.Null);
                Assert.That(item.IsGenericBookRequest, Is.EqualTo(true));
            });
        }

        [TestCase("hc:123", "hc:123", null)]
        [TestCase("gr:123", "gr:123", null)]
        [TestCase("hc-ed:123", null, "hc-ed:123")]
        [TestCase("gr-ed:123", null, "gr-ed:123")]
        [TestCase("hc:edition:123", null, "hc-ed:123")]
        [TestCase("hc:edition:123-audiobook", null, "hc-ed:123")]
        [TestCase(" HC:EDITION:123-ebook ", null, "hc-ed:123")]
        [TestCase("ol:OL123W", "ol:OL123W", null)]
        [TestCase("ol:OL123M", null, "ol:OL123M")]
        [TestCase("gb:volume-id", null, "gb:volume-id")]
        public void should_accept_explicit_provider_identity_without_a_link(string id, string workId, string editionId)
        {
            var item = Parse(id, null).Single();

            Assert.Multiple(() =>
            {
                Assert.That(item.BookProviderId, Is.EqualTo(workId));
                Assert.That(item.EditionProviderId, Is.EqualTo(editionId));
                Assert.That(item.IsGenericBookRequest, Is.EqualTo(true));
            });
        }

        [TestCase("123", null)]
        [TestCase("123", "invalid link")]
        [TestCase("123", "https://example.com/book/123")]
        [TestCase("123", "https://goodreads.com.example.com/book/show/123")]
        [TestCase("123", "https://hardcover.app.example.com/books/example")]
        [TestCase(null, "https://www.goodreads.com/book/show/123")]
        [TestCase("unknown:123", null)]
        [TestCase("hc:", null)]
        [TestCase("hc:edition:", null)]
        public void should_skip_items_whose_provider_identity_cannot_be_established(string id, string link)
        {
            Assert.That(Parse(id, link), Is.Empty);
        }

        private static IList<ImportListItemInfo> Parse(string id, string link)
        {
            var content = JsonConvert.SerializeObject(new[] { new { BookID = id, BookLink = link, BookName = "Example Book", AuthorName = "Example Author" } });
            var request = new HttpRequest("https://example.com/api");
            var response = new HttpResponse(request, new HttpHeader { ContentType = "application/json" }, content, HttpStatusCode.OK);
            return new LazyLibrarianImportParser().ParseResponse(new ImportListResponse(new ImportListRequest(request), response));
        }
    }
}
