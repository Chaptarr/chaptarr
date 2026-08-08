using System.Collections.Generic;
using Chaptarr.Api.V1.Books;

namespace Chaptarr.Api.V1.Bookshelf
{
    public class BookshelfAuthorResource
    {
        public int Id { get; set; }
        public bool? Monitored { get; set; }
        public List<BookResource> Books { get; set; }
    }
}
