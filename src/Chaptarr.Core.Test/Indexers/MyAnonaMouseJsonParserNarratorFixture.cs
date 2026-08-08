using System.Collections.Generic;
using System.Linq;
using System.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    public class MyAnonaMouseJsonParserNarratorFixture
    {
        [Test]
        public void should_extract_narrator_from_narrator_info_json()
        {
            var settings = new MyAnonaMouseSettings
            {
                BaseUrl = "https://www.myanonamouse.net"
            };

            var payload = new JObject
            {
                ["data"] = new JArray
                {
                    new JObject
                    {
                        ["id"] = 1226155,
                        ["title"] = "Trailer Park Bikini Vampires 2",
                        ["dl"] = "hash",
                        ["size"] = "541.1 MiB",
                        ["seeders"] = 3,
                        ["leechers"] = 3,
                        ["added"] = "2026-03-08 17:55:01",
                        ["author_info"] = JsonConvert.SerializeObject(new Dictionary<string, string>
                        {
                            { "349274", "Virgil Knightley" }
                        }),
                        ["narrator_info"] = JsonConvert.SerializeObject(new Dictionary<string, string>
                        {
                            { "1393", "Jonathan Waters" },
                            { "47153", "Amber Hartt" }
                        }),
                        ["category"] = 108,
                        ["main_cat"] = 13,
                        ["mediatype"] = 1,
                        ["catname"] = "Audiobooks - Urban Fantasy"
                    }
                }
            };

            var request = new HttpRequest("https://example.com/api");
            var indexerRequest = new IndexerRequest(request);
            var headers = new HttpHeader { ContentType = "application/json" };
            var response = new HttpResponse(request, headers, payload.ToString(Formatting.None), HttpStatusCode.OK);
            var indexerResponse = new IndexerResponse(indexerRequest, response);

            var parser = new MyAnonaMouseJsonParser(settings);
            var releases = parser.ParseResponse(indexerResponse);

            var torrent = releases.OfType<TorrentInfo>().Single();
            Assert.That(torrent.Narrator, Is.EqualTo("Jonathan Waters, Amber Hartt"));
        }


        [Test]
        public void should_extract_all_authors_from_author_info_json()
        {
            var settings = new MyAnonaMouseSettings
            {
                BaseUrl = "https://www.myanonamouse.net"
            };

            var payload = new JObject
            {
                ["data"] = new JArray
                {
                    new JObject
                    {
                        ["id"] = 1226156,
                        ["title"] = "House Harkonnen",
                        ["dl"] = "hash",
                        ["size"] = "723.8 MiB",
                        ["seeders"] = 72,
                        ["leechers"] = 0,
                        ["added"] = "2026-03-08 17:55:01",
                        ["author_info"] = JsonConvert.SerializeObject(new Dictionary<string, string>
                        {
                            { "1", "Brian Herbert" },
                            { "2", "Kevin J Anderson" }
                        }),
                        ["category"] = 39,
                        ["main_cat"] = 13,
                        ["mediatype"] = 1,
                        ["catname"] = "Audiobooks - Sci-Fi"
                    }
                }
            };

            var request = new HttpRequest("https://example.com/api");
            var indexerRequest = new IndexerRequest(request);
            var headers = new HttpHeader { ContentType = "application/json" };
            var response = new HttpResponse(request, headers, payload.ToString(Formatting.None), HttpStatusCode.OK);
            var indexerResponse = new IndexerResponse(indexerRequest, response);

            var parser = new MyAnonaMouseJsonParser(settings);
            var releases = parser.ParseResponse(indexerResponse);

            var torrent = releases.OfType<TorrentInfo>().Single();
            Assert.That(torrent.Author, Is.EqualTo("Brian Herbert, Kevin J Anderson"));
        }

        [Test]
        public void should_html_decode_title_and_author_info()
        {
            var settings = new MyAnonaMouseSettings
            {
                BaseUrl = "https://www.myanonamouse.net"
            };

            var payload = new JObject
            {
                ["data"] = new JArray
                {
                    new JObject
                    {
                        ["id"] = 9999999,
                        ["title"] = "First Word &amp; Other O&#039;Words",
                        ["dl"] = "hash",
                        ["size"] = "276.1 MiB",
                        ["seeders"] = 10,
                        ["leechers"] = 0,
                        ["added"] = "2026-03-08 17:55:01",
                        ["author_info"] = JsonConvert.SerializeObject(new Dictionary<string, string>
                        {
                            { "1", "Jane O&#039;Author" }
                        }),
                        ["category"] = 39,
                        ["main_cat"] = 13,
                        ["mediatype"] = 1,
                        ["catname"] = "Audiobooks - Fantasy"
                    }
                }
            };

            var request = new HttpRequest("https://example.com/api");
            var indexerRequest = new IndexerRequest(request);
            var headers = new HttpHeader { ContentType = "application/json" };
            var response = new HttpResponse(request, headers, payload.ToString(Formatting.None), HttpStatusCode.OK);
            var indexerResponse = new IndexerResponse(indexerRequest, response);

            var parser = new MyAnonaMouseJsonParser(settings);
            var releases = parser.ParseResponse(indexerResponse);

            var torrent = releases.OfType<TorrentInfo>().Single();
            Assert.That(torrent.Title, Is.EqualTo("First Word & Other O'Words"));
            Assert.That(torrent.Author, Is.EqualTo("Jane O'Author"));
        }

        [Test]
        public void should_not_use_html_author_blurb_as_narrator()
        {
            var settings = new MyAnonaMouseSettings
            {
                BaseUrl = "https://www.myanonamouse.net"
            };

            var payload = new JObject
            {
                ["data"] = new JArray
                {
                    new JObject
                    {
                        ["id"] = 1226157,
                        ["title"] = "Tuesdays with Morrie",
                        ["dl"] = "hash",
                        ["size"] = "100 MiB",
                        ["seeders"] = 164,
                        ["leechers"] = 0,
                        ["added"] = "2026-03-08 17:55:01",
                        ["author_info"] = JsonConvert.SerializeObject(new Dictionary<string, string>
                        {
                            { "1", "Mitch Albom" }
                        }),
                        ["narrator_info"] = "the author Mitch Albom<br /><br /><br /> 64kbs<br /><br /><br /> This true story about the love between a spiritual mentor",
                        ["category"] = 39,
                        ["main_cat"] = 13,
                        ["mediatype"] = 1,
                        ["catname"] = "Audiobooks"
                    }
                }
            };

            var request = new HttpRequest("https://example.com/api");
            var indexerRequest = new IndexerRequest(request);
            var headers = new HttpHeader { ContentType = "application/json" };
            var response = new HttpResponse(request, headers, payload.ToString(Formatting.None), HttpStatusCode.OK);
            var indexerResponse = new IndexerResponse(indexerRequest, response);

            var parser = new MyAnonaMouseJsonParser(settings);
            var releases = parser.ParseResponse(indexerResponse);

            var torrent = releases.OfType<TorrentInfo>().Single();
            Assert.That(torrent.Narrator, Is.Null);
        }

        [Test]
        public void should_extract_narrator_from_explicit_description_label_after_html_cleanup()
        {
            var settings = new MyAnonaMouseSettings
            {
                BaseUrl = "https://www.myanonamouse.net"
            };

            var payload = new JObject
            {
                ["data"] = new JArray
                {
                    new JObject
                    {
                        ["id"] = 1226158,
                        ["title"] = "Dune",
                        ["dl"] = "hash",
                        ["size"] = "500 MiB",
                        ["seeders"] = 12,
                        ["leechers"] = 0,
                        ["added"] = "2026-03-08 17:55:01",
                        ["author_info"] = JsonConvert.SerializeObject(new Dictionary<string, string>
                        {
                            { "1", "Frank Herbert" }
                        }),
                        ["description"] = "Format: MP3<br />Narrated by Scott Brick; 64 kbps",
                        ["category"] = 39,
                        ["main_cat"] = 13,
                        ["mediatype"] = 1,
                        ["catname"] = "Audiobooks"
                    }
                }
            };

            var request = new HttpRequest("https://example.com/api");
            var indexerRequest = new IndexerRequest(request);
            var headers = new HttpHeader { ContentType = "application/json" };
            var response = new HttpResponse(request, headers, payload.ToString(Formatting.None), HttpStatusCode.OK);
            var indexerResponse = new IndexerResponse(indexerRequest, response);

            var parser = new MyAnonaMouseJsonParser(settings);
            var releases = parser.ParseResponse(indexerResponse);

            var torrent = releases.OfType<TorrentInfo>().Single();
            Assert.That(torrent.Narrator, Is.EqualTo("Scott Brick"));
        }
    }
}
