using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Books
{
    public interface IBookAddedService
    {
        void SearchForRecentlyAdded(int authorId);
    }

    public class BookAddedService : IHandle<BookInfoRefreshedEvent>, IBookAddedService
    {
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly IBookService _bookService;
        private readonly Logger _logger;
        private readonly ICached<List<int>> _addedBooksCache;

        public BookAddedService(ICacheManager cacheManager,
                                   IManageCommandQueue commandQueueManager,
                                   IBookService bookService,
                                   Logger logger)
        {
            _commandQueueManager = commandQueueManager;
            _bookService = bookService;
            _logger = logger;
            _addedBooksCache = cacheManager.GetCache<List<int>>(GetType());
        }

        public void SearchForRecentlyAdded(int authorId)
        {
            var timer = Stopwatch.StartNew();
            var allBooks = _bookService.GetBooksByAuthor(authorId);
            var searchRequested = allBooks
                .Where(book => book != null && book.Id > 0 && book.AddOptions?.SearchForNewBook == true)
                .ToList();

            if (searchRequested.Any())
            {
                var queued = new List<Book>();
                var failed = 0;
                foreach (var book in searchRequested)
                {
                    try
                    {
                        _commandQueueManager.Push(new BookSearchCommand(new List<int> { book.Id }));
                        queued.Add(book);
                    }
                    catch (Exception e)
                    {
                        failed++;
                        _logger.Warn(e, "Could not queue requested search for book {0}; retaining its search intent", book.Id);
                    }
                }

                // Only accepted searches clear intent; unrelated targets still complete the same batch.
                if (queued.Any())
                {
                    queued.ForEach(book => book.AddOptions.SearchForNewBook = false);
                    _bookService.SetAddOptions(queued);
                }

                if (failed > 0)
                {
                    _logger.Warn("Could not queue {0} of {1} requested book searches for author {2} after {3} ms",
                        failed, searchRequested.Count, authorId, timer.ElapsedMilliseconds);
                }
            }

            var requestedBookIds = searchRequested.Select(book => book.Id).ToHashSet();
            var recentlyAddedIds = _addedBooksCache.Find(authorId.ToString())?.ToHashSet();
            var automaticallyEligible = recentlyAddedIds == null
                ? new List<Book>()
                : allBooks.Where(book => book != null && book.Id > 0 &&
                                         recentlyAddedIds.Contains(book.Id) &&
                                         !requestedBookIds.Contains(book.Id) &&
                                         book.IsMonitoredWithAuthor()).ToList();

            if (automaticallyEligible.Any())
            {
                try
                {
                    _commandQueueManager.Push(new BookSearchCommand(automaticallyEligible.Select(book => book.Id).ToList()));
                }
                catch (Exception e)
                {
                    _addedBooksCache.Set(authorId.ToString(), automaticallyEligible.Select(book => book.Id).ToList());
                    _logger.Warn(e, "Could not queue the {0}-book automatic search batch for author {1} after {2} ms; retaining cached search intent",
                        automaticallyEligible.Count, authorId, timer.ElapsedMilliseconds);
                    return;
                }
            }

            _addedBooksCache.Remove(authorId.ToString());
            _logger.Debug("Checked {0} requested searches and {1} automatic targets for author {2} in {3} ms",
                searchRequested.Count, automaticallyEligible.Count, authorId, timer.ElapsedMilliseconds);
        }

        public void Handle(BookInfoRefreshedEvent message)
        {
            if (message.Author.AddOptions == null)
            {
                if (message.Added.Empty())
                {
                    _logger.Debug("No new books, skipping search");
                    return;
                }

                if (message.Added.None(a => a.ReleaseDate.HasValue))
                {
                    _logger.Debug("No new books have an release date");
                    return;
                }

                var previouslyReleased = message.Added.Where(a => a.ReleaseDate.HasValue && a.ReleaseDate.Value.Before(DateTime.UtcNow.AddDays(1)) && a.IsMonitoredWithAuthor()).ToList();

                if (previouslyReleased.Empty())
                {
                    _logger.Debug("No newly added released books are eligible for search");
                    return;
                }

                _addedBooksCache.Set(message.Author.Id.ToString(), previouslyReleased.Select(e => e.Id).ToList());
            }
        }
    }
}
