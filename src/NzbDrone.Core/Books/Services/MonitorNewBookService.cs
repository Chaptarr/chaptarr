using System;
using NLog;

namespace NzbDrone.Core.Books
{
    public interface IMonitorNewBookService
    {
        bool ShouldMonitorNewBook(Book addedBook, Author author, NewItemMonitorTypes monitorNewItems);
    }

    public class MonitorNewBookService : IMonitorNewBookService
    {
        private readonly Logger _logger;

        public MonitorNewBookService(Logger logger)
        {
            _logger = logger;
        }

        public bool ShouldMonitorNewBook(Book addedBook, Author author, NewItemMonitorTypes monitorNewItems)
        {
            if (monitorNewItems == NewItemMonitorTypes.None)
            {
                return false;
            }

            if (monitorNewItems == NewItemMonitorTypes.All)
            {
                return true;
            }

            if (monitorNewItems == NewItemMonitorTypes.New)
            {
                if (!addedBook.ReleaseDate.HasValue || author == null || author.Added == default)
                {
                    _logger.Debug(
                        "Not monitoring newly inserted book '{0}' as a future release because its release date or the author's added date is unavailable",
                        addedBook.Title);
                    return false;
                }

                // ReleaseDate is day-granular metadata. Compare dates so the time of day
                // when the author was added cannot change the result.
                return addedBook.ReleaseDate.Value.Date > author.Added.Date;
            }

            throw new NotImplementedException($"Unknown new item monitor type {monitorNewItems}");
        }
    }
}
