using System.Collections.Generic;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.NarratorStats
{
    public class NarratorStatistics : ResultSet
    {
        public int NarratorId { get; set; }
        public int BookFileCount { get; set; }
        public int BookCount { get; set; }
        public int AvailableBookCount { get; set; }
        public int TotalBookCount { get; set; }
        public int SeriesCount { get; set; }
        public long SizeOnDisk { get; set; }

        public List<BookStatistics> BookStatistics { get; set; }

        public NarratorStatistics()
        {
            BookStatistics = new List<BookStatistics>();
        }
    }
}
