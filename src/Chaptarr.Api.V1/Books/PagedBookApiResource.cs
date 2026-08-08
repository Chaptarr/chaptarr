using System.Collections.Generic;

namespace Chaptarr.Api.V1.Books
{
    public class PagedBookApiResource
    {
        public List<BookResource> Records { get; set; } = new List<BookResource>();
        public int TotalCount { get; set; }
        public int Offset { get; set; }
        public int PageSize { get; set; }
    }
}
