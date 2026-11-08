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
                    _logger.Trace("Release {0} soft-rejected as multi-book pack. Type={1}, Match={2}",
                        subject.Release?.Title,
                        detection.PackType,
                        detection.MatchedValue);

                    // Soft rather than hard. Both are kept out of automatic grabbing, because
                    // DownloadDecision.Approved requires zero rejections of any kind. The only difference is
                    // that soft lets the user grab it deliberately from interactive search.
                    return Decision.RejectSoftFilter("Release appears to contain multiple books", "Pack");

                case ReleasePackDetectionVerdict.AudiobookFragment:
                    _logger.Trace("Release {0} soft-rejected as audiobook fragment. Type={1}, Match={2}",
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
