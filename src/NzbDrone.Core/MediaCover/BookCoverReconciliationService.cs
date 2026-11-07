using System;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.MediaCover
{
    public class BookCoverReconciliationService : IHandle<AuthorScannedEvent>
    {
        private readonly IBookService _bookService;
        private readonly IEditionService _editionService;
        private readonly IMapCoversToLocal _mediaCoverService;
        private readonly IBookCoverSidecarReader _sidecarReader;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;

        public BookCoverReconciliationService(IBookService bookService,
                                              IEditionService editionService,
                                              IMapCoversToLocal mediaCoverService,
                                              IBookCoverSidecarReader sidecarReader,
                                              IEventAggregator eventAggregator,
                                              Logger logger)
        {
            _bookService = bookService;
            _editionService = editionService;
            _mediaCoverService = mediaCoverService;
            _sidecarReader = sidecarReader;
            _eventAggregator = eventAggregator;
            _logger = logger;
        }

        // File matching pins editions outside refreshes and nothing else revisits the cover; scans are where that settles.
        public void Handle(AuthorScannedEvent message)
        {
            var author = message.Author;

            if (author == null || author.Id <= 0)
            {
                return;
            }

            var books = _bookService.GetBooksByAuthor(author.Id);

            if (!books.Any())
            {
                return;
            }

            var editionsByBook = _editionService.GetEditionsByBook(books.Select(b => b.Id))
                .GroupBy(e => e.BookId)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var book in books)
            {
                if (!editionsByBook.TryGetValue(book.Id, out var editions))
                {
                    continue;
                }

                var monitored = editions.FirstOrDefault(e => e.Monitored);

                if (monitored == null)
                {
                    continue;
                }

                var storedEditionId = _sidecarReader.GetStoredCoverEditionId(book.Id);
                var coverDrift = storedEditionId != null && storedEditionId != monitored.Id;
                var imagesDrift = HasImagesDrift(book, monitored);

                if (!coverDrift && !imagesDrift)
                {
                    continue;
                }

                _logger.Debug("Cover state for book {0} does not match monitored edition {1} (stored edition {2}, images drift {3}); reconciling", book.Id, monitored.Id, storedEditionId, imagesDrift);

                try
                {
                    book.Editions = editions;
                    _mediaCoverService.EnsureBookCovers(book);

                    if (imagesDrift)
                    {
                        // The UI renders the book row's denormalized images; realign them with the stored artwork.
                        book.Images = monitored.Images;
                        _bookService.UpdateBook(book);
                    }

                    _eventAggregator.PublishEvent(new MediaCoversUpdatedEvent(book));
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Unable to reconcile the cover for book {0}", book.Id);
                }
            }
        }

        private static bool HasImagesDrift(Book book, Edition monitored)
        {
            var monitoredCoverUrl = monitored.Images?.FirstOrDefault(i => i?.CoverType == MediaCoverTypes.Cover)?.Url;

            if (monitoredCoverUrl.IsNullOrWhiteSpace())
            {
                return false;
            }

            var bookCoverUrl = book.Images?.FirstOrDefault(i => i?.CoverType == MediaCoverTypes.Cover)?.Url;

            return !monitoredCoverUrl.Equals(bookCoverUrl, StringComparison.OrdinalIgnoreCase);
        }
    }
}
