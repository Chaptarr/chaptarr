using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Download;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.DecisionEngine.Specifications.RssSync
{
    public class IndexerTagSpecification : IDecisionEngineSpecification
    {
        private readonly Logger _logger;
        private readonly IIndexerFactory _indexerFactory;

        public IndexerTagSpecification(Logger logger, IIndexerFactory indexerFactory)
        {
            _logger = logger;
            _indexerFactory = indexerFactory;
        }

        public SpecificationPriority Priority => SpecificationPriority.Default;
        public RejectionType Type => RejectionType.Permanent;

        public virtual Decision IsSatisfiedBy(RemoteBook subject, SearchCriteriaBase searchCriteria)
        {
            if (subject.Release == null || subject.Author == null || subject.Release.IndexerId == 0)
            {
                return Decision.Accept();
            }

            IndexerDefinition indexer;
            try
            {
                indexer = _indexerFactory.Get(subject.Release.IndexerId);
            }
            catch (ModelNotFoundException)
            {
                _logger.Trace("Indexer with id {0} does not exist, skipping indexer tags check", subject.Release.IndexerId);
                return Decision.Accept();
            }

            // If indexer has tags, check that at least one of them is present on the series
            var indexerTags = indexer.Tags;
            var mediaType = subject.GetPreferredMediaType();
            var authorTags = subject.Author.GetTagsForMediaType(mediaType);

            if (indexerTags.Any() && indexerTags.Intersect(authorTags).Empty())
            {
                _logger.Trace("Indexer {0} has tags. None of these are present on author {1}. Rejecting", subject.Release.Indexer, subject.Author);

                return Decision.Reject("Author tags do not match any of the indexer tags");
            }

            return Decision.Accept();
        }
    }
}
