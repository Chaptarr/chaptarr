using System.Linq;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.DecisionEngine.Specifications.RssSync
{
    public class MonitoredBookSpecification : IDecisionEngineSpecification
    {
        private readonly Logger _logger;

        public MonitoredBookSpecification(Logger logger)
        {
            _logger = logger;
        }

        public SpecificationPriority Priority => SpecificationPriority.Default;
        public RejectionType Type => RejectionType.Permanent;

        public virtual Decision IsSatisfiedBy(RemoteBook subject, SearchCriteriaBase searchCriteria)
        {
            if (InteractiveBookSearchSpecificationHelper.IsRequestedBookInteractiveSearch(subject, searchCriteria))
            {
                _logger.Debug("Skipping monitored-book rejection for explicit interactive book search");
                return Decision.Accept();
            }

            var authorMonitoredCount = subject.Books.Count(book =>
                book != null && subject.Author.IsMonitoredForMediaType(book.MediaType == BookMediaType.Audiobook));
            if (subject.Books.Count > 0 && authorMonitoredCount == 0)
            {
                _logger.Debug("Author is not monitored for the release's media type. Rejecting");
                return Decision.Reject("Author is not monitored");
            }

            var monitoredCount = subject.Books.Count(book =>
                book != null &&
                book.IsMonitored() &&
                subject.Author.IsMonitoredForMediaType(book.MediaType == BookMediaType.Audiobook));

            if (monitoredCount == subject.Books.Count)
            {
                return Decision.Accept();
            }

            if (subject.Books.Count == 1)
            {
                _logger.Debug("Book is not monitored. Rejecting");
                return Decision.Reject("Book is not monitored");
            }

            if (monitoredCount == 0)
            {
                _logger.Debug("No books in the release are monitored. Rejecting", monitoredCount, subject.Books.Count);
            }
            else
            {
                _logger.Debug("Only {0}/{1} books in the release are monitored. Rejecting", monitoredCount, subject.Books.Count);
            }

            return Decision.Reject("Book is not monitored");
        }
    }
}
