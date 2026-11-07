using NUnit.Framework;
using NzbDrone.Core.MetadataSource.BookInfo;

namespace Chaptarr.Core.Test.MetadataSource.BookInfo
{
    [TestFixture]
    public class AdditionalMetadataSourcesFixture
    {
        [TestCase("googlebooks", "volume/123:edition", "googlebooks", "volume/123:edition")]
        [TestCase("loc-author", "person/123:456", "loc-author", "person/123:456")]
        [TestCase("openlibrary", "OL123W", "openlibrary", "OL123W")]
        public void should_round_trip_supported_catalog_provider_ids(
            string provider,
            string sourceId,
            string expectedProvider,
            string expectedSourceId)
        {
            var providerId = AdditionalMetadataSources.CreateProviderId(provider, sourceId);
            var parsed = AdditionalMetadataSources.TryParseProviderId(providerId, out var parsedProvider, out var parsedSourceId);

            Assert.Multiple(() =>
            {
                Assert.That(providerId, Does.StartWith("meta:"));
                Assert.That(parsed, Is.True);
                Assert.That(parsedProvider, Is.EqualTo(expectedProvider));
                Assert.That(parsedSourceId, Is.EqualTo(expectedSourceId));
            });
        }

        [Test]
        public void should_canonicalize_qualified_provider_ids_without_losing_colons_in_the_payload()
        {
            var providerId = AdditionalMetadataSources.CanonicalizeQualifiedProviderId("internetarchive:item:with:colons");
            var parsed = AdditionalMetadataSources.TryParseProviderId(providerId, out var provider, out var sourceId);

            Assert.Multiple(() =>
            {
                Assert.That(parsed, Is.True);
                Assert.That(provider, Is.EqualTo("internetarchive"));
                Assert.That(sourceId, Is.EqualTo("item:with:colons"));
            });
        }

        [Test]
        public void should_reject_unsupported_and_malformed_provider_ids()
        {
            Assert.Multiple(() =>
            {
                Assert.That(AdditionalMetadataSources.CreateProviderId("unknown", "123"), Is.Null);
                Assert.That(AdditionalMetadataSources.CreateProviderId("loc", " "), Is.Null);
                Assert.That(AdditionalMetadataSources.TryParseProviderId("meta:%%%", out _, out _), Is.False);
                Assert.That(AdditionalMetadataSources.CanonicalizeQualifiedProviderId("unknown:123"), Is.Null);
                Assert.That(AdditionalMetadataSources.CreateProviderId("loc", new string('x', 2000)), Is.Null);
            });
        }

        [Test]
        public void should_apply_environment_selection_and_require_credentials_for_restricted_catalogs()
        {
            var enabled = AdditionalMetadataSources.GetEnabledRuntimeSources(
                "loc;googlebooks;apify-goodreads;unsupported",
                "gutendex",
                hasRuntimeSources: true,
                legacyAdditionalSources: "internetarchive",
                hasLegacyAdditionalSources: true,
                googleBooksApiKey: string.Empty,
                europeanaApiKey: string.Empty,
                apifyActor: "actor",
                apifyToken: string.Empty,
                defaultPrimarySource: null,
                hardcoverConfigured: false);

            Assert.Multiple(() =>
            {
                Assert.That(enabled, Does.Contain("loc"));
                Assert.That(enabled, Does.Not.Contain("gutendex"));
                Assert.That(enabled, Does.Not.Contain("internetarchive"));
                Assert.That(enabled, Does.Not.Contain("googlebooks"));
                Assert.That(enabled, Does.Not.Contain("apify-goodreads"));
                Assert.That(enabled, Does.Not.Contain("unsupported"));
            });
        }

        [Test]
        public void should_preserve_saved_legacy_catalog_selection_and_use_safe_defaults_when_unconfigured()
        {
            var saved = AdditionalMetadataSources.GetConfiguredRuntimeSources(
                environmentSources: null,
                runtimeSources: null,
                hasRuntimeSources: false,
                legacyAdditionalSources: "internetarchive,ndl",
                hasLegacyAdditionalSources: true,
                googleBooksApiKey: string.Empty,
                europeanaApiKey: string.Empty,
                apifyActor: string.Empty,
                apifyToken: string.Empty,
                defaultPrimarySource: null);
            var defaults = AdditionalMetadataSources.GetConfiguredRuntimeSources(
                environmentSources: null,
                runtimeSources: null,
                hasRuntimeSources: false,
                legacyAdditionalSources: null,
                hasLegacyAdditionalSources: false,
                googleBooksApiKey: string.Empty,
                europeanaApiKey: string.Empty,
                apifyActor: string.Empty,
                apifyToken: string.Empty,
                defaultPrimarySource: null);

            Assert.Multiple(() =>
            {
                Assert.That(saved, Is.EquivalentTo(new[] { "internetarchive", "ndl" }));
                Assert.That(defaults, Is.EquivalentTo(new[] { "loc", "gutendex" }));
            });
        }
    }
}
