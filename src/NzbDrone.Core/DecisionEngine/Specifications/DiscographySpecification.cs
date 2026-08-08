using NLog;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.DecisionEngine.Specifications
{
    public class DiscographySpecification : IDecisionEngineSpecification
    {
        private readonly Logger _logger;

        public DiscographySpecification(Logger logger)
        {
            _logger = logger;
        }

        public SpecificationPriority Priority => SpecificationPriority.Default;
        public RejectionType Type => RejectionType.Permanent;

        public virtual Decision IsSatisfiedBy(RemoteBook subject, SearchCriteriaBase searchCriteria)
        {
            if (subject?.ParsedBookInfo?.Discography == true)
            {
                _logger.Debug("Discography release {0} rejected as a multi-book pack.", subject.Release?.Title);
                return Decision.RejectHardFilter("Release appears to contain multiple books (discography)", "Pack");
            }

            return Decision.Accept();
        }
    }
}
