using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.NarratorStats
{
    public class BookStatistics : ResultSet
    {
        public int NarratorId { get; set; }
        public int BookId { get; set; }
        public int BookFileCount { get; set; }
        public int BookCount { get; set; }
        public int AvailableBookCount { get; set; }
        public int TotalBookCount { get; set; }
        public long SizeOnDisk { get; set; }
    }
}
