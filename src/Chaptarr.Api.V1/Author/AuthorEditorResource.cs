using System.Collections.Generic;
using NzbDrone.Core.Books;

namespace Chaptarr.Api.V1.Author
{
    public class AuthorEditorResource
    {
        public List<int> AuthorIds { get; set; }
        // Nullable means "leave this media side unchanged" in a bulk edit. False is
        // an explicit pause, distinct from an unconfigured side.
        public bool? AudiobookMonitored { get; set; }
        public NewItemMonitorTypes? AudiobookMonitorNewItems { get; set; }
        public bool? EbookMonitored { get; set; }
        public NewItemMonitorTypes? EbookMonitorNewItems { get; set; }
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
