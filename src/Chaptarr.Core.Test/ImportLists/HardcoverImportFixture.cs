using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Hardcover;

namespace Chaptarr.Core.Test.ImportLists
{
    [TestFixture]
    public class HardcoverImportFixture
    {
        [Test]
        public void should_build_separate_requests_for_custom_lists_and_reading_statuses()
        {
            var generator = new HardcoverImportRequestGenerator
            {
                Settings = new HardcoverImportSettings
                {
                    ApiKey = "Bearer test-token",
                    User = "fixture-user",
                    ListIds = new[] { "my-list", "status:2", "status:5", "invalid-status:x" }
                }
            };

            var requests = generator.GetListItems()
                .GetAllTiers()
                .SelectMany(tier => tier)
                .Select(request => request.HttpRequest)
                .ToArray();

            Assert.That(requests, Has.Length.EqualTo(2));
            Assert.Multiple(() =>
            {
                Assert.That(requests[0].Url.FullUri, Is.EqualTo("https://api.hardcover.app/v1/graphql"));
                Assert.That(requests[0].Method, Is.EqualTo(System.Net.Http.HttpMethod.Post));
                Assert.That(requests[0].Headers["Authorization"], Is.EqualTo("Bearer test-token"));
                Assert.That(requests[0].Headers["X-Api-Key"], Is.EqualTo("test-token"));
                Assert.That(requests[0].Headers["User-Agent"], Is.EqualTo("ChaptarrNG (Hardcover Import)"));
            });

            using var listRequest = JsonDocument.Parse(Encoding.UTF8.GetString(requests[0].ContentData));
            using var statusRequest = JsonDocument.Parse(Encoding.UTF8.GetString(requests[1].ContentData));
            Assert.Multiple(() =>
            {
                Assert.That(listRequest.RootElement.GetProperty("query").GetString(), Does.Contain("list_books"));
                Assert.That(listRequest.RootElement.GetProperty("variables").GetProperty("user").GetString(), Is.EqualTo("fixture-user"));
                Assert.That(listRequest.RootElement.GetProperty("variables").GetProperty("slugs")[0].GetString(), Is.EqualTo("my-list"));
                Assert.That(statusRequest.RootElement.GetProperty("query").GetString(), Does.Contain("user_books"));
                Assert.That(statusRequest.RootElement.GetProperty("variables").GetProperty("statusIds")[0].GetInt32(), Is.EqualTo(2));
                Assert.That(statusRequest.RootElement.GetProperty("variables").GetProperty("statusIds")[1].GetInt32(), Is.EqualTo(5));
            });
        }

        [Test]
        public void should_parse_hardcover_book_and_author_ids_from_lists_and_statuses()
        {
            const string content = """
                {"data":{"users":[{"lists":[{"list_books":[{"book":{"id":42,"title":"Fixture Book","contributions":[{"author":{"id":7,"name":"Fixture Author"}}]}}]}],"user_books":[{"book":{"id":43,"title":"Status Book","contributions":[{"author":{"id":8,"name":"Status Author"}}]}}]}]}}
                """;
            var request = new ImportListRequest("https://api.hardcover.app/v1/graphql", HttpAccept.Json);
            var response = new HttpResponse(request.HttpRequest, new HttpHeader { ContentType = "application/json" }, content, HttpStatusCode.OK);

            var items = new HardcoverImportParser().ParseResponse(new ImportListResponse(request, response));

            Assert.That(items, Has.Count.EqualTo(2));
            Assert.Multiple(() =>
            {
                Assert.That(items[0].Book, Is.EqualTo("Fixture Book"));
                Assert.That(items[0].Author, Is.EqualTo("Fixture Author"));
                Assert.That(items[0].BookProviderId, Is.EqualTo("hc:42"));
                Assert.That(items[0].AuthorProviderId, Is.EqualTo("hc:7"));
                Assert.That(items[1].Book, Is.EqualTo("Status Book"));
                Assert.That(items[1].BookProviderId, Is.EqualTo("hc:43"));
            });
        }
    }
}
