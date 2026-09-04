using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Notifications.CalibreContentServer
{
    public class RePushBookService : IExecute<RePushBookCommand>, IHandle<MediaCoversUpdatedEvent>
    {
        private readonly IBookService _bookService;
        private readonly IMediaFileService _mediaFileService;
        private readonly INotificationFactory _notificationFactory;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly Logger _logger;

        public RePushBookService(IBookService bookService,
                                 IMediaFileService mediaFileService,
                                 INotificationFactory notificationFactory,
                                 IManageCommandQueue commandQueueManager,
                                 Logger logger)
        {
            _bookService = bookService;
            _mediaFileService = mediaFileService;
            _notificationFactory = notificationFactory;
            _commandQueueManager = commandQueueManager;
            _logger = logger;
        }

        public void Handle(MediaCoversUpdatedEvent message)
        {
            if (message.Book != null && message.Book.Id > 0)
            {
                _commandQueueManager.Push(new RePushBookCommand { BookId = message.Book.Id, FromLibraryEdit = true });
                return;
            }

            var author = message.Author;

            if (author == null || author.Id <= 0)
            {
                return;
            }

            foreach (var book in _bookService.GetBooksByAuthor(author.Id))
            {
                if (book == null || book.Id <= 0)
                {
                    continue;
                }

                var hasEbookFiles = _mediaFileService.GetFilesByBook(book.Id)
                    .Any(f => f.Path.IsNotNullOrWhiteSpace() && QualityMediaTypeHelper.IsEbookFileQuality(f.Quality.Quality));

                if (hasEbookFiles)
                {
                    _commandQueueManager.Push(new RePushBookCommand { BookId = book.Id, FromLibraryEdit = true });
                }
            }
        }

        public void Execute(RePushBookCommand message)
        {
            var bookIds = message.BookIds != null && message.BookIds.Any()
                ? message.BookIds.Where(id => id > 0).Distinct().ToList()
                : new List<int> { message.BookId };

            var connectors = _notificationFactory.GetAvailableProviders().OfType<CalibreContentServer>().ToList();

            if (message.FromLibraryEdit)
            {
                connectors = connectors.Where(c => ((CalibreContentServerSettings)c.Definition.Settings).PushLibraryEdits).ToList();
            }

            if (!connectors.Any())
            {
                return;
            }

            foreach (var bookId in bookIds)
            {
                try
                {
                    RePushBook(bookId, connectors);
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Unable to resend book {0} to the content server connections", bookId);
                }
            }
        }

        private void RePushBook(int bookId, List<CalibreContentServer> connectors)
        {
            var book = _bookService.GetBook(bookId);

            if (book == null)
            {
                return;
            }

            var files = _mediaFileService.GetFilesByBook(book.Id)
                .Where(f => f.Path.IsNotNullOrWhiteSpace() && QualityMediaTypeHelper.IsEbookFileQuality(f.Quality.Quality))
                .ToList();

            if (!files.Any())
            {
                _logger.Info("No ebook files on disk for {0}, nothing to push", book.Title);
                return;
            }

            foreach (var connector in connectors)
            {
                connector.RePush(book, files);
            }

            _logger.Info("Re-pushed {0} file(s) of {1} to {2} content server connector(s)",
                files.Count,
                book.Title,
                connectors.Count);
        }
    }
}
