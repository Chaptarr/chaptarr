using System.Collections.Generic;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Books
{
    public class BookFooterStatistics
    {
        public int TotalBooks { get; set; }
        public int MonitoredBooks { get; set; }
        public int FileCount { get; set; }
        public long TotalFileSize { get; set; }
        public int AuthorCount { get; set; }
    }

    public class BookBucketResource
    {
        public Dictionary<string, int> Buckets { get; set; } = new Dictionary<string, int>();
        public int TotalCount { get; set; }
        public Dictionary<string, int> CumulativeIndexes { get; set; } = new Dictionary<string, int>();
        public BookFooterStatistics FooterStatistics { get; set; } = new BookFooterStatistics();
    }

    public class PagedBookResource
    {
        public List<Book> Records { get; set; } = new List<Book>();
        public int TotalCount { get; set; }
        public int Offset { get; set; }
        public int PageSize { get; set; }
    }
}
