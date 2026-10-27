using System;
using System.Linq;
using System.Net;
using Chaptarr.Http.REST;
using NzbDrone.Core.Books;
using NzbDroneClientException = NzbDrone.Core.Exceptions.NzbDroneClientException;

namespace Chaptarr.Api.V1.MediaTypes
{
    public static class MediaTypeParameterParser
    {
        public const string FormatRequiredMessage = "Specify ?mediaType=audiobook or ?mediaType=ebook, use a media-scoped /audiobook or /ebook base URL, or supply per-format author fields such as audiobookQualityProfileId / ebookQualityProfileId and audiobookRootFolderPath / ebookRootFolderPath.";

        public static string ResolveWriteScope(string mediaType, string facadeMediaType)
        {
            var requested = NormalizeOptional(mediaType, allowAll: false);
            if (requested != null && facadeMediaType != null && requested != facadeMediaType)
            {
                throw new BadRequestException("mediaType must agree with the media-scoped API base URL.");
            }

            return facadeMediaType ?? requested;
        }

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

        public static BookMediaType? ParseDeletionScope(bool applyToBothFormats, string facadeMediaType, params string[] mediaTypes)
        {
            if (ParseOptional(facadeMediaType, allowAll: false).HasValue && applyToBothFormats)
            {
                throw new NzbDroneClientException(HttpStatusCode.Conflict,
                    "A media-scoped base URL cannot delete both formats. Use the unscoped API with applyToBothFormats=true. Nothing was changed.");
            }

            var scopes = mediaTypes.Append(facadeMediaType).Select(value => ParseOptional(value, allowAll: false))
                .Where(value => value.HasValue).Distinct().ToList();
            if (scopes.Count > 1)
            {
                throw new BadRequestException("Conflicting deletion media types. The route, query, and body must agree.");
            }

            return scopes.SingleOrDefault();
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
