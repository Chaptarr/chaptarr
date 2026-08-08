using System.Linq;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.DecisionEngine.Specifications
{
    internal static class InteractiveBookSearchSpecificationHelper
    {
        public static bool IsRequestedBookInteractiveSearch(RemoteBook subject, SearchCriteriaBase searchCriteria)
        {
            var bookSearch = searchCriteria as BookSearchCriteria;
            if (bookSearch?.InteractiveSearch != true)
            {
                return false;
            }

            if (subject?.Books == null || subject.Books.Count == 0)
            {
                return false;
            }

            var requestedBookIds = bookSearch.Books?
                .Where(book => book != null && book.Id > 0)
                .Select(book => book.Id)
                .ToHashSet();

            if (requestedBookIds == null || requestedBookIds.Count == 0)
            {
                return false;
            }

            return subject.Books.Any(book => book != null && requestedBookIds.Contains(book.Id));
        }

        public static bool IsResolvedInteractiveBookSearch(RemoteBook subject, SearchCriteriaBase searchCriteria)
        {
            var bookSearch = searchCriteria as BookSearchCriteria;
            if (bookSearch?.InteractiveSearch != true)
            {
                return false;
            }

            if (subject?.SearchCriteriaMatch?.IsMatch != true)
            {
                return false;
            }

            if (subject.Books == null || subject.Books.Count == 0)
            {
                return false;
            }

            var requestedBookIds = bookSearch.Books?
                .Where(book => book != null && book.Id > 0)
                .Select(book => book.Id)
                .ToHashSet();

            if (requestedBookIds == null || requestedBookIds.Count == 0)
            {
                return false;
            }

            return subject.Books.Any(book => book != null && requestedBookIds.Contains(book.Id));
        }
    }
}
