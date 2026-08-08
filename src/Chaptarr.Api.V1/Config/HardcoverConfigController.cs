using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Chaptarr.Api.V1;
using Chaptarr.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using NLog;
using NzbDrone.Core.Configuration;
using NzbDrone.Common.Http;

namespace Chaptarr.Api.V1.Config
{
    [V1ApiController("config/hardcover")]
    public class HardcoverConfigController : Controller
    {
        private readonly IConfigService _configService;
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        public HardcoverConfigController(IConfigService configService, IHttpClient httpClient)
        {
            _configService = configService;
            _httpClient = httpClient;
            _logger = LogManager.GetCurrentClassLogger();
        }

        [HttpGet]
        [ProducesResponseType(typeof(HardcoverConfigResource), 200)]
        public ActionResult<HardcoverConfigResource> GetConfig()
        {
            var enabled = _configService.HardcoverEnabled;
            var hasToken = !string.IsNullOrEmpty(_configService.HardcoverApiToken);
            var username = _configService.HardcoverUsername ?? "";
            var avatarUrl = _configService.HardcoverUserImageUrl ?? "";

            // Backfill username/avatar for existing installs that configured Hardcover before these fields existed.
            if (enabled && hasToken && (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(avatarUrl)))
            {
                var validation = ValidateHardcoverToken(_configService.HardcoverApiToken);
                if (validation.IsValid && validation.UserInfo != null && !string.IsNullOrEmpty(validation.UserInfo.Username))
                {
                    if (string.IsNullOrEmpty(username))
                    {
                        username = validation.UserInfo.Username;
                        _configService.HardcoverUsername = username;
                    }

                    if (string.IsNullOrEmpty(avatarUrl) && !string.IsNullOrEmpty(validation.UserInfo.AvatarUrl))
                    {
                        avatarUrl = validation.UserInfo.AvatarUrl;
                        _configService.HardcoverUserImageUrl = avatarUrl;
                    }
                }
            }

            return Ok(new HardcoverConfigResource
            {
                Enabled = enabled,
                HasToken = hasToken,
                Username = username,
                AvatarUrl = avatarUrl
            });
        }

        [HttpPost]
        [ProducesResponseType(typeof(ApiSuccessResource), 200)]
        [ProducesResponseType(typeof(ApiErrorResource), 400)]
        [ProducesResponseType(typeof(ApiErrorResource), 500)]
        [ProducesResponseType(typeof(ApiErrorResource), 503)]
        public ActionResult<ApiSuccessResource> SaveToken([FromBody] HardcoverTokenRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request?.Token))
                {
                    return BadRequest(new ApiErrorResource { Error = "API token is required" });
                }

                // Normalize token - trim whitespace and remove "Bearer " prefix if present
                var token = request.Token.Trim();
                if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    token = token.Substring(7);
                }

                // Validate token and get user info
                var validation = ValidateHardcoverToken(token);
                if (!validation.IsValid)
                {
                    if (validation.IsServiceUnavailable)
                    {
                        return StatusCode((int)HttpStatusCode.ServiceUnavailable, new ApiErrorResource { Error = validation.ErrorMessage });
                    }

                    return BadRequest(new ApiErrorResource { Error = validation.ErrorMessage });
                }

                // Save the token and username securely
                _configService.HardcoverApiToken = token;
                _configService.HardcoverUsername = validation.UserInfo.Username;
                _configService.HardcoverUserImageUrl = validation.UserInfo.AvatarUrl ?? "";
                _configService.HardcoverEnabled = true;

                _logger.Info("Hardcover API token configured successfully");

                return Ok(new ApiSuccessResource { Success = true });
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to save Hardcover API token");
                return StatusCode(500, new ApiErrorResource { Error = "Failed to save API token. Please try again." });
            }
        }

        [HttpDelete]
        [ProducesResponseType(typeof(ApiSuccessResource), 200)]
        [ProducesResponseType(typeof(ApiErrorResource), 500)]
        public ActionResult<ApiSuccessResource> ClearToken()
        {
            try
            {
                _configService.HardcoverApiToken = "";
                _configService.HardcoverUsername = "";
                _configService.HardcoverUserImageUrl = "";
                _configService.HardcoverEnabled = false;

                _logger.Info("Hardcover API token cleared");

                return Ok(new ApiSuccessResource { Success = true });
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to clear Hardcover API token");
                return StatusCode(500, new ApiErrorResource { Error = "Failed to clear API token" });
            }
        }

        private HardcoverTokenValidationResult ValidateHardcoverToken(string token)
        {
            try
            {
                // Simple validation query - get user info to test authentication
                var query = JsonConvert.SerializeObject(new
                {
                    query = "query Test { me { username image { url } } }"
                });

                var request = new HttpRequestBuilder("https://api.hardcover.app/v1/graphql")
                    .SetHeader("Content-Type", "application/json")
                    .SetHeader("Accept", "application/json")
                    .SetHeader("User-Agent", ExternalImageRequestHeaders.GetRandomUserAgent())
                    .Build();

                request.Method = HttpMethod.Post;
                request.SuppressHttpError = true;
                request.LogHttpError = false;
                request.RequestTimeout = TimeSpan.FromSeconds(8);
	                request.SetContent(query);
	                
	                // Add Authorization AFTER building, like other working code
	                request.Headers.Add("Authorization", $"Bearer {token}");
	                _logger.Debug("Hardcover token validation request sent to {0}", request.Url);

                // Short timeout for validation
                var response = _httpClient.Execute(request);
                
	                _logger.Debug("Hardcover token validation response status: {0}", response.StatusCode);

                if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                {
                    return HardcoverTokenValidationResult.InvalidToken("Hardcover rejected the API token. Please verify it and try again.");
                }

                if ((int)response.StatusCode >= 500)
                {
                    return HardcoverTokenValidationResult.ServiceUnavailable("Hardcover's API is currently unavailable. Please try again later.");
                }

                // Check if we got a successful response
                if (response.HasHttpError)
                {
                    _logger.Debug($"Hardcover token validation failed with status: {response.StatusCode}");
                    return HardcoverTokenValidationResult.ServiceUnavailable($"Hardcover token validation failed with status: {response.StatusCode}");
                }

                if (string.IsNullOrWhiteSpace(response.Content))
                {
                    return HardcoverTokenValidationResult.ServiceUnavailable("Hardcover returned an empty response while validating the token. Please try again later.");
                }

                // Try to parse the response and extract username
                try
                {
                    var responseData = JsonConvert.DeserializeObject(response.Content);
                    dynamic data = responseData;

                    if (data?.errors != null && data.errors.Count > 0)
                    {
                        string errorMessage = data.errors[0]?.message;
                        if (!string.IsNullOrWhiteSpace(errorMessage) && errorMessage.Contains("unauth", StringComparison.OrdinalIgnoreCase))
                        {
                            return HardcoverTokenValidationResult.InvalidToken("Invalid API token. Please check your token and try again.");
                        }

                        return HardcoverTokenValidationResult.ServiceUnavailable("Hardcover returned an error while validating the token. Please try again later.");
                    }
                    
                    // Extract username from response: data.me[0].username
                    if (data?.data?.me != null && data.data.me.Count > 0 && data.data.me[0].username != null)
                    {
                        string username = data.data.me[0].username;
                        string avatarUrl = data.data.me[0].image?.url;
	                        _logger.Debug($"Hardcover token validation successful for user: {username}");
                        return HardcoverTokenValidationResult.Success(new HardcoverUserInfo
                        {
                            Username = username,
                            AvatarUrl = avatarUrl
                        });
                    }
                    
	                    _logger.Debug("Hardcover token validation failed - no username in response");
                    return HardcoverTokenValidationResult.InvalidToken("Invalid API token. Please check your token and try again.");
                }
                catch (JsonException ex)
                {
	                    _logger.Debug($"Hardcover token validation failed - invalid JSON response: {ex.Message}");
                    return HardcoverTokenValidationResult.ServiceUnavailable("Hardcover returned an invalid response while validating the token. Please try again later.");
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Hardcover token validation failed with exception");
                return HardcoverTokenValidationResult.ServiceUnavailable("Unable to connect to Hardcover's API. Please try again later.");
            }
        }

        private class HardcoverTokenValidationResult
        {
            public bool IsValid { get; private set; }
            public bool IsServiceUnavailable { get; private set; }
            public string ErrorMessage { get; private set; }
            public HardcoverUserInfo UserInfo { get; private set; }

            public static HardcoverTokenValidationResult Success(HardcoverUserInfo userInfo)
            {
                return new HardcoverTokenValidationResult
                {
                    IsValid = true,
                    IsServiceUnavailable = false,
                    ErrorMessage = null,
                    UserInfo = userInfo
                };
            }

            public static HardcoverTokenValidationResult InvalidToken(string errorMessage)
            {
                return new HardcoverTokenValidationResult
                {
                    IsValid = false,
                    IsServiceUnavailable = false,
                    ErrorMessage = errorMessage,
                    UserInfo = null
                };
            }

            public static HardcoverTokenValidationResult ServiceUnavailable(string errorMessage)
            {
                return new HardcoverTokenValidationResult
                {
                    IsValid = false,
                    IsServiceUnavailable = true,
                    ErrorMessage = errorMessage,
                    UserInfo = null
                };
            }
        }

        private class HardcoverUserInfo
        {
            public string Username { get; set; }
            public string AvatarUrl { get; set; }
        }
    }

    public class HardcoverTokenRequest
    {
        public string Token { get; set; }
    }
}
