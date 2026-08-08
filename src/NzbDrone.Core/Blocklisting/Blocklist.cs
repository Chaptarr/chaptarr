using System;
using System.Collections.Generic;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Blocklisting
{
    public class Blocklist : ModelBase
    {
        // Provider-prefixed IDs to avoid collisions across providers
        // Format: ["hc:123", "gr:456", "ol:789"]
        // NO local IDs - only provider IDs to prevent false matches
        public List<string> AuthorProviderIds { get; set; }
        public List<string> BookProviderIds { get; set; }
        
        // Download metadata
        public string SourceTitle { get; set; }
        public QualityModel Quality { get; set; }
        public DateTime Date { get; set; }
        public DateTime? PublishedDate { get; set; }
        public long? Size { get; set; }
        public DownloadProtocol Protocol { get; set; }
        public string Indexer { get; set; }
        public IndexerFlags IndexerFlags { get; set; }
        public string Message { get; set; }
        public string TorrentInfoHash { get; set; }
        
        public Blocklist()
        {
            AuthorProviderIds = new List<string>();
            BookProviderIds = new List<string>();
        }
    }
}
