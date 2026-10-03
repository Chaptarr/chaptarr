using Chaptarr.Api.V1.System;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class SystemIntegrationCapabilitiesResourceFixture
    {
        [TestCase(true, "hc")]
        [TestCase(false, "gr")]
        public void should_advertise_the_provider_id_dialect_selected_by_configuration(
            bool hardcoverEnabled,
            string expectedDialect)
        {
            var resource = SystemIntegrationCapabilitiesResource.Create(hardcoverEnabled);

            Assert.That(resource.Contract, Is.EqualTo("chaptarrng-seerr-bookshelf"));
            Assert.That(resource.ContractVersion, Is.EqualTo(1));
            Assert.That(resource.AppName, Is.EqualTo("Chaptarr"));
            Assert.That(resource.ProviderIdDialect, Is.EqualTo(expectedDialect));
            Assert.That(resource.MediaTypes, Is.EqualTo(new[] { "ebook", "audiobook" }));
            Assert.That(resource.Features.FormatScopedFacade, Is.True);
            Assert.That(resource.Features.PendingAuthorImports, Is.True);
            Assert.That(resource.Features.PendingImportCancellation, Is.True);
        }
    }
}
