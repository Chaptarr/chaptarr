using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
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
        private readonly ConcurrentDictionary<int, byte> _scheduledOrRunningAuthors = new ConcurrentDictionary<int, byte>();

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
            var authors = _authorService.GetAllAuthors().Where(a => a.Id == authorMetadataId).ToList();
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

                // Check if series were actually created
                var newSeriesCount = _seriesService.GetByAuthorId(authorMetadataId).Count;
                _logger.Debug("[SERIES-IMPORT] Series refresh completed for author {0}. Series count: before={1}, after={2}",
                    author.Name, existingSeries.Count, newSeriesCount);

                // Update the author's series collection and trigger event for variant processing
                if (newSeriesCount > existingSeries.Count)
                {
                    author.Series = _seriesService.GetByAuthorId(author.Id);
                    // For now, process each series individually
                    foreach (var series in author.Series)
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
            if (!_scheduledOrRunningAuthors.TryAdd(authorId, 0))
            {
                _logger.Debug("[SERIES-IMPORT] Skipping duplicate series import trigger for author {0} from {1}; import already pending/running", authorName, source);
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    ProcessSeriesForAuthor(authorId, remoteAuthorSnapshot);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "[SERIES-IMPORT] Error in async series processing from {0} for author {1}", source, authorName);
                }
                finally
                {
                    _scheduledOrRunningAuthors.TryRemove(authorId, out _);
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

            ScheduleSeriesImport(message.Author.Id, message.Author.Name, nameof(AuthorRefreshCompleteEvent), message.Author);
        }
    }
}
