using System;
using System.Linq;
using NLog;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.DecisionEngine.Specifications
{
    public class MyAnonaMouseVipAccessSpecification : IDecisionEngineSpecification
    {
        private readonly IIndexerFactory _indexerFactory;
        private readonly Logger _logger;

        public MyAnonaMouseVipAccessSpecification(IIndexerFactory indexerFactory, Logger logger)
        {
            _indexerFactory = indexerFactory;
            _logger = logger;
        }

        public SpecificationPriority Priority => SpecificationPriority.Default;
        public RejectionType Type => RejectionType.Permanent;

        public Decision IsSatisfiedBy(RemoteBook subject, SearchCriteriaBase searchCriteria)
        {
            var release = subject?.Release;

            if (release == null ||
                !release.IndexerFlags.HasFlag(IndexerFlags.VipExclusive) ||
                !IsMamRelease(release))
            {
                return Decision.Accept();
            }

            if (IsVipEnabledForRelease(release))
            {
                return Decision.Accept();
            }

            _logger.Trace("Rejecting MAM VIP-only release '{0}' because current MAM status is not VIP", release.Title);
            return Decision.RejectHardFilter("MAM VIP-only torrent requires VIP membership", "Indexer");
        }

        private bool IsVipEnabledForRelease(ReleaseInfo release)
        {
            var definition = GetIndexerDefinition(release);
            return definition?.Settings is MyAnonaMouseSettings { IsVip: true };
        }

        private IndexerDefinition GetIndexerDefinition(ReleaseInfo release)
        {
            if (release.IndexerId > 0)
            {
                try
                {
                    return _indexerFactory.Get(release.IndexerId);
                }
                catch (ModelNotFoundException)
                {
                    _logger.Trace("Indexer with id {0} does not exist, falling back to MAM indexer name match", release.IndexerId);
                }
            }

            return _indexerFactory.All()
                .FirstOrDefault(definition => definition.Enable &&
                                              definition.Settings is MyAnonaMouseSettings &&
                                              IsMamIndexer(definition.Implementation));
        }

        private static bool IsMamRelease(ReleaseInfo release)
        {
            return IsMamIndexer(release.Indexer);
        }

        private static bool IsMamIndexer(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   (value.IndexOf("MyAnonaMouse", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    value.Equals("MAM", StringComparison.OrdinalIgnoreCase));
        }
    }
}
