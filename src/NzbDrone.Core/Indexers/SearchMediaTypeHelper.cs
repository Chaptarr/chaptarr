using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Books;
using NzbDrone.Core.IndexerSearch.Definitions;

namespace NzbDrone.Core.Indexers
{
    internal static class SearchMediaTypeHelper
    {
        public static BookMediaType? GetRequestedMediaType(SearchCriteriaBase searchCriteria)
        {
            if (searchCriteria?.Books == null || searchCriteria.Books.Count == 0)
            {
                return null;
            }

            var mediaTypes = searchCriteria.Books
                .Where(book => book != null)
                .Select(book => book.MediaType)
                .Distinct()
                .ToList();

            return mediaTypes.Count == 1 ? mediaTypes[0] : null;
        }

        public static List<int> FilterCategoriesForMediaType(IEnumerable<int> categories, BookMediaType? mediaType)
        {
            var configuredCategories = (categories ?? Enumerable.Empty<int>())
                .Distinct()
                .ToList();

            if (!mediaType.HasValue)
            {
                return configuredCategories;
            }

            // Newznab/Torznab categories are user-selected at the indexer level,
            // but single-book searches must still be media-type scoped. Keep every
            // configured category under the matching parent range; never add
            // categories the user did not configure.
            return configuredCategories
                .Where(category => CategoryMatchesMediaType(category, mediaType.Value))
                .ToList();
        }

        private static bool CategoryMatchesMediaType(int category, BookMediaType mediaType)
        {
            return mediaType switch
            {
                BookMediaType.Ebook => IsEbookCategory(category),
                _ => IsAudiobookCategory(category),
            };
        }

        private static bool IsAudiobookCategory(int category)
        {
            return category >= 3000 && category < 4000;
        }

        private static bool IsEbookCategory(int category)
        {
            return category >= 7000 && category < 8000;
        }
    }
}
