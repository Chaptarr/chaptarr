using System.Linq;
using System.Reflection;
using Chaptarr.Http;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Languages;
using NzbDrone.Http.REST.Attributes;

namespace Chaptarr.Api.V1.Config
{
    [V1ApiController("config/ui")]
    public class UiConfigController : ConfigController<UiConfigResource>
    {
        private readonly IConfigFileProvider _configFileProvider;

        public UiConfigController(IConfigFileProvider configFileProvider, IConfigService configService)
            : base(configService)
        {
            _configFileProvider = configFileProvider;
            SharedValidator.RuleFor(c => c.UILanguage).Custom((value, context) =>
            {
                if (!Language.All.Any(o => o.Id == value))
                {
                    context.AddFailure("Invalid UI Language value");
                }
            });

            SharedValidator.RuleFor(c => c.UILanguage)
                           .GreaterThanOrEqualTo(1)
                           .WithMessage("The UI Language value cannot be less than 1");

            SharedValidator.RuleFor(c => c.AddNewDefaultMediaType).Custom((value, context) =>
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return;
                }

                var normalized = value.Trim().ToLowerInvariant();
                if (normalized != "audiobook" && normalized != "ebook" && normalized != "both")
                {
                    context.AddFailure("Invalid Add New default media type value");
                }
            });
        }

        [RestPutById]
        public override ActionResult<UiConfigResource> SaveConfig([FromBody] UiConfigResource resource)
        {
            var dictionary = resource.GetType()
                                     .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                                     .ToDictionary(prop => prop.Name, prop => prop.GetValue(resource, null));

            _configFileProvider.SaveConfigDictionary(dictionary);
            _configService.SaveConfigDictionary(dictionary);

            var updatedResource = ToResource(_configService);
            updatedResource.Id = 1;

            return Accepted(updatedResource);
        }

        protected override UiConfigResource ToResource(IConfigService model)
        {
            return UiConfigResourceMapper.ToResource(_configFileProvider, model);
        }
    }
}
