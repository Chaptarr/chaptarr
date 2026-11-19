using System;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using NLog;
using NLog.Config;
using NLog.Targets;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Common.Http.Dispatchers;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.Download.Clients.Aria2;
using NzbDrone.Core.Notifications.Apprise;
using NzbDrone.Core.Notifications.Slack;
using NzbDrone.Core.Notifications.Slack.Payloads;
using NzbDrone.Core.Notifications.Subsonic;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Http
{
    [TestFixture]
    [NonParallelizable]
    public class ProviderCredentialLoggingFixture
    {
        private const string Secret = "synthetic-provider-secret";
        private sealed class RejectingDispatcher : IHttpDispatcher
        {
            public HttpRequest Request;
            public Task<HttpResponse> GetResponseAsync(HttpRequest request, CookieContainer cookies)
            {
                Request = request;
                return Task.FromResult(new HttpResponse(request, new HttpHeader { ContentType = "application/json" },
                    Encoding.UTF8.GetBytes("{\"data\":{\"sid\":\"synthetic-session-secret\"},\"message\":\"diagnostic detail\"}"), HttpStatusCode.BadRequest));
            }
        }

        [TestCase("slack")]
        [TestCase("apprise")]
        [TestCase("subsonic")]
        [TestCase("aria2")]
        public void real_provider_request_and_http_failure_use_the_output_cleanser(string provider)
        {
            // Initialize the shared XML-RPC logger before installing the capture target.
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(XmlRpcRequestBuilder).TypeHandle);
            var previous = LogManager.Configuration;
            var memory = new MemoryTarget { Layout = new CleanseLayout { Inner = "${level}|${message}|${exception:format=tostring}" } };
            var configuration = new LoggingConfiguration();
            configuration.AddRule(LogLevel.Trace, LogLevel.Fatal, memory);
            LogManager.Configuration = configuration;
            try
            {
                var dispatcher = new RejectingDispatcher();
                var logger = LogManager.GetLogger("ProviderCredentialProof");
                var http = new NzbDrone.Common.Http.HttpClient(Array.Empty<IHttpRequestInterceptor>(), new CacheManager(), null, dispatcher, logger);
                Assert.Catch(() =>
                {
                    switch (provider)
                    {
                        case "slack":
                            new SlackProxy(http, logger).SendPayload(new SlackPayload(), new SlackSettings { WebHookUrl = "https://hooks.slack.com/services/T0/B0/" + Secret });
                            break;
                        case "apprise":
                            new AppriseProxy(http, logger).SendNotification("title", "message", new AppriseSettings { ServerUrl = "https://apprise.example", ConfigurationKey = Secret });
                            break;
                        case "subsonic":
                            new SubsonicServerProxy(http, logger).Version(new SubsonicSettings { Host = "subsonic.example", Port = 4040, Username = Secret, Password = Secret });
                            break;
                        case "aria2":
                            new Aria2Proxy(http).GetFiles(new Aria2Settings { Host = "aria2.example", Port = 6800, SecretToken = Secret }, "id");
                            break;
                    }
                });
                Assert.That(dispatcher.Request, Is.Not.Null, "The real provider must reach the transport");
                Assert.That(dispatcher.Request.Url.FullUri + Encoding.UTF8.GetString(dispatcher.Request.ContentData ?? Array.Empty<byte>()), Does.Contain(Secret), "Only logging is scrubbed; the actual credential is sent");
                Assert.That(memory.Logs, Has.Some.Contains("Warn|HTTP Error"));
                Assert.That(memory.Logs, Has.Some.Contains("Trace|Response content"));
                Assert.That(memory.Logs, Has.Some.Contains("diagnostic detail"));
                Assert.That(memory.Logs, Has.None.Contains(Secret));
                Assert.That(memory.Logs, Has.None.Contains("synthetic-session-secret"));
                if (provider == "aria2") Assert.That(memory.Logs, Has.Some.Contains("methodCall aria2.getFiles body:"));
            }
            finally { LogManager.Configuration = previous; }
        }
    }
}
