using System.Linq;
using System.Text;
using NLog;
using NLog.Config;
using NLog.Targets;
using NzbDrone.Common.Instrumentation;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Common
{
    [TestFixture]
    public class CleanseLogMessageFixture
    {
        [TestCase("api_key=synthetic-secret", "api_key=(removed)")]
        [TestCase("Archive refused api_key=synthetic-secret", "Archive refused api_key=(removed)")]
        [TestCase("to=alice%40example.com", "to=(removed)%40example.com")]
        [TestCase("Alice <alice@example.com>", "Alice <(removed)@example.com>")]
        [TestCase("иван@example.com", "(removed)@example.com")]
        [TestCase("读者@example.com", "(removed)@example.com")]
        [TestCase("प्रेम@example.com", "(removed)@example.com")]
        [TestCase("@handle Name (@handle) (removed)@host", "@handle Name (@handle) (removed)@host")]
        [TestCase("https://hooks.slack.com/services/T0/B0/synthetic-secret", "https://hooks.slack.com/services/(removed)")]
        [TestCase("https://hooks.slack-gov.com/services/T0/B0/synthetic-secret", "https://hooks.slack-gov.com/services/(removed)")]
        [TestCase("https://discordapp.com/api/webhooks/123/synthetic-secret", "https://discordapp.com/api/webhooks/(removed)/(removed)")]
        [TestCase("https://apprise.example/base/notify/synthetic-secret?tag=books", "https://apprise.example/base/notify/(removed)?tag=books")]
        [TestCase("https://apprise.example/notify", "https://apprise.example/notify")]
        [TestCase("{\"data\":{\"sid\":\"synthetic-secret\",\"inside\":\"keep\",\"residual\":\"keep\"}}", "{\"data\":{\"sid\":\"(removed)\",\"inside\":\"keep\",\"residual\":\"keep\"}}")]
        [TestCase("{\"sessionID\":\"synthetic-secret\"}", "{\"sessionID\":\"(removed)\"}")]
        [TestCase("{\"mam_id\":\"synthetic-secret\"}", "{\"mam_id\":\"(removed)\"}")]
        [TestCase("mam_id=synthetic-secret", "mam_id=(removed)")]
        [TestCase("https://books.example/rest/ping?u=synthetic-user&p=synthetic-secret&c=Chaptarr&v=1.15", "https://books.example/rest/ping?u=(removed)&p=(removed)&c=Chaptarr&v=1.15")]
        [TestCase("https://books.example/rest/ping?p=synthetic-secret&u=synthetic-user&c=Chaptarr", "https://books.example/rest/ping?p=(removed)&u=(removed)&c=Chaptarr")]
        [TestCase("https://books.example/rest/ping?u=synthetic-user&amp;p=synthetic-secret", "https://books.example/rest/ping?u=(removed)&amp;p=(removed)")]
        [TestCase("https://books.example/callback?code_verifier=synthetic-secret", "https://books.example/callback?code_verifier=(removed)")]
        [TestCase("https://books.example/search?u=keep&p=2", "https://books.example/search?u=keep&p=2")]
        [TestCase("Cookie: session=abc123; other=def456", "Cookie: (removed)")]
        public void provider_wire_shapes_use_the_shared_credential_rules(string input, string expected)
        {
            Assert.That(CleanseLogMessage.Cleanse(input), Is.EqualTo(expected));
            Assert.That(CleanseLogMessage.Cleanse(expected), Is.EqualTo(expected), "Redaction must be idempotent");
        }

        [TestCase("<link>?pwd=secret</link>", "<link>?pwd=(removed)</link>")]
        [TestCase("<link>?i=1&pwd=secret</link>", "<link>?i=1&pwd=(removed)</link>")]
        [TestCase("<link>?i=1&amp;pwd=secret</link>", "<link>?i=1&amp;pwd=(removed)</link>")]
        [TestCase("?sessionid=secret&_sid=secret", "?sessionid=(removed)&_sid=(removed)")]
        [TestCase("Host=db;Password=alpha<tag>secret;Port=5432", "Host=db;Password=(removed);Port=5432")]
        [TestCase("Host=db;Pwd=alpha<tag>secret;Port=5432", "Host=db;Pwd=(removed);Port=5432")]
        [TestCase("https://books.example/rest/ping.view?p=enc:secret&v=1", "https://books.example/rest/ping.view?p=(removed)&v=1")]
        [TestCase("https://books.example/rest/ping.view?user=name&amp;p=secret&amp;v=1", "https://books.example/rest/ping.view?user=(removed)&amp;p=(removed)&amp;v=1")]
        [TestCase("https://indexer.example/search?p=2", "https://indexer.example/search?p=2")]
        public void should_preserve_credential_masking_across_query_and_connection_string_boundaries(string input, string expected)
        {
            Assert.That(CleanseLogMessage.Cleanse(input), Is.EqualTo(expected));
        }

        [Test]
        public void should_redact_keys_in_trace_xml_without_removing_metadata(
            [Values("getnzb/book.nzb?r", "api?apikey", "api?api_key", "download?torrent_pass", "download?authkey", "download?token")] string pathAndKey,
            [Values("?", "&", "&amp;")] string separator)
        {
            const string secret = "synthetic-xml-key";
            var query = separator == "?" ? separator : "?i=1" + separator;
            var url = $"https://indexer.example/{pathAndKey.Replace("?", query)}={secret}";
            var body = $"<rss><item><title>Synthetic Title</title><link>{url}</link><enclosure url=\"{url}&amp;type=nzb\"/><enclosure url='{url}'/></item></rss>";
            var memory = new MemoryTarget { Layout = new CleanseLayout { Inner = "${level}|${message}" } };
            var configuration = new LoggingConfiguration();
            configuration.AddRule(LogLevel.Trace, LogLevel.Fatal, memory);
            using var logs = new LogFactory { Configuration = configuration };

            logs.GetLogger("HttpClient").Trace("Response content ({0} bytes): {1}", Encoding.UTF8.GetByteCount(body), body);

            Assert.That(memory.Logs.Single(), Is.EqualTo(
                $"Trace|Response content ({Encoding.UTF8.GetByteCount(body)} bytes): {body.Replace(secret, "(removed)")}"));
        }

        [Test]
        public void should_redact_each_xml_link_without_consuming_its_neighbor(
            [Values("apikey", "torrent_pass")] string firstKey,
            [Values("apikey", "torrent_pass")] string secondKey,
            [Values("", "\n")] string separator)
        {
            var body = $"<link>https://indexer.example/api?{firstKey}=synthetic-first</link>{separator}" +
                $"<link>https://indexer.example/api?{secondKey}=synthetic-second</link>";

            Assert.That(CleanseLogMessage.Cleanse(body), Is.EqualTo(
                body.Replace("synthetic-first", "(removed)").Replace("synthetic-second", "(removed)")));
        }

        [Test]
        public void should_not_apply_getnzb_key_rule_outside_its_url()
        {
            const string body = "<link>https://indexer.example/getnzb/book.nzb</link><title>Book?r=keep</title>";

            Assert.That(CleanseLogMessage.Cleanse(body), Is.EqualTo(body));
        }

        [TestCase("<auth token=\"synthetic-secret\"/>", "synthetic-secret")]
        [TestCase("<auth token='synthetic-secret'/>", "synthetic-secret")]
        [TestCase("<auth token=\"synthetic'quoted&value\"/>", "synthetic'quoted&value")]
        [TestCase("<auth token='synthetic\"quoted&value'/>", "synthetic\"quoted&value")]
        [TestCase("username=\"synthetic name\" next=keep", "synthetic name")]
        [TestCase("token=\"synthetic=with;delimiters\" next=keep", "synthetic=with;delimiters")]
        [TestCase("token=\"synthetic\\\"quoted\" next=keep", "synthetic\\\"quoted")]
        [TestCase("https://indexer.example/api?apikey=synthetic'key&format=xml", "synthetic'key")]
        [TestCase("https://indexer.example/api?apikey=synthetic'key", "synthetic'key")]
        [TestCase("https://indexer.example/api?apikey=synthetic\"key", "synthetic\"key")]
        [TestCase("https://indexer.example/api?apikey='synthetic'key", "'synthetic'key")]
        [TestCase("{\"uri\":\"https://indexer.example/api?apikey=synthetic'key\",\"title\":\"keep\"}", "synthetic'key")]
        [TestCase("[\"https://indexer.example/api?apikey=synthetic-key\"]", "synthetic-key")]
        [TestCase("{\"uri\":\"https://indexer.example/api?apikey=synthetic-key\"}", "synthetic-key")]
        [TestCase("https://indexer.example/api?apikey=synthetic,key&format=xml", "synthetic,key")]
        [TestCase("<link>https://indexer.example/api?apikey=synthetic'key</link>", "synthetic'key")]
        [TestCase("<enclosure url=\"https://indexer.example/api?apikey=synthetic'key\"/>", "synthetic'key")]
        [TestCase("<enclosure url='https://indexer.example/api?apikey=synthetic-key'/>", "synthetic-key")]
        [TestCase("<link><![CDATA[https://indexer.example/api?apikey=synthetic-key]]></link>", "synthetic-key")]
        [TestCase("https://indexer.example/api?apikey=synthetic]key&format=xml", "synthetic]key")]
        [TestCase("https://indexer.example/rss?t=-2&dl=1&i=42&r=synthetic-key", "synthetic-key")]
        [TestCase("<link>https://indexer.example/rss?t=-2&amp;r=synthetic-key</link>", "synthetic-key")]
        [TestCase("<enclosure url='https://indexer.example/rss?t=-2&amp;r=synthetic-key'/>", "synthetic-key")]
        [TestCase("https://indexer.example/feeds.php?feed=torrents_notify_synthetic-key&format=xml", "synthetic-key")]
        [TestCase("<link href=\"https://indexer.example/feeds.php?feed=torrents_notify_synthetic-key&amp;format=xml\"/>", "synthetic-key")]
        public void should_mask_complete_credential_without_consuming_its_container(string message, string secret)
        {
            Assert.That(CleanseLogMessage.Cleanse(message), Is.EqualTo(message.Replace(secret, "(removed)")));
        }

        [TestCase("?apikey=alpha'token=SECOND", "?apikey=(removed)'token=(removed)")]
        [TestCase("<link>https://example.invalid/?apikey=alpha\"token=SECOND</link>", "<link>https://example.invalid/?apikey=(removed)\"token=(removed)</link>")]
        [TestCase("?apikey=alpha'password=SECOND", "?apikey=(removed)'password=(removed)")]
        [TestCase("?apikey=alpha<SECOND&mode=keep", "?apikey=(removed)&mode=keep")]
        [TestCase("<link>?apikey=alpha<SECOND</link><title>keep</title>", "<link>?apikey=(removed)</link><title>keep</title>")]
        [TestCase("<link>?apikey=alpha<title>keep</title></link>", "<link>?apikey=(removed)<title>keep</title></link>")]
        [TestCase("<link>?username=alpha</link><title>keep</title>", "<link>?username=(removed)</link><title>keep</title>")]
        [TestCase("<link>?password=alpha&amp;mode=keep</link><title>keep</title>", "<link>?password=(removed)&amp;mode=keep</link><title>keep</title>")]
        [TestCase("<link>?password=alpha&mode=keep</link><title>keep</title>", "<link>?password=(removed)&mode=keep</link><title>keep</title>")]
        [TestCase("<link>?X-Plex-Token=alpha</link><title>keep</title>", "<link>?X-Plex-Token=(removed)</link><title>keep</title>")]
        [TestCase("<link>?client_secret=alpha<SECOND</link><title>keep</title>", "<link>?client_secret=(removed)</link><title>keep</title>")]
        [TestCase("token=\"\" next=keep", "token=\"(removed)\" next=keep")]
        [TestCase("token='' next=keep", "token='(removed)' next=keep")]
        [TestCase("?apikey=&mode=keep", "?apikey=&mode=keep")]
        [TestCase("token=\"alpha\\\"SECOND\" next=keep", "token=\"(removed)\" next=keep")]
        [TestCase("<auth password=\"alpha;SECOND\"/><title>keep</title>", "<auth password=\"(removed)\"/><title>keep</title>")]
        [TestCase("Host=db;Password=\"alpha;SECOND\";Port=5432", "Host=db;Password=\"(removed)\";Port=5432")]
        [TestCase("Host=db;Password='alpha;SECOND';Port=5432", "Host=db;Password='(removed)';Port=5432")]
        [TestCase("Host=db;Password=\"alpha\"\"SECOND\";Port=5432", "Host=db;Password=\"(removed)\";Port=5432")]
        [TestCase("Host=db;Password=alpha&SECOND=tail;Port=5432", "Host=db;Password=(removed);Port=5432")]
        [TestCase("<link>?apikey=alpha<SECOND'token=THIRD</link><title>keep</title>", "<link>?apikey=(removed)'token=(removed)</link><title>keep</title>")]
        [TestCase("?apikey=\"alpha\"token=SECOND", "?apikey=\"(removed)\"token=(removed)")]
        [TestCase("{\"password\":\"alpha\\\"SECOND\",\"title\":\"keep\"}", "{\"password\":\"(removed)\",\"title\":\"keep\"}")]
        [TestCase("?token=alpha'apikey=SECOND", "?token=(removed)'apikey=(removed)")]
        [TestCase("?apikey=alpha'X-Plex-Token=SECOND", "?apikey=(removed)'X-Plex-Token=(removed)")]
        [TestCase("{\"Name\":\"ControlPassword\",\"Value\":\"alpha\\\"SECOND\",\"Info\":\"keep\"}", "{\"Name\":\"ControlPassword\",\"Value\":\"(removed)\",\"Info\":\"keep\"}")]
        [TestCase("{\"Name\":\"Server1.Username\",\"Value\":\"alpha\\\"SECOND\",\"Info\":\"keep\"}", "{\"Name\":\"Server1.Username\",\"Value\":\"(removed)\",\"Info\":\"keep\"}")]
        [TestCase("[\"webui.password\",2,\"alpha\\\"SECOND\",{\"access\":\"keep\"}]", "[\"webui.password\",2,\"(removed)\",{\"access\":\"keep\"}]")]
        [TestCase("[\"boss_key\",0,\"alpha\\\"SECOND\",{\"access\":\"keep\"}]", "[\"boss_key\",0,\"(removed)\",{\"access\":\"keep\"}]")]
        [TestCase("auth.login(\"alpha\\\"SECOND\") result=keep", "auth.login(\"(removed)\") result=keep")]
        [TestCase("{\"username\":\"alpha\\\"SECOND\",\"title\":\"keep\"}", "{\"username\":\"(removed)\",\"title\":\"keep\"}")]
        [TestCase("{\"email_from\":\"alpha\\\"SECOND\",\"title\":\"keep\"}", "{\"email_from\":\"(removed)\",\"title\":\"keep\"}")]
        [TestCase("{\"method\":\"getTorrents\",\"params\":[\"alpha\\\"SECOND\",\"keep\"]}", "{\"method\":\"getTorrents\",\"params\":[\"(removed)\",\"keep\"]}")]
        [TestCase("getTorrents(\"alpha\\\"SECOND\", [keep], 100, 0)", "getTorrents(\"(removed)\", [keep], 100, 0)")]
        [TestCase("{\"Name\":\"ControlPassword\",\"Value\":\"alpha\\\\\",\"Info\":\"keep\"}", "{\"Name\":\"ControlPassword\",\"Value\":\"(removed)\",\"Info\":\"keep\"}")]
        public void should_preserve_credential_boundaries_and_diagnostic_markup(string message, string expected)
        {
            Assert.That(CleanseLogMessage.Cleanse(message), Is.EqualTo(expected));
        }

        [TestCase("<link>https://indexer.example/rss</link><title>Book?r=keep</title>")]
        [TestCase("getnzb unavailable https://indexer.example/api?r=keep")]
        [TestCase("rss unavailable https://indexer.example/api?r=keep")]
        [TestCase("get_token=true")]
        public void should_preserve_text_outside_credential_context(string message)
        {
            Assert.That(CleanseLogMessage.Cleanse(message), Is.EqualTo(message));
        }

        [TestCase("http://user:pass@example.com/path", "user:pass")]
        [TestCase("http://user:pass@example.com/path?apikey=abc", "abc")]
        [TestCase("https://api.example.com/feed?api_key=query-key-value&format=json", "query-key-value")]
        [TestCase("Authorization: Bearer supersecrettoken", "supersecrettoken")]
        [TestCase("Proxy-Authorization: Basic dXNlcjpwYXNz", "dXNlcjpwYXNz")]
        [TestCase("X-API-Key: header-key-value", "header-key-value")]
        [TestCase("Cookie: session=abc123; other=def456", "abc123")]
        [TestCase("Set-Cookie: session=abc123; Path=/; HttpOnly", "abc123")]
        public void should_cleanse_common_secrets(string message, string secret)
        {
            var cleansed = CleanseLogMessage.Cleanse(message);

            Assert.That(cleansed, Does.Not.Contain(secret));
            Assert.That(cleansed, Does.Contain("(removed)"));
        }

        [Test]
        public void should_redact_download_fields_in_provider_response_without_hiding_metadata()
        {
            const string response = "{\"data\":[{\"title\":\"Synthetic Title\",\"author_info\":\"Synthetic Author\",\"series_info\":\"Synthetic Series\",\"dl\":\"mam-dl-token\",\"downloadURLs\":[\"https://indexer.example/download?tid=42&passkey=synthetic-passkey\",\"https://indexer.example/private/path-token\"]}]}";

            var cleansed = CleanseLogMessage.Cleanse($"Response content (synthetic): {response}");

            Assert.That(cleansed, Does.Not.Contain("mam-dl-token"));
            Assert.That(cleansed, Does.Not.Contain("synthetic-passkey"));
            Assert.That(cleansed, Does.Not.Contain("private/path-token"));
            Assert.That(cleansed, Does.Contain("\"title\":\"Synthetic Title\""));
            Assert.That(cleansed, Does.Contain("\"author_info\":\"Synthetic Author\""));
            Assert.That(cleansed, Does.Contain("\"series_info\":\"Synthetic Series\""));
            Assert.That(cleansed, Does.Contain("\"dl\":\"(removed)\""));
            Assert.That(cleansed, Does.Contain("\"downloadURLs\":[\"(removed)\",\"(removed)\"]"));
        }

        [Test]
        public void should_redact_credentials_in_trace_response_body_and_keep_http_error_summary()
        {
            const string errorBody = "{\"message\":\"synthetic provider error\",\"Authorization\":\"Bearer synthetic-auth-token\",\"Cookie\":\"mam_id=synthetic-cookie; mam_ssl=1\",\"Set-Cookie\":\"session=synthetic-response-cookie; Path=/\",\"X-API-Key\":\"synthetic-json-key\"}";
            var request = new NzbDrone.Common.Http.HttpRequest("https://api.example.com/feed?api_key=synthetic-query-key&format=json");
            var response = new NzbDrone.Common.Http.HttpResponse(request, new NzbDrone.Common.Http.HttpHeader(), errorBody, System.Net.HttpStatusCode.BadGateway);

            var cleansedTraceBody = CleanseLogMessage.Cleanse($"Response content ({response.ResponseData.Length} bytes): {response.Content}");
            var cleansedErrorSummary = CleanseLogMessage.Cleanse(response.ToString());

            foreach (var secret in new[] { "synthetic-query-key", "synthetic-auth-token", "synthetic-cookie", "synthetic-response-cookie", "synthetic-json-key" })
            {
                Assert.That(cleansedTraceBody, Does.Not.Contain(secret));
                Assert.That(cleansedErrorSummary, Does.Not.Contain(secret));
            }

            Assert.That(cleansedTraceBody, Does.Contain("synthetic provider error"));
            Assert.That(cleansedErrorSummary, Does.Contain("502.BadGateway"));
            Assert.That(cleansedErrorSummary, Does.Not.Contain("synthetic provider error"));
        }
    }
}
