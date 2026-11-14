using System;

namespace NzbDrone.Core.Books
{
    public static class BookMediaTypeParser
    {
        public static BookMediaType? ParseOptional(string mediaType, bool allowAll = true)
        {
            return mediaType?.Trim().ToLowerInvariant() switch
            {
                null or "" => null,
                "all" when allowAll => null,
                "audiobook" => BookMediaType.Audiobook,
                "ebook" => BookMediaType.Ebook,
                _ => throw new ArgumentException(allowAll
                    ? "mediaType must be 'all', 'audiobook', or 'ebook'"
                    : "mediaType must be either 'audiobook' or 'ebook'")
            };
        }
    }
}
