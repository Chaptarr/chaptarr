using System.Collections.Generic;
using System.Text.Json.Nodes;
using Chaptarr.Api.V1.Indexers;
using Chaptarr.Http.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using NUnit.Framework;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Indexers;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    public class BookshelfReleaseCompatibilityFixture
    {
        [Test]
        public void legacy_search_should_emit_predecessor_fields_and_empty_infohash()
        {
            var resource = new ReleaseResource
            {
                Guid = "synthetic-release",
                Protocol = DownloadProtocol.Torrent,
                HasOriginalInfoHash = true,
                OriginalInfoHash = string.Empty,
                AuthorId = 10,
                BookId = 30,
                DisplayTitle = "Native display",
                Rank = 1,
                RejectionDetails = new List<RejectionDetail> { new() { Reason = "native filter detail" } },
                Quality = new QualityModel(Quality.EPUB),
                ReleaseHash = string.Empty
            };
            var result = Apply(new List<ReleaseResource> { resource }, true, "GET");
            var release = ((JsonArray)result.Value)[0];

            Assert.Multiple(() =>
            {
                Assert.That(release["guid"].GetValue<string>(), Is.EqualTo("synthetic-release"));
                Assert.That(release["infoHash"].GetValue<string>(), Is.Empty);
                Assert.That(release["quality"]["quality"]["name"].GetValue<string>(), Is.EqualTo("EPUB"));
                Assert.That(release["quality"]["quality"].AsObject().ContainsKey("isConversionTarget"), Is.False);
                Assert.That(release.AsObject().ContainsKey("authorId"), Is.False);
                Assert.That(release.AsObject().ContainsKey("bookId"), Is.False);
                Assert.That(release.AsObject().ContainsKey("rank"), Is.False);
                Assert.That(release.AsObject().ContainsKey("displayTitle"), Is.False);
                Assert.That(release.AsObject().ContainsKey("rejectionDetails"), Is.False);
                Assert.That(release.AsObject().ContainsKey("releaseHash"), Is.False);
            });
        }

        [Test]
        public void legacy_usenet_search_should_not_add_a_torrent_hash()
        {
            var release = ((JsonArray)Apply(new List<ReleaseResource> { new() { Protocol = DownloadProtocol.Usenet } }, true, "GET").Value)[0];

            Assert.That(release.AsObject().ContainsKey("infoHash"), Is.False);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("0123456789abcdef0123456789abcdef01234567")]
        public void legacy_search_should_preserve_the_original_hash_representation(string original)
        {
            var resource = new ReleaseResource
            {
                Protocol = DownloadProtocol.Torrent,
                InfoHash = "0123456789ABCDEF0123456789ABCDEF01234567",
                HasOriginalInfoHash = true,
                OriginalInfoHash = original
            };
            var release = ((JsonArray)Apply(new List<ReleaseResource> { resource }, true, "GET").Value)[0].AsObject();

            Assert.That(release.ContainsKey("infoHash"), Is.EqualTo(original != null));
            if (original != null)
            {
                Assert.That(release["infoHash"].GetValue<string>(), Is.EqualTo(original));
            }

            Assert.That(resource.InfoHash, Is.EqualTo("0123456789ABCDEF0123456789ABCDEF01234567"));
        }

        [Test]
        public void native_search_should_preserve_its_typed_resource()
        {
            var resources = new List<ReleaseResource> { new() { Rank = 1, BookId = 30 } };

            Assert.That(Apply(resources, false, "GET").Value, Is.SameAs(resources));
        }

        [Test]
        public void legacy_grab_response_should_retain_supplied_request_identities()
        {
            var resource = new ReleaseResource { AuthorId = 10, BookId = 30, SeriesId = 50, DownloadClientId = 60 };
            var release = (JsonObject)Apply(resource, true, "POST").Value;

            Assert.Multiple(() =>
            {
                Assert.That(release["authorId"].GetValue<int>(), Is.EqualTo(10));
                Assert.That(release["bookId"].GetValue<int>(), Is.EqualTo(30));
                Assert.That(release["seriesId"].GetValue<int>(), Is.EqualTo(50));
                Assert.That(release["downloadClientId"].GetValue<int>(), Is.EqualTo(60));
                Assert.That(release.ContainsKey("infoHash"), Is.False);
            });
        }

        private static ObjectResult Apply(object resources, bool compatibility, string method)
        {
            var http = new DefaultHttpContext();
            http.Request.Method = method;
            http.Items[ReadarrFacadeContext.ItemKey] = new ReadarrFacadeContext("hc", null, "/api", compatibility);
            var result = new OkObjectResult(resources);
            var context = new ResultExecutingContext(
                new ActionContext(http, new RouteData(), new ActionDescriptor()),
                new List<IFilterMetadata>(), result, new object());
            new BookshelfReleaseCompatibilityAttribute().OnResultExecuting(context);
            return result;
        }
    }
}
