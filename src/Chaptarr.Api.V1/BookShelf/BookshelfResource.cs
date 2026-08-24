using System.Collections.Generic;
using NzbDrone.Core.Books;

namespace Chaptarr.Api.V1.Bookshelf
{
    public class BookshelfMonitoringOptionsResource
    {
        public MonitorTypes? Monitor { get; set; }
        public BookMediaType? MediaType { get; set; }
        public List<string> BooksToMonitor { get; set; } = new();

        public MonitoringOptions ToModel()
        {
            if (!Monitor.HasValue && (BooksToMonitor == null || BooksToMonitor.Count == 0))
            {
                return null;
            }

            return new MonitoringOptions
            {
                Monitor = Monitor ?? MonitorTypes.SpecificBook,
                MediaType = MediaType,
                BooksToMonitor = BooksToMonitor ?? new List<string>()
            };
        }
    }

    public class BookshelfResource
    {
        public List<BookshelfAuthorResource> Authors { get; set; }
        public BookshelfMonitoringOptionsResource MonitoringOptions { get; set; }
        public NewItemMonitorTypes? MonitorNewItems { get; set; }
    }
}
