using NLog;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.DecisionEngine.Specifications
{
    public class EbookFormatSpecification : IDecisionEngineSpecification
    {
        private readonly Logger _logger;

        public EbookFormatSpecification(Logger logger)
        {
            _logger = logger;
        }

        public SpecificationPriority Priority => SpecificationPriority.Default;
        public RejectionType Type => RejectionType.Permanent;

        public Decision IsSatisfiedBy(RemoteBook subject, SearchCriteriaBase searchCriteria)
        {
            var quality = subject.ParsedBookInfo.Quality.Quality;

            // Check if this is an ebook format
            if (IsEbookFormat(quality))
            {
                // Check if the author wants ebooks
                if (!subject.Author.EbookQualityProfileId.HasValue)
                {
                    _logger.Trace("Rejecting ebook format {0} - author has no ebook quality profile configured", quality);

                    // Soft filter: User preference, can be bypassed in interactive search
                    return Decision.RejectSoftFilter("Author has no ebook quality profile configured", "Format", quality);
                }

                _logger.Trace("Accepting ebook format {0} - author has ebook quality profile configured", quality);
            }
            else if (IsAudiobookFormat(quality))
            {
                // Check if the author wants audiobooks
                if (!subject.Author.AudiobookQualityProfileId.HasValue)
                {
                    _logger.Trace("Rejecting audiobook format {0} - author has no audiobook quality profile configured", quality);

                    // Soft filter: User preference, can be bypassed in interactive search
                    return Decision.RejectSoftFilter("Author has no audiobook quality profile configured", "Format", quality);
                }

                _logger.Trace("Accepting audiobook format {0} - author has audiobook quality profile configured", quality);
            }

            return Decision.Accept();
        }

        private bool IsEbookFormat(Quality quality)
        {
            return quality == Quality.PDF ||
                   quality == Quality.MOBI ||
                   quality == Quality.EPUB ||
                   quality == Quality.AZW3;
        }

        private bool IsAudiobookFormat(Quality quality)
        {
            return quality == Quality.M4B ||
                   quality == Quality.MP3 ||
                   quality == Quality.FLAC ||
                   quality == Quality.UnknownAudio;
        }
    }
}
