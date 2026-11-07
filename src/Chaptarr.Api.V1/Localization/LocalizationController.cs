using System.Text.Json;
using Chaptarr.Http;
using Chaptarr.Http.Middleware;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Localization;

namespace Chaptarr.Api.V1.Localization
{
    [V1ApiController]
    public class LocalizationController : Controller
    {
        private readonly ILocalizationService _localizationService;
        private readonly JsonSerializerOptions _serializerSettings;

        public LocalizationController(ILocalizationService localizationService)
        {
            _localizationService = localizationService;
            _serializerSettings = STJson.GetSerializerSettings();
            _serializerSettings.DictionaryKeyPolicy = null;
            _serializerSettings.PropertyNamingPolicy = null;
        }

        [AllowAnonymous]
        [HttpGet]
        public string GetLocalizationDictionary()
        {
            var dictionary = HttpContext?.GetReadarrFacadeContext()?.IsBookshelfCompatibility == true
                ? _localizationService.GetBookshelfLocalizationDictionary()
                : _localizationService.GetLocalizationDictionary();
            return JsonSerializer.Serialize(dictionary.ToResource(_localizationService.GetCurrentLocale()), _serializerSettings);
        }
    }
}
