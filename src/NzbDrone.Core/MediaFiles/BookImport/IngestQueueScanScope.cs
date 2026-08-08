using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.MediaFiles.BookImport
{
    public sealed class IngestQueueScanScope
    {
        private readonly HashSet<string> _exactPaths;

        public IngestQueueScanScope(string pathPrefix, IEnumerable<string> exactPaths = null)
        {
            PathPrefix = pathPrefix;
            _exactPaths = new HashSet<string>(
                (exactPaths ?? Enumerable.Empty<string>()).Where(path => !string.IsNullOrWhiteSpace(path)),
                PathEqualityComparer.Instance);
        }

        public string PathPrefix { get; }
        public bool IsExact => _exactPaths.Count > 0;
        public IReadOnlyCollection<string> ExactPaths => _exactPaths;

        public List<IngestQueueItem> GetQueuedItems(IIngestQueueRepository repository, int limit, int afterId = 0)
        {
            if (!IsExact)
            {
                return repository.GetQueuedItemsUnderPath(PathPrefix, limit, afterId) ?? new List<IngestQueueItem>();
            }

            return QueryExactPaths(
                path => repository.GetQueuedItemsUnderPath(path, limit, afterId),
                limit,
                afterId);
        }

        public List<IngestQueueItem> GetActiveItemsForSweep(IIngestQueueRepository repository, int limit, int afterId = 0)
        {
            if (!IsExact)
            {
                return repository.GetActiveItemsForSweepUnderPath(PathPrefix, limit, afterId) ?? new List<IngestQueueItem>();
            }

            return QueryExactPaths(
                path => repository.GetActiveItemsForSweepUnderPath(path, limit, afterId),
                limit,
                afterId);
        }

        public List<IngestQueueItem> GetActiveItems(IIngestQueueRepository repository, int limit = 1000)
        {
            if (!IsExact)
            {
                return repository.GetActiveItemsUnderPath(PathPrefix, limit) ?? new List<IngestQueueItem>();
            }

            return QueryExactPaths(
                path => repository.GetActiveItemsUnderPath(path, limit),
                limit,
                afterId: 0);
        }

        private List<IngestQueueItem> QueryExactPaths(
            Func<string, List<IngestQueueItem>> query,
            int limit,
            int afterId)
        {
            return _exactPaths
                .SelectMany(path => (query(path) ?? new List<IngestQueueItem>())
                    .Where(item => item != null &&
                                   item.Id > afterId &&
                                   !string.IsNullOrWhiteSpace(item.Path) &&
                                   _exactPaths.Contains(item.Path)))
                .GroupBy(item => item.Id)
                .Select(group => group.First())
                .OrderBy(item => item.Id)
                .Take(Math.Max(1, limit))
                .ToList();
        }
    }
}
