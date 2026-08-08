using System;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Books.Handlers
{
    /// <summary>
    /// Ensures tri-state monitoring semantics are honored when files arrive.
    /// - For All (1): book remains monitored (handled on creation, reinforced here).
    /// - For Selected (2): mark the specific book as monitored when a physical file is added.
    /// - For None (0/null): do nothing.
    /// </summary>
    public class MonitorOnFileAddedHandler :
        IHandle<BookFileAddedEvent>,
        IHandle<BookFilesAddedEvent>
    {
        private readonly IEditionService _editionService;
        private readonly IBookService _bookService;
        private readonly IAuthorService _authorService;
        private readonly Logger _logger;

        public MonitorOnFileAddedHandler(IEditionService editionService,
                                         IBookService bookService,
                                         IAuthorService authorService,
                                         Logger logger)
        {
            _editionService = editionService;
            _bookService = bookService;
            _authorService = authorService;
            _logger = logger;
        }

        [EventHandleOrder(EventHandleOrder.First)]
        public void Handle(BookFileAddedEvent message)
        {
            if (message?.BookFile == null) return;
            TryMonitorForFile(message.BookFile);
        }

        [EventHandleOrder(EventHandleOrder.First)]
        public void Handle(BookFilesAddedEvent message)
        {
            if (message?.BookFiles == null) return;
            foreach (var file in message.BookFiles)
            {
                TryMonitorForFile(file);
            }
        }

	        private void TryMonitorForFile(BookFile file)
	        {
	            try
	            {
	                // Unmapped files are stored with EditionId=0. Monitoring logic only applies once the file is linked.
	                if (file == null || file.EditionId <= 0) return;

	                var edition = _editionService.GetEdition(file.EditionId);
	                if (edition == null)
	                {
	                    _logger.Debug("[MONITOR-ON-FILE] No edition found for BookFile {0}", file.Id);
	                    return;
                }

                var book = _bookService.GetBook(edition.BookId);
                if (book == null)
                {
                    _logger.Debug("[MONITOR-ON-FILE] No book found for Edition {0}", edition.Id);
                    return;
                }

                var author = book.Author ?? _authorService.GetAuthor(book.AuthorId);
                if (author == null)
                {
                    _logger.Debug("[MONITOR-ON-FILE] No author found for Book {0}", book.Id);
                    return;
                }

                if (book.MediaType == BookMediaType.Audiobook)
                {
                    var mode = author.AudiobookMonitorExisting ?? 0;
                    if (mode == 1 || mode == 2)
                    {
                        if (!book.AudiobookMonitored)
                        {
                            book.AudiobookMonitored = true;
                            book.EbookMonitored = false;
                            book.Monitored = true;
                            _bookService.UpdateBook(book);
                            _logger.Debug("[MONITOR-ON-FILE] Marked audiobook '{0}' monitored due to file import (mode={1})", book.Title, mode);
                        }
                    }
                }
                else if (book.MediaType == BookMediaType.Ebook)
                {
                    var mode = author.EbookMonitorExisting ?? 0;
                    if (mode == 1 || mode == 2)
                    {
                        if (!book.EbookMonitored)
                        {
                            book.EbookMonitored = true;
                            book.AudiobookMonitored = false;
                            book.Monitored = true;
                            _bookService.UpdateBook(book);
                            _logger.Debug("[MONITOR-ON-FILE] Marked ebook '{0}' monitored due to file import (mode={1})", book.Title, mode);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[MONITOR-ON-FILE] Failed to apply monitoring on file add");
            }
        }
    }
}
