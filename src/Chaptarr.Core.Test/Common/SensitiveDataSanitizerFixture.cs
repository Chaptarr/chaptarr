using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Common.Instrumentation;

namespace Chaptarr.Core.Test.Common
{
    [TestFixture]
    public class SensitiveDataSanitizerFixture
    {
        [TestCase("https://api.example.com/endpoint?apikey=supersecretkey123&other=value", "supersecretkey123")]
        [TestCase("https://id.example/callback?code=oauth-code&client_secret=client-secret", "oauth-code")]
        [TestCase("https://id.example/callback?code=oauth-code&client_secret=client-secret", "client-secret")]
        [TestCase("https://id.example/token?id_token=id-token&refresh_token=refresh-token", "id-token")]
        [TestCase("https://id.example/token?id_token=id-token&refresh_token=refresh-token", "refresh-token")]
        public void should_fully_redact_sensitive_url_parameters(string url, string secret)
        {
            var sanitized = SensitiveDataSanitizer.SanitizeUrl(url);

            Assert.That(sanitized, Does.Not.Contain(secret));
            Assert.That(sanitized, Does.Contain("<REDACTED>"));
        }

        [Test]
        public void should_fully_redact_sensitive_headers_and_cookies()
        {
            var headers = new Dictionary<string, string>
            {
                { "Authorization", "Bearer auth-token-value" },
                { "X-API-Key", "api-key-value" },
                { "X-Client-Secret", "client-secret-value" },
                { "Cookie", "auth=cookie-secret; session=session-secret; ChaptarrPlexAuthState=state-secret; preference=private-value" },
                { "Content-Type", "application/json" }
            };

            var sanitized = SensitiveDataSanitizer.SanitizeHeaders(headers);

            Assert.That(sanitized["Authorization"], Is.EqualTo("<REDACTED>"));
            Assert.That(sanitized["X-API-Key"], Is.EqualTo("<REDACTED>"));
            Assert.That(sanitized["X-Client-Secret"], Is.EqualTo("<REDACTED>"));
            Assert.That(sanitized["Cookie"], Is.EqualTo("auth=<REDACTED>; session=<REDACTED>; ChaptarrPlexAuthState=<REDACTED>; preference=<REDACTED>"));
            Assert.That(sanitized["Content-Type"], Is.EqualTo("application/json"));
        }

        [Test]
        public void should_redact_auth_secrets_embedded_in_json_log_content()
        {
            const string content = "{\"access_token\":\"access-secret\",\"refresh_token\":\"refresh-secret\",\"oidcClientSecret\":\"client-secret\",\"authorization\":\"Bearer auth-secret\"}";

            var sanitized = SensitiveDataSanitizer.SanitizeObject(content);
            var cleansed = CleanseLogMessage.Cleanse(content);

            Assert.That(sanitized, Does.Not.Contain("access-secret"));
            Assert.That(sanitized, Does.Not.Contain("refresh-secret"));
            Assert.That(sanitized, Does.Not.Contain("client-secret"));
            Assert.That(sanitized, Does.Not.Contain("auth-secret"));
            Assert.That(cleansed, Does.Not.Contain("access-secret"));
            Assert.That(cleansed, Does.Not.Contain("refresh-secret"));
            Assert.That(cleansed, Does.Not.Contain("client-secret"));
            Assert.That(cleansed, Does.Not.Contain("auth-secret"));
        }

        [Test]
        public void sanitized_log_message_should_clean_the_format_even_without_arguments()
        {
            const string content = "Authorization: Bearer auth-secret";

            var sanitized = SensitiveDataSanitizer.SanitizeLogMessage(content);

            Assert.That(sanitized, Does.Not.Contain("auth-secret"));
        }

        [TestCase("code=oauth-code&client_secret=client-secret", "oauth-code")]
        [TestCase("code=oauth-code&client_secret=client-secret", "client-secret")]
        [TestCase("authorization_code=auth-code&refresh_token=refresh-secret", "auth-code")]
        [TestCase("authorization_code=auth-code&refresh_token=refresh-secret", "refresh-secret")]
        public void final_log_cleanser_should_redact_form_encoded_oauth_secrets(string content, string secret)
        {
            var cleansed = CleanseLogMessage.Cleanse(content);

            Assert.That(cleansed, Does.Not.Contain(secret));
            Assert.That(cleansed, Does.Contain("(removed)"));
        }

        [Test]
        public void should_handle_null_and_empty_values()
        {
            Assert.That(SensitiveDataSanitizer.SanitizeUrl(null), Is.Null);
            Assert.That(SensitiveDataSanitizer.SanitizeCookie(null), Is.Null);
            Assert.That(SensitiveDataSanitizer.SanitizeHeaders(null), Is.Null);
            Assert.That(SensitiveDataSanitizer.SanitizeUrl(string.Empty), Is.Empty);
            Assert.That(SensitiveDataSanitizer.SanitizeCookie(string.Empty), Is.Empty);
        }
    }
}
