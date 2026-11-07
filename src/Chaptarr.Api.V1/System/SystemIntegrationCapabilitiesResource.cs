namespace Chaptarr.Api.V1.System
{
    /// <summary>
    /// Additive feature discovery for clients integrating with Chaptarr's book APIs.
    /// System status continues to report AppName "Chaptarr" for Readarr compatibility.
    /// </summary>
    public class SystemIntegrationCapabilitiesResource
    {
        public static SystemIntegrationCapabilitiesResource Create(bool hardcoverEnabled)
        {
            return new SystemIntegrationCapabilitiesResource
            {
                ProviderIdDialect = hardcoverEnabled ? "hc" : "gr"
            };
        }

        public string Contract { get; set; } = "chaptarrng-seerr-bookshelf";
        public int ContractVersion { get; set; } = 1;
        public string AppName { get; set; } = "Chaptarr";
        public string ProviderIdDialect { get; set; }
        public string FacadePathTemplate { get; set; } = "/readarr/{dialect}/{mediaType}/api/v1";
        public string[] FacadeDialects { get; set; } = new[] { "hc", "gr" };
        public string[] MediaTypes { get; set; } = new[] { "ebook", "audiobook" };
        public SystemIntegrationFeaturesResource Features { get; set; } = new();
    }

    public class SystemIntegrationFeaturesResource
    {
        public bool FormatScopedFacade { get; set; } = true;
        public bool PagedLibrary { get; set; } = true;
        public bool ProviderScopedEditionIdentity { get; set; } = true;
        public bool PendingAuthorImports { get; set; } = true;
        public bool PendingImportCancellation { get; set; } = true;
        public bool RestrictedServiceApiKey { get; set; } = true;
        public bool FormatSpecificAvailabilityStatistics { get; set; } = true;

        /// <summary>A wanted edition can be tracked alongside the current selection (POST /book/{id}/editions/wanted with asNewVariant).</summary>
        public bool WantedEditionVariants { get; set; } = true;

        /// <summary>GET /series/narrators/matrix compares narrators across every volume of a series.</summary>
        public bool SeriesNarratorMatrix { get; set; } = true;

        /// <summary>GET /book/{id}/editions/catalog lists every known edition with narration details.</summary>
        public bool EditionCatalog { get; set; } = true;

        /// <summary>GET /bookshelf/legacy-identities returns durable source IDs from BookshelfNG conversions.</summary>
        public bool BookshelfLegacyIdentityMap { get; set; } = true;
    }
}
