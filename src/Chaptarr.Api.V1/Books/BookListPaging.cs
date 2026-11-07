using System;
using System.Collections.Generic;
using System.Linq;

namespace Chaptarr.Api.V1.Books
{
    internal static class BookListPaging
    {
        internal const int MaxLimit = 1000;

        public static bool IsRequested(int? offset, int? limit) => offset.HasValue || limit.HasValue;

        // Returns the requested window of an id-ordered list. Without offset or limit the list is
        // returned unchanged so existing clients keep receiving the full library.
        public static List<T> Slice<T>(List<T> items, int? offset, int? limit, Func<T, int> idSelector)
        {
            if (!IsRequested(offset, limit))
            {
                return items;
            }

            var skip = Math.Max(0, offset ?? 0);
            var take = Math.Clamp(limit ?? MaxLimit, 1, MaxLimit);

            return items.OrderBy(idSelector).Skip(skip).Take(take).ToList();
        }
    }
}
