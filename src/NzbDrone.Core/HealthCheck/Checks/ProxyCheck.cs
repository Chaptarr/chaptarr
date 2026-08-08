using System;
using System.Linq;
using System.Net;
using NLog;
using NzbDrone.Common.Cloud;
using NzbDrone.Common.Http;
using NzbDrone.Common.Http.Proxy;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Configuration.Events;
using NzbDrone.Core.Http;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Localization;

namespace NzbDrone.Core.HealthCheck.Checks
{
    [CheckOn(typeof(ConfigSavedEvent))]
    public class ProxyCheck : HealthCheckBase
    {
        private readonly Logger _logger;
        private readonly IConfigService _configService;
        private readonly IGlobalProxySettingsResolver _globalProxySettingsResolver;
        private readonly IIndexerHttpClientFactory _indexerHttpClientFactory;

        private readonly IHttpRequestBuilderFactory _cloudRequestBuilder;
        private readonly IHttpRequestBuilderFactory _gitHubRequestBuilder;

        public ProxyCheck(IChaptarrCloudRequestBuilder cloudRequestBuilder,
                          IConfigService configService,
                          IGlobalProxySettingsResolver globalProxySettingsResolver,
                          IIndexerHttpClientFactory indexerHttpClientFactory,
                          ILocalizationService localizationService,
                          Logger logger)
            : base(localizationService)
        {
            _configService = configService;
            _globalProxySettingsResolver = globalProxySettingsResolver;
            _indexerHttpClientFactory = indexerHttpClientFactory;
            _logger = logger;

            _cloudRequestBuilder = cloudRequestBuilder.Services;
            _gitHubRequestBuilder = cloudRequestBuilder.GitHubApi;
        }

        public override HealthCheck Check()
        {
            if (_configService.ProxyEnabled)
            {
                HttpProxySettings proxySettings;
                try
                {
                    proxySettings = _globalProxySettingsResolver.ResolveRequired();
                    var addresses = Dns.GetHostAddresses(proxySettings.Host);
                    if (!addresses.Any())
                    {
                        return new HealthCheck(GetType(), HealthCheckResult.Error, string.Format(_localizationService.GetLocalizedString("ProxyCheckResolveIpMessage"), proxySettings.Host), "#proxy-failed-resolve-ip");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Proxy configuration is invalid");
                    return new HealthCheck(GetType(), HealthCheckResult.Error, ex.Message, "#proxy-failed-configuration");
                }

                // Use GitHub API for proxy check if GitHub updates are enabled
                var request = _configService.UseGitHubUpdates
                    ? _gitHubRequestBuilder.Create()
                                          .Resource("rate_limit")
                                          .Build()
                    : _cloudRequestBuilder.Create()
                                          .Resource("/ping")
                                          .Build();

                try
                {
                    var response = _indexerHttpClientFactory.GetClient(null).Execute(request);

                    // We only care about 400 responses, other error codes can be ignored
                    if (response.StatusCode == HttpStatusCode.BadRequest)
                    {
                        _logger.Error("Proxy Health Check failed: {0}", response.StatusCode);
                        return new HealthCheck(GetType(), HealthCheckResult.Error, string.Format(_localizationService.GetLocalizedString("ProxyCheckBadRequestMessage"), response.StatusCode), "#proxy-failed-test");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Proxy Health Check failed");
                    return new HealthCheck(GetType(), HealthCheckResult.Error, string.Format(_localizationService.GetLocalizedString("ProxyCheckFailedToTestMessage"), request.Url), "#proxy-failed-test");
                }
            }

            return new HealthCheck(GetType());
        }
    }
}
