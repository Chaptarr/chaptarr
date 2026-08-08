using System;
using System.Collections.Generic;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Indexers;

namespace NzbDrone.Core.Download
{
    public enum DownloadClientFileListConfidence
    {
        Unavailable = 0,
        Pending = 1,
        Degraded = 2,
        Disk = 3,
        Authoritative = 4
    }

    public class DownloadClientFileSnapshot : ModelBase
    {
        public int DownloadClientId { get; set; }
        public string DownloadId { get; set; }
        public DownloadProtocol Protocol { get; set; }
        public string Title { get; set; }
        public string Category { get; set; }
        public string OutputPath { get; set; }
        public string Source { get; set; }
        public DownloadClientFileListConfidence Confidence { get; set; }
        public List<string> FilePaths { get; set; } = new();
        public DateTime CreatedAt { get; set; }
        public DateTime LastUpdated { get; set; }
    }
}
