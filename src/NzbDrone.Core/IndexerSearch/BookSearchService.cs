using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Queue;

namespace NzbDrone.Core.IndexerSearch
{
    internal class BookSearchService : IExecute<BookSearchCommand>,
                               IExecute<AuthorSearchCommand>,
                               IExecute<MissingBookSearchCommand>,
                               IExecute<CutoffUnmetBookSearchCommand>
    {
        private readonly ISearchForReleases _releaseSearchService;
        private readonly IBookService _bookService;
        private readonly IBookCutoffService _bookCutoffService;
        private readonly IQueueService _queueService;
        private readonly IProcessDownloadDecisions _processDownloadDecisions;
        private readonly Logger _logger;

        public BookSearchService(ISearchForReleases releaseSearchService,
            IBookService bookService,
            IBookCutoffService bookCutoffService,
            IQueueService queueService,
            IProcessDownloadDecisions processDownloadDecisions,
            Logger logger)
        {
            _releaseSearchService = releaseSearchService;
            _bookService = bookService;
            _bookCutoffService = bookCutoffService;
            _queueService = queueService;
            _processDownloadDecisions = processDownloadDecisions;
            _logger = logger;
        }

        private static BookMediaType? ParseMediaType(string mediaType)
        {
            if (string.IsNullOrWhiteSpace(mediaType))
            {
                return null;
            }

            return mediaType.Trim().ToLowerInvariant() switch
            {
                "audiobook" => BookMediaType.Audiobook,
                "ebook" => BookMediaType.Ebook,
                _ => null
            };
        }

        private async Task<int> SearchForBooks(IEnumerable<Book> books, bool userInvokedSearch, HashSet<int> searchedBookIds = null, List<Book> authorCatalog = null,
            CancellationToken cancellationToken = default, Action<int> onBookCompleted = null, bool allowUnmonitored = false)
        {
            var downloadedCount = 0;
            searchedBookIds ??= new HashSet<int>();

            foreach (var book in books.Where(b => b?.Id > 0).OrderBy(a => a.LastSearchTime ?? DateTime.MinValue))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!searchedBookIds.Add(book.Id))
                {
                    _logger.Debug("Skipping duplicate search request for book: [{0}]", book);
                    continue;
                }

                List<DownloadDecision> decisions;

                try
                {
                    decisions = await _releaseSearchService.BookSearch(book, authorCatalog, false, userInvokedSearch, false, allowUnmonitored);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.Error(ex, "Unable to search for book: [{0}]", book);
                    onBookCompleted?.Invoke(0);
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var processed = await _processDownloadDecisions.ProcessDecisions(decisions);

                downloadedCount += processed.Grabbed.Count;
                onBookCompleted?.Invoke(processed.Grabbed.Count);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return downloadedCount;
        }

        private List<Book> FilterBooksWithConfiguredProfiles(IEnumerable<Book> books)
        {
            var eligibleBooks = new List<Book>();

            foreach (var book in books ?? Enumerable.Empty<Book>())
            {
                if (book == null)
                {
                    continue;
                }

                if (book.Author == null || ReleaseSearchService.HasConfiguredQualityProfileForMediaType(book.Author, book.MediaType))
                {
                    eligibleBooks.Add(book);
                    continue;
                }

                _logger.Debug("Skipping queued {0} search for '{1}' because author '{2}' has no configured quality profile for that media type",
                    book.MediaType,
                    book.Title,
                    book.Author?.Name ?? "Unknown");
            }

            return eligibleBooks;
        }

        private static HashSet<int> GetQueuedBookIds(IEnumerable<Queue.Queue> queue)
        {
            var queuedBookIds = new HashSet<int>();

            foreach (var queueItem in (queue ?? Enumerable.Empty<Queue.Queue>())
                .Where(item => item.TrackedDownloadState != TrackedDownloadState.Imported))
            {
                if (queueItem.Book?.Id > 0)
                {
                    queuedBookIds.Add(queueItem.Book.Id);
                    continue;
                }

                var books = queueItem.RemoteBook?.GetBooksMatchingReleaseMediaType();
                if (books == null || !books.Any())
                {
                    books = queueItem.RemoteBook?.Books;
                }

                foreach (var book in books ?? Enumerable.Empty<Book>())
                {
                    if (book?.Id > 0)
                    {
                        queuedBookIds.Add(book.Id);
                    }
                }
            }

            return queuedBookIds;
        }

        private HashSet<int> GetCurrentSearchTargetIds(IEnumerable<BookSearchTarget> snapshottedTargets, IEnumerable<BookSearchTarget> currentTargets)
        {
            var currentTargetIds = currentTargets.Select(target => target.BookId).ToHashSet();
            currentTargetIds.ExceptWith(GetQueuedBookIds(_queueService.GetQueue()));

            return snapshottedTargets.Select(target => target.BookId)
                .Where(currentTargetIds.Contains)
                .ToHashSet();
        }

        public void Execute(AuthorSearchCommand message)
        {
            Execute(message, CancellationToken.None);
        }

        public void Execute(AuthorSearchCommand message, CancellationToken cancellationToken)
        {
            var timer = Stopwatch.StartNew();
            var authorCatalog = _bookService.GetBooksByAuthor(message.AuthorId, onlyMonitoredEditions: true);
            var books = FilterBooksWithConfiguredProfiles(authorCatalog.Where(book => book.IsMonitoredWithAuthor()));
            var authorName = authorCatalog.FirstOrDefault()?.Author?.Name ?? "Unknown author";
            var completed = 0;
            var grabbed = 0;
            var outcome = "failed";
            _logger.ProgressInfo("Searching {0} monitored books for {1} (author {2})", books.Count, authorName, message.AuthorId);
            try
            {
                SearchForBooks(books, message.Trigger == CommandTrigger.Manual,
                    authorCatalog: authorCatalog, cancellationToken: cancellationToken,
                    onBookCompleted: count => { completed++; grabbed += count; }).GetAwaiter().GetResult();
                outcome = "finished";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                outcome = "cancelled";
                throw;
            }
            finally
            {
                _logger.ProgressInfo("Author search for {0} (author {1}) {2} after {3} of {4} books in {5:F1} s: {6} grabbed",
                    authorName, message.AuthorId, outcome, completed, books.Count, timer.Elapsed.TotalSeconds, grabbed);
            }
        }

        public void Execute(BookSearchCommand message)
        {
            var bookIds = (message.BookIds ?? new List<int>())
                .Where(id => id > 0)
                .Distinct()
                .ToList();

            if (!bookIds.Any())
            {
                return;
            }

            var allowUnmonitored = message.Trigger == CommandTrigger.Manual && !message.MonitoredOnly;
            var selected = _bookService.GetExistingBooks(bookIds).ToDictionary(book => book.Id);
            var catalogs = new Dictionary<int, List<Book>>();
            var lastBookByAuthor = bookIds.Where(selected.ContainsKey).GroupBy(id => selected[id].AuthorId)
                .ToDictionary(group => group.Key, group => group.Last());

            foreach (var bookId in bookIds)
            {
                if (!selected.TryGetValue(bookId, out var book))
                {
                    _logger.Warn("Skipping selected book {0}: it is no longer in the library", bookId);
                    continue;
                }
                if (!catalogs.TryGetValue(book.AuthorId, out var catalog))
                {
                    var timer = Stopwatch.StartNew();
                    catalog = _bookService.GetBooksByAuthor(book.AuthorId, onlyMonitoredEditions: true);
                    catalogs.Add(book.AuthorId, catalog);
                    _logger.Debug("Loaded selected-search catalog for author {0}: {1} books in {2} ms", book.AuthorId, catalog.Count, timer.ElapsedMilliseconds);
                }

                var grabbed = SearchForBooks(new[] { book }, message.Trigger == CommandTrigger.Manual,
                    authorCatalog: catalog, allowUnmonitored: allowUnmonitored).GetAwaiter().GetResult();
                _logger.ProgressInfo("Book search completed. {0} reports downloaded.", grabbed);
                if (lastBookByAuthor[book.AuthorId] == bookId)
                {
                    catalogs.Remove(book.AuthorId);
                }
            }
        }

        public void Execute(MissingBookSearchCommand message)
        {
            var requestedMediaType = ParseMediaType(message.MediaType);
            var targets = _bookService.GetMissingBookSearchTargets(requestedMediaType, message.AuthorId);

            var totalBooks = 0;
            var totalDownloaded = 0;
            var userInvokedSearch = message.Trigger == CommandTrigger.Manual;
            var searchedBookIds = new HashSet<int>();

            _logger.ProgressInfo("Performing missing search...");

            foreach (var authorTargets in targets.GroupBy(target => target.AuthorId).OrderBy(group => group.Key))
            {
                var targetBookIds = GetCurrentSearchTargetIds(
                    authorTargets, _bookService.GetMissingBookSearchTargets(requestedMediaType, authorTargets.Key));
                if (targetBookIds.Count == 0)
                {
                    continue;
                }

                var authorBooks = _bookService.GetBooksByAuthor(authorTargets.Key, onlyMonitoredEditions: true);
                if (authorBooks == null || authorBooks.Count == 0)
                {
                    continue;
                }

                var missing = authorBooks.Where(book => book != null && targetBookIds.Contains(book.Id)).ToList();
                missing = FilterBooksWithConfiguredProfiles(missing);
                totalBooks += missing.Count;
                totalDownloaded += SearchForBooks(missing, userInvokedSearch, searchedBookIds, authorBooks).GetAwaiter().GetResult();
            }

            _logger.ProgressInfo("Completed search for {0} books. {1} reports downloaded.", totalBooks, totalDownloaded);
        }

        public void Execute(CutoffUnmetBookSearchCommand message)
        {
            var requestedMediaType = ParseMediaType(message.MediaType);
            var qualitiesBelowCutoff = _bookCutoffService.GetQualitiesBelowCutoff();
            var targets = _bookCutoffService.GetCutoffUnmetSearchTargets(qualitiesBelowCutoff, requestedMediaType, message.AuthorId);

            var totalBooks = 0;
            var totalDownloaded = 0;
            var userInvokedSearch = message.Trigger == CommandTrigger.Manual;
            var searchedBookIds = new HashSet<int>();

            _logger.ProgressInfo("Performing cutoff unmet search...");

            foreach (var authorTargets in targets.GroupBy(target => target.AuthorId).OrderBy(group => group.Key))
            {
                var targetBookIds = GetCurrentSearchTargetIds(
                    authorTargets, _bookCutoffService.GetCutoffUnmetSearchTargets(qualitiesBelowCutoff, requestedMediaType, authorTargets.Key));

                if (targetBookIds.Count == 0)
                {
                    continue;
                }

                var authorBooks = _bookService.GetBooksByAuthor(authorTargets.Key, onlyMonitoredEditions: true);
                if (authorBooks == null || authorBooks.Count == 0)
                {
                    continue;
                }

                var cutoffUnmet = authorBooks.Where(book => book != null && targetBookIds.Contains(book.Id)).ToList();
                cutoffUnmet = FilterBooksWithConfiguredProfiles(cutoffUnmet);
                totalBooks += cutoffUnmet.Count;
                totalDownloaded += SearchForBooks(cutoffUnmet, userInvokedSearch, searchedBookIds, authorBooks).GetAwaiter().GetResult();
            }

            _logger.ProgressInfo("Completed cutoff unmet search for {0} books. {1} reports downloaded.", totalBooks, totalDownloaded);
        }
    }
}
