using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Books;
using NzbDrone.Core.IndexerSearch.Definitions;

namespace NzbDrone.Core.Indexers
{
    public static class SearchMediaTypeHelper
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
            // but single-book searches must still be media-type scoped. Generic Books
            // can contain either format; never add categories the user did not configure.
            return configuredCategories
                .Where(category => CategoryMatchesMediaType(category, mediaType.Value))
                .ToList();
        }

        private static bool CategoryMatchesMediaType(int category, BookMediaType mediaType)
        {
            return mediaType switch
            {
                BookMediaType.Ebook => IsEbookCategory(category),
                _ => category == 7000 || IsAudiobookCategory(category),
            };
        }

        public static BookMediaType? GetMediaTypeFromCategories(IReadOnlyCollection<int> categories)
        {
            var hasAudio = categories?.Any(IsAudiobookCategory) == true;
            // The broad Books search category (7000) can contain either media type.
            // Only the standardized Books/EBook subcategory proves ebook content.
            var hasEbooks = categories?.Contains(7020) == true;
            return hasAudio == hasEbooks ? null : hasAudio ? BookMediaType.Audiobook : BookMediaType.Ebook;
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
