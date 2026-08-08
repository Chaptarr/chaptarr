using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.NarratorStats
{
    public class NarratorStatisticsService : INarratorStatisticsService,
                                            IHandle<NarratorAddedEvent>,
                                            IHandle<NarratorUpdatedEvent>,
                                            IHandle<NarratorDeletedEvent>,
                                            IHandle<BookAddedEvent>,
                                            IHandle<BookDeletedEvent>,
                                            IHandle<BookImportedEvent>,
                                            IHandle<BookEditedEvent>,
                                            IHandle<BookInfoRefreshedEvent>,
                                            IHandle<BookFileDeletedEvent>
    {
        private readonly INarratorStatisticsRepository _narratorStatisticsRepository;
        private readonly ICached<List<NarratorStatistics>> _cache;

        public NarratorStatisticsService(INarratorStatisticsRepository narratorStatisticsRepository,
                                       ICacheManager cacheManager)
        {
            _narratorStatisticsRepository = narratorStatisticsRepository;
            _cache = cacheManager.GetCache<List<NarratorStatistics>>(GetType());
        }

        public List<NarratorStatistics> NarratorStatistics()
        {
            return _cache.Get("narrator",
            () =>
            {
                var bookStatistics = _narratorStatisticsRepository.NarratorStatistics();
                return MapToNarratorStatistics(bookStatistics);
            },
            TimeSpan.FromMinutes(15));
        }

        public NarratorStatistics NarratorStatistics(int narratorId)
        {
            var stats = NarratorStatistics();

            return stats.SingleOrDefault(s => s.NarratorId == narratorId);
        }

        private List<NarratorStatistics> MapToNarratorStatistics(List<BookStatistics> bookStatistics)
        {
            var narratorStatistics = bookStatistics.GroupBy(s => s.NarratorId).ToList();
            var narratorStatisticsResult = new List<NarratorStatistics>(narratorStatistics.Count);

            foreach (var statistics in narratorStatistics)
            {
                var bookCount = statistics.Sum(s => s.BookCount);
                var bookFileCount = statistics.Sum(s => s.BookFileCount);
                var availableBookCount = statistics.Sum(s => s.AvailableBookCount);
                var totalBookCount = statistics.Sum(s => s.TotalBookCount);
                var sizeOnDisk = statistics.Sum(s => s.SizeOnDisk);
                var seriesCount = statistics.Select(s => s.BookId).Distinct().Count(); // This is simplified - would need proper series count

                narratorStatisticsResult.Add(new NarratorStatistics
                {
                    NarratorId = statistics.Key,
                    BookCount = bookCount,
                    BookFileCount = bookFileCount,
                    AvailableBookCount = availableBookCount,
                    TotalBookCount = totalBookCount,
                    SeriesCount = seriesCount,
                    SizeOnDisk = sizeOnDisk,
                    BookStatistics = statistics.ToList()
                });
            }

            return narratorStatisticsResult;
        }

        private void InvalidateCache()
        {
            _cache.Clear();
        }

        public void Handle(NarratorAddedEvent message)
        {
            InvalidateCache();
        }

        public void Handle(NarratorUpdatedEvent message)
        {
            InvalidateCache();
        }

        public void Handle(NarratorDeletedEvent message)
        {
            InvalidateCache();
        }

        public void Handle(BookAddedEvent message)
        {
            InvalidateCache();
        }

        public void Handle(BookDeletedEvent message)
        {
            InvalidateCache();
        }

        public void Handle(BookImportedEvent message)
        {
            InvalidateCache();
        }

        public void Handle(BookEditedEvent message)
        {
            InvalidateCache();
        }

        public void Handle(BookInfoRefreshedEvent message)
        {
            InvalidateCache();
        }

        public void Handle(BookFileDeletedEvent message)
        {
            InvalidateCache();
        }
    }
}
