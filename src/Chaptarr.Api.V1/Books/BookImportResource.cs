using System.Text.Json.Serialization;
using Chaptarr.Http.REST;
using NzbDrone.Core.Books;

namespace Chaptarr.Api.V1.Books
{
    public class BookImportResource : RestResource
    {
        public string ForeignBookId { get; set; }
        public string ForeignAuthorId { get; set; }
        public string ForeignEditionId { get; set; }
        public string MediaType { get; set; }
        public string RootFolder { get; set; }
        public int QualityProfileId { get; set; }
        public int MetadataProfileId { get; set; }
        public BookImportAuthorMonitoring AuthorMonitoring { get; set; }
    }

    public class BookImportAuthorMonitoring
    {
        // One-time action for the current catalog; it is separate from the
        // persistent per-media author gate and new-row policy.
        [JsonPropertyName("monitor")]
        public string Monitor { get; set; }

        // Deprecated pre-binary-monitoring fields retained for older automation clients.
        [JsonPropertyName("monitorExisting")]
        public string MonitorExisting { get; set; }

        [JsonPropertyName("monitorFuture")]
        public bool? MonitorFuture { get; set; }

        [JsonPropertyName("audiobookMonitored")]
        public bool? AudiobookMonitored { get; set; }

        [JsonPropertyName("audiobookMonitorNewItems")]
        public NewItemMonitorTypes? AudiobookMonitorNewItems { get; set; }

        [JsonPropertyName("audiobookMonitorExistingMode")]
        public MonitorTypes? AudiobookMonitorExistingMode { get; set; }

        [JsonPropertyName("ebookMonitored")]
        public bool? EbookMonitored { get; set; }

        [JsonPropertyName("ebookMonitorNewItems")]
        public NewItemMonitorTypes? EbookMonitorNewItems { get; set; }

        [JsonPropertyName("ebookMonitorExistingMode")]
        public MonitorTypes? EbookMonitorExistingMode { get; set; }

        public bool SearchForMissing { get; set; }
    }
}
