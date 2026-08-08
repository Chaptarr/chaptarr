using System.Collections.Generic;

namespace NzbDrone.Core.NarratorStats
{
    public interface INarratorStatisticsRepository
    {
        List<BookStatistics> NarratorStatistics();
        List<BookStatistics> NarratorStatistics(int narratorId);
    }
}
