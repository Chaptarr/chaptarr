using System.Collections.Generic;
using System.Linq;
using Chaptarr.Http;
using Chaptarr.Http.REST;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Languages;

namespace Chaptarr.Api.V1.Languages
{
    [V1ApiController]
    public class LanguageController : RestController<LanguageResource>
    {
        protected override LanguageResource GetResourceById(int id)
        {
            var language = (Language)id;

            return new LanguageResource
            {
                Id = (int)language,
                Name = language.ToString()
            };
        }

        [HttpGet]
        public List<LanguageResource> GetAll()
        {
            return Language.All.Select(l => new LanguageResource
            {
                Id = (int)l,
                Name = l.ToString()
            })
                              .OrderBy(l => l.Name)
                              .ToList();
        }
    }
}
