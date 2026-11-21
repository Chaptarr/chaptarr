using System.Linq;
using System.Net;
using Chaptarr.Api.V1.Indexers;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    public class MyAnonaMouseCategoryFixture
    {
        internal static TorrentInfo Parse(JObject row)
        {
            var request = new HttpRequest("https://example.com/api");
            var response = new HttpResponse(request, new HttpHeader { ContentType = "application/json" },
                new JObject { ["data"] = new JArray(row) }.ToString(), HttpStatusCode.OK);
            return new MyAnonaMouseJsonParser(new MyAnonaMouseSettings { BaseUrl = "https://www.myanonamouse.net" })
                .ParseResponse(new IndexerResponse(new IndexerRequest(request), response)).Cast<TorrentInfo>().Single();
        }

        internal static TorrentInfo Report(int category, string title, string fileType)
        {
            return Parse(new JObject
            {
                ["id"] = "123", ["name"] = title, ["size"] = "600", ["seeders"] = 1, ["leechers"] = 0,
                ["main_cat"] = category, ["category"] = 108, ["filetype"] = fileType
            });
        }

        [Test]
        public void documented_mam_example_preserves_native_parent_and_genre_categories()
        {
            // Relevant fields from the provider's documented response; no download token is retained.
            var release = Parse(JObject.Parse("""
                {"id":"273200","language":"1","main_cat":"13","category":"108",
                 "catname":"Audiobooks - Urban Fantasy","size":"6324306932","numfiles":"149",
                 "name":"Love at Stake series","filetype":"m4a mp3","seeders":0,"leechers":0,
                 "author_info":"{\"8234\": \"Kerrelyn Sparks\"}"}
                """));

            Assert.That(release.MainCategory, Is.EqualTo(13));
            Assert.That(release.Categories, Is.EqualTo(new[] { 108 }));
            Assert.That(release.FileType, Is.EqualTo("m4a mp3"));
            Assert.That(QualityMediaTypeHelper.GetCategoryMediaType(release), Is.EqualTo(BookMediaType.Audiobook));
        }

        [TestCase(13, null, "Book", 13, BookMediaType.Audiobook)]
        [TestCase(13, "", "Book", 13, BookMediaType.Audiobook)]
        [TestCase(13, "unknown", "Book", 13, BookMediaType.Audiobook)]
        [TestCase(14, null, "Book [Audiobook]", 0, BookMediaType.Ebook)]
        [TestCase(14, "", "Book [Audiobook]", 0, BookMediaType.Ebook)]
        [TestCase(14, "unknown", "Book [Audiobook]", 0, BookMediaType.Ebook)]
        [TestCase(13, null, "Book [EPUB]", 3, BookMediaType.Ebook)]
        [TestCase(14, null, "Book [M4B]", 12, BookMediaType.Audiobook)]
        [TestCase(13, "epub", "Book", 3, BookMediaType.Ebook)]
        [TestCase(14, "m4b", "Book", 12, BookMediaType.Audiobook)]
        [TestCase(15, null, "Book", 0, null)]
        [TestCase(16, null, "Book", 0, null)]
        [TestCase(0, null, "Book", 0, null)]
        public void native_category_fallback_respects_explicit_formats(int category, string fileType, string title, int qualityId, BookMediaType? expected)
        {
            var release = Report(category, title, fileType);
            var media = QualityMediaTypeHelper.GetCategoryMediaType(release);
            var quality = fileType == null
                ? QualityParser.ParseQuality(title, categories: release.Categories, categoryMediaType: media)
                : QualityParser.ParseQualityFromFileType(fileType, title, 0, categoryMediaType: media);
            var resource = new DownloadDecision(new RemoteBook { Release = release, ParsedBookInfo = new() { Quality = quality } }).ToResource();

            Assert.That(quality.Quality.Id, Is.EqualTo(qualityId));
            Assert.That(QualityMediaTypeHelper.DetectMediaType(quality.Quality, release), Is.EqualTo(expected));
            Assert.That(resource.FormatType, Is.EqualTo(expected?.ToString().ToLowerInvariant()));
            Assert.That(release.Categories, Is.EqualTo(new[] { 108 }), "Native genre categories must not be rewritten into Newznab categories.");
        }
    }
}
