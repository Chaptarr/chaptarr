namespace NzbDrone.Core.SystemStats
{
    public interface ISystemStatisticsService
    {
        SystemStatistics GetSystemStatistics(string mediaType);
    }
}