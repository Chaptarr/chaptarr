using NLog;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.DecisionEngine.Specifications
{
    public class MyAnonaMouseUnsatisfiedSlotSpecification : IDecisionEngineSpecification
    {
        private readonly IMamUnsatisfiedSlotGuard _slotGuard;
        private readonly Logger _logger;

        public MyAnonaMouseUnsatisfiedSlotSpecification(IMamUnsatisfiedSlotGuard slotGuard, Logger logger)
        {
            _slotGuard = slotGuard;
            _logger = logger;
        }

        public SpecificationPriority Priority => SpecificationPriority.Default;
        public RejectionType Type => RejectionType.Temporary;

        public Decision IsSatisfiedBy(RemoteBook remoteBook, SearchCriteriaBase searchCriteria)
        {
            var availability = _slotGuard.Check(remoteBook);
            if (availability.Accepted)
            {
                return Decision.Accept();
            }

            _logger.Debug(availability.Reason);
            return Decision.Reject(availability.Reason);
        }
    }
}
