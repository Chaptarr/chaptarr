using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Books
{
    public class EditionNarratorLink : ModelBase
    {
        public int EditionId { get; set; }
        public int NarratorId { get; set; }
        public bool IsPrimary { get; set; }
        public string Role { get; set; }

        // Navigation properties
        public LazyLoaded<Edition> Edition { get; set; }
        public LazyLoaded<Narrator> Narrator { get; set; }

        public EditionNarratorLink()
        {
            IsPrimary = false;
            Role = "Narrator";
        }
    }
}
