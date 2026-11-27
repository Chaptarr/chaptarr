using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaCover.Commands;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.MediaCover
{
    public class DownloadMediaService : IExecute<DownloadAuthorMediaCommand>,
                                      IExecute<DownloadBookMediaCommand>
    {
        private readonly IAuthorService _authorService;
        private readonly IBookService _bookService;
        private readonly IDeferredCoverService _deferredCoverService;
        private readonly IMapCoversToLocal _mediaCoverService;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;

        public DownloadMediaService(IAuthorService authorService,
                                  IBookService bookService,
                                  IDeferredCoverService deferredCoverService,
                                  IMapCoversToLocal mediaCoverService,
                                  IEventAggregator eventAggregator,
                                  Logger logger)
        {
            _authorService = authorService;
            _bookService = bookService;
            _deferredCoverService = deferredCoverService;
            _mediaCoverService = mediaCoverService;
            _eventAggregator = eventAggregator;
            _logger = logger;
        }

        public void Execute(DownloadAuthorMediaCommand message)
        {
            Author author;
            try
            {
                author = _authorService.GetAuthor(message.AuthorId);
            }
            catch (ModelNotFoundException)
            {
                _logger.Warn("Author {0} not found", message.AuthorId);
                return;
            }

            _logger.Debug("Downloading media for author {0}: {1}", author.Id, author.Name);

            _mediaCoverService.EnsureAuthorCovers(author, message.ForceDownload);

            // Also download covers for all books by this author
            var books = _bookService.GetBooksByAuthor(author.Id);
            if (!_deferredCoverService.MarkBooksForCoverDownload(books.Select(book => book.Id)))
            {
                _mediaCoverService.EnsureBookCoversForBooks(books);
            }

            // Publish event to notify UI and other subsystems
            _eventAggregator.PublishEvent(new MediaCoversUpdatedEvent(author));
        }

        public void Execute(DownloadBookMediaCommand message)
        {
            var bookIds = message.BookIds?.Where(id => id > 0).Distinct().ToList() ?? new List<int>();
            var timer = Stopwatch.StartNew();
            var loaded = 0;
            try
            {
                if (bookIds.Count > 0)
                {
                    List<Book> books;
                    try
                    {
                        books = _bookService.GetExistingBooks(bookIds);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _deferredCoverService.RequeueBooksForCoverDownload(bookIds);
                        throw;
                    }
                    if (books.Count != bookIds.Count)
                    {
                        _logger.Warn("Skipping cover downloads for {0} deleted books", bookIds.Count - books.Count);
                    }

                    loaded = books.Count;
                    _mediaCoverService.EnsureBookCoversForBooks(books, message.ForceDownload);
                    return;
                }

                Book book;
                try
                {
                    book = _bookService.GetBook(message.BookId);
                }
                catch (ModelNotFoundException)
                {
                    _logger.Warn("Book {0} not found", message.BookId);
                    return;
                }

                loaded = 1;

                _mediaCoverService.EnsureBookCovers(book, message.ForceDownload);
            }
            finally
            {
                _logger.Debug("Cover request: {0}/{1} books loaded, {2}ms", loaded, bookIds.Count > 0 ? bookIds.Count : 1, timer.ElapsedMilliseconds);
            }
        }

    }
}
