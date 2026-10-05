using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.AuthorStats
{
    public interface IAuthorStatisticsService
    {
        List<AuthorStatistics> AuthorStatistics();
        AuthorStatistics AuthorStatistics(int authorId);
        List<AuthorStatistics> AuthorStatistics(string mediaType);
        AuthorStatistics AuthorStatistics(int authorId, string mediaType);
        List<AuthorStatistics> AuthorStatistics(IEnumerable<int> authorIds, string mediaType);
        void InvalidateAuthorCache(int authorId);
    }

    public class AuthorStatisticsService : IAuthorStatisticsService,
        IHandle<AuthorAddedEvent>,
        IHandle<AuthorUpdatedEvent>,
        IHandle<AuthorEditedEvent>,
        IHandle<AuthorDeletedEvent>,
        IHandle<BookAddedEvent>,
        IHandle<BookDeletedEvent>,
        IHandle<BookImportedEvent>,
        IHandle<BookEditedEvent>,
        IHandle<BookUpdatedEvent>,
        IHandle<BookFileDeletedEvent>,
        IHandle<BookFileAddedEvent>,
        IHandle<BookFileUpdatedEvent>,
        IHandle<BookFilesAddedEvent>,
        IHandle<ImportStageProgressEvent>,
        IHandle<CommandExecutedEvent>
    {
        private readonly IAuthorStatisticsRepository _authorStatisticsRepository;
        private readonly ICached<List<BookStatistics>> _cache;
        private readonly object _deferredFileInvalidationLock = new object();
        private readonly HashSet<int> _deferredFileAuthorIds = new HashSet<int>();
        private bool _hasDeferredFileInvalidation;

        public AuthorStatisticsService(IAuthorStatisticsRepository authorStatisticsRepository,
                                       ICacheManager cacheManager)
        {
            _authorStatisticsRepository = authorStatisticsRepository;
            _cache = cacheManager.GetCache<List<BookStatistics>>(GetType());
        }

        public void InvalidateAuthorCache(int authorId)
        {
            InvalidateGlobalCaches();
            InvalidateAuthorCaches(authorId);
        }

        public List<AuthorStatistics> AuthorStatistics()
        {
            var bookStatistics = _cache.Get("AllAuthors", () => _authorStatisticsRepository.AuthorStatistics());
            return bookStatistics.GroupBy(statistics => statistics.AuthorId).Select(statistics => MapAuthorStatistics(statistics.ToList())).ToList();
        }

        public AuthorStatistics AuthorStatistics(int authorId)
        {
            var statistics = _cache.Get(authorId.ToString(), () => _authorStatisticsRepository.AuthorStatistics(authorId));

            if (statistics == null || statistics.Count == 0)
            {
                return new AuthorStatistics();
            }

            return MapAuthorStatistics(statistics);
        }

        public List<AuthorStatistics> AuthorStatistics(string mediaType)
        {
            var cacheKey = string.IsNullOrEmpty(mediaType) ? "AllAuthors" : $"AllAuthors_{mediaType}";
            var bookStatistics = _cache.Get(cacheKey, () => _authorStatisticsRepository.AuthorStatistics(mediaType));
            return bookStatistics.GroupBy(statistics => statistics.AuthorId).Select(statistics => MapAuthorStatistics(statistics.ToList())).ToList();
        }

        public AuthorStatistics AuthorStatistics(int authorId, string mediaType)
        {
            var cacheKey = GetAuthorCacheKey(authorId, mediaType);
            var statistics = _cache.Get(cacheKey, () => _authorStatisticsRepository.AuthorStatistics(authorId, mediaType));

            if (statistics == null || statistics.Count == 0)
            {
                return new AuthorStatistics();
            }

            return MapAuthorStatistics(statistics);
        }

        // One query for every author that isn't cached yet, stored under the same keys as the single-author lookup.
        public List<AuthorStatistics> AuthorStatistics(IEnumerable<int> authorIds, string mediaType)
        {
            var results = new List<AuthorStatistics>();
            var uncachedAuthorIds = new List<int>();

            foreach (var authorId in authorIds.Distinct())
            {
                var cached = _cache.Find(GetAuthorCacheKey(authorId, mediaType));

                if (cached == null)
                {
                    uncachedAuthorIds.Add(authorId);
                }
                else
                {
                    results.Add(cached.Count == 0 ? new AuthorStatistics() : MapAuthorStatistics(cached));
                }
            }

            if (uncachedAuthorIds.Count == 0)
            {
                return results;
            }

            var statisticsByAuthor = _authorStatisticsRepository.AuthorStatistics(uncachedAuthorIds, mediaType)
                .GroupBy(statistics => statistics.AuthorId)
                .ToDictionary(group => group.Key, group => group.ToList());

            foreach (var authorId in uncachedAuthorIds)
            {
                if (!statisticsByAuthor.TryGetValue(authorId, out var statistics))
                {
                    statistics = new List<BookStatistics>();
                }

                _cache.Set(GetAuthorCacheKey(authorId, mediaType), statistics);
                results.Add(statistics.Count == 0 ? new AuthorStatistics() : MapAuthorStatistics(statistics));
            }

            return results;
        }

        private static string GetAuthorCacheKey(int authorId, string mediaType)
        {
            return string.IsNullOrEmpty(mediaType) ? authorId.ToString() : $"{authorId}_{mediaType}";
        }

        private static AuthorStatistics MapAuthorStatistics(List<BookStatistics> bookStatistics)
        {
            return new AuthorStatistics
            {
                AuthorId = bookStatistics.First().AuthorId,
                BookFileCount = bookStatistics.Sum(statistics => statistics.BookFileCount),
                BookCount = bookStatistics.Sum(statistics => statistics.BookCount),
                AvailableBookCount = bookStatistics.Sum(statistics => statistics.AvailableBookCount),
                TotalBookCount = bookStatistics.Sum(statistics => statistics.TotalBookCount),
                SizeOnDisk = bookStatistics.Sum(statistics => statistics.SizeOnDisk),
                BookStatistics = bookStatistics
            };
        }

        private void InvalidateGlobalCaches()
        {
            _cache.Remove("AllAuthors");
            _cache.Remove("AllAuthors_audiobook");
            _cache.Remove("AllAuthors_ebook");
        }

        private void InvalidateAuthorCaches(int authorId)
        {
            if (authorId <= 0)
            {
                return;
            }

            var key = authorId.ToString();
            _cache.Remove(key);
            _cache.Remove($"{key}_audiobook");
            _cache.Remove($"{key}_ebook");
        }

        private void InvalidateGlobalAndAuthors(IEnumerable<int> authorIds)
        {
            InvalidateGlobalCaches();

            foreach (var authorId in authorIds.Where(id => id > 0).Distinct())
            {
                InvalidateAuthorCaches(authorId);
            }
        }

        private void InvalidateForFileEvent(IEnumerable<int> authorIds)
        {
            var validAuthorIds = authorIds?.Where(id => id > 0).Distinct().ToList() ?? new List<int>();

            lock (_deferredFileInvalidationLock)
            {
                if (ImportSessionProgressTracker.IsImportActive)
                {
                    _hasDeferredFileInvalidation = true;
                    _deferredFileAuthorIds.UnionWith(validAuthorIds);
                    return;
                }
            }

            InvalidateGlobalAndAuthors(validAuthorIds);
        }

        private void FlushDeferredFileInvalidation()
        {
            List<int> authorIds;

            lock (_deferredFileInvalidationLock)
            {
                if (ImportSessionProgressTracker.IsImportActive || !_hasDeferredFileInvalidation)
                {
                    return;
                }

                authorIds = _deferredFileAuthorIds.ToList();
                _deferredFileAuthorIds.Clear();
                _hasDeferredFileInvalidation = false;
            }

            InvalidateGlobalAndAuthors(authorIds);
        }

        [EventHandleOrder(EventHandleOrder.First)]
        public void Handle(AuthorAddedEvent message)
        {
            InvalidateAuthorCache(message.Author.Id);
        }

        [EventHandleOrder(EventHandleOrder.First)]
        public void Handle(AuthorUpdatedEvent message)
        {
            InvalidateAuthorCache(message.Author.Id);
        }

        [EventHandleOrder(EventHandleOrder.First)]
        public void Handle(AuthorEditedEvent message)
        {
            InvalidateAuthorCache(message.Author.Id);
        }

        [EventHandleOrder(EventHandleOrder.First)]
        public void Handle(AuthorDeletedEvent message)
        {
            InvalidateAuthorCache(message.Author.Id);
        }

        [EventHandleOrder(EventHandleOrder.First)]
        public void Handle(BookAddedEvent message)
        {
            InvalidateAuthorCache(message.Book.AuthorId);
        }

        [EventHandleOrder(EventHandleOrder.First)]
        public void Handle(BookDeletedEvent message)
        {
            InvalidateAuthorCache(message.Book.AuthorId);
        }

        [EventHandleOrder(EventHandleOrder.First)]
        public void Handle(BookImportedEvent message)
        {
            InvalidateAuthorCache(message.Author?.Id ?? message.Book?.AuthorId ?? 0);
        }

        [EventHandleOrder(EventHandleOrder.First)]
        public void Handle(BookEditedEvent message)
        {
            InvalidateAuthorCache(message.Book.AuthorId);
        }

        [EventHandleOrder(EventHandleOrder.First)]
        public void Handle(BookUpdatedEvent message)
        {
            InvalidateAuthorCache(message.Book.AuthorId);
        }

        [EventHandleOrder(EventHandleOrder.First)]
        public void Handle(BookFileDeletedEvent message)
        {
            InvalidateForFileEvent(new[] { message.BookFile?.Author?.Id ?? message.BookFile?.Edition?.Book?.AuthorId ?? 0 });
        }

        [EventHandleOrder(EventHandleOrder.First)]
        public void Handle(BookFileAddedEvent message)
        {
            InvalidateForFileEvent(new[] { message.BookFile?.Author?.Id ?? message.BookFile?.Edition?.Book?.AuthorId ?? 0 });
        }

        [EventHandleOrder(EventHandleOrder.First)]
        public void Handle(BookFileUpdatedEvent message)
        {
            InvalidateForFileEvent(new[] { message.BookFile?.Author?.Id ?? message.BookFile?.Edition?.Book?.AuthorId ?? 0 });
        }

        [EventHandleOrder(EventHandleOrder.First)]
        public void Handle(BookFilesAddedEvent message)
        {
            var authorIds = message.BookFiles?.Select(file => file?.Author?.Id ?? file?.Edition?.Book?.AuthorId ?? 0)
                            ?? Enumerable.Empty<int>();
            InvalidateForFileEvent(authorIds);
        }

        [EventHandleOrder(EventHandleOrder.Any)]
        public void Handle(ImportStageProgressEvent message)
        {
            if (message.Stage == ImportStage.ImportComplete)
            {
                FlushDeferredFileInvalidation();
            }
        }

        [EventHandleOrder(EventHandleOrder.Any)]
        public void Handle(CommandExecutedEvent message)
        {
            FlushDeferredFileInvalidation();
        }
    }
}
