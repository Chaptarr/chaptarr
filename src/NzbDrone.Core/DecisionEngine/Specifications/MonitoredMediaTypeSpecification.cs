using System.Linq;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.DecisionEngine.Specifications
{
    public class MonitoredMediaTypeSpecification : IDecisionEngineSpecification
    {
        private readonly Logger _logger;

        public MonitoredMediaTypeSpecification(Logger logger)
        {
            _logger = logger;
        }

        public SpecificationPriority Priority => SpecificationPriority.Default;
        public RejectionType Type => RejectionType.Permanent;

        public Decision IsSatisfiedBy(RemoteBook subject, SearchCriteriaBase searchCriteria)
        {
            if (InteractiveBookSearchSpecificationHelper.IsRequestedBookInteractiveSearch(subject, searchCriteria))
            {
                _logger.Trace("Skipping media-type monitoring rejection for explicit interactive book search");
                return Decision.Accept();
            }

            if (subject?.Books == null || subject.Books.Count == 0)
            {
                _logger.Trace("No books in release, skipping media type monitoring check");
                return Decision.Accept();
            }

            var quality = subject.ParsedBookInfo?.Quality?.Quality;
            if (quality == null)
            {
                _logger.Trace("No quality information available, skipping media type monitoring check");
                return Decision.Accept();
            }

            _logger.Trace("Checking monitoring for quality: {0} (ID: {1})", quality.Name, quality.Id);

            var mediaType = QualityMediaTypeHelper.DetectMediaType(quality, subject.Release);
            var isAudiobook = mediaType == BookMediaType.Audiobook;
            var isEbook = mediaType == BookMediaType.Ebook;

            if (!isAudiobook && !isEbook)
            {
                _logger.Trace("Quality {0} does not map to a known monitored media type, accepting", quality.Name);
                return Decision.Accept();
            }

            var mediaTypeName = isAudiobook ? "audiobook" : "ebook";
            var mediaTypeDisplayName = isAudiobook ? "Audiobook" : "Ebook";
            var authorSideMonitored = subject.Author.IsMonitoredForMediaType(isAudiobook);

            if (!authorSideMonitored)
            {
                _logger.Trace("Rejecting {0} quality {1} - {0} monitoring is disabled for this author", mediaTypeName, quality.Name);
                return Decision.Reject($"{mediaTypeDisplayName} monitoring is disabled for this author");
            }

            var bookSideMonitored = isAudiobook
                ? subject.Books.Any(b => b.AudiobookMonitored)
                : subject.Books.Any(b => b.EbookMonitored);

            if (!bookSideMonitored)
            {
                _logger.Trace("Rejecting {0} quality {1} - no books have {0} monitoring enabled", mediaTypeName, quality.Name);
                return Decision.Reject($"{mediaTypeDisplayName} format is not monitored for this book");
            }

            _logger.Trace("{0} monitoring check passed for quality {1}", mediaTypeName, quality.Name);
            return Decision.Accept();
        }
    }
}
