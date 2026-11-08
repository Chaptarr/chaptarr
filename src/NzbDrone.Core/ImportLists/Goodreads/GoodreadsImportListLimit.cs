using System.Collections.Generic;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.ImportLists.Goodreads
{
    internal static class GoodreadsImportListLimit
    {
        public static bool IsEnabled(int limit)
        {
            return limit > 0;
        }

        public static bool HasReached(int count, int limit)
        {
            return IsEnabled(limit) && count >= limit;
        }

        public static bool TryAdd(
            IList<ImportListItemInfo> target,
            ImportListItemInfo item,
            ISet<string> seenSourceBooks,
            int limit)
        {
            if (item == null)
            {
                return false;
            }

            if (IsEnabled(limit))
            {
                var sourceKey = GetSourceBookKey(item);
                if (sourceKey.IsNotNullOrWhiteSpace() && seenSourceBooks != null)
                {
                    if (!seenSourceBooks.Contains(sourceKey))
                    {
                        if (HasReached(seenSourceBooks.Count, limit))
                        {
                            return false;
                        }

                        seenSourceBooks.Add(sourceKey);
                    }
                }
                else if (HasReached(target.Count, limit))
                {
                    return false;
                }
            }

            target.Add(item);
            return true;
        }

        private static string GetSourceBookKey(ImportListItemInfo item)
        {
            if (item.BookProviderId.IsNotNullOrWhiteSpace())
            {
                return "work:" + ImportListProviderIdHelper.Normalize(item.BookProviderId, "gr");
            }

            // RSS exposes edition IDs before work mapping, so this fallback counts source editions.
            if (item.EditionProviderId.IsNotNullOrWhiteSpace())
            {
                return "edition:" + ImportListProviderIdHelper.Normalize(item.EditionProviderId, "gr");
            }

            if (item.Book.IsNotNullOrWhiteSpace() || item.Author.IsNotNullOrWhiteSpace())
            {
                return $"{item.Author?.Trim()}::{item.Book?.Trim()}".ToLowerInvariant();
            }

            return null;
        }
    }
}
