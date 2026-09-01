using System.Collections.Generic;
using Newtonsoft.Json;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.MetadataSource.BookInfo.V5
{
    public class V5AuthorSearchSummary
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string BirthDate { get; set; }
        public string DeathDate { get; set; }
        public int? BookCount { get; set; }
        public List<V5AuthorSearchPhoto> Photos { get; set; } = new List<V5AuthorSearchPhoto>();
        public ProviderUrlMap ProviderUrls { get; set; } = new ProviderUrlMap();

        [JsonProperty("provider_ids_all")]
        public Dictionary<string, List<string>> ProviderIdsAll { get; set; } = new Dictionary<string, List<string>>();
    }

    public class V5AuthorSearchPhoto
    {
        public string Url { get; set; }
        public string Provider { get; set; }
        public bool IsPrimary { get; set; }
    }
}
