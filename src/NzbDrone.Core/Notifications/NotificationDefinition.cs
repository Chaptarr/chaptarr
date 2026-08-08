using NzbDrone.Core.ThingiProvider;

namespace NzbDrone.Core.Notifications
{
    public class NotificationDefinition : ProviderDefinition
    {
        public bool OnGrab { get; set; }
        public bool OnReleaseImport { get; set; }
        public bool OnUpgrade { get; set; }
        public bool OnRename { get; set; }
        public bool OnAuthorAdded { get; set; }
        public bool OnBookAdded { get; set; }
        public bool OnAuthorDelete { get; set; }
        public bool OnBookDelete { get; set; }
        public bool OnBookFileDelete { get; set; }
        public bool OnBookFileDeleteForUpgrade { get; set; }
        public bool OnHealthIssue { get; set; }
        public bool OnHealthRestored { get; set; }
        public bool OnDownloadFailure { get; set; }
        public bool OnImportFailure { get; set; }
        public bool OnBookRetag { get; set; }
        public bool OnApplicationUpdate { get; set; }
        public bool SupportsOnGrab { get; set; }
        public bool SupportsOnReleaseImport { get; set; }
        public bool SupportsOnUpgrade { get; set; }
        public bool SupportsOnRename { get; set; }
        public bool SupportsOnAuthorAdded { get; set; }
        public bool SupportsOnBookAdded { get; set; }
        public bool SupportsOnAuthorDelete { get; set; }
        public bool SupportsOnBookDelete { get; set; }
        public bool SupportsOnBookFileDelete { get; set; }
        public bool SupportsOnBookFileDeleteForUpgrade { get; set; }
        public bool SupportsOnHealthIssue { get; set; }
        public bool SupportsOnHealthRestored { get; set; }
        public bool IncludeHealthWarnings { get; set; }
        public bool SupportsOnDownloadFailure { get; set; }
        public bool SupportsOnImportFailure { get; set; }
        public bool SupportsOnBookRetag { get; set; }
        public bool SupportsOnApplicationUpdate { get; set; }

        public override bool Enable =>

            // Always enable AudioBookShelf for metadata search functionality
            (Implementation == "AudioBookShelf") ||

            // Standard notification trigger logic for other providers
            (OnGrab || OnReleaseImport || (OnReleaseImport && OnUpgrade) || OnRename || OnAuthorAdded || OnBookAdded || OnAuthorDelete || OnBookDelete || OnBookFileDelete || OnBookFileDeleteForUpgrade || OnHealthIssue || OnHealthRestored || OnDownloadFailure || OnImportFailure || OnBookRetag || OnApplicationUpdate);
    }
}
