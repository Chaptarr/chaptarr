using NzbDrone.Core.MediaCover;

namespace Chaptarr.Api.V1.Author
{
    internal static class AuthorImageHashHelper
    {
        public static string ComputeStableImageHash(string url, MediaCoverTypes coverType)
        {
            return MediaCoverRendition.ComputeStableAuthorImageHash(url, coverType);
        }
    }
}

