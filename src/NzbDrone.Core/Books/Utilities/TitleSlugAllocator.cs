using System;

namespace NzbDrone.Core.Books
{
    internal static class TitleSlugAllocator
    {
        public static string FindAvailable(string baseSlug, Func<string, bool> isReserved)
        {
            var slug = baseSlug;
            var suffix = 2;
            while (isReserved(slug)) slug = $"{baseSlug}_{suffix++}";
            return slug;
        }
    }
}
