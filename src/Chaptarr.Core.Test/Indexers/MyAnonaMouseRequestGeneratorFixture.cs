using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Profiles.Metadata;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    public class MyAnonaMouseRequestGeneratorFixture
    {

        [Test]
        public void should_use_json_recent_requests_for_rss_style_sync()
        {
            var generator = CreateGenerator();

            var chain = generator.GetRecentRequests();

            Assert.That(chain.Tiers, Is.EqualTo(1));

            var requests = chain.GetTier(0).Single().ToList();
            Assert.That(requests, Has.Count.EqualTo(1));
            Assert.That(requests[0].HttpRequest.Url.FullUri, Does.Contain("/tor/js/loadSearchJSONbasic.php"));
            var payload = JObject.Parse(Encoding.UTF8.GetString(requests[0].HttpRequest.ContentData));
            Assert.That(payload["tor"]?["text"]?.Value<string>(), Is.Empty);
            Assert.That(payload["tor"]?["startNumber"]?.Value<string>(), Is.EqualTo("0"));
            Assert.That(payload["tor"]?["searchType"]?.Value<string>(), Is.EqualTo("active"));
            Assert.That(payload["perpage"]?.Value<string>(), Is.EqualTo("500"));
            Assert.That(payload["mediaInfo"]?.Value<string>(), Is.EqualTo("1"));
            Assert.That(payload["description"], Is.Null);
            Assert.That(payload["dlLink"], Is.Null);
            Assert.That(payload["isbn"], Is.Null);
        }

        [Test]
        public void should_use_title_only_as_fallback_tier_when_primary_query_includes_author()
        {
            var generator = CreateGenerator();
            var searchCriteria = new BookSearchCriteria
            {
                Author = new Author { Name = "Guy Hayley" },
                BookTitle = "Godblight",
                InteractiveSearch = true
            };

            var chain = generator.GetSearchRequests(searchCriteria);

            Assert.That(chain.Tiers, Is.EqualTo(2));
            Assert.That(GetRequestPayload(chain, 0), Does.Contain("\"text\":\"Godblight Guy Hayley\""));
            Assert.That(GetRequestPayload(chain, 1), Does.Contain("\"text\":\"Godblight\""));
        }

        [Test]
        public void should_add_book_number_fallback_as_last_tier_for_interactive_search()
        {
            var generator = CreateGenerator();
            var searchCriteria = new BookSearchCriteria
            {
                Author = new Author { Name = "Joe Abercrombie" },
                BookTitle = "The Trouble With Peace, Book 2",
                InteractiveSearch = true
            };

            var chain = generator.GetSearchRequests(searchCriteria);

            Assert.That(chain.Tiers, Is.EqualTo(3));
            Assert.That(GetRequestPayload(chain, 0), Does.Contain("\"text\":\"Trouble With Peace Book 2 Joe Abercrombie\""));
            Assert.That(GetRequestPayload(chain, 1), Does.Contain("\"text\":\"Trouble With Peace Book 2\""));
            Assert.That(GetRequestPayload(chain, 2), Does.Contain("\"text\":\"Trouble With Peace\""));
        }

        [Test]
        public void should_not_strip_leading_parenthetical_title_to_empty_query()
        {
            var generator = CreateGenerator();
            var searchCriteria = new BookSearchCriteria
            {
                Author = new Author { Name = "Alex Gilbert" },
                BookTitle = "(Totally not an) EVIL OVERLADY",
                Books = new List<Book>
                {
                    new Book { MediaType = BookMediaType.Ebook }
                },
                InteractiveSearch = true
            };

            var chain = generator.GetSearchRequests(searchCriteria);
            var payload = GetRequestPayloadObject(chain, 0);

            Assert.That(chain.Tiers, Is.EqualTo(2));
            Assert.That(payload["tor"]?["text"]?.Value<string>(), Is.EqualTo("Totally not an EVIL OVERLADY Alex Gilbert"));
            Assert.That(payload["tor"]?["main_cat"]?.Values<string>(), Is.EqualTo(new[] { "14" }));
            Assert.That(GetRequestPayload(chain, 1), Does.Contain("\"text\":\"Totally not an EVIL OVERLADY\""));
        }

        [Test]
        public void should_not_throw_when_book_and_author_queries_are_empty()
        {
            var generator = CreateGenerator();
            var searchCriteria = new BookSearchCriteria
            {
                Author = new Author { Name = "" },
                BookTitle = "",
                InteractiveSearch = true
            };

            var chain = generator.GetSearchRequests(searchCriteria);

            Assert.That(chain.Tiers, Is.EqualTo(1));
            Assert.That(GetRequestPayload(chain, 0), Does.Contain("\"text\":\"\""));
        }

        [Test]
        public void should_use_stripped_book_query_without_known_subtitle_extra_tiers()
        {
            var generator = CreateGenerator();
            var searchCriteria = new BookSearchCriteria
            {
                Author = new Author { Name = "Mitch Albom" },
                BookTitle = "Tuesdays with Morrie: An Old Man, a Young Man, and Life's Greatest Lesson",
                Books = new List<Book>
                {
                    new Book
                    {
                        Title = "Tuesdays with Morrie",
                        Subtitle = "An Old Man, a Young Man, and Life's Greatest Lesson",
                        Editions = new List<Edition>
                        {
                            new Edition
                            {
                                Title = "Tuesdays with Morrie: An Old Man, a Young Man, and Life's Greatest Lesson",
                                Subtitle = "An Old Man, a Young Man, and Life's Greatest Lesson",
                                Monitored = true
                            }
                        }
                    }
                }
            };

            var chain = generator.GetSearchRequests(searchCriteria);

            Assert.That(chain.Tiers, Is.EqualTo(2));
            Assert.That(GetRequestPayload(chain, 0), Does.Contain("\"text\":\"Tuesdays with Morrie Mitch Albom\""));
            Assert.That(GetRequestPayload(chain, 1), Does.Contain("\"text\":\"Tuesdays with Morrie\""));
            Assert.That(GetRequestPayload(chain, 0), Does.Not.Contain("Old Man"));
            Assert.That(GetRequestPayload(chain, 1), Does.Not.Contain("Old Man"));
        }

        [Test]
        public void should_use_documented_json_payload_and_real_headers()
        {
            var generator = CreateGenerator();
            var searchCriteria = new BookSearchCriteria
            {
                Author = new Author
                {
                    Name = "Brandon Sanderson",
                    AudiobookMetadataProfile = new MetadataProfile
                    {
                        AllowedLanguages = "eng"
                    }
                },
                Books = new List<Book>
                {
                    new Book { MediaType = BookMediaType.Audiobook }
                },
                BookTitle = "Tailored Realities"
            };

            var request = generator.GetSearchRequests(searchCriteria).GetTier(0).Single().First().HttpRequest;
            var payload = JObject.Parse(Encoding.UTF8.GetString(request.ContentData));

            Assert.That(request.Headers.GetSingleValue("User-Agent"), Does.StartWith("Chaptarr/"));
            Assert.That(request.Headers.GetSingleValue("X-Requested-With"), Is.Null);
            Assert.That(request.Headers.GetSingleValue("Referer"), Is.Null);
            Assert.That(request.Headers.Accept, Is.EqualTo("application/json"));
            Assert.That(request.Headers.ContentType, Is.EqualTo("application/json"));

            Assert.That(payload["thumbnail"], Is.Null);
            Assert.That(payload["description"]?.Value<string>(), Is.EqualTo("1"));
            Assert.That(payload["mediaInfo"]?.Value<string>(), Is.EqualTo("1"));
            Assert.That(payload["dlLink"], Is.Null);
            Assert.That(payload["isbn"], Is.Null);
            Assert.That(payload["perpage"]?.Value<string>(), Is.EqualTo("100"));
            Assert.That(payload["tor"]?["searchType"]?.Value<string>(), Is.EqualTo("active"));
            Assert.That(payload["tor"]?["srchIn"]?.Values<string>(), Is.EqualTo(new[] { "title", "author", "narrator", "series", "tags" }));
            Assert.That(payload["tor"]?["cat"]?.Values<string>(), Is.EqualTo(new[] { "0" }));
            Assert.That(payload["tor"]?["main_cat"]?.Values<string>(), Is.EqualTo(new[] { "13" }));
            Assert.That(payload["tor"]?["browse_lang"]?.Values<string>(), Is.EqualTo(new[] { "1" }));
        }

        [Test]
        public void should_map_profile_language_to_live_verified_mam_id()
        {
            var generator = CreateGenerator();
            var searchCriteria = new BookSearchCriteria
            {
                Author = new Author
                {
                    Name = "Author",
                    AudiobookMetadataProfile = new MetadataProfile
                    {
                        AllowedLanguages = "spa"
                    }
                },
                Books = new List<Book>
                {
                    new Book { MediaType = BookMediaType.Audiobook }
                },
                BookTitle = "Spanish Book"
            };

            var payload = GetRequestPayloadObject(generator.GetSearchRequests(searchCriteria), 0);

            Assert.That(payload["tor"]?["browse_lang"]?.Values<string>(), Is.EqualTo(new[] { "4" }));
            Assert.That(payload["tor"]?["main_cat"]?.Values<string>(), Is.EqualTo(new[] { "13" }));
        }

        [TestCase("fra", "36")]
        [TestCase("deu", "37")]
        [TestCase("ita", "43")]
        [TestCase("jpn", "38")]
        [TestCase("por", "34")]
        [TestCase("rus", "16")]
        [TestCase("zho", "2")]
        [TestCase("Albanian", "64")]
        [TestCase("Welsh", "65")]
        public void should_map_supported_metadata_profile_languages_to_mam_browse_ids(string language, string expectedId)
        {
            var generator = CreateGenerator();
            var criteria = CreateAudiobookCriteria(language);

            var payload = GetRequestPayloadObject(generator.GetSearchRequests(criteria), 0);

            Assert.That(payload["tor"]?["browse_lang"]?.Values<string>(), Is.EqualTo(new[] { expectedId }));
        }

        [Test]
        public void should_send_every_supported_metadata_profile_language()
        {
            var payload = GetRequestPayloadObject(
                CreateGenerator().GetSearchRequests(CreateAudiobookCriteria("eng,spa")),
                0);

            Assert.That(payload["tor"]?["browse_lang"]?.Values<string>(), Is.EquivalentTo(new[] { "1", "4" }));
        }

        [TestCase(null)]
        [TestCase("")]
        public void should_omit_language_filter_when_metadata_profile_has_no_language_policy(string allowedLanguages)
        {
            var payload = GetRequestPayloadObject(
                CreateGenerator().GetSearchRequests(CreateAudiobookCriteria(allowedLanguages)),
                0);

            Assert.That(payload["tor"]?["browse_lang"], Is.Null);
        }

        [Test]
        public void should_omit_language_filter_when_metadata_profile_is_unavailable()
        {
            var criteria = new BookSearchCriteria
            {
                Author = new Author { Name = "Author" },
                Books = new List<Book> { new Book { MediaType = BookMediaType.Audiobook } },
                BookTitle = "Book"
            };

            var payload = GetRequestPayloadObject(CreateGenerator().GetSearchRequests(criteria), 0);

            Assert.That(payload["tor"]?["browse_lang"], Is.Null);
        }

        [TestCase("eng,slk")]
        [TestCase("eng,unknown")]
        [TestCase("eng,not-a-language")]
        public void should_omit_language_filter_when_mam_cannot_represent_the_complete_profile_policy(string allowedLanguages)
        {
            var payload = GetRequestPayloadObject(
                CreateGenerator().GetSearchRequests(CreateAudiobookCriteria(allowedLanguages)),
                0);

            Assert.That(payload["tor"]?["browse_lang"], Is.Null);
        }

        [Test]
        public void should_match_the_complete_current_mam_language_dropdown()
        {
            var expected = new Dictionary<string, int>
            {
                ["English"] = 1,
                ["Afrikaans"] = 17,
                ["Albanian"] = 64,
                ["Arabic"] = 32,
                ["Bengali"] = 35,
                ["Bosnian"] = 51,
                ["Bulgarian"] = 18,
                ["Burmese"] = 6,
                ["Cantonese"] = 44,
                ["Catalan"] = 19,
                ["Chinese"] = 2,
                ["Croatian"] = 49,
                ["Czech"] = 20,
                ["Danish"] = 21,
                ["Dutch"] = 22,
                ["Estonian"] = 61,
                ["Farsi"] = 39,
                ["Finnish"] = 23,
                ["French"] = 36,
                ["German"] = 37,
                ["Greek"] = 26,
                ["Greek, Ancient"] = 59,
                ["Gujarati"] = 3,
                ["Hebrew"] = 27,
                ["Hindi"] = 8,
                ["Hungarian"] = 28,
                ["Icelandic"] = 63,
                ["Indonesian"] = 53,
                ["Irish"] = 56,
                ["Italian"] = 43,
                ["Japanese"] = 38,
                ["Javanese"] = 12,
                ["Kannada"] = 5,
                ["Korean"] = 41,
                ["Lithuanian"] = 50,
                ["Latin"] = 46,
                ["Latvian"] = 62,
                ["Malay"] = 33,
                ["Malayalam"] = 58,
                ["Manx"] = 57,
                ["Marathi"] = 9,
                ["Norwegian"] = 48,
                ["Polish"] = 45,
                ["Portuguese"] = 34,
                ["Brazilian Portuguese"] = 52,
                ["Punjabi"] = 14,
                ["Romanian"] = 30,
                ["Russian"] = 16,
                ["Scottish Gaelic"] = 24,
                ["Sanskrit"] = 60,
                ["Serbian"] = 31,
                ["Slovenian"] = 54,
                ["Spanish"] = 4,
                ["Castilian Spanish"] = 55,
                ["Swedish"] = 40,
                ["Tagalog"] = 29,
                ["Tamil"] = 11,
                ["Telugu"] = 10,
                ["Thai"] = 7,
                ["Turkish"] = 42,
                ["Ukrainian"] = 25,
                ["Urdu"] = 15,
                ["Vietnamese"] = 13,
                ["Welsh"] = 65,
                ["Other"] = 47
            };

            Assert.Multiple(() =>
            {
                foreach (var (language, expectedId) in expected)
                {
                    Assert.That(MyAnonaMouseLanguageMapper.TryGetBrowseLanguageId(language, out var actualId), Is.True, language);
                    Assert.That(actualId, Is.EqualTo(expectedId), language);
                }
            });
        }

        [Test]
        public void should_generate_ten_search_pages_and_stop_at_mam_query_limit()
        {
            var chain = CreateGenerator().GetSearchRequests(new AuthorSearchCriteria
            {
                Author = new Author { Name = "Stephen King" }
            });

            var requests = chain.GetTier(0).Single().ToList();

            Assert.That(requests, Has.Count.EqualTo(10));
            Assert.That(JObject.Parse(Encoding.UTF8.GetString(requests[0].HttpRequest.ContentData))["tor"]?["startNumber"]?.Value<string>(), Is.EqualTo("0"));
            Assert.That(JObject.Parse(Encoding.UTF8.GetString(requests[9].HttpRequest.ContentData))["tor"]?["startNumber"]?.Value<string>(), Is.EqualTo("900"));
        }

        [Test]
        public void should_search_all_torrents_when_zero_seeders_are_allowed()
        {
            var generator = CreateGenerator();
            generator.Settings.MinimumSeeders = 0;

            var payload = GetRequestPayloadObject(generator.GetSearchRequests(new AuthorSearchCriteria
            {
                Author = new Author { Name = "Author" }
            }), 0);

            Assert.That(payload["tor"]?["searchType"]?.Value<string>(), Is.EqualTo("all"));
        }

        private static BookSearchCriteria CreateAudiobookCriteria(string allowedLanguages)
        {
            return new BookSearchCriteria
            {
                Author = new Author
                {
                    Name = "Author",
                    AudiobookMetadataProfile = new MetadataProfile
                    {
                        AllowedLanguages = allowedLanguages
                    }
                },
                Books = new List<Book> { new Book { MediaType = BookMediaType.Audiobook } },
                BookTitle = "Book"
            };
        }

        private static MyAnonaMouseRequestGenerator CreateGenerator()
        {
            return new MyAnonaMouseRequestGenerator
            {
                Settings = new MyAnonaMouseSettings(),
                Logger = LogManager.GetCurrentClassLogger()
            };
        }

        private static string GetRequestPayload(NzbDrone.Core.Indexers.IndexerPageableRequestChain chain, int tierIndex)
        {
            var request = chain.GetTier(tierIndex).Single().First();
            return Encoding.UTF8.GetString(request.HttpRequest.ContentData);
        }

        private static JObject GetRequestPayloadObject(NzbDrone.Core.Indexers.IndexerPageableRequestChain chain, int tierIndex)
        {
            return JObject.Parse(GetRequestPayload(chain, tierIndex));
        }
    }
}
