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
                                        IHandle<AuthorScanSkippedEvent>
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
                var hasMediaSpecificMode = false;

                if (author.AddOptions.AudiobookMonitor.HasValue)
                {
                    hasMediaSpecificMode = true;
                    _bookMonitoredService.SetBookMonitoredStatus(author, new MonitoringOptions
                    {
                        Monitor = author.AddOptions.AudiobookMonitor.Value,
                        MediaType = BookMediaType.Audiobook
                    }, initialSetup: true);
                }

                if (author.AddOptions.EbookMonitor.HasValue)
                {
                    hasMediaSpecificMode = true;
                    _bookMonitoredService.SetBookMonitoredStatus(author, new MonitoringOptions
                    {
                        Monitor = author.AddOptions.EbookMonitor.Value,
                        MediaType = BookMediaType.Ebook
                    }, initialSetup: true);
                }

                if (!hasMediaSpecificMode && author.AddOptions.Monitor.HasValue)
                {
                    _bookMonitoredService.SetBookMonitoredStatus(author, author.AddOptions, initialSetup: true);
                }

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
                        _logger.Warn("Retaining initial search for author {0} ({1}); command admission was rejected.", author.Name, author.Id);
                        _eventAggregator.PublishEvent(new AuthorScanCompletedEvent(author));
                        return;
                    }
                }

                author.AddOptions = null;
                _authorService.SetAddOptions(author);
            }

            _bookAddedService.SearchForRecentlyAdded(author.Id);
            _eventAggregator.PublishEvent(new AuthorScanCompletedEvent(author));
        }

        public void Handle(AuthorScannedEvent message)
        {
            HandleScanEvents(message.Author, message.CanCompleteInitialSetup);
        }

        public void Handle(AuthorScanSkippedEvent message)
        {
            HandleScanEvents(message.Author);
        }
    }
}
