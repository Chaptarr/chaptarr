using System.Linq;
using System.Net;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Newznab;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    public class NewznabRssParserFixture
    {
        [Test]
        public void should_html_decode_rss_title_and_newznab_author_and_booktitle_attributes()
        {
            const string xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<rss version=""2.0"" xmlns:newznab=""http://www.newznab.com/DTD/2010/feeds/attributes/"">
  <channel>
    <item>
      <title>First Word&amp;#039;s Trial</title>
      <guid>https://example.com/details/1</guid>
      <link>https://example.com/details/1</link>
      <pubDate>Tue, 26 May 2026 22:49:23 GMT</pubDate>
      <enclosure url=""https://example.com/download/1.nzb"" length=""123"" type=""application/x-nzb"" />
      <newznab:attr name=""author"" value=""Jane O&amp;#039;Author"" />
      <newznab:attr name=""booktitle"" value=""First Word&amp;#039;s Deluxe"" />
      <newznab:attr name=""size"" value=""123"" />
    </item>
  </channel>
</rss>";

            var request = new HttpRequest("https://example.com/api");
            var indexerRequest = new IndexerRequest(request);
            var headers = new HttpHeader { ContentType = "application/xml" };
            var response = new HttpResponse(request, headers, xml, HttpStatusCode.OK);
            var indexerResponse = new IndexerResponse(indexerRequest, response);

            var release = new NewznabRssParser().ParseResponse(indexerResponse).Single();

            Assert.That(release.Title, Is.EqualTo("First Word's Trial"));
            Assert.That(release.Author, Is.EqualTo("Jane O'Author"));
            Assert.That(release.Book, Is.EqualTo("First Word's Deluxe"));
        }
    }
}
