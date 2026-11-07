using NLog;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.DecisionEngine.Specifications
{
    public class TorrentSeedingSpecification : IDecisionEngineSpecification
    {
        private readonly IIndexerFactory _indexerFactory;
        private readonly IConfigService _configService;
        private readonly Logger _logger;

        public TorrentSeedingSpecification(IIndexerFactory indexerFactory, IConfigService configService, Logger logger)
        {
            _indexerFactory = indexerFactory;
            _configService = configService;
            _logger = logger;
        }

        public SpecificationPriority Priority => SpecificationPriority.Default;
        public RejectionType Type => _configService?.BookshelfMatchCompatibility == true
            ? RejectionType.Permanent
            : RejectionType.Temporary;

        public Decision IsSatisfiedBy(RemoteBook remoteBook, SearchCriteriaBase searchCriteria)
        {
            var torrentInfo = remoteBook.Release as TorrentInfo;

            if (torrentInfo == null || torrentInfo.IndexerId == 0)
            {
                return Decision.Accept();
            }

            IndexerDefinition indexer;
            try
            {
                indexer = _indexerFactory.Get(torrentInfo.IndexerId);
            }
            catch (ModelNotFoundException)
            {
                _logger.Trace("Indexer with id {0} does not exist, skipping seeders check", torrentInfo.IndexerId);
                return Decision.Accept();
            }

            var torrentIndexerSettings = indexer.Settings as ITorrentIndexerSettings;

            if (torrentIndexerSettings != null)
            {
                var minimumSeeders = torrentIndexerSettings.MinimumSeeders;

                if (torrentInfo.Seeders.HasValue && torrentInfo.Seeders.Value < minimumSeeders)
                {
                    _logger.Trace("Not enough seeders: {0}. Minimum seeders: {1}", torrentInfo.Seeders, minimumSeeders);

                    if (_configService?.BookshelfMatchCompatibility == true)
                    {
                        return Decision.Reject("Not enough seeders: {0}. Minimum seeders: {1}", torrentInfo.Seeders, minimumSeeders);
                    }

                    return Decision.RejectSoftFilter("Not enough seeders: {0}. Minimum seeders: {1}", "Seeders", torrentInfo.Seeders, minimumSeeders);
                }
            }

            return Decision.Accept();
        }
    }
}
