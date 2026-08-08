using System;
using NLog;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.Housekeeping.Housekeepers
{
    public class CleanupExpiredAuthorImportClaims : IHousekeepingTask
    {
        private readonly IAuthorRepository _authorRepository;
        private readonly Logger _logger;

        public CleanupExpiredAuthorImportClaims(IAuthorRepository authorRepository, Logger logger)
        {
            _authorRepository = authorRepository;
            _logger = logger;
        }

        public void Clean()
        {
            _logger.Debug("Cleaning up expired author import claims");
            
            var expiredCount = _authorRepository.DeleteExpiredClaims();
            
            if (expiredCount > 0)
            {
                _logger.Info("Removed {0} expired author import claims", expiredCount);
            }
        }
    }
}