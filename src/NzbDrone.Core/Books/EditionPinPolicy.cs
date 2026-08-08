using System.Collections.Generic;
using System.Linq;

namespace NzbDrone.Core.Books
{
    public static class EditionPinPolicy
    {
        public static IReadOnlyList<Edition> GetProtectedEditions(Book book, IEnumerable<Edition> editions)
        {
            if (book == null)
            {
                return new List<Edition>();
            }

            var available = editions?
                .Where(edition => edition != null && edition.Id > 0)
                .ToList() ?? new List<Edition>();

            return available
                .Where(edition => edition.ManualAdd || (!book.AnyEditionOk && edition.Monitored))
                .GroupBy(edition => edition.Id)
                .Select(group => group.First())
                .OrderByDescending(edition => edition.ManualAdd)
                .ThenByDescending(edition => edition.Monitored)
                .ThenBy(edition => edition.Id)
                .ToList();
        }

        public static Edition FindConflictingProtectedEdition(Book book, IEnumerable<Edition> editions, int targetEditionId)
        {
            if (targetEditionId <= 0)
            {
                return null;
            }

            return GetProtectedEditions(book, editions)
                .FirstOrDefault(edition => edition.Id != targetEditionId);
        }

        public static bool CanAutomationSelectEdition(Book book, IEnumerable<Edition> editions)
        {
            return GetProtectedEditions(book, editions).Count == 0;
        }

        public static void MarkSelectionAsAutomatic(Book book, IEnumerable<Edition> editions)
        {
            if (book == null)
            {
                return;
            }

            book.AnyEditionOk = true;
            foreach (var edition in editions ?? Enumerable.Empty<Edition>())
            {
                if (edition != null)
                {
                    edition.ManualAdd = false;
                }
            }
        }
    }
}
