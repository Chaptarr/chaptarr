using System;
using NzbDrone.Common.Cache;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Core.Notifications.Plex.PlexTv
{
    public interface IPlexTvService
    {
        PlexTvPinResponse CreatePin();
        PlexTvSignInUrlResponse GetSignInUrl(string callbackUrl, int pinId, string pinCode);
        string GetAuthToken(int pinId);
        PlexTvUserResponse GetUser(string authToken);
        System.Collections.Generic.List<PlexTvResourceResponse> GetResources(string authToken);
        void Ping(string authToken);
    }

    public class PlexTvService : IPlexTvService
    {
        private readonly IPlexTvProxy _proxy;
        private readonly IConfigService _configService;
        private readonly ICached<bool> _cache;

        public PlexTvService(IPlexTvProxy proxy, IConfigService configService, ICacheManager cacheManager)
        {
            _proxy = proxy;
            _configService = configService;
            _cache = cacheManager.GetCache<bool>(GetType());
        }

        public PlexTvPinResponse CreatePin()
        {
            return _proxy.CreatePin(_configService.PlexClientIdentifier);
        }

        public PlexTvSignInUrlResponse GetSignInUrl(string callbackUrl, int pinId, string pinCode)
        {
            var clientIdentifier = _configService.PlexClientIdentifier;

            var requestBuilder = new HttpRequestBuilder("https://app.plex.tv/auth/hashBang")
                                 .AddQueryParam("clientID", clientIdentifier)
                                 .AddQueryParam("forwardUrl", callbackUrl)
                                 .AddQueryParam("code", pinCode)
                                 .AddQueryParam("context[device][product]", BuildInfo.AppName)
                                 .AddQueryParam("context[device][platform]", "Windows")
                                 .AddQueryParam("context[device][platformVersion]", "7")
                                 .AddQueryParam("context[device][version]", BuildInfo.Version.ToString());

            // #! is stripped out of the URL when building, this works around it.
            requestBuilder.Segments.Add("hashBang", "#!");

            var request = requestBuilder.Build();

            return new PlexTvSignInUrlResponse
            {
                OauthUrl = request.Url.ToString(),
                PinId = pinId
            };
        }

        public string GetAuthToken(int pinId)
        {
            var authToken = _proxy.GetAuthToken(_configService.PlexClientIdentifier, pinId);

            return authToken;
        }

        public PlexTvUserResponse GetUser(string authToken)
        {
            return _proxy.GetUser(_configService.PlexClientIdentifier, authToken);
        }

        public System.Collections.Generic.List<PlexTvResourceResponse> GetResources(string authToken)
        {
            return _proxy.GetResources(_configService.PlexClientIdentifier, authToken);
        }

        public void Ping(string authToken)
        {
            // Ping plex.tv if we haven't done so in the last 24 hours for this auth token.
            _cache.Get(authToken, () => _proxy.Ping(_configService.PlexClientIdentifier, authToken), TimeSpan.FromHours(24));
        }
    }
}
