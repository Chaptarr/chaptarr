using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.MediaFiles
{
    public static class RetagMediaScope
    {
        public static void ForEachAuthor(IReadOnlyList<Author> authors, IMediaFileService service, BookMediaType mediaType, TagBatch batch, Action<List<BookFile>> retag)
        {
            for (var index = 0; index < authors.Count; index++)
            {
                batch.CurrentAuthor = authors[index].Name;
                batch.UnattemptedAuthors = authors.Count - index - 1;
                retag(Select(service.GetFilesByAuthor(authors[index].Id), mediaType, batch).ToList());
            }
        }

        public static IEnumerable<BookFile> Select(IEnumerable<BookFile> files, BookMediaType mediaType, TagBatch batch = null)
        {
            // Side membership follows the linked book, independent of extension or denormalized file metadata.
            foreach (var file in files)
            {
                if (file?.Edition?.Book == null)
                {
                    if (file != null) batch?.UnlinkedFiles.Add(file.Id);
                }
                else if (file.Edition.Book.MediaType == mediaType)
                {
                    yield return file;
                }
            }
        }
    }
}
