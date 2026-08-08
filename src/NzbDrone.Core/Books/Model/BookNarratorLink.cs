using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Books
{
    public class BookNarratorLink : ModelBase
    {
        public int BookId { get; set; }
        public int NarratorId { get; set; }
        public bool IsPrimary { get; set; }
        public string Role { get; set; }

        // Navigation properties
        public LazyLoaded<Book> Book { get; set; }
        public LazyLoaded<Narrator> Narrator { get; set; }

        public BookNarratorLink()
        {
            IsPrimary = false;
            Role = "Narrator";
        }
    }
}
