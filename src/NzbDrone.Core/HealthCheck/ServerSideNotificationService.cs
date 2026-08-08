using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Core.HealthCheck
{
    public interface IServerSideNotificationService
    {
        public List<HealthCheck> GetServerChecks();
    }

    public class ServerSideNotificationService : IServerSideNotificationService
    {
        private readonly IHttpClient _client;
        private readonly IConfigFileProvider _configFileProvider;
        private readonly IConfigService _configService;
        private readonly Logger _logger;

        private readonly ICached<List<HealthCheck>> _cache;

        public ServerSideNotificationService(IHttpClient client,
                                             IConfigFileProvider configFileProvider,
                                             IConfigService configService,
                                             ICacheManager cacheManager,
                                             Logger logger)
        {
            _client = client;
            _configFileProvider = configFileProvider;
            _configService = configService;
            _logger = logger;

            _cache = cacheManager.GetCache<List<HealthCheck>>(GetType());
        }

        public List<HealthCheck> GetServerChecks()
        {
            var cacheKey = GetServerChecksCacheKey();
            if (cacheKey == null)
            {
                return new List<HealthCheck>();
            }

            return _cache.Get(cacheKey, () => RetrieveServerChecks(), TimeSpan.FromHours(2));
        }

        private string GetServerChecksCacheKey()
        {
            var metadataServerUrl = _configService.MetadataServerUrl?.Trim();
            if (string.IsNullOrWhiteSpace(metadataServerUrl))
            {
                return null;
            }

            if (!Uri.TryCreate(metadataServerUrl, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            {
                return null;
            }

            // Cache key must not include userinfo/query/path (avoid leaking secrets and ensure URL changes invalidate cache).
            var authority = $"{uri.Scheme}://{uri.Host}:{uri.Port}".ToLowerInvariant();
            return $"ServerChecks::{authority}";
        }

        private List<HealthCheck> RetrieveServerChecks()
        {
            var metadataServerUrl = _configService.MetadataServerUrl?.Trim();
            if (string.IsNullOrWhiteSpace(metadataServerUrl))
            {
                return new List<HealthCheck>();
            }

            if (!Uri.TryCreate(metadataServerUrl, UriKind.Absolute, out var metadataUri) || string.IsNullOrWhiteSpace(metadataUri.Host))
            {
                return new List<HealthCheck>();
            }

            var request = new HttpRequestBuilder(metadataServerUrl.TrimEnd('/') + "/api/v1")
                .Resource("/notification")
                .AddQueryParam("version", BuildInfo.Version)
                .AddQueryParam("os", OsInfo.Os.ToString().ToLowerInvariant())
                .AddQueryParam("arch", RuntimeInformation.OSArchitecture)
                .AddQueryParam("runtime", "netcore")
                .AddQueryParam("branch", _configFileProvider.Branch)
                .Build();

            request.AllowAutoRedirect = false;
            request.RequestTimeout = TimeSpan.FromSeconds(5);
            request.SuppressHttpError = true;
            try
            {
                var target = $"{metadataUri.Scheme}://{metadataUri.Host}:{metadataUri.Port}";
                _logger.Trace("Getting server side health notifications from metadata server {0}", target);
                var response = _client.Execute(request);

                if (response.StatusCode != HttpStatusCode.OK || string.IsNullOrWhiteSpace(response.Content))
                {
                    return new List<HealthCheck>();
                }

                if (!Json.TryDeserialize<List<ServerNotificationResponse>>(response.Content, out var result) || result == null)
                {
                    _logger.Debug("Server notifications response was not valid JSON");
                    return new List<HealthCheck>();
                }

                return result.Select(x => new HealthCheck(GetType(), x.Type, x.Message, x.WikiUrl)).ToList();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to retrieve server notifications");
                return new List<HealthCheck>();
            }
        }
    }

    public class ServerNotificationResponse
    {
        public HealthCheckResult Type { get; set; }
        public string Message { get; set; }
        public string WikiUrl { get; set; }
    }
}
