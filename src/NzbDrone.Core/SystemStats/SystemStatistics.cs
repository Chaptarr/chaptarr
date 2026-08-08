namespace NzbDrone.Core.SystemStats
{
    public class SystemStatistics
    {
        public int TotalBooks { get; set; }
        public int AudiobookCount { get; set; }
        public int EbookCount { get; set; }
        public int MonitoredBooks { get; set; }
        public int AudiobooksMonitored { get; set; }
        public int EbooksMonitored { get; set; }
        public int FileCount { get; set; }
        public long TotalFileSize { get; set; }
        public int AuthorCount { get; set; }
    }
}