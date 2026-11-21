using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Notifications.AudioBookShelf;

namespace Chaptarr.Core.Test.Notifications.AudioBookShelf
{
    [TestFixture]
    public class AudioBookShelfProxyRequestFixture
    {
        private static (AudioBookShelfProxy Proxy, List<HttpRequest> Requests) CreateProxy(string itemsJson = "{\"results\":[]}")
        {
            var requests = new List<HttpRequest>();
            var httpClient = global::Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IHttpClient>((method, args) =>
            {
                var request = (HttpRequest)args[0];
                requests.Add(request);

                var content = request.Method == HttpMethod.Get ? itemsJson : "{}";
                return new HttpResponse(request, new HttpHeader { ContentType = "application/json" }, content);
            });

            return (new AudioBookShelfProxy(httpClient, LogManager.GetCurrentClassLogger()), requests);
        }

        private static AudioBookShelfSettings Settings() => new AudioBookShelfSettings { Host = "abs", Port = 13378, ApiKey = "test" };

        [Test]
        public void removing_missing_items_should_delete_only_the_missing_items_and_never_their_files()
        {
            var (proxy, requests) = CreateProxy("{\"results\":[" +
                "{\"id\":\"missing-item\",\"relPath\":\"A/Deleted\",\"isMissing\":true}," +
                "{\"id\":\"invalid-item\",\"relPath\":\"A/NoAudio\",\"isMissing\":false,\"isInvalid\":true}," +
                "{\"id\":\"ok-item\",\"relPath\":\"A/Book\",\"isMissing\":false}]}");

            var removed = proxy.RemoveMissingItems(Settings(), "library-audio");

            var deletes = requests.Where(r => r.Method == HttpMethod.Delete).ToList();
            Assert.That(removed, Is.EqualTo(1));
            Assert.That(deletes.Select(r => r.Url.Path), Is.EqualTo(new[] { "/api/items/missing-item" }));
            Assert.That(deletes.Single().Url.Query ?? string.Empty, Does.Not.Contain("hard"));
            Assert.That(requests.Select(r => r.Url.Path), Has.None.EndsWith("/issues"));
        }

        [Test]
        public void a_remote_cover_url_should_be_downloaded_by_audiobookshelf()
        {
            var (proxy, requests) = CreateProxy();

            proxy.UpdateItemCover(Settings(), "item-1", "https://covers.example/cover.jpg");

            var request = requests.Single();
            Assert.That(request.Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(request.Url.Path, Is.EqualTo("/api/items/item-1/cover"));
            Assert.That(Encoding.UTF8.GetString(request.ContentData), Does.Contain("\"url\"").And.Contain("https://covers.example/cover.jpg"));
        }

        [Test]
        public void a_cover_path_on_the_audiobookshelf_server_should_still_be_patched()
        {
            var (proxy, requests) = CreateProxy();

            proxy.UpdateItemCover(Settings(), "item-1", "/abs/audio/A/Book/cover.jpg");

            var request = requests.Single();
            Assert.That(request.Method, Is.EqualTo(HttpMethod.Patch));
            Assert.That(Encoding.UTF8.GetString(request.ContentData), Does.Contain("\"cover\"").And.Contain("/abs/audio/A/Book/cover.jpg"));
        }
    }
}
