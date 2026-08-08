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

        public BookDeletedEvent(Book book, bool deleteFiles, bool addImportListExclusion, bool applyToBothFormats = false, IEnumerable<Book> deletedBooks = null)
        {
            Book = book;
            DeleteFiles = deleteFiles;
            AddImportListExclusion = addImportListExclusion;
            ApplyToBothFormats = applyToBothFormats;
            DeletedBooks = (deletedBooks ?? Enumerable.Repeat(book, 1))
                .Where(item => item != null)
                .ToList();
        }
    }
}
