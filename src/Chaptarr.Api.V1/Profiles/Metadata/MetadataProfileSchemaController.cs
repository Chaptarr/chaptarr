using Chaptarr.Http;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Profiles.Metadata;

namespace Chaptarr.Api.V1.Profiles.Metadata
{
    [V1ApiController("metadataprofile/schema")]
    public class MetadataProfileSchemaController : Controller
    {
        private readonly IConfigService _configService;

        public MetadataProfileSchemaController(IConfigService configService)
        {
            _configService = configService;
        }

        [HttpGet]
        public MetadataProfileResource GetAll()
        {
            var profile = new MetadataProfile
            {
                AllowedLanguages = _configService?.BookshelfMatchCompatibility == true ? "eng" : "",
                PreferredEditionTerms = "",
                PreferredSeries = ""
            };

            return profile.ToResource();
        }
    }
}
