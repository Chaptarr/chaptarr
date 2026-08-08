using System.Collections.Generic;

namespace Chaptarr.Api.V1.Books
{
    public class BookSiblingDeleteInfoResource
    {
        public string SiblingMediaType { get; set; }
        public List<int> BookIds { get; set; } = new List<int>();
        public BookSiblingDetailResource CurrentBook { get; set; }
        public List<BookSiblingDetailResource> Siblings { get; set; } = new List<BookSiblingDetailResource>();
        public BookSiblingStatisticsResource Statistics { get; set; } = new BookSiblingStatisticsResource();
        public int AudiobookCount { get; set; }
        public int EbookCount { get; set; }
    }

    public class BookSiblingStatisticsResource
    {
        public int BookFileCount { get; set; }
        public long SizeOnDisk { get; set; }
    }

    public class BookSiblingDetailResource
    {
        public int BookId { get; set; }
        public string MediaType { get; set; }
        public string Title { get; set; }
        public List<BookSiblingFileResource> Files { get; set; } = new List<BookSiblingFileResource>();
    }

    public class BookSiblingFileResource
    {
        public string Path { get; set; }
        public long Size { get; set; }
    }
}
