using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Books
{
    public interface IAuthorSyncQueueService
    {
        void QueueAuthors(IEnumerable<string> prefixedAuthorIds);
        void QueueAuthor(string prefixedAuthorId);
        List<AuthorSyncQueue> GetPending(int limit = 100, int afterId = 0);
        void MarkProcessing(int queueId);
        void MarkCompleted(int queueId);
        void MarkFailed(int queueId, string error);
        void ClearQueue();
        void ClearCompleted();
        bool HasPendingItems();
        int GetPendingCount();
        int GetTotalCount();
    }

    public class AuthorSyncQueueService : IAuthorSyncQueueService
    {
        private readonly IAuthorSyncQueueRepository _repository;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;

        public AuthorSyncQueueService(
            IAuthorSyncQueueRepository repository,
            IEventAggregator eventAggregator,
            Logger logger)
        {
            _repository = repository;
            _eventAggregator = eventAggregator;
            _logger = logger;
        }

        public void QueueAuthors(IEnumerable<string> prefixedAuthorIds)
        {
            var authorIds = prefixedAuthorIds.ToList();
            _logger.Debug("Queueing {0} authors for sync", authorIds.Count);

            foreach (var authorId in authorIds)
            {
                QueueAuthor(authorId);
            }

            _logger.Info("Queued {0} authors for sync", authorIds.Count);
        }

        public void QueueAuthor(string prefixedAuthorId)
        {
            // Check if already in queue
            var existing = _repository.GetByPrefixedId(prefixedAuthorId);
            
            if (existing != null)
            {
                // If completed or failed, reset to pending for re-processing
                if (existing.IsComplete())
                {
                    _logger.Debug("Resetting {0} to pending for re-sync", prefixedAuthorId);
                    existing.Status = SyncQueueStatus.Pending;
                    existing.AttemptCount = 0;
                    existing.LastError = null;
                    existing.ProcessedAt = null;
                    _repository.Update(existing);
                }
                else
                {
                    _logger.Trace("{0} already in queue with status {1}", prefixedAuthorId, existing.Status);
                }
            }
            else
            {
                // Add new item to queue
                var queueItem = new AuthorSyncQueue
                {
                    PrefixedAuthorId = prefixedAuthorId,
                    Status = SyncQueueStatus.Pending,
                    AttemptCount = 0,
                    CreatedAt = DateTime.UtcNow
                };

                _repository.Insert(queueItem);
                _logger.Trace("Added {0} to sync queue", prefixedAuthorId);
            }
        }

        public List<AuthorSyncQueue> GetPending(int limit = 100, int afterId = 0)
        {
            return _repository.GetPending(limit, afterId);
        }

        public void MarkProcessing(int queueId)
        {
            var item = _repository.Get(queueId);
            if (item != null)
            {
                item.Status = SyncQueueStatus.Processing;
                item.AttemptCount++;
                _repository.Update(item);
                _logger.Trace("Marked queue item {0} as processing (attempt {1})", item.PrefixedAuthorId, item.AttemptCount);
            }
        }

        public void MarkCompleted(int queueId)
        {
            var item = _repository.Get(queueId);
            if (item != null)
            {
                item.Status = SyncQueueStatus.Completed;
                item.ProcessedAt = DateTime.UtcNow;
                item.LastError = null;
                _repository.Update(item);
                _logger.Trace("Marked queue item {0} as completed", item.PrefixedAuthorId);
            }
        }

        public void MarkFailed(int queueId, string error)
        {
            var item = _repository.Get(queueId);
            if (item != null)
            {
                // If we haven't exceeded retry attempts, keep as pending
                if (item.AttemptCount < 3)
                {
                    item.Status = SyncQueueStatus.Pending;
                    _logger.Debug("Queue item {0} failed (attempt {1}), will retry: {2}", 
                        item.PrefixedAuthorId, item.AttemptCount, error);
                }
                else
                {
                    item.Status = SyncQueueStatus.Failed;
                    item.ProcessedAt = DateTime.UtcNow;
                    _logger.Warn("Queue item {0} failed after {1} attempts: {2}", 
                        item.PrefixedAuthorId, item.AttemptCount, error);
                }
                
                item.LastError = error;
                _repository.Update(item);
            }
        }

        public void ClearQueue()
        {
            _logger.Info("Clearing entire sync queue");
            _repository.ClearAll();
        }

        public void ClearCompleted()
        {
            _logger.Debug("Clearing completed items from sync queue");
            _repository.ClearCompleted();
        }

        public bool HasPendingItems()
        {
            return _repository.HasPending();
        }

        public int GetPendingCount()
        {
            return _repository.GetPending(int.MaxValue).Count;
        }

        public int GetTotalCount()
        {
            return _repository.All().Count();
        }
    }
}
