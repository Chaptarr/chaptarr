using System;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Books
{
    public class BookNarratorOption : ModelBase
    {
        public int BookId { get; set; }
        public string Narrator { get; set; }
        public string Source { get; set; }
        public DateTime DateDiscovered { get; set; }
        public bool IsPreferred { get; set; }

        // Navigation property
        public LazyLoaded<Book> Book { get; set; }

        public BookNarratorOption()
        {
            DateDiscovered = DateTime.UtcNow;
            IsPreferred = false;
        }
    }
}
