using NLog;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.DecisionEngine.Specifications
{
    public class MultiBookReleaseSpecification : IDecisionEngineSpecification
    {
        private readonly Logger _logger;

        public MultiBookReleaseSpecification(Logger logger)
        {
            _logger = logger;
        }

        public SpecificationPriority Priority => SpecificationPriority.Default;
        public RejectionType Type => RejectionType.Permanent;

        public Decision IsSatisfiedBy(RemoteBook subject, SearchCriteriaBase searchCriteria)
        {
            var detection = subject?.PackDetection;
            if (detection == null)
            {
                return Decision.Accept();
            }

            switch (detection.Verdict)
            {
                case ReleasePackDetectionVerdict.MultipleBooks:
                    _logger.Debug("Release {0} rejected as multi-book pack. Type={1}, Match={2}",
                        subject.Release?.Title,
                        detection.PackType,
                        detection.MatchedValue);
                    return Decision.RejectHardFilter("Release appears to contain multiple books", "Pack");

                case ReleasePackDetectionVerdict.AudiobookFragment:
                    _logger.Debug("Release {0} soft-rejected as audiobook fragment. Type={1}, Match={2}",
                        subject.Release?.Title,
                        detection.PackType,
                        detection.MatchedValue);
                    return Decision.RejectSoftFilter("Release may be one disc/part of a larger audiobook", "Pack");

                default:
                    return Decision.Accept();
            }
        }
    }
}
