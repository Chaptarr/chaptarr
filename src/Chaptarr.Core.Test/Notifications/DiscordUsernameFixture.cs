using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.Discord;

namespace Chaptarr.Core.Test.Notifications
{
    [TestFixture]
    public class DiscordUsernameFixture
    {
        private sealed class CapturingHttpClient : IHttpClient
        {
            public HttpRequest LastRequest { get; private set; }

            public HttpResponse Execute(HttpRequest request)
            {
                LastRequest = request;
                return new HttpResponse(request, new HttpHeader { ContentType = "application/json" }, "{}");
            }

            public void DownloadFile(string url, string fileName, string userAgent = null) => throw new System.NotImplementedException();
            public HttpResponse Get(HttpRequest request) => throw new System.NotImplementedException();
            public HttpResponse<T> Get<T>(HttpRequest request) where T : new() => throw new System.NotImplementedException();
            public HttpResponse Head(HttpRequest request) => throw new System.NotImplementedException();
            public HttpResponse Post(HttpRequest request) => throw new System.NotImplementedException();
            public HttpResponse<T> Post<T>(HttpRequest request) where T : new() => throw new System.NotImplementedException();
            public Task<HttpResponse> ExecuteAsync(HttpRequest request) => throw new System.NotImplementedException();
            public Task DownloadFileAsync(string url, string fileName, string userAgent = null) => throw new System.NotImplementedException();
            public Task<HttpResponse> GetAsync(HttpRequest request) => throw new System.NotImplementedException();
            public Task<HttpResponse<T>> GetAsync<T>(HttpRequest request) where T : new() => throw new System.NotImplementedException();
            public Task<HttpResponse> HeadAsync(HttpRequest request) => throw new System.NotImplementedException();
            public Task<HttpResponse> PostAsync(HttpRequest request) => throw new System.NotImplementedException();
            public Task<HttpResponse<T>> PostAsync<T>(HttpRequest request) where T : new() => throw new System.NotImplementedException();
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void should_omit_an_unconfigured_username_from_the_webhook_body(string username)
        {
            var httpClient = new CapturingHttpClient();
            var discord = CreateDiscord(httpClient, username);

            var result = discord.Test();

            Assert.That(result.IsValid, Is.True);
            Assert.That(httpClient.LastRequest, Is.Not.Null);
            var payload = JObject.Parse(Encoding.UTF8.GetString(httpClient.LastRequest.ContentData));
            Assert.That(payload.Property("username"), Is.Null);
        }

        [Test]
        public void should_preserve_a_configured_username_in_the_webhook_body()
        {
            var httpClient = new CapturingHttpClient();
            var discord = CreateDiscord(httpClient, "Chaptarr");

            var result = discord.Test();

            Assert.That(result.IsValid, Is.True);
            Assert.That(httpClient.LastRequest, Is.Not.Null);
            var payload = JObject.Parse(Encoding.UTF8.GetString(httpClient.LastRequest.ContentData));
            Assert.That((string)payload["username"], Is.EqualTo("Chaptarr"));
        }

        private static Discord CreateDiscord(CapturingHttpClient httpClient, string username)
        {
            var settings = new DiscordSettings
            {
                WebHookUrl = "https://discord.com/api/webhooks/123456/token",
                Username = username
            };
            Assert.That(settings.Validate().IsValid, Is.True);

            var discord = new Discord(new DiscordProxy(httpClient, LogManager.GetCurrentClassLogger()))
            {
                Definition = new NotificationDefinition { Settings = settings }
            };

            return discord;
        }
    }
}
