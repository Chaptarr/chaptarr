using System.Globalization;
using Chaptarr.Http.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Common;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Configuration;

namespace Chaptarr.Http.Frontend
{
    [Authorize(Policy = "UI")]
    [ApiController]
    [ApiExplorerSettings(IgnoreApi = true)]
    public class InitializeJsonController : Controller
    {
        private readonly IConfigFileProvider _configFileProvider;

        public InitializeJsonController(IConfigFileProvider configFileProvider)
        {
            _configFileProvider = configFileProvider;
        }

        [HttpGet("/initialize.json")]
        public IActionResult Index()
        {
            Response.Headers.DisableCache();

            var urlBase = _configFileProvider.UrlBase;

            var model = new InitializeJsonResource
            {
                ApiRoot = $"{urlBase}/api/v1",
                ApiKey = _configFileProvider.ApiKey,
                Release = BuildInfo.Release,
                Version = BuildInfo.Version.ToString(),
                BuildTime = BuildInfo.BuildDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                InstanceName = _configFileProvider.InstanceName,
                Theme = _configFileProvider.Theme,
                Branch = _configFileProvider.Branch.ToLowerInvariant(),
                UserHash = HashUtil.AnonymousToken(),
                UrlBase = urlBase,
                IsProduction = RuntimeInfo.IsProduction
            };

            return new JsonResult(model);
        }

        private sealed class InitializeJsonResource
        {
            public string ApiRoot { get; set; }
            public string ApiKey { get; set; }
            public string Release { get; set; }
            public string Version { get; set; }
            public string BuildTime { get; set; }
            public string InstanceName { get; set; }
            public string Theme { get; set; }
            public string Branch { get; set; }
            public string UserHash { get; set; }
            public string UrlBase { get; set; }
            public bool IsProduction { get; set; }
        }
    }
}
