using System.IO;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers;

namespace NzbDrone.Core.Download.Clients.Direct
{
    public interface IInternalDirectClientProvider
    {
        IDownloadClient GetClient();
    }

    public class InternalDirectClientProvider : IInternalDirectClientProvider
    {
        private const int InternalDirectClientId = -1;
        private const string InternalStagingFolderName = "direct-staging";

        private readonly IHttpClient _httpClient;
        private readonly IDiskProvider _diskProvider;
        private readonly IConfigService _configService;
        private readonly IAppFolderInfo _appFolderInfo;
        private readonly Logger _logger;

        public InternalDirectClientProvider(IHttpClient httpClient,
                                            IDiskProvider diskProvider,
                                            IConfigService configService,
                                            IAppFolderInfo appFolderInfo,
                                            Logger logger)
        {
            _httpClient = httpClient;
            _diskProvider = diskProvider;
            _configService = configService;
            _appFolderInfo = appFolderInfo;
            _logger = logger;
        }

        public IDownloadClient GetClient()
        {
            var stagingFolder = Path.Combine(_appFolderInfo.AppDataFolder, InternalStagingFolderName);

            _diskProvider.EnsureFolder(stagingFolder);

            var grabUrlResolver = new DirectDownloadGrabUrlResolver(_httpClient);
            return new DirectDownloadClient(_httpClient, _diskProvider, _configService, _logger, grabUrlResolver)
            {
                Definition = new DownloadClientDefinition
                {
                    Id = InternalDirectClientId,
                    Name = "Direct Download",
                    ImplementationName = nameof(DirectDownloadClient),
                    Enable = true,
                    Protocol = DownloadProtocol.Direct,
                    RemoveCompletedDownloads = true,
                    RemoveFailedDownloads = true,
                    Settings = new DirectDownloadClientSettings { StagingFolder = stagingFolder }
                }
            };
        }
    }
}
