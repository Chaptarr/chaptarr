using System;
using System.Linq;
using System.Net;
using Chaptarr.Http.REST;
using NzbDrone.Core.Books;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Profiles.Qualities;
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

        public static ProfileType? GetProfileType(int? profileId, IQualityProfileService profiles)
        {
            if (!(profileId > 0) || profiles == null) return null;
            try
            {
                return profiles.Get(profileId.Value)?.ProfileType;
            }
            catch (NzbDrone.Core.Datastore.ModelNotFoundException)
            {
                throw new BadRequestException("Quality profile does not exist.");
            }
        }

        // I may want to revisit this as we get docs out for other apps. Don't want kevin (and others) to have to adjust too much or break things for other apps too
        public static string InferWriteScope(RootFolder root, NzbDrone.Core.Books.Author author, ProfileType? profileType = null)
        {
            var rootScope = root?.FolderType switch
            {
                FolderType.Audiobook => "audiobook",
                FolderType.Ebook => "ebook",
                _ => null
            };
            var profileScope = profileType switch
            {
                ProfileType.Audiobook => "audiobook",
                ProfileType.Ebook => "ebook",
                _ => null
            };
            if (rootScope != null && profileScope != null && rootScope != profileScope)
            {
                throw new BadRequestException("The quality profile's format does not match the root folder's format.");
            }

            if (rootScope != null || profileScope != null)
            {
                return rootScope ?? profileScope;
            }

            var audio = HasConfiguredMediaSettings(author, BookMediaType.Audiobook);
            var ebook = HasConfiguredMediaSettings(author, BookMediaType.Ebook);
            return audio == ebook ? null : audio ? "audiobook" : "ebook";
        }

        public static bool HasConfiguredMediaSettings(NzbDrone.Core.Books.Author author, BookMediaType mediaType) =>
            author?.HasConfiguredMediaSettings(mediaType) == true;

        public static bool HasConfiguredMediaSettings(global::Chaptarr.Api.V1.Author.AuthorResource author, BookMediaType mediaType) =>
            mediaType == BookMediaType.Audiobook
                ? NzbDrone.Core.Books.Author.HasConfiguredMediaSettings(author?.AudiobookQualityProfileId, author?.AudiobookRootFolderPath)
                : NzbDrone.Core.Books.Author.HasConfiguredMediaSettings(author?.EbookQualityProfileId, author?.EbookRootFolderPath);

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
            var scopes = mediaTypes.Append(facadeMediaType).Select(value => ParseOptional(value, allowAll: false))
                .Where(value => value.HasValue).Distinct().ToList();
            if (applyToBothFormats && scopes.Count > 0)
            {
                throw new NzbDroneClientException(HttpStatusCode.Conflict,
                    "A media-scoped request cannot delete both formats. Use the unscoped API with applyToBothFormats=true and omit mediaType. Nothing was changed.");
            }

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
