using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.MediaFiles.Events
{
    public class BookFileEventHandlers : IHandle<BookImportedEvent>,
                                        IHandle<BookFileDeletedEvent>,
                                        IHandle<BookFileAddedEvent>,
                                        IHandle<BookFileUpdatedEvent>,
                                        IHandle<BookFilesAddedEvent>,
                                        IHandle<BookFileRetaggedEvent>
    {
        private readonly IBookDurationService _bookDurationService;
        private readonly IBookService _bookService;
        private readonly Logger _logger;

        public BookFileEventHandlers(IBookDurationService bookDurationService,
                                     IBookService bookService,
                                     Logger logger)
        {
            _bookDurationService = bookDurationService;
            _bookService = bookService;
            _logger = logger;
        }

        public void Handle(BookImportedEvent message)
        {
            if (message.Book != null)
            {
                _logger.Debug("Updating duration for book {0} after import", message.Book.Title);
                _bookDurationService.UpdateBookDuration(message.Book, message.ImportedBooks);
            }
        }

        public void Handle(BookFileDeletedEvent message)
        {
            var book = message?.BookFile?.Edition?.Book;
            if (book != null)
            {
                _logger.Debug("Updating duration for book {0} after file deletion", book.Title);
                _bookDurationService.UpdateBookDuration(book.Id);
            }
        }

        public void Handle(BookFileAddedEvent message)
        {
            var book = message?.BookFile?.Edition?.Book;
            if (book != null)
            {
                _logger.Debug("Updating duration for book {0} after file added", book.Title);
                _bookDurationService.UpdateBookDuration(book.Id);
            }
        }

        public void Handle(BookFileUpdatedEvent message)
        {
            var book = message?.BookFile?.Edition?.Book;
            if (book != null)
            {
                _logger.Debug("Updating duration for book {0} after file metadata refresh", book.Title);
                _bookDurationService.UpdateBookDuration(book.Id);
            }
        }

        public void Handle(BookFileRetaggedEvent message)
        {
            var book = message?.BookFile?.Edition?.Book;
            if (book != null)
            {
                _logger.Debug("Updating duration for book {0} after file retagged", book.Title);
                _bookDurationService.UpdateBookDuration(book.Id);
            }
        }

        public void Handle(BookFilesAddedEvent message)
        {
            if (message?.BookFiles == null || message.BookFiles.Count == 0)
            {
                return;
            }

            try
            {
                // Try to determine affected book IDs efficiently
                var bookIds = new HashSet<int>();

                foreach (var f in message.BookFiles)
                {
                    var bid = f?.Edition?.Book?.Id;
                    if (bid.HasValue && bid.Value > 0)
                    {
                        bookIds.Add(bid.Value);
                    }
                }

                if (bookIds.Count == 0)
                {
                    // Fallback: resolve via book service using file IDs
                    var fileIds = message.BookFiles.Where(x => x != null && x.Id > 0).Select(x => x.Id).ToList();
                    if (fileIds.Count > 0)
                    {
                        var books = _bookService.GetBooksByFileIds(fileIds);
                        foreach (var b in books)
                        {
                            if (b != null && b.Id > 0)
                            {
                                bookIds.Add(b.Id);
                            }
                        }
                    }
                }

                foreach (var id in bookIds)
                {
                    _logger.Debug("Updating duration for book {0} after batch file add", id);
                    _bookDurationService.UpdateBookDuration(id);
                }
            }
            catch (System.Exception ex)
            {
                _logger.Error(ex, "Failed processing BookFilesAddedEvent for {0} files", message.BookFiles.Count);
            }
        }
    }
}
