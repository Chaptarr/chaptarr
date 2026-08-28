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
    }
}
