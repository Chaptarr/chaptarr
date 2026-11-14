using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.MediaFiles
{
    internal static class EditionFileHydration
    {
        public static List<Edition> HydrateEditionBookFiles(this IMediaFileService mediaFileService, List<Edition> editions)
        {
            var safeEditions = editions?.Where(e => e != null).ToList() ?? new List<Edition>();
            var missingBookFiles = safeEditions.Where(e => e.BookFiles == null && e.BookId > 0).ToList();

            if (!missingBookFiles.Any())
            {
                return safeEditions;
            }

            var bookIds = missingBookFiles.Select(e => e.BookId).Distinct().ToList();
            var filesByEditionId = (mediaFileService.GetFilesByBooks(bookIds) ?? new List<BookFile>())
                .Where(file => file != null && file.EditionId > 0)
                .GroupBy(file => file.EditionId)
                .ToDictionary(group => group.Key, group => group.ToList());

            foreach (var edition in missingBookFiles)
            {
                edition.BookFiles = filesByEditionId.TryGetValue(edition.Id, out var editionFiles)
                    ? editionFiles
                    : new List<BookFile>();
            }

            return safeEditions;
        }
    }
}
