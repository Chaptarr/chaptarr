using NzbDrone.Common.Messaging;
using System.Collections.Generic;
using System.Linq;

namespace NzbDrone.Core.Books.Events
{
    public class BookDeletedEvent : IEvent
    {
        public Book Book { get; private set; }
        public bool DeleteFiles { get; private set; }
        public bool AddImportListExclusion { get; private set; }
        public bool ApplyToBothFormats { get; private set; }
        public IReadOnlyList<Book> DeletedBooks { get; private set; }

        // Set when this book is being deleted as part of a larger author delete, whose own
        // AuthorDeletedEvent handling already covers what a per-book handler would otherwise do
        // again for every one of the author's books: MediaFileDeletionService recursively removes
        // the author's whole folder(s) (so per-book/per-file disk cleanup here is redundant at best
        // and racy - duplicate recycle-bin entries, deleting files out from under each other - at
        // worst), and NotificationService already sends a single OnAuthorDelete notification (so a
        // per-book OnBookDelete on top of that is exactly the "one notification per episode when a
        // whole series is deleted" noise Sonarr/Radarr deliberately avoid). DeleteFiles still
        // independently controls whether MediaFileService deletes vs. unlinks the BookFile DB rows,
        // since that's unrelated to who removes physical files or which notification fires.
        public bool PartOfAuthorDelete { get; private set; }

        public BookDeletedEvent(Book book, bool deleteFiles, bool addImportListExclusion, bool applyToBothFormats = false, IEnumerable<Book> deletedBooks = null, bool partOfAuthorDelete = false)
        {
            Book = book;
            DeleteFiles = deleteFiles;
            AddImportListExclusion = addImportListExclusion;
            ApplyToBothFormats = applyToBothFormats;
            DeletedBooks = (deletedBooks ?? Enumerable.Repeat(book, 1))
                .Where(item => item != null)
                .ToList();
            PartOfAuthorDelete = partOfAuthorDelete;
        }
    }
}
