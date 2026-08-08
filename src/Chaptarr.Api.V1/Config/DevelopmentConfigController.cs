using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using Chaptarr.Http;
using Chaptarr.Http.REST;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using JsonSerializer = NzbDrone.Common.Serializer.Json;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Validation;
using NzbDrone.Http.REST.Attributes;

namespace Prowlarr.Api.V1.Config
{
    [V1ApiController("config/development")]
    public class DevelopmentConfigController : RestController<DevelopmentConfigResource>
    {
        private readonly IConfigFileProvider _configFileProvider;
        private readonly IConfigService _configService;
        private readonly IHttpClient _httpClient;

        private const string ExpectedMetadataServiceName = "super-metadata-server";
        private const int ExpectedHandshakeProtocol = 1;

        public DevelopmentConfigController(IConfigFileProvider configFileProvider,
                                IConfigService configService,
                                IHttpClient httpClient)
        {
            _configFileProvider = configFileProvider;
            _configService = configService;
            _httpClient = httpClient;

            SharedValidator.RuleFor(c => c.MetadataServerUrl).IsValidUrl().When(c => !c.MetadataServerUrl.IsNullOrWhiteSpace());
        }

        protected override DevelopmentConfigResource GetResourceById(int id)
        {
            return GetDevelopmentConfig();
        }

        [HttpGet]
        public DevelopmentConfigResource GetDevelopmentConfig()
        {
            var resource = DevelopmentConfigResourceMapper.ToResource(_configFileProvider, _configService);
            resource.Id = 1;

            return resource;
        }

        [RestPutById]
        public ActionResult<DevelopmentConfigResource> SaveDevelopmentConfig([FromBody] DevelopmentConfigResource resource)
        {
            var dictionary = resource.GetType()
                                     .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                                     .ToDictionary(prop => prop.Name, prop => prop.GetValue(resource, null));

            _configFileProvider.SaveConfigDictionary(dictionary);
            _configService.SaveConfigDictionary(dictionary);

            return Accepted(resource.Id);
        }

        private class MetadataServerPingResponse
        {
            public bool Ok { get; set; }
            public string Service { get; set; }
            public int Protocol { get; set; }
            public List<string> ApiVersions { get; set; }
            public List<string> Capabilities { get; set; }
            public string TimeUtc { get; set; }
        }

        [SkipValidation(true, false)]
        [HttpPost("test")]
        [Consumes("application/json")]
        public object TestDevelopmentConfig([FromBody] DevelopmentConfigResource resource)
        {
            var candidateUrl = resource?.MetadataServerUrl?.Trim();
            var persistedUrl = _configService.MetadataServerUrl?.Trim();

            if (persistedUrl.IsNullOrWhiteSpace())
            {
                throw new ValidationException(new List<ValidationFailure>
                {
                    new ValidationFailure("MetadataServerUrl", "Metadata server URL is required")
                });
            }

            // Avoid per-request SSRF probing to arbitrary destinations by only testing the persisted config value.
            // If the caller is trying to test a new URL, require saving it first.
            if (candidateUrl.IsNotNullOrWhiteSpace() &&
                !persistedUrl.Equals(candidateUrl, StringComparison.InvariantCultureIgnoreCase))
            {
                throw new ValidationException(new List<ValidationFailure>
                {
                    new ValidationFailure("MetadataServerUrl", "Please save the metadata server URL before testing it")
                });
            }

            var baseUrl = persistedUrl;

            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new ValidationException(new List<ValidationFailure>
                {
                    new ValidationFailure("MetadataServerUrl", "Metadata server URL must start with http:// or https://")
                });
            }

            var pingUrl = baseUrl.TrimEnd('/') + "/api/v1/ping";

            var request = new HttpRequestBuilder(pingUrl)
                .Accept(HttpAccept.Json)
                .SetHeader("User-Agent", "Chaptarr")
                .SetHeader("X-Chaptarr-Handshake", ExpectedHandshakeProtocol.ToString())
                .Build();

            request.AllowAutoRedirect = false;
            request.RequestTimeout = TimeSpan.FromSeconds(5);
            request.SuppressHttpError = true;

            HttpResponse response;

            try
            {
                response = _httpClient.Execute(request);
            }
            catch (Exception ex)
            {
                throw new ValidationException(new List<ValidationFailure>
                {
                    new ValidationFailure("MetadataServerUrl", $"Unable to reach metadata server: {ex.Message}")
                });
            }

            if (response == null)
            {
                throw new ValidationException(new List<ValidationFailure>
                {
                    new ValidationFailure("MetadataServerUrl", "No response received from metadata server")
                });
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                var msg = $"Metadata server returned {(int)response.StatusCode} ({response.StatusCode})";

                throw new ValidationException(new List<ValidationFailure>
                {
                    new ValidationFailure("MetadataServerUrl", msg)
                });
            }

            MetadataServerPingResponse ping;
            try
            {
                ping = JsonSerializer.Deserialize<MetadataServerPingResponse>(response.Content);
            }
            catch (Exception ex)
            {
                throw new ValidationException(new List<ValidationFailure>
                {
                    new ValidationFailure("MetadataServerUrl", $"Metadata server ping response was not valid JSON: {ex.Message}")
                });
            }

            if (ping == null || !ping.Ok)
            {
                throw new ValidationException(new List<ValidationFailure>
                {
                    new ValidationFailure("MetadataServerUrl", "Metadata server ping did not return ok=true")
                });
            }

            if (ping.Service.IsNullOrWhiteSpace() ||
                !ping.Service.Equals(ExpectedMetadataServiceName, StringComparison.InvariantCultureIgnoreCase))
            {
                throw new ValidationException(new List<ValidationFailure>
                {
                    new ValidationFailure("MetadataServerUrl", $"Unexpected metadata server service '{ping.Service ?? "unknown"}' (expected '{ExpectedMetadataServiceName}')")
                });
            }

            if (ping.Protocol != ExpectedHandshakeProtocol)
            {
                throw new ValidationException(new List<ValidationFailure>
                {
                    new ValidationFailure("MetadataServerUrl", $"Unexpected metadata server protocol {ping.Protocol} (expected {ExpectedHandshakeProtocol})")
                });
            }

            var caps = ping.Capabilities ?? new List<string>();
            var requiredCaps = new[] { "v5.match", "v5.author", "v1.author" };
            foreach (var required in requiredCaps)
            {
                if (!caps.Any(c => c.Equals(required, StringComparison.InvariantCultureIgnoreCase)))
                {
                    throw new ValidationException(new List<ValidationFailure>
                    {
                        new ValidationFailure("MetadataServerUrl", $"Metadata server is missing required capability '{required}'")
                    });
                }
            }

            var successMessages = new List<string>
            {
                $"Metadata server OK: {ping.Service} (protocol {ping.Protocol})"
            };

            if (ping.TimeUtc.IsNotNullOrWhiteSpace())
            {
                successMessages.Add($"Server time (UTC): {ping.TimeUtc}");
            }

            return new { successMessages };
        }
    }
}
