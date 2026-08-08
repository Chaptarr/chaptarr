using Chaptarr.Http.REST;

namespace Chaptarr.Api.V1.Author
{
    public class BookAggregateResource : RestResource
    {
        public int BookCount { get; set; }
        public int FileCount { get; set; }
        public long TotalFileSize { get; set; }
    }
}