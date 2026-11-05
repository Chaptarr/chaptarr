using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Options;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;
using NUnit.Framework;
using NzbDrone.Core.Configuration;

namespace Chaptarr.Core.Test.Http.Authentication
{
    [TestFixture]
    [NonParallelizable]
    public class BookshelfOidcEnvironmentSettingsFixture
    {
        private static readonly string[] EnvironmentVariables =
        {
            BookshelfOidcEnvironmentSettings.Authority,
            BookshelfOidcEnvironmentSettings.ClientId,
            BookshelfOidcEnvironmentSettings.ClientSecret,
            BookshelfOidcEnvironmentSettings.RedirectUri,
            BookshelfOidcEnvironmentSettings.Scopes,
            BookshelfOidcEnvironmentSettings.AllowedEmails,
            BookshelfOidcEnvironmentSettings.AllowedEmailDomains,
            BookshelfOidcEnvironmentSettings.AllowAnyVerifiedUser,
            BookshelfOidcEnvironmentSettings.AllowPrivateIssuerEnvironmentVariable
        };

        private Dictionary<string, string> _previousValues;
        private string _testAppDataFolder;

        [SetUp]
        public void SetUp()
        {
            _previousValues = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in EnvironmentVariables)
            {
                _previousValues[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, null);
            }
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var name in EnvironmentVariables)
            {
                Environment.SetEnvironmentVariable(name, _previousValues[name]);
            }

            if (!string.IsNullOrWhiteSpace(_testAppDataFolder) && Directory.Exists(_testAppDataFolder))
            {
                Directory.Delete(_testAppDataFolder, recursive: true);
            }
        }

        [Test]
        public void should_use_a_bookshelf_value_when_chaptarr_setting_is_unset()
        {
            Environment.SetEnvironmentVariable(BookshelfOidcEnvironmentSettings.Authority, "https://issuer.example.com");

            var value = BookshelfOidcEnvironmentSettings.Resolve(
                string.Empty,
                BookshelfOidcEnvironmentSettings.Authority,
                string.Empty);

            Assert.That(value, Is.EqualTo("https://issuer.example.com"));
        }

        [Test]
        public void should_prefer_a_non_empty_chaptarr_value_to_a_bookshelf_value()
        {
            Environment.SetEnvironmentVariable(BookshelfOidcEnvironmentSettings.ClientId, "legacy-client");

            var value = BookshelfOidcEnvironmentSettings.Resolve(
                "new-client",
                BookshelfOidcEnvironmentSettings.ClientId,
                string.Empty);

            Assert.That(value, Is.EqualTo("new-client"));
        }

        [Test]
        public void should_use_the_configured_default_when_both_values_are_unset()
        {
            var value = BookshelfOidcEnvironmentSettings.Resolve(
                string.Empty,
                BookshelfOidcEnvironmentSettings.Scopes,
                "openid profile email");

            Assert.That(value, Is.EqualTo("openid profile email"));
        }

        [Test]
        public void should_expose_legacy_values_through_the_config_provider()
        {
            Environment.SetEnvironmentVariable(BookshelfOidcEnvironmentSettings.Authority, "https://issuer.example.com");
            Environment.SetEnvironmentVariable(BookshelfOidcEnvironmentSettings.ClientId, "bookshelf-client");
            Environment.SetEnvironmentVariable(BookshelfOidcEnvironmentSettings.ClientSecret, "legacy-secret");
            Environment.SetEnvironmentVariable(BookshelfOidcEnvironmentSettings.Scopes, "openid profile email groups");
            Environment.SetEnvironmentVariable(BookshelfOidcEnvironmentSettings.AllowedEmails, "operator@example.com");
            Environment.SetEnvironmentVariable(BookshelfOidcEnvironmentSettings.AllowedEmailDomains, "example.net");
            Environment.SetEnvironmentVariable(BookshelfOidcEnvironmentSettings.AllowAnyVerifiedUser, "true");

            var config = CreateConfigFileProvider();

            Assert.That(config.OidcAuthority, Is.EqualTo("https://issuer.example.com"));
            Assert.That(config.OidcClientId, Is.EqualTo("bookshelf-client"));
            Assert.That(config.OidcClientSecret, Is.EqualTo("legacy-secret"));
            Assert.That(config.OidcScopes, Is.EqualTo("openid profile email groups"));
            Assert.That(config.OidcAllowedEmails, Is.EqualTo("operator@example.com"));
            Assert.That(config.OidcAllowedEmailDomains, Is.EqualTo("example.net"));
            Assert.That(config.OidcAllowAnyVerifiedUser, Is.True);
        }

        [Test]
        public void should_prefer_saved_config_values_and_false_over_legacy_environment_values()
        {
            Environment.SetEnvironmentVariable(BookshelfOidcEnvironmentSettings.Authority, "https://legacy.example.com");
            Environment.SetEnvironmentVariable(BookshelfOidcEnvironmentSettings.ClientId, "legacy-client");
            Environment.SetEnvironmentVariable(BookshelfOidcEnvironmentSettings.ClientSecret, "legacy-secret");
            Environment.SetEnvironmentVariable(BookshelfOidcEnvironmentSettings.Scopes, "openid legacy");
            Environment.SetEnvironmentVariable(BookshelfOidcEnvironmentSettings.AllowedEmails, "legacy@example.com");
            Environment.SetEnvironmentVariable(BookshelfOidcEnvironmentSettings.AllowedEmailDomains, "legacy.example.com");
            Environment.SetEnvironmentVariable(BookshelfOidcEnvironmentSettings.AllowAnyVerifiedUser, "true");

            var config = CreateConfigFileProvider(
                ("OidcAuthority", "https://saved.example.com"),
                ("OidcClientId", "saved-client"),
                ("OidcClientSecret", "saved-secret"),
                ("OidcScopes", "openid profile saved"),
                ("OidcAllowedEmails", "saved@example.com"),
                ("OidcAllowedEmailDomains", "saved.example.com"),
                ("OidcAllowAnyVerifiedUser", "false"));

            Assert.That(config.OidcAuthority, Is.EqualTo("https://saved.example.com"));
            Assert.That(config.OidcClientId, Is.EqualTo("saved-client"));
            Assert.That(config.OidcClientSecret, Is.EqualTo("saved-secret"));
            Assert.That(config.OidcScopes, Is.EqualTo("openid profile saved"));
            Assert.That(config.OidcAllowedEmails, Is.EqualTo("saved@example.com"));
            Assert.That(config.OidcAllowedEmailDomains, Is.EqualTo("saved.example.com"));
            Assert.That(config.OidcAllowAnyVerifiedUser, Is.False);
        }

        [Test]
        public void should_preserve_an_explicit_false_over_the_legacy_allow_any_flag()
        {
            Environment.SetEnvironmentVariable(BookshelfOidcEnvironmentSettings.AllowAnyVerifiedUser, "true");

            var value = BookshelfOidcEnvironmentSettings.ResolveBoolean(
                "false",
                BookshelfOidcEnvironmentSettings.AllowAnyVerifiedUser,
                false);

            Assert.That(value, Is.False);
        }

        [Test]
        public void should_accept_the_legacy_private_issuer_flag_only_when_true()
        {
            Environment.SetEnvironmentVariable(BookshelfOidcEnvironmentSettings.AllowPrivateIssuerEnvironmentVariable, "true");
            Assert.That(BookshelfOidcEnvironmentSettings.AllowPrivateIssuer, Is.True);

            Environment.SetEnvironmentVariable(BookshelfOidcEnvironmentSettings.AllowPrivateIssuerEnvironmentVariable, "false");
            Assert.That(BookshelfOidcEnvironmentSettings.AllowPrivateIssuer, Is.False);
        }

        [TestCase(null, "Readarr")]
        [TestCase("Readarr", "Readarr")]
        [TestCase("Readarr Audiobooks", "Readarr Audiobooks")]
        [TestCase("readarr - ebooks", "readarr - ebooks")]
        [TestCase("", "Readarr")]
        [TestCase("Unrelated", "Readarr")]
        public void should_preserve_source_instance_name_rules_without_changing_native_default(string stored, string expected)
        {
            var config = stored == null ? CreateConfigFileProvider() : CreateConfigFileProvider(("InstanceName", stored));
            Assert.That(config.GetBookshelfInstanceName(), Is.EqualTo(expected));
            if (stored != null)
            {
                Assert.That(config.InstanceName, Is.EqualTo(NzbDrone.Common.EnvironmentInfo.BuildInfo.AppName));
            }
        }

        [Test]
        public void should_treat_unpersisted_postgres_database_defaults_as_implicit()
        {
            var config = CreateConfigFileProvider();

            Assert.That(config.IsPostgresMainDbExplicitlyConfigured(), Is.False);
            Assert.That(config.IsPostgresLogDbExplicitlyConfigured(), Is.False);
            Assert.That(config.IsPostgresCacheDbExplicitlyConfigured(), Is.False);
        }

        [Test]
        public void should_preserve_explicit_postgres_database_names_even_when_they_match_defaults()
        {
            var config = CreateConfigFileProvider(
                ("PostgresMainDb", "chaptarr-main"),
                ("PostgresLogDb", "operator-log"));

            Assert.That(config.IsPostgresMainDbExplicitlyConfigured(), Is.True);
            Assert.That(config.IsPostgresLogDbExplicitlyConfigured(), Is.True);
            Assert.That(config.IsPostgresCacheDbExplicitlyConfigured(), Is.False);
        }

        [TestCase("https://books.example.com/signin-oidc", "https://books.example.com/signin-oidc")]
        [TestCase("http://books.example.com/signin-oidc", "")]
        [TestCase("https://books.example.com/callback", "")]
        [TestCase("https://user@books.example.com/signin-oidc", "")]
        [TestCase("https://books.example.com/signin-oidc?next=/admin", "")]
        public void should_use_only_a_safe_legacy_redirect_uri(string configured, string expected)
        {
            Environment.SetEnvironmentVariable(BookshelfOidcEnvironmentSettings.RedirectUri, configured);

            Assert.That(BookshelfOidcEnvironmentSettings.GetRedirectUri(), Is.EqualTo(expected));
        }

        private ConfigFileProvider CreateConfigFileProvider(params (string Key, string Value)[] values)
        {
            _testAppDataFolder = Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                "BookshelfOidcConfig-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_testAppDataFolder);

            var appFolderInfo = DispatchProxy.Create<IAppFolderInfo, AppFolderInfoProxy>();
            ((AppFolderInfoProxy)(object)appFolderInfo).AppDataFolder = _testAppDataFolder;

            var root = new XElement(ConfigFileProvider.CONFIG_ELEMENT_NAME,
                values.Select(value => new XElement(value.Key, value.Value)));
            new XDocument(root).Save(appFolderInfo.GetConfigPath());

            return new ConfigFileProvider(
                appFolderInfo,
                new CacheManager(),
                DispatchProxy.Create<IEventAggregator, NoOpProxy>(),
                DispatchProxy.Create<IDiskProvider, DiskProviderProxy>(),
                Options.Create(new PostgresOptions()),
                Options.Create(new AuthOptions()),
                Options.Create(new AppOptions()),
                Options.Create(new ServerOptions()),
                Options.Create(new UpdateOptions()),
                Options.Create(new LogOptions()));
        }

        private class AppFolderInfoProxy : DispatchProxy
        {
            public string AppDataFolder { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    "get_AppDataFolder" => AppDataFolder,
                    _ => throw new NotImplementedException(targetMethod?.Name)
                };
            }
        }

        private class NoOpProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return null;
            }
        }

        private class DiskProviderProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    "FileExists" => File.Exists((string)args[0]),
                    "ReadAllText" => File.ReadAllText((string)args[0]),
                    _ => throw new NotImplementedException(targetMethod?.Name)
                };
            }
        }
    }
}
