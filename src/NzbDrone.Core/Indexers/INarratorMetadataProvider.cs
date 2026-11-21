using System.Collections.Generic;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Indexers
{
    /// <summary>
    /// Optional per-indexer hook to enrich releases with extra metadata (e.g. narrator/duration)
    /// using the indexer's official API endpoints.
    /// </summary>
    public interface INarratorMetadataProvider
    {
        bool CanProvideNarratorMetadata { get; }
        bool? SupportsNarratorMetadata { get; }
        void TestNarratorMetadata(IEnumerable<ReleaseInfo> releases);

        /// <summary>
        /// Attempts to populate extra metadata onto the given release. Returns true when any fields were populated.
        /// Implementations must be safe to call repeatedly.
        /// </summary>
        bool TryPopulateNarratorMetadata(ReleaseInfo release);
    }
}

