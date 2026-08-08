using Equ;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Books
{
    public class SeriesBookLink : Entity<SeriesBookLink>
    {
        public string Position { get; set; }
        public int SeriesPosition { get; set; }
        public int SeriesId { get; set; }
        public int BookId { get; set; }
        public bool IsPrimary { get; set; }

        // New fields for narrator variant support
        public string SeriesInstanceType { get; set; } = "original";
        public bool IsInheritedLink { get; set; } = false;

        [MemberwiseEqualityIgnore]
        public LazyLoaded<Series> Series { get; set; }
        [MemberwiseEqualityIgnore]
        public LazyLoaded<Book> Book { get; set; }

        public override void UseMetadataFrom(SeriesBookLink other)
        {
            Position = other.Position;
            SeriesPosition = other.SeriesPosition;
            IsPrimary = other.IsPrimary;
            SeriesInstanceType = other.SeriesInstanceType;
            IsInheritedLink = other.IsInheritedLink;
        }

        public override void UseDbFieldsFrom(SeriesBookLink other)
        {
            Id = other.Id;
            SeriesId = other.SeriesId;
            BookId = other.BookId;
            IsPrimary = other.IsPrimary;
            IsInheritedLink = other.IsInheritedLink;
        }
    }
}
