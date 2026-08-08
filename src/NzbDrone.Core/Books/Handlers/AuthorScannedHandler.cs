using NLog;
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
                                    Logger logger)
        {
            _bookMonitoredService = bookMonitoredService;
            _authorService = authorService;
            _commandQueueManager = commandQueueManager;
            _bookAddedService = bookAddedService;
            _eventAggregator = eventAggregator;
            _logger = logger;
        }

        private void HandleScanEvents(Author author)
        {
            _logger.Debug("[FLOW-DEBUG] Processing author '{0}' (ID: {1})", author.Name, author.Id);

            // Process immediately - no deferral
            if (author.AddOptions != null)
            {
                _bookMonitoredService.SetBookMonitoredStatus(author, author.AddOptions);

                if (author.AddOptions.SearchForMissingBooks)
                {
                    _commandQueueManager.Push(new MissingBookSearchCommand(author.Id));
                }

                author.AddOptions = null;
                _authorService.RemoveAddOptions(author);
            }

            _bookAddedService.SearchForRecentlyAdded(author.Id);
            _eventAggregator.PublishEvent(new AuthorScanCompletedEvent(author));
        }

        public void Handle(AuthorScannedEvent message)
        {
            HandleScanEvents(message.Author);
        }

        public void Handle(AuthorScanSkippedEvent message)
        {
            HandleScanEvents(message.Author);
        }
    }
}
