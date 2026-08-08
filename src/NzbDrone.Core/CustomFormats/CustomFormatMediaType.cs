using NzbDrone.Core.Books;
using NzbDrone.Core.Profiles.Qualities;

namespace NzbDrone.Core.CustomFormats
{
    public enum CustomFormatMediaType
    {
        Both = 0,
        Audiobook = 1,
        Ebook = 2
    }

    public static class CustomFormatMediaTypeExtensions
    {
        public static bool AppliesToMediaType(this CustomFormat format, BookMediaType? mediaType)
        {
            if (format == null)
            {
                return false;
            }

            if (format.AppliesTo == CustomFormatMediaType.Both)
            {
                return true;
            }

            if (!mediaType.HasValue)
            {
                return false;
            }

            return format.AppliesTo == CustomFormatMediaType.Audiobook
                ? mediaType.Value == BookMediaType.Audiobook
                : mediaType.Value == BookMediaType.Ebook;
        }

        public static bool AppliesToProfile(this CustomFormat format, ProfileType profileType)
        {
            return format.AppliesTo == CustomFormatMediaType.Both ||
                   (format.AppliesTo == CustomFormatMediaType.Audiobook && profileType == ProfileType.Audiobook) ||
                   (format.AppliesTo == CustomFormatMediaType.Ebook && profileType == ProfileType.Ebook);
        }
    }
}
