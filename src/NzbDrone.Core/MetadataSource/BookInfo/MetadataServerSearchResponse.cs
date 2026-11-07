using System.Collections.Generic;

namespace NzbDrone.Core.MetadataSource.BookInfo
{
    // GET /api/v5/search?term=...&mediaType=audiobook|ebook returns a results array.
    // Each row has type (author or book), a canonical provider-prefixed id, and
    // book rows also carry the canonical authorId selected by the search provider.
    public class MetadataServerSearchResponse
    {
        public List<MetadataServerSearchCandidate> Results { get; set; } = new List<MetadataServerSearchCandidate>();
    }

    public class MetadataServerSearchCandidate
    {
        public string Type { get; set; }
        public string Id { get; set; }
        public string AuthorId { get; set; }
    }

    public sealed class ProviderSearchResult
    {
        public ProviderSearchResult(object entity, string preferredAuthorId)
        {
            Entity = entity;
            PreferredAuthorId = preferredAuthorId;
        }

        public object Entity { get; }
        public string PreferredAuthorId { get; }
    }
}
