using NLog;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Jobs
{
    public class CalculateBookDurationsCommand : Command
    {
    }

    public class CalculateBookDurationsCommandHandler : IExecute<CalculateBookDurationsCommand>
    {
        private readonly IBookDurationService _bookDurationService;
        private readonly Logger _logger;

        public CalculateBookDurationsCommandHandler(IBookDurationService bookDurationService,
                                                   Logger logger)
        {
            _bookDurationService = bookDurationService;
            _logger = logger;
        }

        public void Execute(CalculateBookDurationsCommand message)
        {
            _logger.Info("Starting book duration calculation");
            _bookDurationService.UpdateAllBookDurations();
            _logger.Info("Completed book duration calculation");
        }
    }
}
