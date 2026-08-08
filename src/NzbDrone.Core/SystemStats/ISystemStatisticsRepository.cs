using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.SystemStats
{
    public interface ISystemStatisticsRepository
    {
        SystemStatistics GetSystemStatistics(string mediaType);
    }
}