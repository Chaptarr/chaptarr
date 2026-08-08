using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MetadataSource.Hardcover;

namespace Chaptarr.Core.Test.MetadataSource
{
    [TestFixture]
    public class HardcoverSearchClientFixture
    {
        private const string EmptySearchPayload = @"{
            ""data"": {
                ""search"": { ""results"": { ""hits"": [] } },
                ""authors"": { ""results"": { ""hits"": [] } },
                ""books"": { ""results"": { ""hits"": [] } },
                ""series"": { ""results"": { ""hits"": [] } }
            }
        }";

        private const string AuthorSearchPayload = @"{
            ""data"": {
                ""search"": {
                    ""results"": {
                        ""hits"": [{
                            ""document"": {
                                ""id"": ""241306"",
                                ""name"": ""Matt Dinniman"",
                                ""alternate_names"": [],
                                ""books_count"": 61,
                                ""slug"": ""matt-dinniman""
                            }
                        }]
                    }
                }
            }
        }";

        private const string BookSearchPayload = @"{
            ""data"": {
                ""search"": {
                    ""results"": {
                        ""hits"": [{
                            ""document"": {
                                ""id"": ""446681"",
                                ""title"": ""Dungeon Crawler Carl"",
                                ""contributions"": [{
                                    ""contribution"": null,
                                    ""author_id"": 241306,
                                    ""author"": { ""id"": 241306, ""name"": ""Matt Dinniman"" }
                                }]
                            }
                        }]
                    }
                }
            }
        }";

        private const string SeriesSearchPayload = @"{
            ""data"": {
                ""search"": {
                    ""results"": {
                        ""hits"": [{
                            ""document"": {
                                ""id"": ""12717"",
                                ""name"": ""Dungeon Crawler Carl"",
                                ""slug"": ""dungeon-crawler-carl"",
                                ""books_count"": 11
                            }
                        }]
                    }
                }
            }
        }";

        private const string AuthorBooksPayload = @"{
            ""data"": {
                ""author0"": [{
                    ""id"": 446681,
                    ""title"": ""Dungeon Crawler Carl"",
                    ""contributions"": [{
                        ""contribution"": null,
                        ""author_id"": 241306,
                        ""author"": { ""id"": 241306, ""name"": ""Matt Dinniman"" }
                    }]
                }]
            }
        }";

        private const string EnrichmentPayload = @"{
            ""data"": {
                ""authors"": [{ ""id"": 241306, ""bio"": ""Author bio"", ""image"": null }],
                ""series"": [{ ""id"": 12717, ""primary_book_series"": [], ""book_series"": [] }]
            }
        }";

        private const string EmptyEnrichmentPayload = @"{
            ""data"": {
                ""authors"": [],
                ""series"": []
            }
        }";

        private sealed class RecordingHttpClient : IHttpClient
        {
            private readonly Func<HttpRequest, HttpResponse> _handler;

            public RecordingHttpClient(Func<HttpRequest, HttpResponse> handler)
            {
                _handler = handler;
            }

            public List<HttpRequest> Requests { get; } = new();

            public HttpResponse Execute(HttpRequest request)
            {
                Requests.Add(request);
                return _handler(request);
            }

            public void DownloadFile(string url, string fileName, string userAgent = null) => throw new NotImplementedException();
            public HttpResponse Get(HttpRequest request) => throw new NotImplementedException();
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

        private class ConfigServiceProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    "get_HardcoverEnabled" => true,
                    "get_HardcoverApiToken" => "test-token",
                    _ => throw new NotImplementedException($"Test proxy does not implement {targetMethod?.Name}")
                };
            }
        }

        private static HardcoverSearchClient CreateClient(IHttpClient httpClient)
        {
            var configService = DispatchProxy.Create<IConfigService, ConfigServiceProxy>();
            return new HardcoverSearchClient(httpClient, configService);
        }

        private static HttpResponse JsonResponse(HttpRequest request, string content, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            return new HttpResponse(request, new HttpHeader { ContentType = "application/json" }, content, statusCode);
        }

        private static string BuildHttpBatch(params string[] operationResponses)
        {
            return $"[{string.Join(",", operationResponses)}]";
        }

        [Test]
        public void should_send_one_search_root_per_http_batched_operation()
        {
            var httpClient = new RecordingHttpClient(request => JsonResponse(request,
                BuildHttpBatch(EmptySearchPayload, EmptySearchPayload, EmptySearchPayload)));
            var client = CreateClient(httpClient);

            var results = client.Search("matt dinniman");

            Assert.That(results, Is.Not.Null.And.Empty);
            Assert.That(httpClient.Requests, Has.Count.EqualTo(1));

            var queryTypes = new List<string>();
            using var payload = JsonDocument.Parse(Encoding.UTF8.GetString(httpClient.Requests.Single().ContentData));
            Assert.That(payload.RootElement.ValueKind, Is.EqualTo(JsonValueKind.Array));
            Assert.That(payload.RootElement.GetArrayLength(), Is.EqualTo(3));

            foreach (var operation in payload.RootElement.EnumerateArray())
            {
                var query = operation.GetProperty("query").GetString();
                var variables = operation.GetProperty("variables");

                Assert.That(query.Split("search(", StringSplitOptions.None), Has.Length.EqualTo(2));
                Assert.That(variables.GetProperty("q").GetString(), Is.EqualTo("matt dinniman"));
                Assert.That(variables.GetProperty("limit").GetInt32(), Is.EqualTo(10));
                Assert.That(variables.GetProperty("page").GetInt32(), Is.EqualTo(1));
                queryTypes.Add(variables.GetProperty("query_type").GetString());
            }

            Assert.That(queryTypes, Is.EquivalentTo(new[] { "Author", "Book", "Series" }));
        }

        [Test]
        public void should_not_retry_or_disguise_a_forbidden_search_contract()
        {
            var httpClient = new RecordingHttpClient(request =>
            {
                var response = JsonResponse(request,
                    @"{""errors"":[""top_level_limit_exceeded"",""top_level_search_limit_exceeded""]}",
                    HttpStatusCode.Forbidden);
                throw new HttpException(request, response);
            });
            var client = CreateClient(httpClient);

            var results = client.Search("matt dinniman");

            Assert.That(results, Is.Null);
            Assert.That(httpClient.Requests, Has.Count.EqualTo(1));
        }

        [Test]
        public void should_fail_the_combined_search_on_a_bare_string_graphql_error()
        {
            const string PartialErrorPayload = @"{
                ""errors"": [""top_level_search_limit_exceeded""],
                ""data"": { ""search"": { ""results"": { ""hits"": [] } } }
            }";
            var httpClient = new RecordingHttpClient(request => JsonResponse(request,
                BuildHttpBatch(PartialErrorPayload, EmptySearchPayload, EmptySearchPayload)));
            var client = CreateClient(httpClient);

            var results = client.Search("matt dinniman");

            Assert.That(results, Is.Null);
            Assert.That(httpClient.Requests, Has.Count.EqualTo(1));
        }

        [Test]
        public void should_merge_live_shaped_author_book_and_series_results()
        {
            var httpClient = new RecordingHttpClient(request =>
            {
                using var payload = JsonDocument.Parse(Encoding.UTF8.GetString(request.ContentData));
                var root = payload.RootElement;

                if (root.ValueKind == JsonValueKind.Array)
                {
                    var operations = root.EnumerateArray().ToList();
                    var firstQuery = operations[0].GetProperty("query").GetString();
                    var firstVariables = operations[0].GetProperty("variables");

                    if (firstVariables.TryGetProperty("query_type", out _))
                    {
                        var responses = operations.Select(operation =>
                        {
                            var queryType = operation.GetProperty("variables").GetProperty("query_type").GetString();
                            return queryType switch
                            {
                                "Author" => AuthorSearchPayload,
                                "Book" => BookSearchPayload,
                                "Series" => SeriesSearchPayload,
                                _ => throw new AssertionException($"Unexpected Hardcover query type {queryType}")
                            };
                        }).ToArray();

                        return JsonResponse(request, BuildHttpBatch(responses));
                    }

                    if (firstQuery.Contains("BooksByAuthors", StringComparison.Ordinal))
                    {
                        return JsonResponse(request, BuildHttpBatch(AuthorBooksPayload));
                    }
                }

                var query = root.GetProperty("query").GetString();
                if (query.Contains("SearchEnrichment", StringComparison.Ordinal))
                {
                    return JsonResponse(request, EnrichmentPayload);
                }

                throw new AssertionException($"Unexpected Hardcover query: {query}");
            });
            var client = CreateClient(httpClient);

            var results = client.Search("matt dinniman");

            Assert.That(results, Has.Count.EqualTo(3));
            Assert.That(results[0], Is.TypeOf<HardcoverAuthorResult>());
            Assert.That(((HardcoverAuthorResult)results[0]).Id, Is.EqualTo("241306"));
            Assert.That(((HardcoverAuthorResult)results[0]).Name, Is.EqualTo("Matt Dinniman"));
            Assert.That(results[1], Is.TypeOf<HardcoverBookResult>());
            Assert.That(((HardcoverBookResult)results[1]).Id, Is.EqualTo("446681"));
            Assert.That(((HardcoverBookResult)results[1]).Title, Is.EqualTo("Dungeon Crawler Carl"));
            Assert.That(results[2], Is.TypeOf<HardcoverSeriesResult>());
            Assert.That(((HardcoverSeriesResult)results[2]).Id, Is.EqualTo("12717"));
            Assert.That(httpClient.Requests, Has.Count.EqualTo(3));
        }

        [Test]
        public void should_anchor_an_exact_author_then_place_provider_id_books_and_series_below_them()
        {
            const string authorSearch = @"{
                ""data"": { ""search"": { ""results"": { ""hits"": [
                    { ""text_match"": 1000, ""document"": {
                        ""id"": ""80626"", ""name"": ""J.K. Rowling"", ""alternate_names"": [],
                        ""books_count"": 500, ""slug"": ""jk-rowling""
                    } },
                    { ""text_match"": 900, ""document"": {
                        ""id"": ""92162"", ""name"": ""Bradley Steffens"", ""alternate_names"": [],
                        ""books_count"": 10, ""slug"": ""bradley-steffens""
                    } }
                ] } } }
            }";
            const string bookSearch = @"{
                ""data"": { ""search"": { ""results"": { ""hits"": [
                    { ""text_match"": 1000, ""document"": {
                        ""id"": ""486517"", ""title"": ""J.K. Rowling"",
                        ""contributions"": [{
                            ""contribution"": null,
                            ""author"": { ""id"": 92162, ""name"": ""Bradley Steffens"" }
                        }],
                        ""series_ids"": [], ""series_names"": []
                    } }
                ] } } }
            }";
            const string seriesSearch = @"{
                ""data"": { ""search"": { ""results"": { ""hits"": [
                    { ""text_match"": 800, ""document"": {
                        ""id"": ""1185"", ""name"": ""Harry Potter"", ""books_count"": 7,
                        ""author_name"": ""J.K. Rowling"",
                        ""author"": { ""id"": 80626, ""name"": ""J.K. Rowling"" }
                    } },
                    { ""text_match"": 700, ""document"": {
                        ""id"": ""9999"", ""name"": ""Unrelated Series"", ""books_count"": 2,
                        ""author_name"": ""Bradley Steffens"",
                        ""author"": { ""id"": 92162, ""name"": ""Bradley Steffens"" }
                    } }
                ] } } }
            }";
            const string authorBooks = @"{
                ""data"": {
                    ""author0"": [{
                        ""id"": 383236, ""title"": ""Harry Potter and the Goblet of Fire"",
                        ""contributions"": [{
                            ""contribution"": null,
                            ""author_id"": 80626,
                            ""author"": { ""id"": 80626, ""name"": ""J.K. Rowling"" }
                        }]
                    }],
                    ""author1"": [{
                        ""id"": 700001, ""title"": ""A Bradley Steffens Book"",
                        ""contributions"": [{
                            ""contribution"": null,
                            ""author_id"": 92162,
                            ""author"": { ""id"": 92162, ""name"": ""Bradley Steffens"" }
                        }]
                    }]
                }
            }";

            var httpClient = new RecordingHttpClient(request =>
            {
                using var payload = JsonDocument.Parse(Encoding.UTF8.GetString(request.ContentData));
                var root = payload.RootElement;

                if (root.ValueKind == JsonValueKind.Array)
                {
                    var operations = root.EnumerateArray().ToList();
                    var variables = operations[0].GetProperty("variables");
                    if (variables.TryGetProperty("query_type", out _))
                    {
                        return JsonResponse(request, BuildHttpBatch(authorSearch, bookSearch, seriesSearch));
                    }

                    return JsonResponse(request, BuildHttpBatch(authorBooks));
                }

                return JsonResponse(request, EmptyEnrichmentPayload);
            });
            var client = CreateClient(httpClient);

            var results = client.Search("JK rowling");

            Assert.That(results, Has.Count.EqualTo(5));
            Assert.That(results[0], Is.TypeOf<HardcoverAuthorResult>());
            Assert.That(((HardcoverAuthorResult)results[0]).Id, Is.EqualTo("80626"));
            Assert.That(results[1], Is.TypeOf<HardcoverBookResult>());
            Assert.That(((HardcoverBookResult)results[1]).Id, Is.EqualTo("383236"));
            Assert.That(results[2], Is.TypeOf<HardcoverSeriesResult>());
            Assert.That(((HardcoverSeriesResult)results[2]).Id, Is.EqualTo("1185"));
            Assert.That(((HardcoverSeriesResult)results[2]).AuthorId, Is.EqualTo("80626"));
            Assert.That(results[3], Is.TypeOf<HardcoverAuthorResult>());
            Assert.That(((HardcoverAuthorResult)results[3]).Id, Is.EqualTo("92162"));
            Assert.That(httpClient.Requests, Has.Count.EqualTo(3));
        }

        [Test]
        public void should_anchor_the_closest_book_then_place_its_provider_id_author_and_series_below_it()
        {
            const string authorSearch = @"{
                ""data"": { ""search"": { ""results"": { ""hits"": [
                    { ""text_match"": 1000, ""document"": {
                        ""id"": ""80626"", ""name"": ""J.K. Rowling"", ""alternate_names"": [],
                        ""books_count"": 500, ""slug"": ""jk-rowling""
                    } },
                    { ""text_match"": 900, ""document"": {
                        ""id"": ""92162"", ""name"": ""Bradley Steffens"", ""alternate_names"": [],
                        ""books_count"": 10, ""slug"": ""bradley-steffens""
                    } }
                ] } } }
            }";
            const string bookSearch = @"{
                ""data"": { ""search"": { ""results"": { ""hits"": [
                    { ""text_match"": 2000, ""document"": {
                        ""id"": ""383236"", ""title"": ""Harry Potter and the Goblet of Fire"",
                        ""contributions"": [{
                            ""contribution"": null,
                            ""author"": { ""id"": 80626, ""name"": ""J.K. Rowling"" }
                        }],
                        ""series_ids"": [1185], ""series_names"": [""Harry Potter""]
                    } },
                    { ""text_match"": 1500, ""document"": {
                        ""id"": ""2075910"", ""title"": ""Harry Potter and the Goblet of Fire Chapter Outlines"",
                        ""contributions"": [{
                            ""contribution"": null,
                            ""author"": { ""id"": 92162, ""name"": ""Bradley Steffens"" }
                        }],
                        ""series_ids"": [], ""series_names"": []
                    } }
                ] } } }
            }";
            const string seriesSearch = @"{
                ""data"": { ""search"": { ""results"": { ""hits"": [
                    { ""text_match"": 1000, ""document"": {
                        ""id"": ""194045"", ""name"": ""Harry Potter and the Goblet of Fire"", ""books_count"": 1,
                        ""author_name"": ""Some Other Author"",
                        ""author"": { ""id"": 70000, ""name"": ""Some Other Author"" }
                    } },
                    { ""text_match"": 900, ""document"": {
                        ""id"": ""1185"", ""name"": ""Harry Potter"", ""books_count"": 7,
                        ""author_name"": ""J.K. Rowling"",
                        ""author"": { ""id"": 80626, ""name"": ""J.K. Rowling"" }
                    } }
                ] } } }
            }";
            const string authorBooks = @"{
                ""data"": {
                    ""author0"": [{
                        ""id"": 383236, ""title"": ""Harry Potter and the Goblet of Fire"",
                        ""contributions"": [{
                            ""contribution"": null,
                            ""author_id"": 80626,
                            ""author"": { ""id"": 80626, ""name"": ""J.K. Rowling"" }
                        }]
                    }],
                    ""author1"": [{
                        ""id"": 700001, ""title"": ""A Bradley Steffens Book"",
                        ""contributions"": [{
                            ""contribution"": null,
                            ""author_id"": 92162,
                            ""author"": { ""id"": 92162, ""name"": ""Bradley Steffens"" }
                        }]
                    }]
                }
            }";

            var httpClient = new RecordingHttpClient(request =>
            {
                using var payload = JsonDocument.Parse(Encoding.UTF8.GetString(request.ContentData));
                var root = payload.RootElement;

                if (root.ValueKind == JsonValueKind.Array)
                {
                    var operations = root.EnumerateArray().ToList();
                    var variables = operations[0].GetProperty("variables");
                    if (variables.TryGetProperty("query_type", out _))
                    {
                        return JsonResponse(request, BuildHttpBatch(authorSearch, bookSearch, seriesSearch));
                    }

                    return JsonResponse(request, BuildHttpBatch(authorBooks));
                }

                return JsonResponse(request, EmptyEnrichmentPayload);
            });
            var client = CreateClient(httpClient);

            var results = client.Search("Harry Potter goblet of fire");

            Assert.That(results, Has.Count.EqualTo(6));
            Assert.That(results[0], Is.TypeOf<HardcoverBookResult>());
            var anchorBook = (HardcoverBookResult)results[0];
            Assert.That(anchorBook.Id, Is.EqualTo("383236"));
            Assert.That(anchorBook.AuthorIds, Is.EqualTo(new[] { "80626" }));
            Assert.That(anchorBook.SeriesIds, Is.EqualTo(new[] { "1185" }));
            Assert.That(results[1], Is.TypeOf<HardcoverAuthorResult>());
            Assert.That(((HardcoverAuthorResult)results[1]).Id, Is.EqualTo("80626"));
            Assert.That(results[2], Is.TypeOf<HardcoverSeriesResult>());
            Assert.That(((HardcoverSeriesResult)results[2]).Id, Is.EqualTo("1185"));
            Assert.That(((HardcoverSeriesResult)results[2]).AuthorId, Is.EqualTo("80626"));
            Assert.That(httpClient.Requests, Has.Count.EqualTo(3));
        }

        [Test]
        public void should_use_hardcover_text_match_to_choose_between_non_exact_entity_types()
        {
            const string bookSearch = @"{
                ""data"": { ""search"": { ""results"": { ""hits"": [
                    { ""text_match"": 2000, ""document"": {
                        ""id"": ""404904"", ""title"": ""The Way of Kings"",
                        ""contributions"": [], ""series_ids"": [3022],
                        ""series_names"": [""The Stormlight Archive""]
                    } }
                ] } } }
            }";
            const string seriesSearch = @"{
                ""data"": { ""search"": { ""results"": { ""hits"": [
                    { ""text_match"": 3000, ""document"": {
                        ""id"": ""3022"", ""name"": ""The Stormlight Archive"",
                        ""books_count"": 5, ""author_name"": ""Brandon Sanderson"",
                        ""author"": { ""id"": 42590, ""name"": ""Brandon Sanderson"" }
                    } }
                ] } } }
            }";

            var httpClient = new RecordingHttpClient(request =>
            {
                using var payload = JsonDocument.Parse(Encoding.UTF8.GetString(request.ContentData));
                if (payload.RootElement.ValueKind == JsonValueKind.Array)
                {
                    return JsonResponse(request, BuildHttpBatch(EmptySearchPayload, bookSearch, seriesSearch));
                }

                return JsonResponse(request, EmptyEnrichmentPayload);
            });
            var client = CreateClient(httpClient);

            var results = client.Search("stormlight archive");

            Assert.That(results, Has.Count.EqualTo(2));
            Assert.That(results[0], Is.TypeOf<HardcoverSeriesResult>());
            Assert.That(((HardcoverSeriesResult)results[0]).Id, Is.EqualTo("3022"));
            Assert.That(((HardcoverSeriesResult)results[0]).SearchScore, Is.EqualTo(3000));
            Assert.That(results[1], Is.TypeOf<HardcoverBookResult>());
            Assert.That(httpClient.Requests, Has.Count.EqualTo(2));
        }

        [Test]
        public void should_retry_a_server_error_once_then_continue_the_search()
        {
            var requestCount = 0;
            var httpClient = new RecordingHttpClient(request =>
            {
                requestCount++;
                if (requestCount == 1)
                {
                    var response = JsonResponse(request, @"{""errors"":[""temporary""]}", HttpStatusCode.InternalServerError);
                    throw new HttpException(request, response);
                }

                return JsonResponse(request, BuildHttpBatch(EmptySearchPayload, EmptySearchPayload, EmptySearchPayload));
            });
            var client = CreateClient(httpClient);

            var results = client.Search("matt dinniman");

            Assert.That(results, Is.Not.Null.And.Empty);
            Assert.That(httpClient.Requests, Has.Count.EqualTo(2));
        }

        [Test]
        public void should_chunk_ten_author_book_roots_into_two_http_batched_operations()
        {
            var authorHits = Enumerable.Range(1, 10)
                .Select(id => new
                {
                    document = new
                    {
                        id = id.ToString(),
                        name = $"Smith {id}",
                        alternate_names = Array.Empty<string>(),
                        books_count = 1,
                        slug = $"smith-{id}"
                    }
                })
                .ToArray();
            var authorSearchPayload = JsonSerializer.Serialize(new
            {
                data = new
                {
                    search = new
                    {
                        results = new { hits = authorHits }
                    }
                }
            });

            var httpClient = new RecordingHttpClient(request =>
            {
                using var payload = JsonDocument.Parse(Encoding.UTF8.GetString(request.ContentData));
                var operations = payload.RootElement.EnumerateArray().ToList();
                var variables = operations[0].GetProperty("variables");

                if (variables.TryGetProperty("query_type", out _))
                {
                    return JsonResponse(request,
                        BuildHttpBatch(authorSearchPayload, EmptySearchPayload, EmptySearchPayload));
                }

                var responses = operations.Select(operation =>
                {
                    var query = operation.GetProperty("query").GetString();
                    var data = new Dictionary<string, object>();
                    var aliasCount = query.Split(": books(", StringSplitOptions.None).Length - 1;
                    for (var index = 0; index < aliasCount; index++)
                    {
                        data[$"author{index}"] = Array.Empty<object>();
                    }

                    return JsonSerializer.Serialize(new { data });
                }).ToArray();

                return JsonResponse(request, BuildHttpBatch(responses));
            });
            var client = CreateClient(httpClient);

            var results = client.Search("smith");

            Assert.That(results, Is.Not.Null.And.Empty);
            Assert.That(httpClient.Requests, Has.Count.EqualTo(2));

            using var authorBooksPayload = JsonDocument.Parse(Encoding.UTF8.GetString(httpClient.Requests[1].ContentData));
            Assert.That(authorBooksPayload.RootElement.GetArrayLength(), Is.EqualTo(2));
            foreach (var operation in authorBooksPayload.RootElement.EnumerateArray())
            {
                var query = operation.GetProperty("query").GetString();
                Assert.That(query.Split(": books(", StringSplitOptions.None), Has.Length.EqualTo(6));
            }
        }

        [TestCase(null, true)]
        [TestCase("", true)]
        [TestCase(" ", true)]
        [TestCase("Author", true)]
        [TestCase("author", true)]
        [TestCase("AUTHOR", true)]
        [TestCase("Pseudonym", true)]
        [TestCase("pseudonym", true)]
        [TestCase("House Name", true)]
        [TestCase("house   name", true)]
        [TestCase("Author's Hebrew Name", true)]
        [TestCase("author's hebrew name", true)]
        [TestCase("Authors", false)]
        [TestCase("Co-author", false)]
        [TestCase("Author/Illustrator", false)]
        [TestCase("Writer", false)]
        [TestCase("Original Author", false)]
        [TestCase("Narrator", false)]
        [TestCase("Editor", false)]
        public void should_classify_primary_author_contribution_roles(string contribution, bool expected)
        {
            Assert.That(HardcoverContributionRoles.IsPrimaryAuthor(contribution), Is.EqualTo(expected));
        }

        [TestCase("G. Edward Griffin, G. Edward Griffin, Carleen Taylor, Peter Klimon", true)]
        [TestCase("G. Edward Griffin, Carleen Taylor", true)]
        [TestCase("Neil Gaiman & Terry Pratchett", true)]
        [TestCase("Stephen King and Peter Straub", true)]
        [TestCase("Larry and Jerry Pournelle Niven", true)]
        [TestCase("John Smith with Jane Doe", true)]
        [TestCase("King, Stephen", false)]
        [TestCase("Tolkien, J.R.R.", false)]
        [TestCase("George R. R. Martin", false)]
        [TestCase("Queen Elizabeth II", false)]
        public void should_match_server_multi_person_identity_guard(string name, bool expected)
        {
            Assert.That(HardcoverAuthorIdentity.IsLikelyMultiPerson(name), Is.EqualTo(expected));
        }

        [Test]
        public void should_keep_null_blank_and_exact_author_contributions_only()
        {
            var element = JsonDocument.Parse(@"{
                ""contributions"": [
                    { ""contribution"": ""Editor"", ""author_id"": 1, ""author"": { ""id"": 1, ""name"": ""Gary K. Beauchamp"" } },
                    { ""contribution"": null, ""author_id"": 2, ""author"": { ""id"": 2, ""name"": ""Linda Bartoshuk"" } },
                    { ""contribution"": ""Author"", ""author_id"": 3, ""author"": { ""id"": 3, ""name"": ""Stephen Hackett"" } },
                    { ""contribution"": """", ""author_id"": 7, ""author"": { ""id"": 7, ""name"": ""Blank Role Primary"" } },
                    { ""contribution"": ""Co-author"", ""author_id"": 4, ""author"": { ""id"": 4, ""name"": ""Robert Sheckley"" } },
                    { ""contribution"": ""Author/Illustrator"", ""author_id"": 5, ""author"": { ""id"": 5, ""name"": ""Tony DiTerlizzi"" } },
                    { ""contribution"": ""author"", ""author_id"": 6, ""author"": { ""id"": 6, ""name"": ""Lowercase Typo"" } }
                ]
            }").RootElement;

            var pairs = HardcoverSearchClient.GetPrimaryAuthorContributorsFromGraphQL(element);

            Assert.That(pairs.Select(p => p.Name), Is.EqualTo(new[] { "Linda Bartoshuk", "Stephen Hackett", "Blank Role Primary", "Lowercase Typo" }));
            Assert.That(pairs.Select(p => p.Id), Is.EqualTo(new[] { "2", "3", "7", "6" }));
        }

        [Test]
        public void should_choose_same_best_primary_author_as_hardcover_importer()
        {
            var element = JsonDocument.Parse(@"{
                ""contributions"": [
                    { ""contribution"": null, ""author_id"": 673682, ""author"": { ""id"": 673682, ""name"": ""Dennis Jürgensen"" } },
                    { ""contribution"": ""Narrator"", ""author_id"": 264002, ""author"": { ""id"": 264002, ""name"": ""Torben Sekov"" } }
                ]
            }").RootElement;

            var primary = HardcoverSearchClient.GetBestPrimaryAuthorContributorFromGraphQL(element);

            Assert.That(primary.Id, Is.EqualTo("673682"));
            Assert.That(primary.Name, Is.EqualTo("Dennis Jürgensen"));
        }

        [Test]
        public void should_rank_explicit_primary_persona_roles_before_null_author_rows()
        {
            var element = JsonDocument.Parse(@"{
                ""contributions"": [
                    { ""contribution"": null, ""author_id"": 1, ""author"": { ""id"": 1, ""name"": ""Legal Name"" } },
                    { ""contribution"": ""Pseudonym"", ""author_id"": 2, ""author"": { ""id"": 2, ""name"": ""Pen Name"" } }
                ]
            }").RootElement;

            var primary = HardcoverSearchClient.GetBestPrimaryAuthorContributorFromGraphQL(element);

            Assert.That(primary.Id, Is.EqualTo("2"));
            Assert.That(primary.Name, Is.EqualTo("Pen Name"));
        }

        [Test]
        public void should_project_author_names_and_ids_from_the_same_valid_pairs()
        {
            var pairs = new List<(string Id, string Name)>
            {
                ("10", "Neil Gaiman"),
                (null, "Name Without Id"),
                ("12", null),
                ("13", "Terry Pratchett")
            };

            var authorArrays = HardcoverSearchClient.BuildAuthorArrays(pairs);

            Assert.That(authorArrays.AuthorNames, Is.EqualTo(new[] { "Neil Gaiman", "Terry Pratchett" }));
            Assert.That(authorArrays.AuthorIds, Is.EqualTo(new[] { "10", "13" }));
        }
    }
}
