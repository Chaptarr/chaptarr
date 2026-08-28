using System.Text.Json.Serialization;
using Chaptarr.Http.REST;
using NzbDrone.Core.Books;

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

        // One-time action for the books in the author catalog at import time.
        // This is intentionally separate from the persistent per-media new-row policy.
        [JsonPropertyName("monitor")]
        public string Monitor { get; set; }

        // Deprecated pre-binary-monitoring fields. Kept at the wire boundary so older clients
        // retain their previous intent while the core model uses the new gate/policy split.
        [JsonPropertyName("monitorExisting")]
        public string MonitorExisting { get; set; }

        [JsonPropertyName("monitorFuture")]
        public bool? MonitorFuture { get; set; }

        [JsonPropertyName("audiobookMonitored")]
        public bool? AudiobookMonitored { get; set; }

        [JsonPropertyName("audiobookMonitorNewItems")]
        public NewItemMonitorTypes? AudiobookMonitorNewItems { get; set; }

        // One-time action for the current audiobook catalog; not persisted as an author policy.
        [JsonPropertyName("audiobookMonitorExistingMode")]
        public MonitorTypes? AudiobookMonitorExistingMode { get; set; }

        [JsonPropertyName("ebookMonitored")]
        public bool? EbookMonitored { get; set; }

        [JsonPropertyName("ebookMonitorNewItems")]
        public NewItemMonitorTypes? EbookMonitorNewItems { get; set; }

        // One-time action for the current ebook catalog; not persisted as an author policy.
        [JsonPropertyName("ebookMonitorExistingMode")]
        public MonitorTypes? EbookMonitorExistingMode { get; set; }

        [JsonPropertyName("manualFlag")]
        public bool ManualFlag { get; set; }

        [JsonPropertyName("searchForMissingBooks")]
        public bool? SearchForMissingBooks { get; set; }
    }
}
