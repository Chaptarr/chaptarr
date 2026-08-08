using NzbDrone.Core.Indexers;

namespace Chaptarr.Api.V1.Indexers
{
    public class IndexerResource : ProviderResource<IndexerResource>
    {
        public bool Enable { get; set; }
        public bool EnableRss { get; set; }
        public bool EnableAutomaticSearch { get; set; }
        public bool EnableInteractiveSearch { get; set; }
        public bool SupportsRss { get; set; }
        public bool SupportsSearch { get; set; }
        public DownloadProtocol Protocol { get; set; }
        public int Priority { get; set; }
        public int DownloadClientId { get; set; }
        public int? ProxyId { get; set; }
    }

    public class IndexerResourceMapper : ProviderResourceMapper<IndexerResource, IndexerDefinition>
    {
        public override IndexerResource ToResource(IndexerDefinition definition)
        {
            if (definition == null)
            {
                return null;
            }

            var resource = base.ToResource(definition);

            resource.Enable = definition.Enable;
            resource.EnableRss = definition.EnableRss;
            resource.EnableAutomaticSearch = definition.EnableAutomaticSearch;
            resource.EnableInteractiveSearch = definition.EnableInteractiveSearch;
            resource.SupportsRss = definition.SupportsRss;
            resource.SupportsSearch = definition.SupportsSearch;
            resource.Protocol = definition.Protocol;
            resource.Priority = definition.Priority;
            resource.DownloadClientId = definition.DownloadClientId;
            resource.ProxyId = definition.ProxyId;

            return resource;
        }

        public override IndexerDefinition ToModel(IndexerResource resource)
        {
            if (resource == null)
            {
                return null;
            }

            var definition = base.ToModel(resource);

            definition.Enable = resource.Enable;
            definition.EnableRss = resource.EnableRss;
            definition.EnableAutomaticSearch = resource.EnableAutomaticSearch;
            definition.EnableInteractiveSearch = resource.EnableInteractiveSearch;
            definition.Priority = resource.Priority;
            definition.DownloadClientId = resource.DownloadClientId;
            definition.ProxyId = resource.ProxyId;

            return definition;
        }
    }
}
