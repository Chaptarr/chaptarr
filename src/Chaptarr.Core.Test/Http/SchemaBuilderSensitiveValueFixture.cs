using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Text.Json;
using Chaptarr.Http.ClientSchema;
using NUnit.Framework;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ImportLists.LazyLibrarianImport;
using NzbDrone.Core.Notifications.Discord;
using NzbDrone.Core.Notifications.Join;
using NzbDrone.Core.Notifications.Mailgun;
using NzbDrone.Core.Notifications.SendGrid;
using NzbDrone.Core.Notifications.Slack;
using NzbDrone.Core.Notifications.Webhook;
using NzbDrone.Core.Localization;
using NzbDrone.Core.ThingiProvider;

namespace Chaptarr.Core.Test.Http
{
    [TestFixture]
    public class SchemaBuilderSensitiveValueFixture
    {
        private sealed class TestLocalizationService : ILocalizationService
        {
            public Dictionary<string, string> GetLocalizationDictionary()
            {
                return new Dictionary<string, string>();
            }

            public string GetLocalizedString(string phrase)
            {
                return phrase;
            }

            public string GetLocalizedString(string phrase, Dictionary<string, object> tokens)
            {
                return phrase;
            }
        }

        private sealed class SensitiveSettings
        {
            [FieldDefinition(0, Privacy = PrivacyLevel.ApiKey)]
            public string ApiKey { get; set; }

            [FieldDefinition(1)]
            public string Name { get; set; }
        }

        private static readonly Regex SensitiveFieldPattern = new Regex(
            "api\\s*key|apikey|api_key|token|secret|password|cookie|web\\s*hook\\s*url|webhookurl|webhook_url|passphrase",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static IEnumerable<TestCaseData> SensitiveProviderFields()
        {
            var configType = typeof(IProviderConfig);

            return configType.Assembly.GetTypes()
                .Where(t => configType.IsAssignableFrom(t) &&
                            !t.IsInterface &&
                            !t.IsAbstract)
                .SelectMany(t => t.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                    .Select(p => new
                    {
                        SettingsType = t,
                        Property = p,
                        Field = p.GetCustomAttribute<FieldDefinitionAttribute>()
                    }))
                .Where(x => x.Field != null)
                .Where(x => x.Property.GetCustomAttribute<NotSensitiveAttribute>() == null)
                .Where(x => SensitiveFieldPattern.IsMatch($"{x.Property.Name} {x.Field.Label}"))
                .Select(x => new TestCaseData(x.SettingsType, x.Property, x.Field)
                    .SetName($"should_mark_sensitive_provider_field_{x.SettingsType.Name}_{x.Property.Name}"));
        }

        [SetUp]
        public void SetUp()
        {
            typeof(SchemaBuilder)
                .GetField("_localizationService", BindingFlags.NonPublic | BindingFlags.Static)
                .SetValue(null, new TestLocalizationService());
        }

        [Test]
        public void should_mask_sensitive_values_sent_to_client()
        {
            var schema = SchemaBuilder.ToSchema(new SensitiveSettings
            {
                ApiKey = "real-secret",
                Name = "visible"
            });

            Assert.That(schema.Single(f => f.Name == "apiKey").Value, Is.EqualTo("********"));
            Assert.That(schema.Single(f => f.Name == "name").Value, Is.EqualTo("visible"));
        }

        [Test]
        public void should_leave_empty_sensitive_values_unmasked()
        {
            var schema = SchemaBuilder.ToSchema(new SensitiveSettings
            {
                ApiKey = string.Empty,
                Name = "visible"
            });

            Assert.That(schema.Single(f => f.Name == "apiKey").Value, Is.EqualTo(string.Empty));
        }

        [Test]
        public void should_keep_existing_sensitive_value_when_placeholder_is_saved()
        {
            using var nameValue = JsonDocument.Parse("\"new\"");
            var existing = new SensitiveSettings
            {
                ApiKey = "real-secret",
                Name = "old"
            };

            SchemaBuilder.ReadFromSchema(new List<Field>
            {
                new() { Name = "apiKey", Value = "********" },
                new() { Name = "name", Value = nameValue.RootElement.Clone() }
            }, existing);

            Assert.That(existing.ApiKey, Is.EqualTo("real-secret"));
            Assert.That(existing.Name, Is.EqualTo("new"));
        }

        [TestCase(typeof(SendGridSettings), "apiKey")]
        [TestCase(typeof(MailgunSettings), "apiKey")]
        [TestCase(typeof(JoinSettings), "apiKey")]
        [TestCase(typeof(LazyLibrarianImportSettings), "apiKey")]
        [TestCase(typeof(SlackSettings), "webHookUrl")]
        [TestCase(typeof(DiscordSettings), "webHookUrl")]
        [TestCase(typeof(WebhookSettings), "url")]
        public void should_mask_known_provider_secrets(System.Type settingsType, string fieldName)
        {
            var settings = System.Activator.CreateInstance(settingsType);
            var propertyName = fieldName[0].ToString().ToUpperInvariant() + fieldName.Substring(1);
            var property = settingsType.GetProperty(propertyName) ??
                           settingsType.GetProperties().Single(p => p.Name.ToLowerInvariant() == fieldName.ToLowerInvariant());

            property.SetValue(settings, "real-secret");

            var schema = SchemaBuilder.ToSchema(settings);

            Assert.That(schema.Single(f => f.Name == fieldName).Value, Is.EqualTo("********"));
        }

        [TestCaseSource(nameof(SensitiveProviderFields))]
        public void should_mark_sensitive_provider_fields(System.Type settingsType, PropertyInfo property, FieldDefinitionAttribute field)
        {
            var isSensitive =
                field.Privacy == PrivacyLevel.ApiKey ||
                field.Privacy == PrivacyLevel.Password ||
                field.Type == FieldType.Password;

            Assert.That(isSensitive, Is.True, $"{settingsType.FullName}.{property.Name} has sensitive-looking label/name but is not marked sensitive.");
        }
    }
}
