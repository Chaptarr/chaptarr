using System;

namespace NzbDrone.Core.Parser.Model
{
    public class ImportListItemInfo
    {
        public int ImportListId { get; set; }
        public string ImportList { get; set; }
        public string Author { get; set; }
        public string AuthorProviderId { get; set; }
        public string Book { get; set; }
        public string BookProviderId { get; set; }
        public string EditionProviderId { get; set; }
        public string Isbn13 { get; set; }
        public string Asin { get; set; }
        public int? HardcoverReadingFormatId { get; set; }
        public DateTime ReleaseDate { get; set; }

        public string AuthorGoodreadsId
        {
            get => AuthorProviderId;
            set => AuthorProviderId = value;
        }

        public string BookGoodreadsId
        {
            get => BookProviderId;
            set => BookProviderId = value;
        }

        public string EditionGoodreadsId
        {
            get => EditionProviderId;
            set => EditionProviderId = value;
        }

        public override string ToString()
        {
            return string.Format("[{0}] {1} [{2}]", ReleaseDate, Author, Book);
        }
    }
}
