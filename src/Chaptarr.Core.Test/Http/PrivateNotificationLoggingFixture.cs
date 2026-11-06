using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Common.Http.Dispatchers;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.Notifications.Ntfy;
using NzbDrone.Core.Notifications.Slack;
using NzbDrone.Core.Notifications.Slack.Payloads;
using NzbDrone.Core.Notifications.Discord;
using NzbDrone.Core.Notifications.Discord.Payloads;
using NzbDrone.Core.Notifications.Webhook;

namespace Chaptarr.Core.Test.Http
{
    [TestFixture]
    public class PrivateNotificationLoggingFixture
    {
        private const string Secret = "private-destination-credential";

        private sealed class Dispatcher(Func<HttpRequest, int, HttpResponse> respond) : IHttpDispatcher
        {
            public readonly List<string> SentUrls = new();

            public Task<HttpResponse> GetResponseAsync(HttpRequest request, CookieContainer cookies)
            {
                SentUrls.Add(request.Url.FullUri);
                return Task.FromResult(respond(request, SentUrls.Count));
            }
        }

        private static HttpResponse Reply(HttpRequest request, HttpStatusCode status, string body) =>
            new(request, new HttpHeader { ContentType = "application/json" }, body, status);

        [TestCase("webhook", HttpStatusCode.BadRequest)]
        [TestCase("webhook", HttpStatusCode.Unauthorized)]
        [TestCase("webhook", HttpStatusCode.TooManyRequests)]
        [TestCase("ntfy", HttpStatusCode.BadRequest)]
        [TestCase("ntfy", HttpStatusCode.Unauthorized)]
        [TestCase("ntfy", HttpStatusCode.TooManyRequests)]
        [TestCase("slack", HttpStatusCode.BadRequest)]
        [TestCase("slack", HttpStatusCode.Unauthorized)]
        [TestCase("slack", HttpStatusCode.TooManyRequests)]
        [TestCase("discord", HttpStatusCode.BadRequest)]
        [TestCase("discord", HttpStatusCode.Unauthorized)]
        [TestCase("discord", HttpStatusCode.TooManyRequests)]
        public void real_proxy_failures_hide_destination_but_keep_host_status_and_reason(string provider, HttpStatusCode status)
        {
            using var logs = new LogFactory();
            var memory = new MemoryTarget { Layout = new CleanseLayout { Inner = "${level}|${message}|${exception:format=tostring}" } };
            var configuration = new LoggingConfiguration(logs);
            configuration.AddRule(LogLevel.Trace, LogLevel.Fatal, memory);
            logs.Configuration = configuration;
            // Echo the configured destination, which is the request-local redaction contract.
            // A bare fragment of a path is not globally secret (e.g. /api/v1/3).
            var dispatcher = new Dispatcher((request, _) => Reply(request, status,
                System.Text.Json.JsonSerializer.Serialize(new { message = "destination refused", destination = request.Url.FullUri })));
            var logger = logs.GetLogger("PrivateNotificationProof");
            var client = new NzbDrone.Common.Http.HttpClient(Array.Empty<IHttpRequestInterceptor>(), new CacheManager(), null, dispatcher, logger);

            var exception = Assert.Catch(() =>
            {
                if (provider == "webhook")
                {
                    new WebhookProxy(client).SendWebhook(new WebhookPayload(), new WebhookSettings
                    {
                        Url = "https://notify.example/" + Secret + "?destination=" + Secret,
                        Method = (int)WebhookMethod.POST
                    });
                }
                else if (provider == "slack")
                {
                    new SlackProxy(client, logger).SendPayload(new SlackPayload(), new SlackSettings
                    {
                        WebHookUrl = "https://notify.example/hooks/" + Secret + "?destination=" + Secret
                    });
                }
                else if (provider == "discord")
                {
                    new DiscordProxy(client, logger).SendPayload(new DiscordPayload(), new DiscordSettings
                    {
                        WebHookUrl = "https://notify.example/hooks/" + Secret + "?destination=" + Secret
                    });
                }
                else
                {
                    new NtfyProxy(client, logger).SendNotification("A book arrived", "Notification body", new NtfySettings
                    {
                        ServerUrl = "https://notify.example",
                        Topics = new[] { Secret },
                        Tags = Array.Empty<string>()
                    });
                }
            });

            Assert.That(dispatcher.SentUrls, Has.Count.EqualTo(1));
            Assert.That(dispatcher.SentUrls[0], Does.Contain(Secret), "The real transport still receives the credential");
            Assert.That(exception.ToString(), Does.Not.Contain(Secret));
            Assert.That(memory.Logs, Has.None.Contains(Secret));
            Assert.That(memory.Logs, Has.Some.Contains("notify.example/(removed)"));
            Assert.That(memory.Logs, Has.Some.Contains(((int)status) + "." + status));
            Assert.That(memory.Logs, Has.Some.Contains("destination refused"));
            Assert.That(memory.Logs.Where(line => line.StartsWith("Warn|")), Has.None.Contains("destination refused"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task redirects_hide_each_destination_and_keep_actual_transport_unchanged(bool blocked)
        {
            using var logs = new LogFactory();
            var memory = new MemoryTarget { Layout = "${level}|${message}|${exception:format=tostring}" };
            var configuration = new LoggingConfiguration(logs);
            configuration.AddRule(LogLevel.Trace, LogLevel.Fatal, memory);
            logs.Configuration = configuration;
            var next = (blocked ? "http://169.254.169.254" : "https://redirect.example") + "/redirect-private?opaque=redirect-query";
            var dispatcher = new Dispatcher((request, count) =>
            {
                if (count == 1)
                {
                    var response = Reply(request, HttpStatusCode.Found, "");
                    response.Headers["Location"] = next;
                    return response;
                }

                return Reply(request, HttpStatusCode.BadRequest, "original " + Secret + " redirected redirect-private opaque=redirect-query: destination refused");
            });
            var client = new NzbDrone.Common.Http.HttpClient(Array.Empty<IHttpRequestInterceptor>(), new CacheManager(), null, dispatcher, logs.GetLogger("Redirect"));
            var request = new HttpRequest("https://notify.example/" + Secret) { RedactUrlInLogs = true, AllowAutoRedirect = true };
            Exception failure = null;
            try { await client.ExecuteAsync(request); }
            catch (Exception ex) { failure = ex; }
            Assert.That(failure, Is.Not.Null);
            Assert.That(dispatcher.SentUrls, Has.Count.EqualTo(blocked ? 1 : 2));
            if (!blocked) Assert.That(dispatcher.SentUrls[1], Is.EqualTo(next));
            foreach (var secret in new[] { Secret, "redirect-private", "redirect-query" })
            {
                Assert.That(memory.Logs, Has.None.Contains(secret));
                Assert.That(failure.ToString(), Does.Not.Contain(secret));
            }
        }

        [TestCase("a%22b", "a\"b")]
        [TestCase("caf%C3%A9", "café")]
        [TestCase("private+phrase", "private phrase")]
        public void response_echoes_are_redacted_encoded_decoded_and_json_escaped(string encoded, string decoded)
        {
            var request = new HttpRequest("https://notify.example/" + encoded + "?opaque=" + encoded) { RedactUrlInLogs = true };
            var body = System.Text.Json.JsonSerializer.Serialize(new { destination = decoded, wire = encoded, message = "destination refused" });
            var sanitized = request.SanitizeLogMessage(body);
            Assert.That(sanitized, Does.Not.Contain(encoded));
            Assert.That(sanitized, Does.Not.Contain(System.Text.Json.JsonSerializer.Serialize(decoded)[1..^1]));
            Assert.That(sanitized, Does.Contain("destination refused"));
        }

        [Test]
        public void private_destinations_do_not_redact_common_path_segments_or_priority_numbers()
        {
            var request = new HttpRequest("https://notify.example/api/v1/private-destination?priority=3&opaque=private-query") { RedactUrlInLogs = true };
            var sanitized = request.SanitizeLogMessage("api v1 priority 3: refused api/v1/private-destination with priority=3&opaque=private-query");
            Assert.That(sanitized, Is.EqualTo("api v1 priority 3: refused (removed) with (removed)"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void error_response_body_is_decoded_for_logging_only_when_trace_is_enabled(bool trace)
        {
            using var logs = new LogFactory();
            var memory = new MemoryTarget { Layout = "${message}" };
            var configuration = new LoggingConfiguration(logs);
            configuration.AddRule(trace ? LogLevel.Trace : LogLevel.Info, LogLevel.Fatal, memory);
            logs.Configuration = configuration;
            var dispatcher = new Dispatcher((request, _) => new HttpResponse(request,
                new HttpHeader { ContentType = "application/json" }, System.Text.Encoding.UTF8.GetBytes("diagnostic detail"), HttpStatusCode.BadRequest));
            var client = new NzbDrone.Common.Http.HttpClient(Array.Empty<IHttpRequestInterceptor>(), new CacheManager(), null, dispatcher, logs.GetLogger("TraceDecode"));
            var response = client.Execute(new HttpRequest("https://provider.example/api") { SuppressHttpError = true });
            var decoded = typeof(HttpResponse).GetField("_content", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(response);
            Assert.That(decoded, trace ? Is.Not.Null : Is.Null);
            Assert.That(memory.Logs.Any(line => line.Contains("diagnostic detail")), Is.EqualTo(trace));
        }

        [Test]
        public void actual_ntfy_failure_names_the_connection_through_existing_notification_dispatch()
        {
            using var logs = new LogFactory();
            var memory = new MemoryTarget { Layout = new CleanseLayout { Inner = "${level}|${message}|${exception:format=tostring}" } };
            var configuration = new LoggingConfiguration(logs);
            configuration.AddRule(LogLevel.Trace, LogLevel.Fatal, memory);
            logs.Configuration = configuration;
            var logger = logs.GetLogger("NtfyConnection");
            var dispatcher = new Dispatcher((request, _) => Reply(request, HttpStatusCode.BadRequest, "destination refused"));
            var client = new NzbDrone.Common.Http.HttpClient(Array.Empty<IHttpRequestInterceptor>(), new CacheManager(), null, dispatcher, logger);
            var notification = new Ntfy(new NtfyProxy(client, logger))
            {
                Definition = new NzbDrone.Core.Notifications.NotificationDefinition
                {
                    Id = 81, Name = "Library phone", Tags = new HashSet<int>(),
                    Settings = new NtfySettings { ServerUrl = "https://notify.example", Topics = new[] { Secret }, Tags = Array.Empty<string>() }
                }
            };
            var failures = new List<int>();
            var factory = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<NzbDrone.Core.Notifications.INotificationFactory>((method, _) =>
                method.Name == "OnAuthorAddedEnabled" ? new List<NzbDrone.Core.Notifications.INotification> { notification } : throw new InvalidOperationException(method.Name));
            var status = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<NzbDrone.Core.Notifications.INotificationStatusService>((method, args) =>
            {
                if (method.Name != "RecordFailure") throw new InvalidOperationException(method.Name);
                failures.Add((int)args[0]);
                return null;
            });
            new NzbDrone.Core.Notifications.NotificationService(factory, status, null, logger)
                .Handle(new NzbDrone.Core.Books.Events.AuthorAddedEvent(new NzbDrone.Core.Books.Author { Name = "Author" }));
            Assert.That(failures, Is.EqualTo(new[] { 81 }));
            Assert.That(dispatcher.SentUrls, Has.Count.EqualTo(1));
            Assert.That(memory.Logs, Has.Some.Contains("Library phone"));
            Assert.That(memory.Logs, Has.Some.Contains("notify.example"));
            Assert.That(memory.Logs, Has.Some.Contains("destination refused"));
            Assert.That(memory.Logs, Has.None.Contains(Secret));
        }

        [Test]
        public void ordinary_http_diagnostics_and_private_status_codes_are_not_removed()
        {
            var ordinary = new HttpRequest("https://indexer.example/api?query=Dune");
            Assert.That(ordinary.ToString(), Does.Contain("/api?query=Dune"));
            Assert.That(ordinary.SanitizeLogMessage("diagnostic detail"), Is.EqualTo("diagnostic detail"));

            var request = new HttpRequest("https://notify.example/4?topic=BadRequest") { RedactUrlInLogs = true };
            var failure = new HttpException(Reply(request, HttpStatusCode.BadRequest, ""));
            Assert.That(failure.Message, Does.Contain("400:BadRequest"));
            Assert.That(failure.Message, Does.Contain("notify.example/(removed)"));
        }
    }
}
