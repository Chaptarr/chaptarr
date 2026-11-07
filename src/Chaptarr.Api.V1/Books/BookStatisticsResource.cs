using NzbDrone.Core.AuthorStats;

namespace Chaptarr.Api.V1.Books
{
    public class BookStatisticsResource
    {
        public int BookFileCount { get; set; }
        public int EbookFileCount { get; set; }
        public int AudiobookFileCount { get; set; }
        public int BookCount { get; set; }
        public int AvailableBookCount { get; set; }
        public int TotalBookCount { get; set; }
        public long SizeOnDisk { get; set; }
        public long EbookSizeOnDisk { get; set; }
        public long AudiobookSizeOnDisk { get; set; }
        internal bool BookshelfCompatibility { get; set; }

        public decimal PercentOfBooks
        {
            get
            {
                if (BookCount == 0)
                {
                    return 0;
                }

                var count = BookshelfCompatibility ? BookFileCount : AvailableBookCount;
                return count / (decimal)BookCount * 100;
            }
        }
    }

    public static class BookStatisticsResourceMapper
    {
        public static BookStatisticsResource ToResource(this BookStatistics model, bool bookshelfCompatibility = false)
        {
            if (model == null)
            {
                return null;
            }

            return new BookStatisticsResource
            {
                BookshelfCompatibility = bookshelfCompatibility,
                BookFileCount = model.BookFileCount,
                EbookFileCount = model.EbookFileCount,
                AudiobookFileCount = model.AudiobookFileCount,
                BookCount = model.BookCount,
                AvailableBookCount = model.AvailableBookCount,
                SizeOnDisk = model.SizeOnDisk,
                EbookSizeOnDisk = model.EbookSizeOnDisk,
                AudiobookSizeOnDisk = model.AudiobookSizeOnDisk,
                TotalBookCount = model.TotalBookCount
            };
        }
    }
}
