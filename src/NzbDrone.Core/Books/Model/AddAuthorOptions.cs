namespace NzbDrone.Core.Books
{
    public class AddAuthorOptions : MonitoringOptions
    {
        // Chaptarr can configure each media side independently. These one-time
        // modes are retained until the initial disk scan finishes, when file
        // ownership is known. Monitor remains the Readarr-compatible fallback.
        public MonitorTypes? AudiobookMonitor { get; set; }
        public MonitorTypes? EbookMonitor { get; set; }
        public bool SearchForMissingBooks { get; set; }
        public bool AwaitingMetadata { get; set; }
        // A declared provider refusal belongs to the existing Wanted Authors request.
        // Keep setup until Retry or its owning request's cancellation/deletion.
        public bool MetadataUnavailable { get; set; }
        public bool AudiobookSetupPending { get; set; }
        public bool EbookSetupPending { get; set; }
        public bool AudiobookAwaitingMetadata { get; set; }
        public bool EbookAwaitingMetadata { get; set; }
        public int? AudiobookSetupPendingImportId { get; set; }
        public int? EbookSetupPendingImportId { get; set; }
        public bool AudiobookSetupSearchForMissingBooks { get; set; }
        public bool EbookSetupSearchForMissingBooks { get; set; }
        public bool IndependentSearchForMissingBooks { get; set; }

        public string GetInitialScanMediaType()
        {
            var audiobook = AudiobookSetupPending || AudiobookMonitor.HasValue || AudiobookSetupSearchForMissingBooks;
            var ebook = EbookSetupPending || EbookMonitor.HasValue || EbookSetupSearchForMissingBooks;
            if (IndependentSearchForMissingBooks || audiobook && ebook) return "all";
            if (audiobook) return "audiobook";
            if (ebook) return "ebook";
            return MediaType?.ToString().ToLowerInvariant() ?? "all";
        }

        public void RecordInitialSearch(bool search, bool audiobookSetup, bool ebookSetup)
        {
            PreserveLegacySearch();
            if (search)
            {
                AudiobookSetupSearchForMissingBooks |= audiobookSetup;
                EbookSetupSearchForMissingBooks |= ebookSetup;
                IndependentSearchForMissingBooks |= !audiobookSetup && !ebookSetup;
            }

            UpdateInitialSearch();
        }

        public void CancelInitialSearch(bool audiobookSetup, bool ebookSetup)
        {
            PreserveLegacySearch();
            if (audiobookSetup) AudiobookSetupSearchForMissingBooks = false;
            if (ebookSetup) EbookSetupSearchForMissingBooks = false;
            UpdateInitialSearch();
        }

        private void PreserveLegacySearch()
        {
            if (SearchForMissingBooks && !AudiobookSetupSearchForMissingBooks && !EbookSetupSearchForMissingBooks)
                IndependentSearchForMissingBooks = true;
        }

        private void UpdateInitialSearch() => SearchForMissingBooks = AudiobookSetupSearchForMissingBooks ||
            EbookSetupSearchForMissingBooks || IndependentSearchForMissingBooks;
    }
}
