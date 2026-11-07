using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Books;

namespace Chaptarr.Api.V1.Books
{
    internal sealed class BookshelfLegacyBookSelector
    {
        private readonly HashSet<int> _sourceEditionBookIds;
        private readonly HashSet<int> _monitoredBookIds;

        internal BookshelfLegacyBookSelector(IEnumerable<Edition> editions)
        {
            var monitored = editions.Where(edition => edition?.Monitored == true).ToList();
            _sourceEditionBookIds = monitored.Where(edition => !string.IsNullOrWhiteSpace(edition.ForeignEditionId))
                .Select(edition => edition.BookId).ToHashSet();
            _monitoredBookIds = monitored.Select(edition => edition.BookId).ToHashSet();
        }

        internal int Select(IReadOnlyCollection<Book> books)
        {
            if (books == null || books.Count == 0)
            {
                return 0;
            }

            var monitored = books.Where(book => _monitoredBookIds.Contains(book.Id)).ToList();
            if (monitored.Count > 0)
            {
                // The importer keeps the source provider identity on the original edition;
                // synthetic copies for the other format intentionally have no such identity.
                return monitored.OrderByDescending(book => _sourceEditionBookIds.Contains(book.Id))
                    .ThenBy(book => book.MediaType).ThenBy(book => book.Id).First().Id;
            }

            return books.OrderByDescending(book => book.HasFiles)
                .ThenBy(book => book.MediaType).ThenBy(book => book.Id).First().Id;
        }
    }
}
