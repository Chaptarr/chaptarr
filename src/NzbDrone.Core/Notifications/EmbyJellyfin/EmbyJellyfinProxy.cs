using System;
using System.Net;
using System.Net.Http;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;

namespace NzbDrone.Core.Notifications.EmbyJellyfin
{
    public interface IEmbyJellyfinProxy
    {
        void RefreshLibrary(EmbyJellyfinSettings settings);
        ValidationFailure Test(EmbyJellyfinSettings settings);
    }

    public class EmbyJellyfinProxy : IEmbyJellyfinProxy
    {
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        public EmbyJellyfinProxy(IHttpClient httpClient, Logger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public void RefreshLibrary(EmbyJellyfinSettings settings)
        {
            Execute(settings, "/Library/Refresh", HttpMethod.Post);
        }

        public ValidationFailure Test(EmbyJellyfinSettings settings)
        {
            try
            {
                Execute(settings, "/System/Info", HttpMethod.Get);
                return null;
            }
            catch (HttpException ex)
            {
                _logger.Debug(ex, "Emby/Jellyfin connection test failed");

                if (ex.Response?.StatusCode == HttpStatusCode.Unauthorized || ex.Response?.StatusCode == HttpStatusCode.Forbidden)
                {
                    return new ValidationFailure(nameof(EmbyJellyfinSettings.ApiKey), "Authentication failed. Check the API key and server type.");
                }

                return new ValidationFailure(nameof(EmbyJellyfinSettings.Host), $"Unable to connect to Emby/Jellyfin: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Emby/Jellyfin connection test failed");
                return new ValidationFailure(nameof(EmbyJellyfinSettings.Host), $"Unable to connect to Emby/Jellyfin: {ex.Message}");
            }
        }

        private void Execute(EmbyJellyfinSettings settings, string resource, HttpMethod method)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            if (settings.Host.IsNullOrWhiteSpace())
            {
                throw new ArgumentException("Emby/Jellyfin host must be configured", nameof(settings));
            }

            if (settings.ApiKey.IsNullOrWhiteSpace())
            {
                throw new ArgumentException("Emby/Jellyfin API key must be configured", nameof(settings));
            }

            var baseUrl = HttpRequestBuilder.BuildBaseUrl(settings.UseSsl, settings.Host.ToUrlHost(), settings.Port, settings.UrlBase);
            var request = new HttpRequestBuilder(baseUrl)
                .Resource(resource)
                .Build();

            request.Method = method;
            request.RequestTimeout = RequestTimeout;
            request.Headers.Add("User-Agent", $"{BuildInfo.AppName}/{BuildInfo.Version}");

            if (settings.ServerType == (int)EmbyJellyfinServerType.Jellyfin)
            {
                var token = Uri.EscapeDataString(settings.ApiKey);
                var version = Uri.EscapeDataString(BuildInfo.Version.ToString());
                request.Headers.Add("Authorization", $"MediaBrowser Client=\"Chaptarr\", Device=\"Chaptarr\", DeviceId=\"chaptarr\", Version=\"{version}\", Token=\"{token}\"");
            }
            else if (settings.ServerType == (int)EmbyJellyfinServerType.Emby)
            {
                request.Headers.Add("X-Emby-Token", settings.ApiKey);
            }
            else
            {
                throw new ArgumentException("Emby/Jellyfin server type is invalid", nameof(settings));
            }

            var response = _httpClient.Execute(request);

            if (response.HasHttpError)
            {
                throw new HttpException(response);
            }
        }
    }
}
