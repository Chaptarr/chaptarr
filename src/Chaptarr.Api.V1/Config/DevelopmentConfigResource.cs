using Chaptarr.Http.REST;
using NzbDrone.Core.Configuration;

namespace Prowlarr.Api.V1.Config
{
    public class DevelopmentConfigResource : RestResource
    {
        public string MetadataServerUrl { get; set; }
        public string ConsoleLogLevel { get; set; }
        public bool LogSql { get; set; }
        public int LogRotate { get; set; }
        public bool FilterSentryEvents { get; set; }
    }

    public static class DevelopmentConfigResourceMapper
    {
        public static DevelopmentConfigResource ToResource(this IConfigFileProvider model, IConfigService configService)
        {
            return new DevelopmentConfigResource
            {
                MetadataServerUrl = configService.MetadataServerUrl,
                ConsoleLogLevel = model.ConsoleLogLevel,
                LogSql = model.LogSql,
                LogRotate = model.LogRotate,
                FilterSentryEvents = model.FilterSentryEvents
            };
        }
    }
}
