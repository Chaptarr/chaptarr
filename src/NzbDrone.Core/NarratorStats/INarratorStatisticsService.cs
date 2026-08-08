using System.Collections.Generic;

namespace NzbDrone.Core.NarratorStats
{
    public interface INarratorStatisticsService
    {
        List<NarratorStatistics> NarratorStatistics();
        NarratorStatistics NarratorStatistics(int narratorId);
    }
}
