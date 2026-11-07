using System.Linq;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.DecisionEngine.Specifications.RssSync
{
    public class MonitoredBookSpecification : IDecisionEngineSpecification
    {
        private readonly Logger _logger;
        private readonly IConfigService _configService;

        public MonitoredBookSpecification(IConfigService configService, Logger logger)
        {
            _configService = configService;
            _logger = logger;
        }

        public SpecificationPriority Priority => SpecificationPriority.Default;
        public RejectionType Type => RejectionType.Permanent;

        public virtual Decision IsSatisfiedBy(RemoteBook subject, SearchCriteriaBase searchCriteria)
        {
            if (_configService?.BookshelfMatchCompatibility == true)
            {
                if (searchCriteria != null && !searchCriteria.MonitoredBooksOnly)
                {
                    _logger.Trace("Skipping monitored check during BookshelfNG-compatible search");
                    return Decision.Accept();
                }

                if (!subject.Author.Monitored)
                {
                    _logger.Trace("{0} is present in the database but not tracked", subject.Author);
                    return Decision.Reject("Author is not monitored");
                }

                // Monitored is an obsolete in-memory property with no native database
                // column. Migrated rows persist the source intent in their format flag.
                var legacyMonitoredCount = subject.Books.Count(book => book.IsMonitored());
                if (legacyMonitoredCount == subject.Books.Count)
                {
                    return Decision.Accept();
                }

                if (legacyMonitoredCount == 0)
                {
                    _logger.Trace("No books in the release are monitored. Rejecting");
                }
                else
                {
                    _logger.Trace("Only {0}/{1} books in the release are monitored. Rejecting", legacyMonitoredCount, subject.Books.Count);
                }

                return Decision.Reject("Book is not monitored");
            }

            if (InteractiveBookSearchSpecificationHelper.IsRequestedBookInteractiveSearch(subject, searchCriteria) ||
                InteractiveBookSearchSpecificationHelper.IsRequestedBookSearchAllowingUnmonitored(subject, searchCriteria))
            {
                _logger.Trace("Skipping monitored-book rejection for explicit book search");
                return Decision.Accept();
            }

            var authorMonitoredCount = subject.Books.Count(book =>
                book != null && subject.Author.IsMonitoredForMediaType(book.MediaType == BookMediaType.Audiobook));
            if (subject.Books.Count > 0 && authorMonitoredCount == 0)
            {
                _logger.Trace("Author is not monitored for the release's media type. Rejecting");
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
                _logger.Trace("Book is not monitored. Rejecting");
                return Decision.Reject("Book is not monitored");
            }

            if (monitoredCount == 0)
            {
                _logger.Trace("No books in the release are monitored. Rejecting", monitoredCount, subject.Books.Count);
            }
            else
            {
                _logger.Trace("Only {0}/{1} books in the release are monitored. Rejecting", monitoredCount, subject.Books.Count);
            }

            return Decision.Reject("Book is not monitored");
        }
    }
}
