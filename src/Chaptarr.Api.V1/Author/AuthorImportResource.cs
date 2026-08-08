using System.Text.Json.Serialization;
using Chaptarr.Http.REST;

namespace Chaptarr.Api.V1.Author
{
    public class AuthorImportResource : RestResource
    {
        [JsonPropertyName("foreignAuthorId")]
        public string ForeignAuthorId { get; set; }

        [JsonPropertyName("mediaType")]
        public string MediaType { get; set; }

        [JsonPropertyName("rootFolder")]
        public string RootFolder { get; set; }

        [JsonPropertyName("qualityProfileId")]
        public int QualityProfileId { get; set; }

        [JsonPropertyName("metadataProfileId")]
        public int MetadataProfileId { get; set; }

        [JsonPropertyName("monitorExisting")]
        public string MonitorExisting { get; set; }

        [JsonPropertyName("monitorFuture")]
        public bool MonitorFuture { get; set; }

        [JsonPropertyName("manualFlag")]
        public bool ManualFlag { get; set; }

        [JsonPropertyName("searchForMissingBooks")]
        public bool? SearchForMissingBooks { get; set; }
    }
}
