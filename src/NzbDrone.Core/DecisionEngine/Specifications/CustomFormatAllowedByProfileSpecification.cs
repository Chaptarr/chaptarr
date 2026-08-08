using NzbDrone.Common.Extensions;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.DecisionEngine.Specifications
{
    public class CustomFormatAllowedbyProfileSpecification : IDecisionEngineSpecification
    {
        public SpecificationPriority Priority => SpecificationPriority.Default;
        public RejectionType Type => RejectionType.Permanent;

        public virtual Decision IsSatisfiedBy(RemoteBook subject, SearchCriteriaBase searchCriteria)
        {
            var profile = subject.Author.GetQualityProfileForQuality(subject.ParsedBookInfo.Quality.Quality);
            if (profile == null)
            {
                return Decision.Reject("No quality profile configured for {0} files", subject.ParsedBookInfo.Quality.Quality.Name);
            }

            var minScore = profile.MinFormatScore;
            var score = subject.CustomFormatScore;

            if (score < minScore)
            {
                return Decision.Reject("Custom Formats {0} have score {1} below Author profile minimum {2}", subject.CustomFormats.ConcatToString(), score, minScore);
            }

            return Decision.Accept();
        }
    }
}
