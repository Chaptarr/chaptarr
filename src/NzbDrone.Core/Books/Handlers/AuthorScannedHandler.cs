using System.Collections.Generic;
using NLog;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Books
{
    public class AuthorScannedHandler : IHandle<AuthorScannedEvent>,
                                        IHandle<AuthorScanSkippedEvent>,
                                        IExecute<BulkSyncFormatMonitoringCommand>
    {
        private readonly IBookMonitoredService _bookMonitoredService;
        private readonly IBookService _bookService;
        private readonly IAuthorService _authorService;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly IBookAddedService _bookAddedService;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;

        public AuthorScannedHandler(IBookMonitoredService bookMonitoredService,
                                    IAuthorService authorService,
                                    IManageCommandQueue commandQueueManager,
                                    IBookAddedService bookAddedService,
                                    IEventAggregator eventAggregator,
                                    Logger logger,
                                    IBookService bookService)
        {
            _bookMonitoredService = bookMonitoredService;
            _bookService = bookService ?? throw new System.ArgumentNullException(nameof(bookService));
            _authorService = authorService;
            _commandQueueManager = commandQueueManager;
            _bookAddedService = bookAddedService;
            _eventAggregator = eventAggregator;
            _logger = logger;
        }

        private void HandleScanEvents(Author author, bool canCompleteInitialSetup = true)
        {
            _logger.Debug("[FLOW-DEBUG] Processing author '{0}' (ID: {1})", author.Name, author.Id);

            if (author.AddOptions != null && !canCompleteInitialSetup)
            {
                _logger.Info("Retaining initial monitoring and search for author {0} ({1}); this scan did not cover its pending setup folders.", author.Name, author.Id);
                return;
            }

            if (author.AddOptions?.AwaitingMetadata == true)
            {
                _logger.Info("Retaining initial monitoring and search for author {0} ({1}); the requested catalog has not loaded yet.", author.Name, author.Id);
            }
            else if (author.AddOptions != null)
            {
                if (author.AddOptions.InitialFormatSyncNotBefore > System.DateTime.UtcNow)
                {
                    _logger.Debug("Initial format-sync retry for author {0} is not due until {1:O}", author.Id, author.AddOptions.InitialFormatSyncNotBefore);
                    _eventAggregator.PublishEvent(new AuthorScanCompletedEvent(author));
                    return;
                }
                var hasMediaSpecificMode = author.AddOptions.AudiobookMonitor.HasValue || author.AddOptions.EbookMonitor.HasValue;
                if (author.AddOptions.AudiobookMonitor.HasValue) ApplyInitialMonitoring(author, BookMediaType.Audiobook);
                if (author.AddOptions.EbookMonitor.HasValue) ApplyInitialMonitoring(author, BookMediaType.Ebook);
                if (!hasMediaSpecificMode && author.AddOptions.Monitor.HasValue) ApplyInitialMonitoring(author);

                if (author.SyncMonitoredAcrossFormats == true)
                {
                    // Reconcile after both initial media-side defaults have been applied.
                    _bookService.SyncMonitoredAcrossFormats(new List<int> { author.Id });
                }

                if (author.AddOptions.SearchForMissingBooks)
                {
                    var options = author.AddOptions;
                    var searchSide = !options.IndependentSearchForMissingBooks &&
                        options.AudiobookSetupSearchForMissingBooks != options.EbookSetupSearchForMissingBooks
                        ? options.AudiobookSetupSearchForMissingBooks ? "audiobook" : "ebook"
                        : null;
                    if (_commandQueueManager.Push(new MissingBookSearchCommand(author.Id) { MediaType = searchSide }) == null)
                    {
                        throw new System.InvalidOperationException("Initial search command admission was rejected");
                    }
                }

                var completedOptions = author.AddOptions;
                author.AddOptions = null;
                try
                {
                    _authorService.SetAddOptions(author);
                }
                catch
                {
                    author.AddOptions = completedOptions;
                    throw;
                }
            }

            _bookAddedService.SearchForRecentlyAdded(author.Id);
            _eventAggregator.PublishEvent(new AuthorScanCompletedEvent(author));
        }

        private void ApplyInitialMonitoring(Author author, BookMediaType? mediaType = null)
        {
            var options = author.AddOptions;
            var monitor = options.Monitor;
            var audiobookMonitor = options.AudiobookMonitor;
            var ebookMonitor = options.EbookMonitor;
            _bookMonitoredService.SetBookMonitoredStatus(author, mediaType.HasValue ? new MonitoringOptions
            {
                Monitor = (mediaType == BookMediaType.Audiobook ? audiobookMonitor : ebookMonitor).Value,
                MediaType = mediaType
            } : options, initialSetup: true);
            if (mediaType == BookMediaType.Audiobook) options.AudiobookMonitor = null;
            if (mediaType == BookMediaType.Ebook) options.EbookMonitor = null;
            options.Monitor = null;
            try
            {
                _authorService.SetAddOptions(author);
            }
            catch
            {
                // A failed checkpoint does not prove consumption was durable. Keep
                // its pending choice for failure recording or cancellation propagation.
                options.Monitor = monitor;
                options.AudiobookMonitor = audiobookMonitor;
                options.EbookMonitor = ebookMonitor;
                throw;
            }
        }

        private void RecordSetupFailure(Author author, System.Exception exception)
        {
            var options = author.AddOptions;
            options.InitialFormatSyncFailureCount = System.Math.Min(options.InitialFormatSyncFailureCount, int.MaxValue - 1) + 1;
            options.InitialFormatSyncNotBefore = AuthorRetrySchedule.NextAttempt(options.InitialFormatSyncFailureCount);
            var attempt = options.InitialFormatSyncFailureCount;
            const string message = "Initial setup failed for author {0} ({1}), attempt {2}; retry after {3:O}. Pending setup will be retried.";
            if (AuthorRetrySchedule.ShouldWarn(attempt))
                _logger.Warn(exception, message, author.Name, author.Id, attempt, options.InitialFormatSyncNotBefore);
            else
                _logger.Debug(exception, message, author.Name, author.Id, attempt, options.InitialFormatSyncNotBefore);
            try
            {
                _authorService.SetAddOptions(author);
            }
            catch (System.Exception saveException) when (saveException is not System.OperationCanceledException)
            {
                _logger.Warn(saveException, "Could not save the setup retry schedule for author {0}; the next scan can retry pending setup, but the minute retry schedule was not confirmed", author.Id);
            }
        }

        public void Execute(BulkSyncFormatMonitoringCommand message)
        {
            if (message.InitialSetupAttempt == 0)
            {
                _bookService.SyncMonitoredAcrossFormats(message.AuthorIds);
                return;
            }

            foreach (var author in _authorService.GetAuthors(message.AuthorIds))
            {
                if (author.AddOptions == null) continue;
                HandleScanWithRetry(author);
            }
        }

        private void HandleScanWithRetry(Author author, bool canCompleteInitialSetup = true)
        {
            try
            {
                HandleScanEvents(author, canCompleteInitialSetup);
            }
            catch (System.Exception ex) when (ex is not System.OperationCanceledException)
            {
                if (author.AddOptions != null) RecordSetupFailure(author, ex);
                else _logger.Warn(ex, "Author {0} setup completed but its completion handling failed", author.Id);
                _eventAggregator.PublishEvent(new AuthorScanCompletedEvent(author));
            }
        }

        public void Handle(AuthorScannedEvent message)
        {
            HandleScanWithRetry(message.Author, message.CanCompleteInitialSetup);
        }

        public void Handle(AuthorScanSkippedEvent message)
        {
            HandleScanWithRetry(message.Author);
        }
    }
}
