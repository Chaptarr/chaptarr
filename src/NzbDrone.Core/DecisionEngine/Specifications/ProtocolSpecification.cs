using NLog;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Delay;
using NzbDrone.Common.Instrumentation;

namespace NzbDrone.Core.DecisionEngine.Specifications
{
    public class ProtocolSpecification : IDecisionEngineSpecification
    {
        private readonly IDelayProfileService _delayProfileService;
        private readonly IConfigService _configService;
        private readonly Logger _logger;

        public ProtocolSpecification(IDelayProfileService delayProfileService,
                                     IConfigService configService,
                                     Logger logger)
        {
            _delayProfileService = delayProfileService;
            _configService = configService;
            _logger = logger;
        }

        public SpecificationPriority Priority => SpecificationPriority.Default;
        public RejectionType Type => RejectionType.Permanent;

        public virtual Decision IsSatisfiedBy(RemoteBook subject, SearchCriteriaBase searchCriteria)
        {
            var mediaType = subject.GetPreferredMediaType();
            var tags = _configService?.BookshelfMatchCompatibility == true
                ? subject.Author.Tags
                : subject.Author.GetTagsForMediaType(mediaType);
            var delayProfile = _delayProfileService.BestForTags(tags);

            if (subject.Release.DownloadProtocol == DownloadProtocol.Usenet && !delayProfile.EnableUsenet)
            {
                _logger.TraceSafe("[{0}] Usenet is not enabled for this author", subject.Release.Title);
                return Decision.Reject("Usenet is not enabled for this author");
            }

            if (subject.Release.DownloadProtocol == DownloadProtocol.Torrent && !delayProfile.EnableTorrent)
            {
                _logger.TraceSafe("[{0}] Torrent is not enabled for this author", subject.Release.Title);
                return Decision.Reject("Torrent is not enabled for this author");
            }

            return Decision.Accept();
        }
    }
}
