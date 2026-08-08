using NzbDrone.Core.Extras.Metadata;

namespace Chaptarr.Api.V1.Metadata
{
    public class MetadataBulkResource : ProviderBulkResource<MetadataBulkResource>
    {
    }

    public class MetadataBulkResourceMapper : ProviderBulkResourceMapper<MetadataBulkResource, MetadataDefinition>
    {
    }
}
