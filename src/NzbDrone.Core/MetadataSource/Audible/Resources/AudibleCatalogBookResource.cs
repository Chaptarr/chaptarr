using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace NzbDrone.Core.MetadataSource.Audible.Resources
{
    public class AudibleCatalogBookResource
    {
        [JsonProperty("asin")]
        public string Asin { get; set; }

        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("subtitle")]
        public string Subtitle { get; set; }

        [JsonProperty("region")]
        public string Region { get; set; }

        [JsonProperty("regions")]
        public List<string> Regions { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("summary")]
        public string Summary { get; set; }

        [JsonProperty("copyright")]
        public string Copyright { get; set; }

        [JsonProperty("bookFormat")]
        public string BookFormat { get; set; }

        [JsonProperty("imageUrl")]
        public string ImageUrl { get; set; }

        [JsonProperty("lengthMinutes")]
        public int? LengthMinutes { get; set; }

        [JsonProperty("whisperSync")]
        public bool? WhisperSync { get; set; }

        [JsonProperty("publisher")]
        public string Publisher { get; set; }

        [JsonProperty("isbn")]
        public string Isbn { get; set; }

        [JsonProperty("language")]
        public string Language { get; set; }

        [JsonProperty("rating")]
        public decimal? Rating { get; set; }

        [JsonProperty("releaseDate")]
        public DateTime? ReleaseDate { get; set; }

        [JsonProperty("explicit")]
        public bool? Explicit { get; set; }

        [JsonProperty("hasPdf")]
        public bool? HasPdf { get; set; }

        [JsonProperty("link")]
        public string Link { get; set; }

        [JsonProperty("sku")]
        public string Sku { get; set; }

        [JsonProperty("skuGroup")]
        public string SkuGroup { get; set; }

        [JsonProperty("isListenable")]
        public bool? IsListenable { get; set; }

        [JsonProperty("isAvailable")]
        public bool? IsAvailable { get; set; }

        [JsonProperty("contentType")]
        public string ContentType { get; set; }

        [JsonProperty("contentDeliveryType")]
        public string ContentDeliveryType { get; set; }

        [JsonProperty("authors")]
        public List<AudibleCatalogAuthorResource> Authors { get; set; }

        [JsonProperty("narrators")]
        public List<AudibleCatalogNarratorResource> Narrators { get; set; }

        [JsonProperty("genres")]
        public List<AudibleCatalogGenreResource> Genres { get; set; }

        [JsonProperty("series")]
        public List<AudibleCatalogSeriesResource> Series { get; set; }

        [JsonProperty("updatedAt")]
        public DateTime? UpdatedAt { get; set; }

        // Helper properties
        public string FullTitle => string.IsNullOrEmpty(Subtitle) ? Title : $"{Title}: {Subtitle}";

        public TimeSpan? Duration => LengthMinutes.HasValue ? TimeSpan.FromMinutes(LengthMinutes.Value) : null;

        public bool IsGraphicAudio =>
            Publisher?.Contains("GraphicAudio", StringComparison.OrdinalIgnoreCase) == true ||
            Narrators?.Count > 5 ||
            (Narrators?.Any(n => n.Name?.Contains("cast", StringComparison.OrdinalIgnoreCase) == true) == true);
    }

    public class AudibleCatalogAuthorResource
    {
        [JsonProperty("asin")]
        public string Asin { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("region")]
        public string Region { get; set; }

        [JsonProperty("regions")]
        public List<string> Regions { get; set; }

        [JsonProperty("image")]
        public string Image { get; set; }

        [JsonProperty("updatedAt")]
        public DateTime? UpdatedAt { get; set; }
    }

    public class AudibleCatalogNarratorResource
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("asin")]
        public string Asin { get; set; }

        [JsonProperty("image")]
        public string Image { get; set; }

        [JsonProperty("updatedAt")]
        public DateTime? UpdatedAt { get; set; }
    }

    public class AudibleCatalogGenreResource
    {
        [JsonProperty("asin")]
        public string Asin { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("betterType")]
        public string BetterType { get; set; }

        [JsonProperty("updatedAt")]
        public DateTime? UpdatedAt { get; set; }
    }

    public class AudibleCatalogSeriesResource
    {
        [JsonProperty("asin")]
        public string Asin { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("position")]
        public string Position { get; set; }

        [JsonProperty("updatedAt")]
        public DateTime? UpdatedAt { get; set; }
    }

    public class AudibleCatalogProductsResponse
    {
        [JsonProperty("products")]
        public List<AudibleCatalogProductResource> Products { get; set; }
    }

    public class AudibleCatalogProductResponse
    {
        [JsonProperty("product")]
        public AudibleCatalogProductResource Product { get; set; }
    }

    public class AudibleCatalogProductResource
    {
        [JsonProperty("asin")]
        public string Asin { get; set; }

        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("subtitle")]
        public string Subtitle { get; set; }

        [JsonProperty("merchandising_summary")]
        public string MerchandisingSummary { get; set; }

        [JsonProperty("publisher_name")]
        public string PublisherName { get; set; }

        [JsonProperty("language")]
        public string Language { get; set; }

        [JsonProperty("release_date")]
        public DateTime? ReleaseDate { get; set; }

        [JsonProperty("issue_date")]
        public DateTime? IssueDate { get; set; }

        [JsonProperty("publication_datetime")]
        public DateTime? PublicationDateTime { get; set; }

        [JsonProperty("runtime_length_min")]
        public int? RuntimeLengthMin { get; set; }

        [JsonProperty("format_type")]
        public string FormatType { get; set; }

        [JsonProperty("content_type")]
        public string ContentType { get; set; }

        [JsonProperty("content_delivery_type")]
        public string ContentDeliveryType { get; set; }

        [JsonProperty("is_listenable")]
        public bool? IsListenable { get; set; }

        [JsonProperty("sku")]
        public string Sku { get; set; }

        [JsonProperty("sku_lite")]
        public string SkuLite { get; set; }

        [JsonProperty("product_images")]
        public Dictionary<string, string> ProductImages { get; set; }

        [JsonProperty("authors")]
        public List<AudibleCatalogContributorResource> Authors { get; set; }

        [JsonProperty("narrators")]
        public List<AudibleCatalogContributorResource> Narrators { get; set; }
    }

    public class AudibleCatalogContributorResource
    {
        [JsonProperty("asin")]
        public string Asin { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }
    }
}
