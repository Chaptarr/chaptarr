using System.Collections.Generic;
using NzbDrone.Core.Books;

namespace Chaptarr.Api.V1.Author
{
    public class AuthorEditorResource
    {
        public List<int> AuthorIds { get; set; }
        public bool? Monitored { get; set; }
        // TRI-STATE MONITORING SYSTEM - Integer per media type
        // Values: 0 = None (monitor nothing), 1 = All (monitor everything), 2 = Selected (monitor specific books only)
        public int? AudiobookMonitorExisting { get; set; } // 0=None, 1=All, 2=Selected
        public bool? AudiobookMonitorFuture { get; set; } // true=monitor, false=don't monitor
        public int? EbookMonitorExisting { get; set; } // 0=None, 1=All, 2=Selected
        public bool? EbookMonitorFuture { get; set; } // true=monitor, false=don't monitor
        public bool? SyncMonitoredAcrossFormats { get; set; }
        public int? AudiobookQualityProfileId { get; set; }
        public int? EbookQualityProfileId { get; set; }
        public int? MetadataProfileId { get; set; }
        public int? AudiobookMetadataProfileId { get; set; }
        public int? EbookMetadataProfileId { get; set; }
        public string AudiobookRootFolderPath { get; set; }
        public string EbookRootFolderPath { get; set; }
        public List<int> Tags { get; set; }
        public ApplyTags ApplyTags { get; set; }
        public bool MoveFiles { get; set; }
        public bool DeleteFiles { get; set; }
    }
}
