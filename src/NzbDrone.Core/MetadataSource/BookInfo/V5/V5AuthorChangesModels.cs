using System.Collections.Generic;
using Newtonsoft.Json;

namespace NzbDrone.Core.MetadataSource.BookInfo.V5
{
    // Request model for scoped bulk author diff checks.
    public class V5AuthorChangesRequest
    {
        [JsonProperty("items")]
        public List<V5AuthorETag> Items { get; set; } = new List<V5AuthorETag>();
    }

    // Individual owned author with its current ETag.
    public class V5AuthorETag
    {
        [JsonProperty("requestedId")]
        public string RequestedId { get; set; } // Prefixed ID like "hc:123" or "gr:456"
        
        [JsonProperty("etag")]
        public string ETag { get; set; } // Client's current opaque ETag for this author
    }

    public class V5ChangedAuthor
    {
        [JsonProperty("requestedId")]
        public string RequestedId { get; set; }

        [JsonProperty("canonicalId")]
        public string CanonicalId { get; set; }

        [JsonProperty("etag")]
        public string ETag { get; set; }
    }

    // Response model for scoped bulk author diffs.
    public class V5AuthorChangesResponse
    {
        [JsonProperty("changed")]
        public List<V5ChangedAuthor> Changed { get; set; } = new List<V5ChangedAuthor>();
        
        [JsonProperty("deleted")]
        public List<string> Deleted { get; set; } = new List<string>();
        
        [JsonProperty("merged")]
        public List<V5MergedAuthor> Merged { get; set; } = new List<V5MergedAuthor>();

        [JsonProperty("rejected")]
        public List<V5RejectedAuthor> Rejected { get; set; } = new List<V5RejectedAuthor>();
    }

    // Merged author information
    public class V5MergedAuthor
    {
        [JsonProperty("from")]
        public string From { get; set; } // Old ID
        
        [JsonProperty("to")]
        public string To { get; set; } // New ID this was merged into
    }

    public class V5RejectedAuthor
    {
        [JsonProperty("requestedId")]
        public string RequestedId { get; set; }

        [JsonProperty("reason")]
        public string Reason { get; set; }
    }
}
