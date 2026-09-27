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
        // AuthorDeletedEvent handler (MediaFileDeletionService) already recursively removes the
        // author's whole folder(s). Per-book/per-file disk handlers should skip their own disk work
        // in that case - it's redundant at best and racy (duplicate recycle-bin entries, deleting
        // files out from under each other) at worst - while DeleteFiles still controls whether
        // DB-only cleanup (e.g. actually deleting BookFile rows instead of just unlinking them)
        // happens, since that's independent of who removes the physical files.
        public bool SkipDiskCleanup { get; private set; }

        public BookDeletedEvent(Book book, bool deleteFiles, bool addImportListExclusion, bool applyToBothFormats = false, IEnumerable<Book> deletedBooks = null, bool skipDiskCleanup = false)
        {
            Book = book;
            DeleteFiles = deleteFiles;
            AddImportListExclusion = addImportListExclusion;
            ApplyToBothFormats = applyToBothFormats;
            DeletedBooks = (deletedBooks ?? Enumerable.Repeat(book, 1))
                .Where(item => item != null)
                .ToList();
            SkipDiskCleanup = skipDiskCleanup;
        }
    }
}
