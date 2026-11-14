using System;
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
        private readonly IEditionFtsRepository _editionFtsRepository;
        private readonly Logger _logger;

        public UpdateFtsCommandHandler(IFtsMaintenanceService ftsMaintenanceService, IEditionFtsRepository editionFtsRepository, Logger logger)
        {
            _ftsMaintenanceService = ftsMaintenanceService;
            _editionFtsRepository = editionFtsRepository;
            _logger = logger;
        }

        public void Execute(UpdateFtsCommand message)
        {
            _logger.Debug("[FTS-UPDATE] Starting FTS update");
            try
            {
                _ftsMaintenanceService.RebuildAllFts();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[FTS-UPDATE] Failed to rebuild the author search index; continuing with the book search index");
            }
            _editionFtsRepository.EnsureIndexPopulated();
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
