using System.Linq;
using System.Reflection;
using Chaptarr.Http.REST;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Configuration;
using NzbDrone.Http.REST.Attributes;

namespace Chaptarr.Api.V1.Config
{
    public abstract class ConfigController<TResource> : RestController<TResource>
        where TResource : RestResource, new()
    {
        protected readonly IConfigService _configService;

        protected ConfigController(IConfigService configService)
        {
            _configService = configService;
        }

        protected override TResource GetResourceById(int id)
        {
            return GetConfig();
        }

        [HttpGet]
        public TResource GetConfig()
        {
            var resource = ToResource(_configService);
            resource.Id = 1;

            return resource;
        }

        [RestPutById]
        public virtual ActionResult<TResource> SaveConfig([FromBody] TResource resource)
        {
            var dictionary = resource.GetType()
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .ToDictionary(prop => prop.Name, prop => prop.GetValue(resource, null));

            _configService.SaveConfigDictionary(dictionary);

            // Return the updated configuration to show any normalization that occurred
            var updatedResource = ToResource(_configService);
            updatedResource.Id = 1;

            return Accepted(updatedResource);
        }

        protected abstract TResource ToResource(IConfigService model);
    }
}
