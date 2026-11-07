using Chaptarr.Http.REST;
using NzbDrone.Core.Configuration;

namespace Prowlarr.Api.V1.Config
{
    public class DevelopmentConfigResource : RestResource
    {
        private string _metadataServerUrl;
        private string _metadataSource;

        public string MetadataServerUrl
        {
            get => _metadataServerUrl;
            set
            {
                _metadataServerUrl = value;
                MetadataServerUrlWasProvided = true;
            }
        }
        public string MetadataSource
        {
            get => _metadataSource;
            set
            {
                _metadataSource = value;
                MetadataSourceWasProvided = true;
            }
        }
        public string ConsoleLogLevel { get; set; }
        public bool LogSql { get; set; }
        public int LogRotate { get; set; }
        public bool FilterSentryEvents { get; set; }

        internal bool MetadataServerUrlWasProvided { get; private set; }
        internal bool MetadataSourceWasProvided { get; private set; }

        internal void ApplyBookshelfCompatibilityAliases()
        {
            if (!MetadataServerUrlWasProvided && MetadataSourceWasProvided)
            {
                MetadataServerUrl = MetadataSource;
            }
        }
    }

    public static class DevelopmentConfigResourceMapper
    {
        public static DevelopmentConfigResource ToResource(this IConfigFileProvider model, IConfigService configService)
        {
            return new DevelopmentConfigResource
            {
                MetadataServerUrl = configService.MetadataServerUrl,
                MetadataSource = configService.MetadataServerUrl,
                ConsoleLogLevel = model.ConsoleLogLevel,
                LogSql = model.LogSql,
                LogRotate = model.LogRotate,
                FilterSentryEvents = model.FilterSentryEvents
            };
        }
    }
}
