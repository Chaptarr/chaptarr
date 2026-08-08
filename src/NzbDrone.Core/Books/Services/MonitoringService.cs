namespace NzbDrone.Core.Books.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using NLog;
    using NzbDrone.Core.Books;
    public enum MonitoringMode
    {
        None,      // Unmonitor all except exceptions
        All,       // Monitor all
        InSeries   // Future: monitor books in specific series
    }

    public interface IMonitoringService
    {
        void ApplyAuthorScope(Author author, BookMediaType mediaType, MonitoringMode mode, IReadOnlyCollection<int> exceptionBookIds = null);
    }

    public class MonitoringService : IMonitoringService
    {
        private readonly IBookRepository _bookRepository;
        private readonly IBookService _bookService;
        private readonly Logger _logger;

        public MonitoringService(IBookRepository bookRepository,
                                IBookService bookService,
                                Logger logger)
        {
            _bookRepository = bookRepository;
            _bookService = bookService;
            _logger = logger;
        }

        public void ApplyAuthorScope(Author author, BookMediaType mediaType, MonitoringMode mode, IReadOnlyCollection<int> exceptionBookIds = null)
        {
            if (author == null)
            {
                _logger.Warn("ApplyAuthorScope called with null author");
                return;
            }

            _logger.Debug("Applying monitoring scope for author {0} ({1}), mediaType: {2}, mode: {3}, exceptions: {4}",
                author.Name, author.Id, mediaType, mode, exceptionBookIds?.Count ?? 0);

            switch (mode)
            {
                case MonitoringMode.None:
                    // Unmonitor all books of this media type except the specified exceptions
                    // This uses a single SQL UPDATE query for efficiency
                    _bookRepository.UpdateMonitoringByAuthorAndMediaType(author.Id, mediaType, false, exceptionBookIds);
                    
                    // If there are exception books, monitor them
                    if (exceptionBookIds != null && exceptionBookIds.Any())
                    {
                        // Monitor the exception books with a separate update
                        var exceptionBooks = _bookService.GetBooksByAuthor(author.Id)
                            .Where(b => b.MediaType == mediaType && exceptionBookIds.Contains(b.Id))
                            .ToList();
                        
                        foreach (var book in exceptionBooks)
                        {
                            if (mediaType == BookMediaType.Audiobook)
                            {
                                book.AudiobookMonitored = true;
                            }
                            else if (mediaType == BookMediaType.Ebook)
                            {
                                book.EbookMonitored = true;
                            }
                        }
                        
                        if (exceptionBooks.Any())
                        {
                            _bookService.UpdateMany(exceptionBooks);
                            _logger.Debug("Set monitoring to true for {0} exception {1} books", exceptionBooks.Count, mediaType);
                        }
                    }
                    break;

                case MonitoringMode.All:
                    // Monitor all books of this media type using a single SQL UPDATE
                    _bookRepository.UpdateMonitoringByAuthorAndMediaType(author.Id, mediaType, true, null);
                    break;

                case MonitoringMode.InSeries:
                    // Future implementation for series-specific monitoring
                    _logger.Warn("InSeries monitoring mode not yet implemented");
                    break;

                default:
                    _logger.Warn("Unknown monitoring mode: {0}", mode);
                    break;
            }
        }
    }
}