using Chaptarr.Api.V1.MediaTypes;
using Chaptarr.Http.REST;
using NzbDrone.Core.Books;
using NzbDrone.Core.Profiles.Metadata;

namespace Chaptarr.Api.V1.Profiles.Metadata
{
    internal enum MetadataProfileMediaType
    {
        Audiobook,
        Ebook
    }

    internal static class MetadataProfileMediaTypeParser
    {
        public static MetadataProfileMediaType? ParseOrNull(string mediaType)
        {
            var parsed = MediaTypeParameterParser.ParseOptional(mediaType, allowAll: false);
            return parsed switch
            {
                null => null,
                BookMediaType.Audiobook => MetadataProfileMediaType.Audiobook,
                BookMediaType.Ebook => MetadataProfileMediaType.Ebook,
                _ => null
            };
        }

        public static MetadataProfileType ToProfileType(this MetadataProfileMediaType mediaType)
        {
            return mediaType switch
            {
                MetadataProfileMediaType.Audiobook => MetadataProfileType.Audiobook,
                MetadataProfileMediaType.Ebook => MetadataProfileType.Ebook,
                _ => throw new BadRequestException("mediaType must be either 'audiobook' or 'ebook'")
            };
        }
    }
}
