using System.Linq;
using Chaptarr.Http;
using FluentValidation;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Api.V1.Config
{
    [V1ApiController("config/conversion")]
    public class ConversionConfigController : ConfigController<ConversionConfigResource>
    {
        public ConversionConfigController(IConfigService configService)
            : base(configService)
        {

        }

        protected override ConversionConfigResource ToResource(IConfigService model)
        {
            return ConversionConfigResourceMapper.ToResource(model);
        }
    }
}
