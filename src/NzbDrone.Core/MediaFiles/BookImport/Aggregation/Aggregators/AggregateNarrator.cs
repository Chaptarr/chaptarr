using NLog;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles.BookImport.Aggregation.Aggregators
{
    public class AggregateNarrator : IAggregate<LocalBook>
    {
        private readonly Logger _logger;

        public AggregateNarrator(Logger logger)
        {
            _logger = logger;
        }

        public LocalBook Aggregate(LocalBook localTrack, bool otherFiles)
        {
            // IMPORTANT: This aggregator is disabled to prevent using unreliable narrator data from file tags
            // Narrator information should ONLY come from trusted metadata already stored on library metadata
            // See AggregateNarratorFromMetadata for the proper narrator enrichment
            _logger.Trace("Skipping narrator aggregation from file tags - narrator data must come from metadata sources");

            // Do not set narrator from local file tags
            // localTrack.Narrator remains null/empty until enriched from metadata
            return localTrack;
        }
    }
}
