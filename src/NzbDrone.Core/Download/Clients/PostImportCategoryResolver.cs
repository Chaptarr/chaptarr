using System;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.Download.Clients
{
    internal static class PostImportCategoryResolver
    {
        public static string Resolve(DownloadClientItem downloadClientItem,
                                     string audiobookCategory,
                                     string ebookCategory,
                                     string audiobookImportedCategory,
                                     string ebookImportedCategory)
        {
            if (downloadClientItem == null)
            {
                return null;
            }

            // Prefer explicit media type (set when a RemoteBook is known or the client provides it).
            if (downloadClientItem.MediaType == BookMediaType.Ebook)
            {
                return ebookImportedCategory.IsNullOrWhiteSpace() ? null : ebookImportedCategory;
            }

            if (downloadClientItem.MediaType == BookMediaType.Audiobook)
            {
                return audiobookImportedCategory.IsNullOrWhiteSpace() ? null : audiobookImportedCategory;
            }

            var itemCategory = downloadClientItem.Category;

            var matchesEbookCategory = itemCategory.IsNotNullOrWhiteSpace() &&
                                       ebookCategory.IsNotNullOrWhiteSpace() &&
                                       ebookCategory.Equals(itemCategory, StringComparison.InvariantCultureIgnoreCase);

            var matchesAudiobookCategory = itemCategory.IsNotNullOrWhiteSpace() &&
                                           audiobookCategory.IsNotNullOrWhiteSpace() &&
                                           audiobookCategory.Equals(itemCategory, StringComparison.InvariantCultureIgnoreCase);

            // When the same category is used for both ebooks and audiobooks, both can match. In that case, prefer
            // whichever post-import category is configured (non-empty). If both are configured and different, we
            // can't pick a single correct value without additional context.
            var hasEbookPostImport = ebookImportedCategory.IsNotNullOrWhiteSpace();
            var hasAudiobookPostImport = audiobookImportedCategory.IsNotNullOrWhiteSpace();

            string resolved = null;

            if (matchesEbookCategory && hasEbookPostImport)
            {
                resolved = ebookImportedCategory;
            }

            if (matchesAudiobookCategory && hasAudiobookPostImport)
            {
                if (resolved == null)
                {
                    resolved = audiobookImportedCategory;
                }
                else if (!resolved.Equals(audiobookImportedCategory, StringComparison.InvariantCultureIgnoreCase))
                {
                    return null;
                }
            }

            if (resolved != null)
            {
                return resolved;
            }

            // Single shared post-import category.
            if (hasAudiobookPostImport &&
                audiobookImportedCategory.Equals(ebookImportedCategory, StringComparison.InvariantCultureIgnoreCase))
            {
                return audiobookImportedCategory;
            }

            return null;
        }

        public static bool IsInResolvedPostImportCategory(DownloadClientItem downloadClientItem,
                                                          string audiobookCategory,
                                                          string ebookCategory,
                                                          string audiobookImportedCategory,
                                                          string ebookImportedCategory)
        {
            var postImportCategory = Resolve(downloadClientItem,
                audiobookCategory,
                ebookCategory,
                audiobookImportedCategory,
                ebookImportedCategory);

            return postImportCategory.IsNotNullOrWhiteSpace() &&
                   postImportCategory.Equals(downloadClientItem?.Category, StringComparison.InvariantCultureIgnoreCase);
        }
    }
}
