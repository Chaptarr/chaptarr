using Equ;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Books
{
    public class EditionChapter : MemberwiseEquatable<EditionChapter>, IEmbeddedDocument
    {
        public string Title { get; set; }
        public int StartOffsetMs { get; set; }
        public int StartOffsetSec { get; set; }
        public int LengthMs { get; set; }
    }
}
