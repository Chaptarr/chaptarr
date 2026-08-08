using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;

namespace NzbDrone.Core.ImportLists.Hardcover.Library
{
    public interface IHardcoverIdentityService
    {
        bool TryGetIdentity(string baseUrl, string apiToken, out HardcoverUserIdentity identity);
    }

    public sealed class HardcoverUserIdentity
    {
        public string Username { get; set; }
        public string AvatarUrl { get; set; }
    }

    public class HardcoverIdentityService : IHardcoverIdentityService
    {
        private const string DefaultGraphQlEndpoint = "https://api.hardcover.app/v1/graphql";
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        public HardcoverIdentityService(IHttpClient httpClient, Logger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public bool TryGetIdentity(string baseUrl, string apiToken, out HardcoverUserIdentity identity)
        {
            identity = null;

            var token = NormalizeToken(apiToken);
            if (token.IsNullOrWhiteSpace())
            {
                return false;
            }

            try
            {
                var endpoint = BuildGraphQlEndpoint(baseUrl);

                var payload = JsonSerializer.Serialize(new
                {
                    query = "query Me { me { username image { url } } }"
                });

                var request = new HttpRequestBuilder(endpoint)
                    .SetHeader("Content-Type", "application/json")
                    .SetHeader("Accept", "application/json")
                    .SetHeader("User-Agent", ExternalImageRequestHeaders.GetRandomUserAgent())
                    .Build();

                request.Method = HttpMethod.Post;
                request.RequestTimeout = TimeSpan.FromSeconds(4);
                request.SuppressHttpError = true;
                request.LogHttpError = false;
                request.SetContent(payload);
                request.Headers.Add("Authorization", $"Bearer {token}");

                var response = _httpClient.Execute(request);

                if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                {
                    return false;
                }

                if ((int)response.StatusCode >= 500)
                {
                    _logger.Debug("Hardcover identity request failed with status {0}", response.StatusCode);
                    return false;
                }

                if (response.HasHttpError || response.Content.IsNullOrWhiteSpace())
                {
                    return false;
                }

                var parsed = JsonSerializer.Deserialize<GraphQlResponse<MeResponseData>>(response.Content, JsonOptions);
                if (parsed?.Errors?.Count > 0)
                {
                    return false;
                }

                var user = parsed?.Data?.Me != null && parsed.Data.Me.Count > 0 ? parsed.Data.Me[0] : null;
                if (user == null || user.Username.IsNullOrWhiteSpace())
                {
                    return false;
                }

                identity = new HardcoverUserIdentity
                {
                    Username = user.Username ?? string.Empty,
                    AvatarUrl = user.Image?.Url ?? string.Empty
                };

                return true;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Hardcover identity lookup failed");
                return false;
            }
        }

        private static string NormalizeToken(string token)
        {
            if (token.IsNullOrWhiteSpace())
            {
                return null;
            }

            token = token.Trim();
            if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                token = token.Substring("Bearer ".Length);
            }

            return token.Trim();
        }

        private static string BuildGraphQlEndpoint(string baseUrl)
        {
            if (baseUrl.IsNullOrWhiteSpace())
            {
                return DefaultGraphQlEndpoint;
            }

            var trimmed = baseUrl.Trim().TrimEnd('/');

            if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = "https://" + trimmed;
            }

            if (trimmed.EndsWith("/v1/graphql", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }

            return trimmed + "/v1/graphql";
        }

        private sealed class GraphQlResponse<T>
        {
            [JsonPropertyName("data")]
            public T Data { get; set; }

            [JsonPropertyName("errors")]
            public List<GraphQlError> Errors { get; set; } = new();
        }

        private sealed class GraphQlError
        {
            [JsonPropertyName("message")]
            public string Message { get; set; }
        }

        private sealed class MeResponseData
        {
            [JsonPropertyName("me")]
            public List<MeUser> Me { get; set; } = new();
        }

        private sealed class MeUser
        {
            [JsonPropertyName("username")]
            public string Username { get; set; }

            [JsonPropertyName("image")]
            public MeImage Image { get; set; }
        }

        private sealed class MeImage
        {
            [JsonPropertyName("url")]
            public string Url { get; set; }
        }
    }
}
