using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using Chaptarr.Api.V1.System.Backup;
using Chaptarr.Http;
using Chaptarr.Http.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using NzbDrone.Host;

namespace Chaptarr.Core.Test.Http
{
    [TestFixture]
    public class HttpSecuritySettingsFixture
    {
        [Test]
        public void should_only_process_forwarded_for_and_proto_from_explicitly_trusted_proxies()
        {
            using var provider = CreateServices(CreateConfiguration()).BuildServiceProvider();
            var options = provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

            Assert.That(options.ForwardedHeaders, Is.EqualTo(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto));
            Assert.That(options.ForwardLimit, Is.EqualTo(1));
            Assert.That(options.RequireHeaderSymmetry, Is.True);
            Assert.That(options.KnownIPNetworks.Any(network => network.Contains(IPAddress.Parse("10.20.30.40"))), Is.False);
            Assert.That(options.KnownIPNetworks.Any(network => network.Contains(IPAddress.Parse("192.168.4.5"))), Is.False);
            Assert.That(options.KnownIPNetworks.Any(network => network.Contains(IPAddress.Parse("100.64.1.2"))), Is.False);
        }

        [Test]
        public void should_accept_configured_legacy_proxy_ips_and_cidrs_with_chaptarr_settings_preferred()
        {
            var configuration = CreateConfiguration(new Dictionary<string, string>
            {
                ["Readarr:Server:TrustedProxies"] = "10.0.0.0/8;192.0.2.10",
                ["Chaptarr:Server:TrustedProxies"] = "192.168.0.0/16, 2001:db8::5"
            });
            using var provider = CreateServices(configuration).BuildServiceProvider();
            var options = provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

            Assert.That(options.KnownIPNetworks.Any(network => network.Contains(IPAddress.Parse("192.168.4.5"))), Is.True);
            Assert.That(options.KnownIPNetworks.Any(network => network.Contains(IPAddress.Parse("10.20.30.40"))), Is.False);
            Assert.That(options.KnownProxies, Does.Contain(IPAddress.Parse("2001:db8::5")));
            Assert.That(options.KnownProxies, Does.Not.Contain(IPAddress.Parse("192.0.2.10")));
        }

        [Test]
        public void should_disable_cross_origin_cors_until_origins_are_configured()
        {
            using var provider = CreateServices(CreateConfiguration()).BuildServiceProvider();
            var policies = provider.GetRequiredService<IOptions<CorsOptions>>().Value;
            var apiPolicy = policies.GetPolicy(VersionedApiControllerAttribute.API_CORS_POLICY);
            var getPolicy = policies.GetPolicy("AllowGet");

            Assert.That(apiPolicy.AllowAnyOrigin, Is.False);
            Assert.That(apiPolicy.Origins, Is.Empty);
            Assert.That(getPolicy.AllowAnyOrigin, Is.False);
            Assert.That(getPolicy.Origins, Is.Empty);
        }

        [Test]
        public void should_apply_configured_cors_origins_to_api_and_static_get_policies()
        {
            var configuration = CreateConfiguration(new Dictionary<string, string>
            {
                ["Readarr:Server:AllowedOrigins"] = "https://legacy.example",
                ["Chaptarr:Server:AllowedOrigins"] = "https://app.example; https://admin.example"
            });
            using var provider = CreateServices(configuration).BuildServiceProvider();
            var policies = provider.GetRequiredService<IOptions<CorsOptions>>().Value;
            var apiPolicy = policies.GetPolicy(VersionedApiControllerAttribute.API_CORS_POLICY);
            var getPolicy = policies.GetPolicy("AllowGet");

            Assert.That(apiPolicy.AllowAnyOrigin, Is.False);
            Assert.That(apiPolicy.Origins, Is.EquivalentTo(new[] { "https://app.example", "https://admin.example" }));
            Assert.That(apiPolicy.ExposedHeaders, Does.Contain("X-Bookshelf-Request-Id"));
            Assert.That(getPolicy.AllowAnyOrigin, Is.False);
            Assert.That(getPolicy.Origins, Is.EquivalentTo(new[] { "https://app.example", "https://admin.example" }));
            Assert.That(getPolicy.Methods, Is.EquivalentTo(new[] { "GET", "OPTIONS" }));
        }

        [Test]
        public async Task should_rate_limit_each_remote_ip_and_configure_429_rejections()
        {
            using var provider = CreateServices(CreateConfiguration()).BuildServiceProvider();
            var options = provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value;
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.10");

            Assert.That(options.RejectionStatusCode, Is.EqualTo(StatusCodes.Status429TooManyRequests));

            for (var request = 0; request < HttpSecuritySettings.DefaultRequestsPerMinute; request++)
            {
                using var lease = await options.GlobalLimiter.AcquireAsync(context);
                Assert.That(lease.IsAcquired, Is.True, $"request {request + 1} should be permitted");
            }

            using (var rejected = await options.GlobalLimiter.AcquireAsync(context))
            {
                Assert.That(rejected.IsAcquired, Is.False);
            }

            context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.11");
            using var otherClient = await options.GlobalLimiter.AcquireAsync(context);
            Assert.That(otherClient.IsAcquired, Is.True);
        }

        [Test]
        public async Task should_include_the_legacy_request_id_header_even_when_request_logging_is_disabled()
        {
            var context = new DefaultHttpContext();
            var middleware = new LoggingMiddleware(_ => Task.CompletedTask);

            await middleware.InvokeAsync(context);

            Assert.That(context.Response.Headers["X-Bookshelf-Request-Id"].ToString(), Is.Not.Empty);
        }

        [Test]
        public void should_keep_the_backup_restore_upload_limit_above_the_general_request_limit()
        {
            var method = typeof(BackupController).GetMethod(nameof(BackupController.UploadAndRestore));
            var requestSizeLimit = method.GetCustomAttribute<RequestSizeLimitAttribute>();
            var formLimits = method.GetCustomAttribute<RequestFormLimitsAttribute>();
            const long backupRestoreLimit = 1024L * 1024 * 1024;

            Assert.That(((IRequestSizeLimitMetadata)requestSizeLimit).MaxRequestBodySize, Is.EqualTo(backupRestoreLimit));
            Assert.That(formLimits.MultipartBodyLengthLimit, Is.EqualTo(backupRestoreLimit));
        }

        [TestCase(null, HttpSecuritySettings.DefaultRequestsPerMinute)]
        [TestCase("1", HttpSecuritySettings.MinimumRequestsPerMinute)]
        [TestCase("300", 300)]
        [TestCase("9000", HttpSecuritySettings.MaximumRequestsPerMinute)]
        public void should_bound_configured_requests_per_minute(string configuredValue, int expected)
        {
            var configuration = CreateConfiguration(new Dictionary<string, string>
            {
                ["Readarr:Server:RequestsPerMinute"] = configuredValue
            });

            Assert.That(HttpSecuritySettings.GetRequestsPerMinute(configuration), Is.EqualTo(expected));
        }

        [TestCase(null, HttpSecuritySettings.DefaultMaxRequestBodySize)]
        [TestCase("0", HttpSecuritySettings.MinimumMaxRequestBodySize)]
        [TestCase("8388608", 8388608)]
        [TestCase("268435456", HttpSecuritySettings.MaximumMaxRequestBodySize)]
        public void should_bound_configured_global_request_body_size(string configuredValue, long expected)
        {
            var configuration = CreateConfiguration(new Dictionary<string, string>
            {
                ["Readarr:Server:MaxRequestBodySize"] = configuredValue
            });

            Assert.That(HttpSecuritySettings.GetMaxRequestBodySize(configuration), Is.EqualTo(expected));
        }

        private static ServiceCollection CreateServices(IConfiguration configuration)
        {
            var services = new ServiceCollection();
            new Startup(configuration).ConfigureServices(services);
            return services;
        }

        private static IConfiguration CreateConfiguration(IDictionary<string, string> values = null)
        {
            var configurationValues = new Dictionary<string, string>(values ?? new Dictionary<string, string>())
            {
                ["dataProtectionFolder"] = System.IO.Path.GetTempPath()
            };

            return new ConfigurationBuilder()
                .AddInMemoryCollection(configurationValues)
                .Build();
        }
    }
}
