using NzbDrone.Common.Instrumentation;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Common
{
    [TestFixture]
    public class CleanseLogMessageFixture
    {
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
