using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Extras;

namespace NzbDrone.Core.Books
{
    public static class AuthorDeletionScope
    {
        public static void Validate(IEnumerable<Author> authors, Func<Author, IEnumerable<Book>> books, BookMediaType? mediaType, bool both)
        {
            if (both || mediaType.HasValue) return;
            var ambiguous = authors.Where(author =>
            {
                var formats = books(author).Select(book => book.MediaType).ToHashSet();
                return (author.AudiobookRootFolderPath.IsNotNullOrWhiteSpace() || author.AudiobookPath.IsNotNullOrWhiteSpace() || formats.Contains(BookMediaType.Audiobook)) &&
                    (author.EbookRootFolderPath.IsNotNullOrWhiteSpace() || author.EbookPath.IsNotNullOrWhiteSpace() || formats.Contains(BookMediaType.Ebook));
            }).ToList();
            if (ambiguous.Count > 0)
                throw new NzbDroneClientException(HttpStatusCode.Conflict,
                    "Specify mediaType=audiobook or ebook, use a scoped base URL, or explicitly set applyToBothFormats=true. Ambiguous authors: {0}. Nothing was changed.",
                    string.Join("; ", ambiguous.Select(author => author.Name + " [" + string.Join(", ", ExtraFilePathHelper.GetAuthorBasePaths(author)) + "]")));
        }
    }
}
