using Chaptarr.Api.V1.MediaTypes;
using Chaptarr.Http.REST;
using NzbDrone.Core.Books;
using NzbDrone.Core.Profiles.Qualities;

namespace Chaptarr.Api.V1.Profiles.Quality
{
    internal enum QualityProfileMediaType
    {
        Audiobook,
        Ebook
    }

    internal static class QualityProfileMediaTypeParser
    {
        public static QualityProfileMediaType? ParseOrNull(string mediaType)
        {
            var parsed = MediaTypeParameterParser.ParseOptional(mediaType, allowAll: false);
            return parsed switch
            {
                null => null,
                BookMediaType.Audiobook => QualityProfileMediaType.Audiobook,
                BookMediaType.Ebook => QualityProfileMediaType.Ebook,
                _ => null
            };
        }

        public static ProfileType ToProfileType(this QualityProfileMediaType mediaType)
        {
            return mediaType switch
            {
                QualityProfileMediaType.Audiobook => ProfileType.Audiobook,
                QualityProfileMediaType.Ebook => ProfileType.Ebook,
                _ => throw new BadRequestException("mediaType must be either 'audiobook' or 'ebook'")
            };
        }
    }
}
