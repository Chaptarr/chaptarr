using System;
using System.Collections.Generic;
using System.Linq;
using NLog;

namespace NzbDrone.Core.Books
{
    public interface IBookMonitoredService
    {
        void SetBookMonitoredStatus(Author author, MonitoringOptions monitoringOptions, bool initialSetup = false);
    }

    public class BookMonitoredService : IBookMonitoredService
    {
        private readonly IAuthorService _authorService;
        private readonly IBookService _bookService;
        private readonly Logger _logger;

        public BookMonitoredService(IAuthorService authorService, IBookService bookService, Logger logger)
        {
            _authorService = authorService;
            _bookService = bookService;
            _logger = logger;
        }

        public void SetBookMonitoredStatus(Author author, MonitoringOptions monitoringOptions, bool initialSetup = false)
        {
            var initialMediaChoice = initialSetup && monitoringOptions?.MediaType.HasValue == true;
            if (monitoringOptions != null)
            {
                _logger.Debug("[{0}] Setting book monitored status.", author.Name);

                var allBooks = initialMediaChoice
                    ? _bookService.GetBooksByAuthorId(author.Id)
                    : _bookService.GetBooksByAuthor(author.Id);
                var books = allBooks;

                if (monitoringOptions.MediaType.HasValue)
                {
                    books = allBooks
                        .Where(b => b.MediaType == monitoringOptions.MediaType.Value)
                        .ToList();
                }

                var needsFileState = monitoringOptions.BooksToMonitor.Count == 0 &&
                    (monitoringOptions.Monitor is MonitorTypes.Future or MonitorTypes.Missing or MonitorTypes.Existing);
                var booksWithFiles = needsFileState
                    ? _bookService.GetAuthorBooksWithFiles(author)
                    : new List<Book>();
                if (monitoringOptions.MediaType.HasValue)
                {
                    booksWithFiles = booksWithFiles
                        .Where(b => b.MediaType == monitoringOptions.MediaType.Value)
                        .ToList();
                }

                var booksWithFilesIds = booksWithFiles.Select(e => e.Id).ToHashSet();
                var booksWithoutFiles = books
                    .Where(c => !booksWithFilesIds.Contains(c.Id))
                    .ToList();
                var booksWithoutFilesIds = booksWithoutFiles.Select(e => e.Id).ToHashSet();

                var monitoredBooks = monitoringOptions.BooksToMonitor;
                var requestedBooks = initialSetup
                    ? books.Where(book => book.AddOptions?.AddType == BookAddType.Manual &&
                        (book.IsMonitored() || book.AddOptions.MonitorBook.HasValue))
                        .Select(book => (Book: book, Monitored: book.IsMonitored())).ToList()
                    : new List<(Book Book, bool Monitored)>();

                // If specific books are passed use those instead of the monitoring options.
                if (monitoredBooks.Any())
                {
                    var selectedIds = new HashSet<string>(monitoredBooks);
                    var selectedByMedia = books
                        .Where(b => selectedIds.Contains(b.Id.ToString()))
                        .GroupBy(b => b.MediaType);

                    foreach (var group in selectedByMedia)
                    {
                        // Monitor only the selected IDs in this media type
                        ToggleBooksMonitoredState(
                            books.Where(b => b.MediaType == group.Key && selectedIds.Contains(b.Id.ToString())), true);

                        // Unmonitor all other books of the same media type
                        ToggleBooksMonitoredState(
                            books.Where(b => b.MediaType == group.Key && !selectedIds.Contains(b.Id.ToString())), false);
                    }
                }
                else
                {
                    switch (monitoringOptions.Monitor)
                    {
                        case MonitorTypes.All:
                            ToggleBooksMonitoredState(books, true);
                            break;
                        case MonitorTypes.Future:
                            _logger.Debug("Unmonitoring Books with Files");
                            ToggleBooksMonitoredState(books.Where(e => booksWithFilesIds.Contains(e.Id)), false);
                            _logger.Debug("Unmonitoring released Books without Files");
                            ToggleBooksMonitoredState(
                                books.Where(e => booksWithoutFilesIds.Contains(e.Id) && e.ReleaseDate <= DateTime.UtcNow),
                                false);
                            break;
                        case MonitorTypes.None:
                            ToggleBooksMonitoredState(books, false);
                            break;
                        case MonitorTypes.Missing:
                            _logger.Debug("Unmonitoring Books with Files");
                            ToggleBooksMonitoredState(books.Where(e => booksWithFilesIds.Contains(e.Id)), false);
                            _logger.Debug("Monitoring Books without Files");
                            ToggleBooksMonitoredState(books.Where(e => booksWithoutFilesIds.Contains(e.Id)), true);
                            break;
                        case MonitorTypes.Existing:
                            _logger.Debug("Monitoring Books with Files");
                            ToggleBooksMonitoredState(books.Where(e => booksWithFilesIds.Contains(e.Id)), true);
                            _logger.Debug("Unmonitoring Books without Files");
                            ToggleBooksMonitoredState(books.Where(e => booksWithoutFilesIds.Contains(e.Id)), false);
                            break;
                        case MonitorTypes.Latest:
                            ToggleBooksMonitoredState(books, false);
                            ToggleBooksMonitoredState(books.OrderByDescending(e => e.ReleaseDate).Take(1), true);
                            break;
                        case MonitorTypes.First:
                            ToggleBooksMonitoredState(books, false);
                            ToggleBooksMonitoredState(books.OrderBy(e => e.ReleaseDate).Take(1), true);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }

                // Initial root defaults must not undo a book explicitly requested during import.
                // Explicit monitoring edits remain authoritative even while an initial scan is owed.
                foreach (var group in requestedBooks.GroupBy(request => request.Monitored))
                {
                    ToggleBooksMonitoredState(group.Select(request => request.Book), group.Key);
                }

                if (initialMediaChoice)
                {
                    // Apply each initial side independently; the scan handler reconciles
                    // matching sibling rows after both sides have their final defaults.
                    var mediaType = monitoringOptions.MediaType == BookMediaType.Ebook ? "ebook" : "audiobook";
                    foreach (var group in books.GroupBy(book => book.IsMonitored()))
                    {
                        _bookService.SetMonitoredForMediaType(group.Select(book => book.Id), mediaType, group.Key);
                    }
                }
                else
                {
                    _bookService.UpdateManyWithLifecycle(books);
                }
            }

            if (!initialMediaChoice)
            {
                _authorService.UpdateAuthor(author);
            }
        }

        private void ToggleBooksMonitoredState(IEnumerable<Book> books, bool monitored)
        {
            foreach (var book in books)
            {
                // Set the media-type-specific monitoring flag based on the book's MediaType
                if (book.MediaType == BookMediaType.Audiobook)
                {
                    book.AudiobookMonitored = monitored;
                }
                else if (book.MediaType == BookMediaType.Ebook)
                {
                    book.EbookMonitored = monitored;
                }
            }
        }
    }
}
