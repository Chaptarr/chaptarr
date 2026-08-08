using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.MediaTypes;
using NzbDrone.Core.Books;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Api.V1.RootFolders
{
    public static class RootFolderMediaTypeFilter
    {
        public static List<RootFolder> Filter(IEnumerable<RootFolder> rootFolders, string mediaType)
        {
            var result = rootFolders?.ToList() ?? new List<RootFolder>();

            var parsed = MediaTypeParameterParser.ParseOptional(mediaType);
            if (!parsed.HasValue)
            {
                return result;
            }

            return parsed.Value switch
            {
                BookMediaType.Audiobook => result
                    .Where(r => r.FolderType == FolderType.Mixed || r.FolderType == FolderType.Audiobook)
                    .ToList(),
                BookMediaType.Ebook => result
                    .Where(r => r.FolderType == FolderType.Mixed || r.FolderType == FolderType.Ebook)
                    .ToList(),
                _ => result
            };
        }
    }
}
