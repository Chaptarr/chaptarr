using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.DecisionEngine;

namespace NzbDrone.Core.IndexerSearch
{
    public class IndexerSearchStatistics
    {
        private readonly ConcurrentDictionary<int, bool> _attempts = new();

        public int Queried => _attempts.Count;
        public int Failed => _attempts.Values.Count(failed => failed);

        public void RecordRequest(int indexerId) => _attempts.TryAdd(indexerId, false);
        public void RecordFailure(int indexerId) => _attempts[indexerId] = true;
    }

    public class BookSearchResult
    {
        public List<DownloadDecision> Decisions { get; init; } = new();
        public IndexerSearchStatistics Indexers { get; init; } = new();
    }
}
