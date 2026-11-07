using System;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Chaptarr.Http.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Options;
using NzbDrone.Core.Configuration;

namespace Chaptarr.Core.Test.Http.Authentication
{
    [TestFixture]
    [NonParallelizable]
    public class ApiKeyAuthenticationHandlerFixture
    {
        private const string ApiKey = "fixture-api-key";
        private string _previousBookshelfApiKeyQuery;
        private string _previousBookshelfSeerrKey;

        [SetUp]
        public void Setup()
        {
            _previousBookshelfApiKeyQuery = Environment.GetEnvironmentVariable("BOOKSHELF_ALLOW_API_KEY_QUERY");
            _previousBookshelfSeerrKey = Environment.GetEnvironmentVariable("BOOKSHELF_SEERRNG_API_KEY");
            Environment.SetEnvironmentVariable("BOOKSHELF_ALLOW_API_KEY_QUERY", null);
            Environment.SetEnvironmentVariable("BOOKSHELF_SEERRNG_API_KEY", null);
        }

        [TearDown]
        public void TearDown()
        {
            Environment.SetEnvironmentVariable("BOOKSHELF_ALLOW_API_KEY_QUERY", _previousBookshelfApiKeyQuery);
            Environment.SetEnvironmentVariable("BOOKSHELF_SEERRNG_API_KEY", _previousBookshelfSeerrKey);
        }

        [Test]
        public async Task should_allow_query_api_key_by_default()
        {
            var result = await Authenticate("API", "apikey", "?apikey=fixture-api-key", new AuthOptions());

            Assert.That(result.Succeeded, Is.True);
        }

        [Test]
        public async Task should_reject_query_api_key_when_configured_off()
        {
            var result = await Authenticate("API", "apikey", "?apikey=fixture-api-key", new AuthOptions { AllowApiKeyQuery = false });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Failure, Is.Null);
        }

        [Test]
        public async Task should_honor_the_bookshelf_environment_switch_when_chaptarr_setting_is_unset()
        {
            Environment.SetEnvironmentVariable("BOOKSHELF_ALLOW_API_KEY_QUERY", "false");

            var result = await Authenticate("API", "apikey", "?apikey=fixture-api-key", new AuthOptions());

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Failure, Is.Null);
        }

        [Test]
        public async Task should_prefer_chaptarr_setting_over_bookshelf_environment_switch()
        {
            Environment.SetEnvironmentVariable("BOOKSHELF_ALLOW_API_KEY_QUERY", "false");

            var result = await Authenticate("API", "apikey", "?apikey=fixture-api-key", new AuthOptions { AllowApiKeyQuery = true });

            Assert.That(result.Succeeded, Is.True);
        }

        [Test]
        public async Task should_accept_legacy_bookshelf_seerr_key_when_chaptarr_key_is_unset()
        {
            Environment.SetEnvironmentVariable("BOOKSHELF_SEERRNG_API_KEY", "legacy-seerr-key");

            var result = await Authenticate("API", "apikey", "?apikey=legacy-seerr-key", new AuthOptions());

            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.Principal.FindFirst("ApiKeyScope").Value, Is.EqualTo("seerrng"));
        }

        [Test]
        public async Task should_prefer_chaptarr_seerr_key_over_legacy_environment_key()
        {
            Environment.SetEnvironmentVariable("BOOKSHELF_SEERRNG_API_KEY", "legacy-seerr-key");

            var chaptarrKey = await Authenticate("API", "apikey", "?apikey=chaptarr-seerr-key", new AuthOptions(), "chaptarr-seerr-key");
            var legacyKey = await Authenticate("API", "apikey", "?apikey=legacy-seerr-key", new AuthOptions(), "chaptarr-seerr-key");

            Assert.That(chaptarrKey.Succeeded, Is.True);
            Assert.That(chaptarrKey.Principal.FindFirst("ApiKeyScope").Value, Is.EqualTo("seerrng"));
            Assert.That(legacyKey.Succeeded, Is.False);
        }

        [Test]
        public async Task should_not_fall_back_to_legacy_seerr_key_when_chaptarr_key_is_explicitly_empty()
        {
            Environment.SetEnvironmentVariable("BOOKSHELF_SEERRNG_API_KEY", "legacy-seerr-key");

            var result = await Authenticate("API", "apikey", "?apikey=legacy-seerr-key", new AuthOptions(), string.Empty);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Failure, Is.Null);
        }

        [Test]
        public async Task should_continue_to_accept_header_api_keys_when_query_keys_are_disabled()
        {
            var context = new DefaultHttpContext();
            context.Request.Headers["X-Api-Key"] = ApiKey;
            var result = await Authenticate(context, "API", "apikey", new AuthOptions { AllowApiKeyQuery = false });

            Assert.That(result.Succeeded, Is.True);
        }

        [Test]
        public async Task should_continue_to_accept_bearer_api_keys_when_query_keys_are_disabled()
        {
            var context = new DefaultHttpContext();
            context.Request.Headers.Authorization = $"Bearer {ApiKey}";
            var result = await Authenticate(context, "API", "apikey", new AuthOptions { AllowApiKeyQuery = false });

            Assert.That(result.Succeeded, Is.True);
        }

        [Test]
        public async Task should_continue_to_accept_signalr_access_tokens_when_api_key_query_is_disabled()
        {
            var result = await Authenticate("SignalR", "access_token", "?access_token=fixture-api-key", new AuthOptions { AllowApiKeyQuery = false });

            Assert.That(result.Succeeded, Is.True);
        }

        private static async Task<AuthenticateResult> Authenticate(
            string schemeName,
            string queryName,
            string queryString,
            AuthOptions authOptions,
            string seerrApiKey = null)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = "GET";
            context.Request.Path = "/api/v1/system/status";
            context.Request.QueryString = new QueryString(queryString);
            return await Authenticate(context, schemeName, queryName, authOptions, seerrApiKey);
        }

        private static async Task<AuthenticateResult> Authenticate(
            HttpContext context,
            string schemeName,
            string queryName,
            AuthOptions authOptions,
            string seerrApiKey = null)
        {
            var config = new Mock<IConfigFileProvider>();
            config.SetupGet(x => x.ApiKey).Returns(ApiKey);
            config.SetupGet(x => x.SeerrApiKey).Returns(seerrApiKey);

            var options = new ApiKeyAuthenticationOptions
            {
                HeaderName = "X-Api-Key",
                QueryName = queryName
            };
            var handler = new ApiKeyAuthenticationHandler(
                new StaticOptionsMonitor<ApiKeyAuthenticationOptions>(options),
                LoggerFactory.Create(_ => { }),
                UrlEncoder.Default,
                config.Object,
                Options.Create(authOptions));

            var scheme = new AuthenticationScheme(schemeName, schemeName, typeof(ApiKeyAuthenticationHandler));
            await handler.InitializeAsync(scheme, context);
            return await handler.AuthenticateAsync();
        }

        private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
        {
            private readonly T _value;

            public StaticOptionsMonitor(T value)
            {
                _value = value;
            }

            public T CurrentValue => _value;
            public T Get(string name) => _value;
            public IDisposable OnChange(Action<T, string> listener) => NullDisposable.Instance;

            private sealed class NullDisposable : IDisposable
            {
                public static readonly NullDisposable Instance = new();

                public void Dispose()
                {
                }
            }
        }
    }
}
