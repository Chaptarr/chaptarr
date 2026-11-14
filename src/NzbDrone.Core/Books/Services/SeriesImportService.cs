using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource;

namespace NzbDrone.Core.Books
{
    public class SeriesImportService :
        IHandle<AuthorRefreshCompleteEvent>
    {
        private readonly IAuthorService _authorService;
        private readonly ISeriesService _seriesService;
        private readonly IBookService _bookService;
        private readonly IRefreshSeriesService _refreshSeriesService;
        private readonly IProvideAuthorInfo _authorInfo;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;
        private readonly Dictionary<int, PendingImport> _pendingAuthors = new();

        private sealed class PendingImport
        {
            public Author Snapshot { get; set; }
            public bool HasPending { get; set; } = true;
            public int Coalesced { get; set; }
        }

        public SeriesImportService(
            IAuthorService authorService,
            ISeriesService seriesService,
            IBookService bookService,
            IRefreshSeriesService refreshSeriesService,
            IProvideAuthorInfo authorInfo,
            IEventAggregator eventAggregator,
            Logger logger)
        {
            _authorService = authorService;
            _seriesService = seriesService;
            _bookService = bookService;
            _refreshSeriesService = refreshSeriesService;
            _authorInfo = authorInfo;
            _eventAggregator = eventAggregator;
            _logger = logger;
        }

        public void ProcessSeriesForAuthor(int authorMetadataId, Author remoteAuthorSnapshot = null)
        {
            _logger.Debug("[SERIES-IMPORT] Processing series for author metadata ID: {0}", authorMetadataId);

            // Get the author
            var authors = _authorService.GetExistingAuthors(new List<int> { authorMetadataId });
            if (!authors.Any())
            {
                _logger.Warn("[SERIES-IMPORT] No author found with metadata ID: {0}", authorMetadataId);
                return;
            }

            var author = authors.First();
            var authorProviderId = author.GoodreadsAuthorId ?? author.HardcoverAuthorId ?? author.OpenLibraryAuthorId ?? "none";
            _logger.Debug("[SERIES-IMPORT] Found author: {0} (ID: {1}, ProviderId: {2})", author.Name, author.Id, authorProviderId);

            // Check if we have books in the database for this author
            var books = _bookService.GetBooksByAuthor(authorMetadataId);
            _logger.Debug("[SERIES-IMPORT] Author has {0} books in database", books.Count);

            if (!books.Any())
            {
                _logger.Warn("[SERIES-IMPORT] No books found for author, skipping series import");
                return;
            }

            // Check if we already have series for this author
            var existingSeries = _seriesService.GetByAuthorId(authorMetadataId);
            _logger.Debug("[SERIES-IMPORT] Author currently has {0} series in database", existingSeries.Count);

            try
            {
                Author remoteAuthor;
                if (remoteAuthorSnapshot?.Series != null)
                {
                    remoteAuthor = remoteAuthorSnapshot;
                    _logger.Debug("[SERIES-IMPORT] Reusing author refresh snapshot with {0} series for author {1}",
                        remoteAuthor.Series.Count,
                        author.Name);
                }
                else
                {
                    _logger.Debug("[SERIES-IMPORT] Fetching fresh author data from V5 API for series information");
                    var providerIdForApi =
                        ProviderIdHelper.Normalize(author.HardcoverAuthorId, "hc") ??
                        ProviderIdHelper.Normalize(author.GoodreadsAuthorId, "gr") ??
                        ProviderIdHelper.Normalize(author.OpenLibraryAuthorId, "ol");

                    // No local-row-id fallback: Author.Id is our database key, not a provider
                    // identity. Sending it upstream asks the metadata server for whichever
                    // Hardcover author happens to carry the same digits.
                    if (string.IsNullOrEmpty(providerIdForApi))
                    {
                        _logger.Warn("[SERIES-IMPORT] No provider ID available for author {0}", author.Name);
                        return;
                    }

                    var fetchStopwatch = Stopwatch.StartNew();
                    remoteAuthor = _authorInfo.GetAuthorInfo(providerIdForApi);
                    fetchStopwatch.Stop();
                    _logger.Debug("[SERIES-IMPORT-TIMING] Fetched author data for series import for {0} in {1}ms",
                        author.Name,
                        fetchStopwatch.ElapsedMilliseconds);
                }

                if (remoteAuthor == null)
                {
                    _logger.Warn("[SERIES-IMPORT] Failed to fetch author data from V5 API");
                    return;
                }

                if (remoteAuthor.Series == null || !remoteAuthor.Series.Any())
                {
                    _logger.Debug("[SERIES-IMPORT] No series found in V5 API response for author");
                    return;
                }

                _logger.Debug("[SERIES-IMPORT] Found {0} series from V5 API for author", remoteAuthor.Series.Count);

                // Log details about each series
                foreach (var series in remoteAuthor.Series)
                {
                    _logger.Debug("[SERIES-IMPORT] Series: '{0}' (ID: {1}) with {2} books",
                        series.Title, series.GoodreadsSeriesId ?? series.AmazonSeriesAsin ?? series.HardcoverSeriesId ?? series.OpenLibrarySeriesId ?? series.Id.ToString(), series.SeriesBooks?.Count ?? 0);
                }

                // Now process the series
                _logger.Debug("[SERIES-IMPORT] Calling RefreshSeriesInfo to process series");
                var refreshStopwatch = Stopwatch.StartNew();
                _refreshSeriesService.RefreshSeriesInfo(authorMetadataId, remoteAuthor.Series, remoteAuthor, false, false, null);
                refreshStopwatch.Stop();
                _logger.Debug("[SERIES-IMPORT-TIMING] RefreshSeriesInfo for author {0} finished in {1}ms",
                    author.Name,
                    refreshStopwatch.ElapsedMilliseconds);

                // Use the refreshed result for both change detection and follow-up events.
                var refreshedSeries = _seriesService.GetByAuthorId(authorMetadataId);
                var newSeriesCount = refreshedSeries.Count;
                _logger.Debug("[SERIES-IMPORT] Series refresh completed for author {0}. Series count: before={1}, after={2}",
                    author.Name, existingSeries.Count, newSeriesCount);

                if (newSeriesCount > existingSeries.Count)
                {
                    // For now, process each series individually.
                    foreach (var series in refreshedSeries)
                    {
                        _eventAggregator.PublishEvent(new SeriesRefreshCompleteEvent(series));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[SERIES-IMPORT] Error processing series for author {0}", author.Name);
            }
        }

        private void ScheduleSeriesImport(int authorId, string authorName, string source, Author remoteAuthorSnapshot = null)
        {
            PendingImport pending;
            lock (_pendingAuthors)
            {
                if (_pendingAuthors.TryGetValue(authorId, out pending))
                {
                    pending.Snapshot = remoteAuthorSnapshot;
                    pending.HasPending = true;
                    pending.Coalesced++;
                    _logger.Debug("[SERIES-IMPORT] Coalesced {0} newer trigger(s) for author {1} from {2}", pending.Coalesced, authorName, source);
                    return;
                }

                pending = new PendingImport { Snapshot = remoteAuthorSnapshot };
                _pendingAuthors.Add(authorId, pending);
            }

            // Refresh events run synchronously under the author lock; the work must leave that stack.
            Task.Run(() =>
            {
                while (true)
                {
                    Author snapshot;
                    lock (_pendingAuthors)
                    {
                        if (!pending.HasPending)
                        {
                            _pendingAuthors.Remove(authorId);
                            return;
                        }

                        snapshot = pending.Snapshot;
                        pending.Snapshot = null;
                        pending.HasPending = false;
                        _logger.Debug("[SERIES-IMPORT] Applying newest snapshot for author {0}; {1} trigger(s) coalesced", authorName, pending.Coalesced);
                        pending.Coalesced = 0;
                    }

                    var timer = Stopwatch.StartNew();
                    try
                    {
                        ProcessSeriesForAuthor(authorId, snapshot);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "[SERIES-IMPORT] Error in async series processing from {0} for author {1}", source, authorName);
                    }
                    finally
                    {
                        _logger.Debug("[SERIES-IMPORT] Finished processing author {0} ({1}) in {2} ms", authorName, authorId, timer.ElapsedMilliseconds);
                    }
                }
            });
        }

        public void Handle(AuthorRefreshCompleteEvent message)
        {
            if (message?.Author == null)
            {
                return;
            }

            _logger.Debug("[SERIES-IMPORT] AuthorRefreshCompleteEvent received for author {0}", message.Author.Name);

            // Local series rows cannot restore missing links; only an explicit server snapshot
            // contains incoming memberships.
            ScheduleSeriesImport(message.Author.Id, message.Author.Name, nameof(AuthorRefreshCompleteEvent), message.RemoteAuthor);
        }
    }
}
