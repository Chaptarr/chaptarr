using Chaptarr.Http;
using Chaptarr.Http.Validation;
using FluentValidation;
using NzbDrone.Core.Configuration;

namespace Chaptarr.Api.V1.Config
{
    [V1ApiController("config/indexer")]
    public class IndexerConfigController : ConfigController<IndexerConfigResource>
    {
        public IndexerConfigController(IConfigService configService)
            : base(configService)
        {

        }

        protected override IndexerConfigResource ToResource(IConfigService model)
        {
            return IndexerConfigResourceMapper.ToResource(model);
        }
    }
}
