using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.ThingiProvider;

namespace NzbDrone.Core.Notifications.AudioBookShelf
{
    public class AudioBookShelfLibraryEditService : IHandle<MediaCoversUpdatedEvent>
    {
        private readonly IBookService _bookService;
        private readonly IMediaFileService _mediaFileService;
        private readonly INotificationFactory _notificationFactory;
        private readonly INotificationStatusService _notificationStatusService;
        private readonly Logger _logger;

        public AudioBookShelfLibraryEditService(IBookService bookService,
                                                IMediaFileService mediaFileService,
                                                INotificationFactory notificationFactory,
                                                INotificationStatusService notificationStatusService,
                                                Logger logger)
        {
            _bookService = bookService;
            _mediaFileService = mediaFileService;
            _notificationFactory = notificationFactory;
            _notificationStatusService = notificationStatusService;
            _logger = logger;
        }

        [EventHandleOrder(EventHandleOrder.Last)]
        public void Handle(MediaCoversUpdatedEvent message)
        {
            var author = message.Author;

            if (author == null && message.Book != null)
            {
                author = message.Book.Author;
            }

            var blockedProviders = new HashSet<int>(_notificationStatusService.GetBlockedProviders().Select(v => v.ProviderId));

            var shelves = _notificationFactory.GetAvailableProviders()
                .OfType<AudioBookShelf>()
                .Where(s => ((AudioBookShelfSettings)s.Definition.Settings).PushLibraryEdits)
                .Where(s => !blockedProviders.Contains(s.Definition.Id))
                .Where(s => ShouldHandleAuthor(s.Definition, author))
                .ToList();

            if (!shelves.Any())
            {
                return;
            }

            var books = GetBooks(message)
                .Select(book => (Book: book, Files: _mediaFileService.GetFilesByBook(book.Id)
                    .Where(f => f.Path.IsNotNullOrWhiteSpace())
                    .ToList()))
                .Where(x => x.Files.Any())
                .ToList();

            if (!books.Any())
            {
                return;
            }

            foreach (var shelf in shelves)
            {
                try
                {
                    shelf.PushBooksCovers(books);
                    _notificationStatusService.RecordSuccess(shelf.Definition.Id);
                }
                catch (Exception ex)
                {
                    _notificationStatusService.RecordFailure(shelf.Definition.Id);
                    _logger.Warn(ex, "Unable to push covers to AudioBookShelf: " + shelf.Definition.Name);
                }
            }
        }

        private static bool ShouldHandleAuthor(ProviderDefinition definition, Author author)
        {
            if (definition.Tags.Empty() || author == null)
            {
                return true;
            }

            return definition.Tags.Intersect(author.Tags).Any();
        }

        private IEnumerable<Book> GetBooks(MediaCoversUpdatedEvent message)
        {
            if (message.Book != null && message.Book.Id > 0)
            {
                return new[] { message.Book };
            }

            var author = message.Author;

            if (author == null || author.Id <= 0)
            {
                return Array.Empty<Book>();
            }

            return _bookService.GetBooksByAuthor(author.Id).Where(b => b.Id > 0);
        }
    }
}
