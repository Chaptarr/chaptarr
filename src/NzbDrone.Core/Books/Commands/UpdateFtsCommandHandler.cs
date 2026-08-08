using NLog;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Books.Commands
{
    public class UpdateFtsCommandHandler : IExecute<UpdateFtsCommand>, IHandle<ApplicationStartedEvent>
    {
        private readonly IFtsMaintenanceService _ftsMaintenanceService;
        private readonly Logger _logger;

        public UpdateFtsCommandHandler(IFtsMaintenanceService ftsMaintenanceService, Logger logger)
        {
            _ftsMaintenanceService = ftsMaintenanceService;
            _logger = logger;
        }

        public void Execute(UpdateFtsCommand message)
        {
            _logger.Debug("[FTS-UPDATE] Starting FTS update");
            _ftsMaintenanceService.RebuildAllFts();
            _logger.Debug("[FTS-UPDATE] FTS update completed");
        }

        public void Handle(ApplicationStartedEvent message)
        {
            // Run FTS update on startup to ensure all entries are properly normalized
            _logger.Debug("[FTS-UPDATE] Application started - scheduling FTS update");
            Execute(new UpdateFtsCommand());
        }
    }
}
