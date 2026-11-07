using System;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
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
        private readonly IDiskProvider _diskProvider;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;

        public DownloadMediaService(IAuthorService authorService,
                                  IBookService bookService,
                                  IDeferredCoverService deferredCoverService,
                                  IMapCoversToLocal mediaCoverService,
                                  IDiskProvider diskProvider,
                                  IEventAggregator eventAggregator,
                                  Logger logger)
        {
            _authorService = authorService;
            _bookService = bookService;
            _deferredCoverService = deferredCoverService;
            _mediaCoverService = mediaCoverService;
            _diskProvider = diskProvider;
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

            if (message.ForceDownload)
            {
                // Delete existing covers to force re-download
                var authorCoverPath = Path.Combine(_diskProvider.GetParentFolder(_mediaCoverService.GetCoverPath(author.Id, MediaCoverEntity.Author, MediaCoverTypes.Poster, ".jpg")));
                if (_diskProvider.FolderExists(authorCoverPath))
                {
                    _logger.Debug("Deleting existing covers for forced download");
                    _diskProvider.DeleteFolder(authorCoverPath, true);
                }
            }

            // Directly download author covers instead of publishing event
            // This ensures covers are downloaded immediately on import
            _mediaCoverService.EnsureAuthorCovers(author);
            
            // Also download covers for all books by this author
            var books = _bookService.GetBooksByAuthor(author.Id);
            if (!_deferredCoverService.MarkBooksForCoverDownload(books.Select(b => b.Id)))
            {
                foreach (var book in books)
                {
                    _mediaCoverService.EnsureBookCovers(book);
                }
            }

            // Publish event to notify UI and other subsystems
            _eventAggregator.PublishEvent(new MediaCoversUpdatedEvent(author));
        }

        public void Execute(DownloadBookMediaCommand message)
        {
            var bookIds = message.BookIds?.Where(id => id > 0).Distinct().ToList();
            if (bookIds == null || bookIds.Count == 0)
            {
                bookIds = new[] { message.BookId }.Where(id => id > 0).ToList();
            }

            foreach (var bookId in bookIds)
            {
                DownloadBookMedia(bookId, message.ForceDownload);
            }
        }

        private void DownloadBookMedia(int bookId, bool forceDownload)
        {
            Book book;
            try
            {
                book = _bookService.GetBook(bookId);
            }
            catch (ModelNotFoundException)
            {
                _logger.Warn("Book {0} not found", bookId);
                return;
            }

            _logger.Debug("Downloading media for book {0}: {1}", book.Id, book.Title);

            if (forceDownload)
            {
                // Delete existing covers to force re-download
                var bookCoverPath = Path.Combine(_diskProvider.GetParentFolder(_mediaCoverService.GetCoverPath(book.Id, MediaCoverEntity.Book, MediaCoverTypes.Cover, ".jpg")));
                if (_diskProvider.FolderExists(bookCoverPath))
                {
                    _logger.Debug("Deleting existing covers for forced download");
                    _diskProvider.DeleteFolder(bookCoverPath, true);
                }
            }

            _mediaCoverService.EnsureBookCovers(book);
        }
    }
}
