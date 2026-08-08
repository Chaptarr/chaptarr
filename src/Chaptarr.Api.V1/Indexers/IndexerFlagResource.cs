using Chaptarr.Http.REST;

namespace Chaptarr.Api.V1.Indexers
{
    public class IndexerFlagResource : RestResource
    {
        public new int Id { get; set; }
        public string Name { get; set; }
        public string NameLower => Name.ToLowerInvariant();
    }
}
