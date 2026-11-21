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
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Qualities;
using Chaptarr.Api.V1.Indexers;

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
        public void should_preserve_description_for_known_target_narrator_matching_after_html_cleanup()
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
            Assert.That(torrent.Narrator, Is.Null);
            Assert.That(torrent.NarratorEvidenceFields, Is.EqualTo(new[] { "Format: MP3 Narrated by Scott Brick; 64 kbps" }));
            var book = new Book
            {
                MediaType = BookMediaType.Audiobook, AnyEditionOk = false,
                Editions = new List<Edition> { new Edition { ReadingFormatId = 2, ManualAdd = true, NarratorNames = new List<string> { "Scott Brick" } } }
            };
            var remote = new RemoteBook
            {
                Books = new List<Book> { book }, Release = torrent,
                ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.MP3) }
            };
            Assert.That(new DownloadDecision(remote).ToResource().Narrator, Is.EqualTo("Scott Brick"));
        }

        [Test]
        public void should_preserve_structured_series_memberships_without_treating_a_single_position_as_a_pack()
        {
            var settings = new MyAnonaMouseSettings { BaseUrl = "https://www.myanonamouse.net" };
            var payload = new JObject
            {
                ["data"] = new JArray
                {
                    new JObject
                    {
                        ["id"] = 1226159,
                        ["title"] = "Dune",
                        ["dl"] = "hash",
                        ["size"] = "500 MiB",
                        ["seeders"] = 12,
                        ["leechers"] = 0,
                        ["added"] = "2026-03-08 17:55:01",
                        ["series_info"] = "{\"12922\":[\"Dune Chronicles\",\"1\",1.0],\"28664\":[\"Dune Universe\",\"10\",10.0],\"37638\":[\"Dungeons &amp; Dragons 5th Edition\",\"\",9999.0]}",
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
            var releases = new MyAnonaMouseJsonParser(settings).ParseResponse(new IndexerResponse(indexerRequest, response));
            var torrent = releases.OfType<TorrentInfo>().Single();

            Assert.That(torrent.SeriesInfo.Select(series => (series.ProviderId, series.Name, series.Position)), Is.EquivalentTo(new[]
            {
                ("12922", "Dune Chronicles", "1"),
                ("28664", "Dune Universe", "10"),
                ("37638", "Dungeons & Dragons 5th Edition", null)
            }));
        }
        [Test]
        public void structured_cast_should_keep_every_person_without_a_count_placeholder()
        {
            var payload = new JObject
            {
                ["data"] = new JArray { new JObject
                {
                    ["id"] = 1234567, ["title"] = "A voice book", ["dl"] = "hash", ["size"] = "100 MiB",
                    ["seeders"] = 2, ["leechers"] = 0, ["added"] = "2026-03-08 17:55:01",
                    ["narrator_info"] = JsonConvert.SerializeObject(new Dictionary<string, string>
                    { ["1"] = "Anna Mueller", ["2"] = "Ben Reader", ["3"] = "Cara Reader" }),
                    ["category"] = 39, ["main_cat"] = 13, ["mediatype"] = 1, ["catname"] = "Audiobooks"
                } }
            };
            var request = new HttpRequest("https://example.com/api");
            var response = new HttpResponse(request, new HttpHeader { ContentType = "application/json" },
                payload.ToString(Formatting.None), HttpStatusCode.OK);
            var release = new MyAnonaMouseJsonParser(new MyAnonaMouseSettings { BaseUrl = "https://www.myanonamouse.net" })
                .ParseResponse(new IndexerResponse(new IndexerRequest(request), response)).Single();
            Assert.That(release.Narrator, Is.EqualTo("Anna Mueller, Ben Reader, Cara Reader"));
        }

    }
}
