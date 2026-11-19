using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Writers;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Swashbuckle.AspNetCore.Swagger;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class OpenApiContractFixture
    {
        private static string Generate()
        {
            using var services = OpenApiNullableReferenceFixture.CreateServices();
            var document = services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
            using var text = new StringWriter();
            document.SerializeAsV3(new OpenApiJsonWriter(text));
            return text.ToString();
        }

        [Test]
        public void published_contract_must_match_current_api()
        {
            var generated = Generate();
            var output = Path.Combine(TestContext.CurrentContext.WorkDirectory, "openapi.generated.json");
            File.WriteAllText(output, generated);
            TestContext.AddTestAttachment(output);
            var snapshot = File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "openapi.json"));
            AssertMatches(JToken.Parse(snapshot), JToken.Parse(generated));
        }

        private static void AssertMatches(JToken snapshot, JToken generated)
        {
            Assert.That(JToken.DeepEquals(snapshot, generated), Is.True,
                "openapi.json is stale. Regenerate it with scripts/docs.sh, review the diff and include it with the API change. See openapi.generated.json in the test attachments.");
        }

        [TestCase("field")]
        [TestCase("route")]
        [TestCase("parameter")]
        public void missing_public_contract_must_fail_the_gate(string missing)
        {
            var current = JToken.Parse(Generate());
            var stale = current.DeepClone();
            if (missing == "field")
            {
                ((JObject)stale["components"]["schemas"]["QualityProfileResource"]["properties"]).Remove("confirmAutomaticUpgrades");
            }
            else if (missing == "route")
            {
                ((JObject)stale["paths"]).Remove("/api/v1/manualimport/replacementfiles");
            }
            else
            {
                stale["paths"]["/api/v1/author"]["post"]["parameters"].First.Remove();
            }

            Assert.Throws<AssertionException>(() => AssertMatches(stale, current));
        }

        [Test]
        public void initial_setup_state_is_read_only_but_search_options_are_writable()
        {
            var schema = JToken.Parse(Generate())["components"]["schemas"]["AddAuthorOptions"]["properties"];
            foreach (var field in new[] { "awaitingMetadata", "metadataUnavailable", "initialFormatSyncFailureCount", "initialFormatSyncNotBefore",
                "audiobookSetupPending", "ebookSetupPending", "audiobookAwaitingMetadata", "ebookAwaitingMetadata",
                "audiobookSetupPendingImportId", "ebookSetupPendingImportId", "audiobookSetupSearchForMissingBooks",
                "ebookSetupSearchForMissingBooks", "independentSearchForMissingBooks" })
                Assert.That((bool?)schema[field]["readOnly"], Is.True, field);
            Assert.That((bool?)schema["searchForMissingBooks"]["readOnly"], Is.Not.True);
        }

        [Test]
        public void book_series_labels_are_a_read_only_string_array()
        {
            var field = JToken.Parse(Generate())["components"]["schemas"]["BookResource"]["properties"]["seriesLabels"];
            Assert.Multiple(() =>
            {
                Assert.That((string)field["type"], Is.EqualTo("array"));
                Assert.That((string)field["items"]["type"], Is.EqualTo("string"));
                Assert.That((bool?)field["readOnly"], Is.True);
            });
        }

        [Test]
        public void generation_must_be_deterministic()
        {
            Assert.That(Generate(), Is.EqualTo(Generate()));
        }

        [Test]
        public void edition_copy_status_omits_legacy_flag_and_marks_copy_fields_read_only()
        {
            var fields = JToken.Parse(Generate())["components"]["schemas"]["EditionResource"]["properties"];
            Assert.Multiple(() =>
            {
                Assert.That(fields["monitoredByAnotherAudiobookBook"], Is.Null);
                Assert.That((bool?)fields["existingCopyId"]["readOnly"], Is.True);
                Assert.That((bool?)fields["existingCopyMonitored"]["readOnly"], Is.True);
            });
        }
    }
}
