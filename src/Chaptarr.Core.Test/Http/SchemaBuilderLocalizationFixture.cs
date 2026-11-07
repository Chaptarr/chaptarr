using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Chaptarr.Http.ClientSchema;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Notifications;

namespace Chaptarr.Core.Test.Http
{
    [TestFixture]
    [NonParallelizable]
    public class SchemaBuilderLocalizationFixture
    {
        [Test]
        public void should_localize_cached_schema_metadata_for_each_response_without_changing_values_or_example_hints()
        {
            var locale = "ar";
            var localization = new Mock<ILocalizationService>();
            localization.Setup(service => service.GetLocalizedString(It.IsAny<string>(), It.IsAny<Dictionary<string, object>>()))
                .Returns((string id, Dictionary<string, object> tokens) =>
                    id == "UseSslHelpText" ? locale + " " + tokens["serviceName"] : locale + " " + id);
            var serviceField = typeof(SchemaBuilder).GetField("_localizationService", BindingFlags.NonPublic | BindingFlags.Static);
            var previous = serviceField.GetValue(null);
            serviceField.SetValue(null, localization.Object);
            try
            {
                var settings = new LocalizedSettings { Mode = 7 };
                var first = SchemaBuilder.ToSchema(settings).Single();
                Assert.That(first.Label, Is.EqualTo("ar Title"));
                Assert.That(first.HelpText, Is.EqualTo("ar Provider"));
                Assert.That(first.Value, Is.EqualTo(7));
                Assert.That(first.SelectOptions[0].Name, Is.EqualTo("ar Low"));
                Assert.That(first.SelectOptions[0].Hint, Is.EqualTo("J.R.R. Tolkien"));
                Assert.That(first.SelectOptions[1].Hint, Is.EqualTo("ar provider.option.testHint"));

                locale = "de";
                var second = SchemaBuilder.ToSchema(settings).Single();
                Assert.That(second.Label, Is.EqualTo("de Title"));
                Assert.That(second.HelpText, Is.EqualTo("de Provider"));
                Assert.That(second.SelectOptions[0].Name, Is.EqualTo("de Low"));
                Assert.That(first.SelectOptions[0].Name, Is.EqualTo("ar Low"));
                Assert.That(SchemaBuilder.GetFieldMappings(typeof(LocalizedSettings))[0].Field.Label, Is.EqualTo("Title"));
                Assert.That(SchemaBuilder.GetFieldMappings(typeof(LocalizedSettings))[0].Field.SelectOptions[0].Name, Is.EqualTo("Low"));
            }
            finally
            {
                serviceField.SetValue(null, previous);
            }
        }

        [Test]
        public void every_static_provider_option_should_have_an_explicit_catalog_label_while_its_protocol_value_stays_unchanged()
        {
            var core = typeof(INotification).Assembly;
            var english = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "Localization", "Core", "en.json")));
            var optionTypes = core.GetTypes().SelectMany(type => type.GetProperties())
                .Select(property => property.GetCustomAttribute<FieldDefinitionAttribute>())
                .Where(attribute => attribute?.SelectOptions?.IsEnum == true)
                .Select(attribute => attribute.SelectOptions).Distinct().ToList();
            Assert.That(optionTypes.Count, Is.EqualTo(27));
            foreach (var type in optionTypes)
            {
                foreach (var field in type.GetFields().Where(field => field.IsStatic && !field.IsDefined(typeof(System.ObsoleteAttribute))))
                {
                    var option = field.GetCustomAttribute<FieldOptionAttribute>();
                    Assert.That(option?.Label, Is.Not.Null.And.Not.Empty, type.FullName + "." + field.Name);
                    Assert.That(english.ContainsKey(option.Label), Is.True, type.FullName + "." + field.Name);
                    Assert.That(field.GetRawConstantValue(), Is.Not.Null);
                }
            }
        }

        public class LocalizedSettings
        {
            [FieldDefinition(0, Label = "Title", HelpText = "UseSslHelpText", Type = FieldType.Select, SelectOptions = typeof(LocalizedOptions))]
            [FieldToken(TokenField.HelpText, "Title", "serviceName", "Provider")]
            public int Mode { get; set; }
        }

        public enum LocalizedOptions
        {
            [FieldOption(Label = "Low", Hint = "J.R.R. Tolkien")]
            Low = 7,
            [FieldOption(Label = "High", Hint = "provider.option.testHint")]
            High = 9
        }
    }
}
