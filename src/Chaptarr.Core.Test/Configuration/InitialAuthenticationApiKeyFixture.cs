using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Xml.Linq;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Common;
using Chaptarr.Http.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Options;
using NzbDrone.Core.Authentication;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.HealthCheck.Checks;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Configuration
{
    [TestFixture]
    public class InitialAuthenticationApiKeyFixture
    {
        private sealed class Harness
        {
            public string Xml = "<Config><AuthenticationMethod>None</AuthenticationMethod></Config>";
            public AuthOptions Auth = new();
            public bool FailWrite;
            public List<string> Writes = new();

            public ConfigFileProvider Open()
            {
                var disk = InterfaceProxy.Proxy<IDiskProvider>((method, args) => method.Name switch
                {
                    "FileExists" => true,
                    "ReadAllText" => Xml,
                    "WriteAllText" => Save((string)args[1]),
                    _ => throw new NotImplementedException(method.Name)
                });
                return new ConfigFileProvider(InterfaceProxy.Proxy<IAppFolderInfo>((_, _) => Path.GetTempPath()),
                    new CacheManager(), InterfaceProxy.Proxy<IEventAggregator>((_, _) => null), disk,
                    Options.Create(new PostgresOptions()), Options.Create(Auth), Options.Create(new AppOptions()),
                    Options.Create(new ServerOptions()), Options.Create(new UpdateOptions()), Options.Create(new LogOptions()));
            }

            private object Save(string xml)
            {
                if (FailWrite) throw new IOException("Cannot save config");
                Writes.Add(xml);
                Xml = xml;
                return null;
            }
        }

        [TestCase(AuthenticationType.Forms)]
        [TestCase(AuthenticationType.Basic)]
        [TestCase(AuthenticationType.Plex)]
        [TestCase(AuthenticationType.Oidc)]
        public async Task initial_setup_invalidates_the_old_api_key_and_persists_the_new_one(AuthenticationType method)
        {
            var harness = new Harness();
            var config = harness.Open();
            var oldKey = config.ApiKey;
            config.GetConfigDictionary();
            harness.Writes.Clear();
            config.SaveConfigDictionary(new() { ["AuthenticationMethod"] = method });
            var newKey = config.ApiKey;
            Assert.That(newKey, Is.Not.EqualTo(oldKey));
            Assert.That(harness.Writes, Has.Count.EqualTo(1), "The login method and key must be saved together");
            Assert.That(harness.Open().ApiKey, Is.EqualTo(newKey));
            Assert.That(harness.Open().ConfiguredAuthenticationMethod, Is.EqualTo(method));
            Assert.That(await Authenticates(config, oldKey), Is.False);
            Assert.That(await Authenticates(config, newKey), Is.True);

            config.SaveConfigDictionary(new() { ["AuthenticationMethod"] = AuthenticationType.Forms, ["LogLevel"] = "debug" });
            Assert.That(config.ApiKey, Is.EqualTo(newKey));
            config.SetValue("AuthenticationMethod", AuthenticationType.None);
            config = harness.Open();
            config.SaveConfigDictionary(new() { ["AuthenticationMethod"] = AuthenticationType.Forms });
            Assert.That(config.ApiKey, Is.EqualTo(newKey), "First setup is not repeated after an administrative method reset");
            config.Execute(new ResetApiKeyCommand());
            Assert.That(config.ApiKey, Is.Not.EqualTo(newKey));
        }

        [Test]
        public void repairing_oidc_does_not_rotate_an_established_key()
        {
            var harness = new Harness { Xml = "<Config><AuthenticationMethod>Oidc</AuthenticationMethod></Config>" };
            var config = harness.Open();
            var key = config.ApiKey;
            Assert.That(config.AuthenticationMethod, Is.EqualTo(AuthenticationType.None));
            config.SaveConfigDictionary(new()
            {
                ["AuthenticationMethod"] = AuthenticationType.Oidc,
                ["OidcAuthority"] = "https://login.example", ["OidcClientId"] = "client", ["OidcClientSecret"] = "secret"
            });
            Assert.That(config.AuthenticationMethod, Is.EqualTo(AuthenticationType.Oidc));
            Assert.That(config.ApiKey, Is.EqualTo(key));
        }

        [Test]
        public void environment_key_warning_survives_restart_and_clears_when_the_key_changes()
        {
            var harness = new Harness { Auth = new AuthOptions { ApiKey = "fixed-test-key" } };
            var config = harness.Open();
            config.SaveConfigDictionary(new() { ["AuthenticationMethod"] = AuthenticationType.Forms });
            Assert.That(config.ApiKey, Is.EqualTo("fixed-test-key"));
            Assert.That(harness.Open().ApiKeyNeedsManualRotation, Is.True);
            var localization = InterfaceProxy.Proxy<ILocalizationService>((_, _) => "The old key remains valid until the administrator changes the environment value.");
            Assert.That(new ApiKeySetupCheck(harness.Open(), localization).Check().Type, Is.EqualTo(HealthCheckResult.Warning));
            harness.Auth.ApiKey = "changed-test-key";
            Assert.That(new ApiKeySetupCheck(harness.Open(), localization).Check().Type, Is.EqualTo(HealthCheckResult.Ok));
        }

        [Test]
        public void reading_duplicate_config_keys_keeps_the_first_direct_child_without_writing_a_default()
        {
            var harness = new Harness { Xml = "<Config><AllowedHosts>private.example</AllowedHosts><AllowedHosts>other.example</AllowedHosts></Config>" };
            var original = harness.Xml;
            Assert.That(harness.Open().AllowedHosts, Is.EqualTo("private.example"));
            Assert.That(harness.Xml, Is.EqualTo(original));
            Assert.That(harness.Writes, Is.Empty);
        }

        [Test]
        [NonParallelizable]
        public void duplicate_config_warning_identifies_the_key_without_logging_its_values()
        {
            var originalLogging = LogManager.Configuration;
            var messages = new NLog.Targets.MemoryTarget { Layout = "${level}|${message}" };
            var logging = new NLog.Config.LoggingConfiguration();
            logging.AddRule(NLog.LogLevel.Warn, NLog.LogLevel.Fatal, messages);
            LogManager.Configuration = logging;
            try
            {
                var harness = new Harness { Xml = "<Config><ApiKey>first-secret</ApiKey><ApiKey>second-secret</ApiKey></Config>" };
                Assert.That(harness.Open().ApiKey, Is.EqualTo("first-secret"));
                Assert.That(messages.Logs, Has.Some.Contains("Duplicate config key ApiKey"));
                Assert.That(string.Join("\n", messages.Logs), Does.Not.Contain("first-secret").And.Not.Contain("second-secret"));
                Assert.That(harness.Writes, Is.Empty);
            }
            finally
            {
                LogManager.Configuration = originalLogging;
            }
        }

        [Test]
        public void config_updates_preserve_direct_child_position_and_remove_only_direct_duplicates()
        {
            var harness = new Harness { Xml = "<Config><Port>8787</Port><Section><AllowedHosts>nested.example</AllowedHosts></Section><AllowedHosts>old.example</AllowedHosts><LogLevel>info</LogLevel><AllowedHosts>duplicate.example</AllowedHosts></Config>" };
            var config = harness.Open();
            Assert.That(config.AllowedHosts, Is.EqualTo("old.example"));
            config.SetValue("AllowedHosts", "new.example");
            var xml = XElement.Parse(harness.Xml);
            Assert.That(xml.Elements().Select(e => e.Name.LocalName), Is.EqualTo(new[] { "Port", "Section", "AllowedHosts", "LogLevel" }));
            Assert.That(xml.Elements("AllowedHosts").Single().Value, Is.EqualTo("new.example"));
            Assert.That(xml.Element("Section").Element("AllowedHosts").Value, Is.EqualTo("nested.example"));
            Assert.That(harness.Open().AllowedHosts, Is.EqualTo("new.example"));
            harness.Writes.Clear();
            config.SetValue("AllowedHosts", (object)null);
            config.SetValue("AllowedHosts", "new.example");
            Assert.That(harness.Writes, Is.Empty, "Null entries and unchanged values do not rewrite the config");
            Assert.That(config.AllowedHosts, Is.EqualTo("new.example"));
            config.SetValue("AllowedHosts", "");
            Assert.That(harness.Open().AllowedHosts, Is.Empty);
        }

        [Test]
        public void cleanup_and_restart_keep_the_same_direct_child_value_when_a_nested_value_appears_first()
        {
            var harness = new Harness { Xml = "<Config><Section><AllowedHosts>nested.example</AllowedHosts></Section><AllowedHosts>private.example</AllowedHosts></Config>" };
            var config = harness.Open();
            Assert.That(config.AllowedHosts, Is.EqualTo("private.example"));
            typeof(ConfigFileProvider).GetMethod("DeleteOldValues", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(config, null);
            Assert.That(XElement.Parse(harness.Xml).Elements().Select(e => e.Name.LocalName), Is.EqualTo(new[] { "AllowedHosts" }));
            Assert.That(harness.Open().AllowedHosts, Is.EqualTo("private.example"));
            harness.Writes.Clear();
            typeof(ConfigFileProvider).GetMethod("DeleteOldValues", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(config, null);
            Assert.That(harness.Writes, Is.Empty, "Clean startup config is not rewritten");
        }

        [Test]
        public void nested_settings_are_not_an_alternate_write_location()
        {
            var harness = new Harness { Xml = "<Config><Section><AllowedHosts>nested.example</AllowedHosts></Section></Config>" };
            var config = harness.Open();
            Assert.That(config.GetValue("AllowedHosts", "", false), Is.Empty);
            config.SetValue("AllowedHosts", "private.example");
            Assert.That(XElement.Parse(harness.Xml).Elements("AllowedHosts").Single().Value, Is.EqualTo("private.example"));
            Assert.That(harness.Open().AllowedHosts, Is.EqualTo("private.example"));
        }

        [Test]
        public void null_config_batch_throws_instead_of_claiming_a_successful_write()
        {
            var harness = new Harness();
            var config = harness.Open();
            var exception = Assert.Throws<TargetInvocationException>(() => typeof(ConfigFileProvider)
                .GetMethod("SetValues", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(config, new object[] { null }));
            Assert.That(exception.InnerException, Is.TypeOf<ArgumentNullException>());
            Assert.That(harness.Writes, Is.Empty);
        }

        [Test]
        public void environment_key_marker_uses_the_existing_password_hash_and_reset_refuses_to_claim_success()
        {
            var harness = new Harness { Auth = new AuthOptions { ApiKey = "fixed-test-key" } };
            var config = harness.Open();
            config.SaveConfigDictionary(new() { ["AuthenticationMethod"] = AuthenticationType.Forms });
            var marker = XElement.Parse(harness.Xml).Element("InitialAuthenticationSetupKeyHash").Value;
            var pieces = marker.Split(':');
            Assert.That(pieces, Has.Length.EqualTo(3));
            Assert.That(int.Parse(pieces[0]), Is.GreaterThanOrEqualTo(10000));
            Assert.That(marker, Does.Not.Contain(harness.Auth.ApiKey));
            Assert.That(config.ApiKeyNeedsManualRotation, Is.True);
            Assert.Throws<InvalidOperationException>(() => config.Execute(new ResetApiKeyCommand()));
            Assert.That(config.ApiKey, Is.EqualTo(harness.Auth.ApiKey));
            Assert.That(config.ApiKeyNeedsManualRotation, Is.True);
        }

        [Test]
        public void managed_key_setup_marker_contains_no_key_derived_value()
        {
            var harness = new Harness();
            harness.Open().SaveConfigDictionary(new() { ["AuthenticationMethod"] = AuthenticationType.Forms });
            Assert.That(XElement.Parse(harness.Xml).Element("InitialAuthenticationSetupKeyHash").Value, Is.EqualTo("completed"));
        }

        [Test]
        public void failed_setup_save_keeps_the_method_and_key_and_can_be_retried()
        {
            var harness = new Harness();
            var config = harness.Open();
            config.GetConfigDictionary();
            var oldKey = config.ApiKey;
            harness.FailWrite = true;
            Assert.Throws<IOException>(() => config.SaveConfigDictionary(new() { ["AuthenticationMethod"] = AuthenticationType.Forms }));
            harness.FailWrite = false;
            config = harness.Open();
            Assert.That(config.ConfiguredAuthenticationMethod, Is.EqualTo(AuthenticationType.None));
            Assert.That(config.ApiKey, Is.EqualTo(oldKey));
            config.SaveConfigDictionary(new() { ["AuthenticationMethod"] = AuthenticationType.Forms });
            Assert.That(config.ApiKey, Is.Not.EqualTo(oldKey));
        }

        private static async Task<bool> Authenticates(IConfigFileProvider config, string key)
        {
            var collection = new ServiceCollection();
            collection.AddLogging();
            collection.AddSingleton(config);
            collection.AddAuthentication().AddApiKey("API", options => { options.HeaderName = "X-Api-Key"; options.QueryName = "apikey"; });
            using var services = collection.BuildServiceProvider();
            var context = new DefaultHttpContext { RequestServices = services };
            context.Request.Headers["X-Api-Key"] = key;
            return (await context.AuthenticateAsync("API")).Succeeded;
        }
    }
}
