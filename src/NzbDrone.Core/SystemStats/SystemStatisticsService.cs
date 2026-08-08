using System;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.SystemStats
{
    public class SystemStatisticsService : ISystemStatisticsService,
                                          IHandle<BookImportedEvent>,
                                          IHandle<BookDeletedEvent>,
                                          IHandle<BookUpdatedEvent>,
                                          IHandle<BookFileDeletedEvent>,
                                          IHandle<BookAddedEvent>,
                                          IHandle<BookEditedEvent>
    {
        private readonly ISystemStatisticsRepository _repository;
        private readonly ICached<SystemStatistics> _cache;
        private readonly Logger _logger;

        public SystemStatisticsService(ISystemStatisticsRepository repository,
                                      ICacheManager cacheManager,
                                      Logger logger)
        {
            _repository = repository;
            _cache = cacheManager.GetCache<SystemStatistics>(GetType());
            _logger = logger;
        }

        public SystemStatistics GetSystemStatistics(string mediaType)
        {
            // Normalize mediaType to known values
            var normalizedMediaType = NormalizeMediaType(mediaType);
            var cacheKey = normalizedMediaType;
            
            return _cache.Get(cacheKey, () => 
            {
                _logger.Debug("Fetching system statistics for mediaType: {0}", cacheKey);
                return _repository.GetSystemStatistics(normalizedMediaType);
            }, TimeSpan.FromSeconds(30));
        }

        private void InvalidateCache()
        {
            _cache.Clear();
            _logger.Debug("System statistics cache invalidated");
        }

        public void Handle(BookImportedEvent message)
        {
            InvalidateCache();
        }

        public void Handle(BookDeletedEvent message)
        {
            InvalidateCache();
        }

        public void Handle(BookUpdatedEvent message)
        {
            // Invalidate cache on any book update (monitoring status, media type changes, etc.)
            InvalidateCache();
        }

        public void Handle(BookFileDeletedEvent message)
        {
            InvalidateCache();
        }

        public void Handle(BookAddedEvent message)
        {
            InvalidateCache();
        }

        public void Handle(BookEditedEvent message)
        {
            InvalidateCache();
        }
        
        private string NormalizeMediaType(string mediaType)
        {
            if (string.IsNullOrEmpty(mediaType))
                return "all";
                
            var normalized = mediaType.ToLowerInvariant();
            
            // Only accept known media types
            if (normalized == "audiobook" || normalized == "ebook")
                return normalized;
                
            // Default to "all" for unknown values
            return "all";
        }
    }
}