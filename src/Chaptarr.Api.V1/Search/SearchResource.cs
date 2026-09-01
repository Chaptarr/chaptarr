using Chaptarr.Api.V1.Author;
using Chaptarr.Api.V1.Books;
using Chaptarr.Api.V1.Series;
using Chaptarr.Http.REST;

namespace Chaptarr.Api.V1.Search
{
    public class SearchResource : RestResource
    {
        // Readarr-compatible provider identity. This is provider-owned identity only; local row ids live in ExistingLocalId.
        public string ForeignId { get; set; }
        public string ProviderId { get; set; }
        public int? ExistingLocalId { get; set; }
        public int? MetadataBookCount { get; set; }
        public AuthorResource Author { get; set; }
        public BookResource Book { get; set; }
        public SeriesResource Series { get; set; }
    }
}
