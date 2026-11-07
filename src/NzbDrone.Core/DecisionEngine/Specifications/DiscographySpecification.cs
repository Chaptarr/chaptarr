using System;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.DecisionEngine.Specifications
{
    public class DiscographySpecification : IDecisionEngineSpecification
    {
        private readonly Logger _logger;
        private readonly IConfigService _configService;

        public DiscographySpecification(IConfigService configService, Logger logger)
        {
            _configService = configService;
            _logger = logger;
        }

        public SpecificationPriority Priority => SpecificationPriority.Default;
        public RejectionType Type => RejectionType.Permanent;

        public virtual Decision IsSatisfiedBy(RemoteBook subject, SearchCriteriaBase searchCriteria)
        {
            var legacyEmptyDiscography = _configService?.BookshelfMatchCompatibility == true &&
                                         subject?.Books != null && !subject.Books.Any();

            if (subject?.ParsedBookInfo?.Discography == true && !legacyEmptyDiscography && !AllBooksReleased(subject))
            {
                _logger.Debug("Discography release {0} rejected because one or more books have not been released.",
                    subject.Release?.Title?.ReplaceLineEndings(""));
                return Decision.Reject("Discography release rejected. All books haven't released yet.");
            }

            return Decision.Accept();
        }

        internal static bool AllBooksReleased(RemoteBook subject)
        {
            var books = subject?.Books;
            return subject?.ParsedBookInfo?.Discography == true &&
                   books != null && books.Count > 0 &&
                   books.All(book => book?.ReleaseDate.HasValue == true && book.ReleaseDate.Value <= DateTime.UtcNow);
        }
    }
}
