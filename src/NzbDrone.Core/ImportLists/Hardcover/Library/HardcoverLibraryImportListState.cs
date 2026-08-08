using System;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.ImportLists.Hardcover.Library
{
    public class HardcoverLibraryImportListState : ModelBase
    {
        public int ImportListId { get; set; }
        public int HardcoverUserId { get; set; }
        public string SettingsSignature { get; set; }
        public DateTime? CursorUpdatedAt { get; set; }
        public int? CursorUserBookId { get; set; }
        public int? OwnedCursorListBookId { get; set; }
        public DateTime? LastFullSyncAt { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }
}
