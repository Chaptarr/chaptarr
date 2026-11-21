using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Common;
using Chaptarr.Http.Authentication;
using Chaptarr.Http.Middleware;
using DryIoc;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Composition.Extensions;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Options;
using NzbDrone.Core.Authentication;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Configuration.Events;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.HealthCheck.Checks;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Host;

namespace Chaptarr.Core.Test.Http.Authentication
{
    [TestFixture]
    [NonParallelizable]
    public class HostHeaderSecurityFixture
    {
        [TestCase("library.example", null, 200)]
        [TestCase("evil.example", null, 400)]
        [TestCase("evil.example", "localhost", 400)]
        [TestCase("library.example", "evil.example", 400)]
        [TestCase("localhost", "library.example", 200)]
        [TestCase("localhost:8789", null, 200)]
        [TestCase("127.0.0.1:8789", null, 200)]
        [TestCase("[::1]:8789", null, 200)]
        [TestCase("[2001:db8::7]:8789", null, 200)]
        [TestCase("xn--bcher-kva.example", null, 200)]
        public async Task validates_raw_and_forwarded_host(string host, string forwardedHost, int status)
        {
            var config = Config("library.example, bücher.example, 2001:db8::7");
            using var services = Services(config);
            var http = Context(services, host, forwardedHost);
            var reached = false;
            var app = new ApplicationBuilder(services);
            app.UseValidatedForwardedHeaders();
            app.Run(_ => { reached = true; return Task.CompletedTask; });
            await app.Build()(http);
            Assert.That(http.Response.StatusCode, Is.EqualTo(status));
            Assert.That(reached, Is.EqualTo(status == 200));
        }

        [Test]
        public async Task unknown_peer_cannot_rewrite_an_allowed_host()
        {
            using var services = Services(Config("library.example"));
            var http = Context(services, "library.example", "evil.example");
            http.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.50");
            var app = new ApplicationBuilder(services);
            app.UseValidatedForwardedHeaders();
            app.Run(context => { Assert.That(context.Request.Host.Host, Is.EqualTo("library.example")); return Task.CompletedTask; });
            await app.Build()(http);
            Assert.That(http.Response.StatusCode, Is.EqualTo(200));
        }

        [TestCase("")]
        [TestCase(" , ; ")]
        public async Task empty_configuration_preserves_existing_proxy_access(string hosts)
        {
            using var services = Services(Config(hosts));
            var http = Context(services, "internal-proxy.example", "public-proxy.example");
            var app = new ApplicationBuilder(services);
            app.UseValidatedForwardedHeaders();
            app.Run(context => { Assert.That(context.Request.Host.Host, Is.EqualTo("public-proxy.example")); return Task.CompletedTask; });
            await app.Build()(http);
            Assert.That(http.Response.StatusCode, Is.EqualTo(200));
        }

        [TestCase("100.64.1.2", "localhost", "127.0.0.1", AuthenticationType.Forms, false, false)]
        [TestCase("100.64.1.2", "localhost", "127.0.0.1", AuthenticationType.Forms, true, true)]
        [TestCase("172.18.0.2", "localhost", "203.0.113.9, 172.17.0.1", AuthenticationType.Forms, false, false)]
        [TestCase("192.168.1.50", "evil.example", null, AuthenticationType.Forms, false, true)]
        [TestCase("192.168.1.50", "evil.example", null, AuthenticationType.None, false, true)]
        [TestCase("192.168.1.50", "127.0.0.1", null, AuthenticationType.Forms, false, true)]
        [TestCase("192.168.1.50", "localhost", null, AuthenticationType.None, false, true)]
        [TestCase("192.168.1.50", "library.example", null, AuthenticationType.Forms, false, true)]
        [TestCase("fd00::123", "[::1]", null, AuthenticationType.Forms, false, true)]
        [TestCase("192.168.1.50", "xn--bcher-kva.example", null, AuthenticationType.Forms, false, true)]
        public async Task ui_login_bypass_requires_the_original_peer_and_reported_clients_to_be_local(string peer, string host, string forwardedFor,
            AuthenticationType auth, bool trustCgnat, bool permitted)
        {
            // Host filtering and local-login eligibility are separate checks.
            var values = Values("", auth, AuthenticationRequiredType.DisabledForLocalAddresses);
            values["get_TrustCgnatIpAddresses"] = trustCgnat;
            using var services = Services(Proxy<IConfigFileProvider>(values));
            var http = Context(services, host, null);
            http.Connection.RemoteIpAddress = IPAddress.Parse(peer);
            if (forwardedFor != null) http.Request.Headers["X-Forwarded-For"] = forwardedFor;
            // Recognized application names still work when they are explicitly configured.
            if (host is "library.example" or "xn--bcher-kva.example")
                values["get_AllowedHosts"] = "library.example,bücher.example";
            await RunUiRequest(services, http);
            Assert.That(http.Response.StatusCode, Is.EqualTo(permitted ? 200 : 302));
            if (!permitted) Assert.That(http.Response.Headers.Location.ToString(), Does.StartWith("/chaptarr/login?"));
            if (peer == "172.18.0.2") Assert.That(http.Connection.RemoteIpAddress.ToString(), Is.EqualTo("203.0.113.9"));
            if (peer == "100.64.1.2") Assert.That(http.Connection.RemoteIpAddress.ToString(), Is.EqualTo("127.0.0.1"));
        }

        [TestCase("X-Forwarded-For", "127.0.0.1")]
        [TestCase("X-Forwarded-Host", "localhost")]
        [TestCase("X-Forwarded-Proto", "https")]
        [TestCase("Forwarded", "for=127.0.0.1;host=localhost")]
        public async Task incomplete_oidc_allows_recovery_through_local_forwarders(string header, string value)
        {
            var values = Values("", AuthenticationType.None);
            values["get_AuthenticationMethodIsMisconfigured"] = true;
            using var services = Services(Proxy<IConfigFileProvider>(values));
            var http = Context(services, "localhost", null);
            http.Request.Headers[header] = value;
            await RunUiRequest(services, http);
            Assert.That(http.Response.StatusCode, Is.EqualTo(200));
        }

        [Test]
        public async Task direct_machine_name_remains_available_without_configuration()
        {
            using var services = Services(Proxy<IConfigFileProvider>(Values("", AuthenticationType.None)));
            var http = Context(services, Environment.MachineName, null);
            await RunUiRequest(services, http);
            Assert.That(http.Response.StatusCode, Is.EqualTo(200));
        }

        private static async Task RunUiRequest(ServiceProvider services, DefaultHttpContext http)
        {
            http.Request.PathBase = "/chaptarr";
            http.Request.Path = "/initialize.json";
            var app = new ApplicationBuilder(services);
            app.UseValidatedForwardedHeaders();
            app.Run(async context =>
            {
                var policy = await services.GetRequiredService<IAuthorizationPolicyProvider>().GetPolicyAsync("UI");
                var evaluator = services.GetRequiredService<IPolicyEvaluator>();
                var authenticated = await evaluator.AuthenticateAsync(policy, context);
                var authorized = await evaluator.AuthorizeAsync(policy, authenticated, context, context);
                if (!authorized.Succeeded) await context.ChallengeAsync("UIAuth");
                else await context.Response.WriteAsync("initialized");
            });
            await app.Build()(http);
        }

        [TestCase("localhost", "127.0.0.1", null, true)]
        [TestCase("evil.example", "127.0.0.1", null, true)]
        [TestCase("localhost", "10.0.0.10", "127.0.0.1", true)]
        [TestCase("localhost", "10.0.0.10", "203.0.113.9", false)]
        [TestCase("localhost", "203.0.113.9", null, false)]
        public void external_auth_password_recovery_uses_the_same_locality_proof(string host, string peer, string forwarded, bool allowed)
        {
            var lookedUp = 0;
            var user = new User { Username = "test" };
            var users = InterfaceProxy.Proxy<IUserService>((method, _) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(IUserService.FindUser)));
                lookedUp++;
                return user;
            });
            var service = new Chaptarr.Http.Authentication.AuthenticationService(Proxy<IConfigFileProvider>(Values("", AuthenticationType.Oidc)), users, new CacheManager());
            var http = Context(null, host, null);
            http.Connection.RemoteIpAddress = IPAddress.Parse(peer);
            if (forwarded != null) http.Request.Headers["X-Forwarded-For"] = forwarded;
            var result = service.Login(http.Request, "test", "test");
            Assert.That(result, Is.SameAs(allowed ? user : null));
            Assert.That(lookedUp, Is.EqualTo(allowed ? 1 : 0));
        }

        [TestCase("192.168.1.2", "X-Forwarded-For", "192.168.1.5:4711", false, true)]
        [TestCase("192.168.1.2", "X-Forwarded-For", "192.168.1.5, 203.0.113.9", false, false)]
        [TestCase("192.168.1.2", "X-Forwarded-For", "192.168.1.5|203.0.113.9", false, false)]
        [TestCase("192.168.1.2", "X-Forwarded-For", "192.168.1.5, 10.0.0.2", false, true)]
        [TestCase("192.168.1.2", "X-Forwarded-For", "", false, false)]
        [TestCase("192.168.1.2", "X-Real-IP", "203.0.113.9", false, false)]
        [TestCase("192.168.1.2", "CF-Connecting-IP", "203.0.113.9:443", false, false)]
        [TestCase("192.168.1.2", "True-Client-IP", "::ffff:203.0.113.9", false, false)]
        [TestCase("192.168.1.2", "Forwarded", "for=203.0.113.9;proto=https", false, false)]
        [TestCase("192.168.1.2", "Forwarded", "For=\"[fd00::1]:4711\";proto=https", false, true)]
        [TestCase("192.168.1.2", "Forwarded", "for=_hidden", false, false)]
        [TestCase("192.168.1.2", "Forwarded", "for=unknown", false, false)]
        [TestCase("192.168.1.2", "Forwarded", "proto=https;host=library.example", false, false)]
        [TestCase("192.168.1.2", "Forwarded", "for=192.168.1.5|for=203.0.113.9", false, false)]
        [TestCase("192.168.1.2", "X-Real-IP", "::ffff:192.168.1.5", false, true)]
        [TestCase("192.168.1.2", "X-Real-IP", "nonsense", false, false)]
        [TestCase("192.168.1.2", "X-Forwarded-Host", "library.example", false, true)]
        [TestCase("192.168.1.2", "X-Forwarded-Proto", "https", false, true)]
        [TestCase("100.64.1.2", "X-Forwarded-For", "192.168.1.5", false, false)]
        [TestCase("100.64.1.2", "X-Forwarded-For", "192.168.1.5", true, true)]
        [TestCase("192.168.1.2", "X-Forwarded-For", "100.64.1.5", false, false)]
        [TestCase("192.168.1.2", "X-Forwarded-For", "100.64.1.5", true, true)]
        [TestCase("203.0.113.9", "X-Forwarded-For", "192.168.1.5", true, false)]
        public void every_reported_address_and_the_direct_peer_must_be_local(string peer, string header, string value, bool trustCgnat, bool expected)
        {
            var values = Values("");
            values["get_TrustCgnatIpAddresses"] = trustCgnat;
            var config = Proxy<IConfigFileProvider>(values);
            var http = Context(null, "library.example", null);
            http.Connection.RemoteIpAddress = IPAddress.Parse(peer);
            http.Request.Headers[header] = new Microsoft.Extensions.Primitives.StringValues(value.Split('|'));
            Assert.That(TrustedNetworkPolicy.MaySkipLocalLogin(http, config), Is.EqualTo(expected));
        }

        [TestCase("192.168.1.5")]
        [TestCase("203.0.113.9")]
        public async Task initial_setup_remains_accessible_before_authentication_is_configured(string address)
        {
            using var services = Services(Proxy<IConfigFileProvider>(Values("", AuthenticationType.None)));
            var http = Context(services, "library.example", "public.example");
            http.Request.Headers["X-Forwarded-For"] = address;
            await RunUiRequest(services, http);
            Assert.That(http.Response.StatusCode, Is.EqualTo(200));
        }

        [Test]
        public void machine_name_remains_available_after_restore()
        {
            var options = new HostFilteringOptions();
            new ConfigureHostFilteringOptions(Config("previous-machine.example"), LogManager.GetCurrentClassLogger()).Configure(options);
            Assert.That(options.AllowedHosts, Does.Contain(Environment.MachineName));
            Assert.That(options.AllowedHosts, Does.Contain(Dns.GetHostName()));
            Assert.That(options.AllowedHosts, Does.Contain("localhost"));
            Assert.That(options.AllowedHosts, Does.Contain("[::1]"));
        }

        [Test]
        public void parser_reuses_ip_normalization_and_deduplicates_entries()
        {
            Assert.That(AllowedHostsParser.Parse(" LIBRARY.example,library.example; ::1, [::1]; "),
                Is.EqualTo(new[] { "LIBRARY.example", "[::1]" }));
        }

        [TestCase(AuthenticationType.None, AuthenticationRequiredType.Enabled, HealthCheckResult.Warning)]
        [TestCase(AuthenticationType.External, AuthenticationRequiredType.Enabled, HealthCheckResult.Warning)]
        [TestCase(AuthenticationType.Forms, AuthenticationRequiredType.DisabledForLocalAddresses, HealthCheckResult.Warning)]
        [TestCase(AuthenticationType.Basic, AuthenticationRequiredType.DisabledForLocalAddresses, HealthCheckResult.Warning)]
        [TestCase(AuthenticationType.Oidc, AuthenticationRequiredType.DisabledForLocalAddresses, HealthCheckResult.Warning)]
        [TestCase(AuthenticationType.Plex, AuthenticationRequiredType.DisabledForLocalAddresses, HealthCheckResult.Warning)]
        [TestCase(AuthenticationType.Forms, AuthenticationRequiredType.Enabled, HealthCheckResult.Ok)]
        [TestCase(AuthenticationType.Basic, AuthenticationRequiredType.Enabled, HealthCheckResult.Ok)]
        [TestCase(AuthenticationType.Oidc, AuthenticationRequiredType.Enabled, HealthCheckResult.Ok)]
        [TestCase(AuthenticationType.Plex, AuthenticationRequiredType.Enabled, HealthCheckResult.Ok)]
        public void health_warning_reflects_actual_authentication(AuthenticationType auth, AuthenticationRequiredType required, HealthCheckResult expected)
        {
            var values = Values("", auth, required);
            var check = new AllowedHostsCheck(Proxy<IConfigFileProvider>(values), Proxy<ILocalizationService>(new() { ["GetLocalizedString"] = "Configure Allowed Hosts" }));
            Assert.That(check.Check().Type, Is.EqualTo(expected));
            values["get_AllowedHosts"] = "library.example";
            Assert.That(check.Check().Type, Is.EqualTo(HealthCheckResult.Ok));
            var events = typeof(AllowedHostsCheck).GetCustomAttributes<CheckOnAttribute>().Select(a => a.EventType);
            Assert.That(events, Does.Contain(typeof(ConfigFileSavedEvent)));
        }

        [TestCase("*")]
        [TestCase("*,library.example")]
        [TestCase("0.0.0.0;[::]")]
        public void wildcard_and_bind_addresses_cannot_hide_the_health_warning(string hosts)
        {
            var check = new AllowedHostsCheck(Config(hosts), Proxy<ILocalizationService>(new() { ["GetLocalizedString"] = "Configure Allowed Hosts" }));
            Assert.That(check.Check().Type, Is.EqualTo(HealthCheckResult.Warning));
        }

        [Test]
        public async Task all_cookie_redirects_are_relative_and_keep_base_and_return_paths(
            [Values("Forms", "Oidc", "Plex")] string scheme,
            [Values(false, true)] bool xhr,
            [Values("evil.example", "")] string host)
        {
            using var services = Services(Config(""));
            var options = services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(scheme);
            foreach (var denied in new[] { false, true })
            {
                var http = Context(services, host, null);
                http.Request.PathBase = "/chaptarr";
                if (xhr) http.Request.Headers.XRequestedWith = "XMLHttpRequest";
                var expected = "/chaptarr/login?" + (denied ? "loginFailed=true&" : "") + "returnUrl=%2Fchaptarr%2Fauthor%3Fx%3D1";
                var redirect = new RedirectContext<CookieAuthenticationOptions>(http,
                    new AuthenticationScheme(scheme, scheme, typeof(CookieAuthenticationHandler)), options,
                    new AuthenticationProperties(), $"{http.Request.Scheme}://{http.Request.Host}" + expected);
                if (denied) await options.Events.RedirectToAccessDenied(redirect);
                else await options.Events.RedirectToLogin(redirect);
                Assert.That(http.Response.Headers.Location.ToString(), Is.EqualTo(expected));
                Assert.That(http.Response.StatusCode, Is.EqualTo(xhr ? (denied ? 403 : 401) : 302));
            }
        }

        [Test]
        public void content_security_policy_has_no_request_derived_origin()
        {
            var build = typeof(SecurityHeadersMiddleware).GetMethod("BuildContentSecurityPolicy", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(build, Is.Not.Null);
            var args = build.GetParameters().Length == 0 ? null : new object[] { Context(null, "evil.example", null) };
            var csp = (string)build.Invoke(null, args);
            Assert.That(csp, Does.Contain("connect-src 'self';"));
            Assert.That(csp, Does.Not.Contain("evil.example"));
        }

        [Test]
        public void host_options_are_discovered_by_the_production_container()
        {
            using var container = new Container(rules => rules.WithNzbDroneRules());
            container.AutoAddServices(new List<string> { "Chaptarr.Host" });
            container.RegisterInstance<IConfigFileProvider>(Config("library.example"));
            container.RegisterInstance(LogManager.GetCurrentClassLogger());
            var options = new HostFilteringOptions();
            foreach (var configurator in container.ResolveMany<IConfigureOptions<HostFilteringOptions>>()) configurator.Configure(options);
            Assert.That(options.AllowedHosts, Does.Contain("library.example"));
            Assert.That(options.AllowedHosts, Does.Not.Contain("*"));
        }

        [Test]
        public void allowed_hosts_persist_in_config_xml_and_allow_environment_override()
        {
            var xml = "<Config/>";
            var disk = InterfaceProxy.Proxy<IDiskProvider>((method, args) => method.Name switch
            {
                "FileExists" => true,
                "ReadAllText" => xml,
                "WriteAllText" => Save((string)args[1]),
                _ => throw new NotImplementedException(method.Name)
            });
            object Save(string value) { xml = value; return null; }
            ConfigFileProvider Create(string configured = null) => new ConfigFileProvider(
                Proxy<IAppFolderInfo>(new() { ["get_AppDataFolder"] = Path.GetTempPath() }), new CacheManager(),
                Proxy<IEventAggregator>(new()), disk, Options.Create(new PostgresOptions()), Options.Create(new AuthOptions()),
                Options.Create(new AppOptions()), Options.Create(new ServerOptions { AllowedHosts = configured }),
                Options.Create(new UpdateOptions()), Options.Create(new LogOptions()));
            var config = Create();
            Assert.That(config.AllowedHosts, Is.Empty);
            config.SaveConfigDictionary(new Dictionary<string, object> { ["AllowedHosts"] = "library.example" });
            Assert.That(xml, Does.Contain("<AllowedHosts>library.example</AllowedHosts>"));
            Assert.That(Create().AllowedHosts, Is.EqualTo("library.example"));
            Assert.That(Create("recovery.example").AllowedHosts, Is.EqualTo("recovery.example"));
        }

        private static Dictionary<string, object> Values(string hosts, AuthenticationType auth = AuthenticationType.None,
            AuthenticationRequiredType required = AuthenticationRequiredType.Enabled) => new()
        {
            ["get_AllowedHosts"] = hosts, ["get_InstanceName"] = "Chaptarr", ["get_AuthenticationMethod"] = auth,
            ["get_AuthenticationRequired"] = required, ["get_UrlBase"] = "/chaptarr"
        };

        private static IConfigFileProvider Config(string hosts) => Proxy<IConfigFileProvider>(Values(hosts));

        private static T Proxy<T>(Dictionary<string, object> values) where T : class => InterfaceProxy.Proxy<T>((method, _) =>
            values.TryGetValue(method.Name, out var value) ? value : method.ReturnType == typeof(string) ? string.Empty :
            method.ReturnType == typeof(void) ? null : method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null);

        private static ServiceProvider Services(IConfigFileProvider config)
        {
            var services = new ServiceCollection();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
            {
                ["dataProtectionFolder"] = Path.GetTempPath()
            }).Build();
            new Startup(configuration).ConfigureServices(services);
            services.AddSingleton(config);
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
            services.AddSingleton(LogManager.GetCurrentClassLogger());
            services.ConfigureOptions<ConfigureHostFilteringOptions>();
            return services.BuildServiceProvider();
        }

        private static DefaultHttpContext Context(IServiceProvider services, string host, string forwarded)
        {
            var http = new DefaultHttpContext { RequestServices = services };
            http.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.50");
            http.Request.Host = new HostString(host);
            http.Request.Scheme = "http";
            http.Response.Body = new MemoryStream();
            if (forwarded != null) http.Request.Headers["X-Forwarded-Host"] = forwarded;
            return http;
        }
    }
}
