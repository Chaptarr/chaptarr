using System.Collections.Generic;

namespace Chaptarr.Api.V1.MatchingLogs
{
    public class MatchingLogPreviewResource
    {
        public int TotalEntries { get; set; }
        public int SampleCount { get; set; }
        public int MaxEntries { get; set; }
        public int MinutesBack { get; set; }
        public bool FailedMatchesOnly { get; set; }
        public string MediaType { get; set; }
        public string Scope { get; set; }
        public int LogsRotateMinutes { get; set; } = 30;
        public List<MatchingLogPreviewEntryResource> Samples { get; set; } = new List<MatchingLogPreviewEntryResource>();
    }

    public class MatchingLogPreviewEntryResource
    {
        public long Timestamp { get; set; }
        public string Path { get; set; }
        public string FileName { get; set; }
        public string MediaType { get; set; }
        public bool Success { get; set; }
        public string Reason { get; set; }
        public string Decision { get; set; }
        public string AuthorMatched { get; set; }
        public string BookMatched { get; set; }
        public string EditionMatched { get; set; }
        public string MatchedVia { get; set; }
        public string MatchedEditionTitle { get; set; }
        public string TopRejectionReason { get; set; }
        public string TopRejectionDetail { get; set; }
        public string TopRejectionTitle { get; set; }
        public string UploadEntryJson { get; set; }
        public Dictionary<string, List<string>> Tags { get; set; } = new Dictionary<string, List<string>>();
    }
}
