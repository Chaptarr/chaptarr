using System.Collections.Generic;
using System.Linq;

namespace NzbDrone.Core.Books
{
    public static class EditionPinPolicy
    {
        public static bool IsManuallyPinned(Edition edition) => edition?.ManualAdd == true;

        internal static bool IsProtected(Edition edition, bool anyEditionOk) =>
            IsManuallyPinned(edition) || (!anyEditionOk && edition?.Monitored == true);

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
                .Where(edition => IsProtected(edition, book.AnyEditionOk))
                .GroupBy(edition => edition.Id)
                .Select(group => group.First())
                .OrderByDescending(IsManuallyPinned)
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

        public static void ApplyExplicitSelection(List<Edition> editions, int selectedId,
            IReadOnlySet<int> previouslyMonitoredIds, bool isManualSelection = false)
        {
            foreach (var edition in editions)
            {
                edition.Monitored = edition.Id == selectedId;
                if (edition.Monitored)
                {
                    edition.ManualAdd |= isManualSelection;
                }
                else if (previouslyMonitoredIds.Contains(edition.Id))
                {
                    // Explicit displacement releases the old choice, not unrelated retention.
                    edition.ManualAdd = false;
                }
            }
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
