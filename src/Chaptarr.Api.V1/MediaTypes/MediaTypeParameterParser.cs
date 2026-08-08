using System;
using Chaptarr.Http.REST;
using NzbDrone.Core.Books;

namespace Chaptarr.Api.V1.MediaTypes
{
    public static class MediaTypeParameterParser
    {
        public static BookMediaType? ParseOptional(string mediaType, bool allowAll = true)
        {
            if (string.IsNullOrWhiteSpace(mediaType))
            {
                return null;
            }

            switch (mediaType.Trim().ToLowerInvariant())
            {
                case "all" when allowAll:
                    return null;
                case "audiobook":
                    return BookMediaType.Audiobook;
                case "ebook":
                    return BookMediaType.Ebook;
                default:
                    throw new BadRequestException(allowAll
                        ? "mediaType must be 'all', 'audiobook', or 'ebook'"
                        : "mediaType must be either 'audiobook' or 'ebook'");
            }
        }

        public static BookMediaType ParseRequired(string mediaType)
        {
            var parsed = ParseOptional(mediaType, allowAll: false);
            if (!parsed.HasValue)
            {
                throw new BadRequestException("mediaType must be either 'audiobook' or 'ebook'");
            }

            return parsed.Value;
        }

        public static string NormalizeOptional(string mediaType, bool allowAll = true)
        {
            var parsed = ParseOptional(mediaType, allowAll);
            return parsed.HasValue ? ToApiValue(parsed.Value) : null;
        }

        public static string ToApiValue(BookMediaType mediaType)
        {
            return mediaType == BookMediaType.Ebook ? "ebook" : "audiobook";
        }
    }
}
