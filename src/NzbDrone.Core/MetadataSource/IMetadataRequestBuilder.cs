using System;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Core.MetadataSource
{
    public interface IMetadataRequestBuilder
    {
        IHttpRequestBuilderFactory GetRequestBuilder();
    }

    public class MetadataRequestBuilder : IMetadataRequestBuilder
    {
        private readonly IConfigService _configService;

        public MetadataRequestBuilder(IConfigService configService)
        {
            _configService = configService;
        }

        public IHttpRequestBuilderFactory GetRequestBuilder()
        {
            var serverUrl = _configService.MetadataServerUrl;
            if (serverUrl.IsNullOrWhiteSpace())
            {
                throw new InvalidOperationException("Metadata server URL is not configured");
            }

            return new HttpRequestBuilder(serverUrl.TrimEnd("/") + "/{route}").KeepAlive().CreateFactory();
        }

    }
}
