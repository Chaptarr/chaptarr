using System.Collections.Generic;
using NzbDrone.Core.Books;

namespace Chaptarr.Api.V1.Bookshelf
{
    public class BookshelfMonitoringOptionsResource
    {
        public MonitorTypes? Monitor { get; set; }
        public BookMediaType? MediaType { get; set; }
        public List<string> BooksToMonitor { get; set; } = new();
        public bool Monitored { get; set; }

        public MonitoringOptions ToModel()
        {
            var booksToMonitor = BooksToMonitor ?? new List<string>();

            return new MonitoringOptions
            {
                // Keep BookshelfNG's All default, while honoring Chaptarr payloads that only provide the specific-book list.
                Monitor = Monitor ?? (booksToMonitor.Count > 0 ? MonitorTypes.SpecificBook : MonitorTypes.All),
                MediaType = MediaType,
                BooksToMonitor = booksToMonitor,
                Monitored = Monitored
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
