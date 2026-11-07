using System.Collections.Generic;

namespace Chaptarr.Api.V1.Books
{
    public class BookRelatedFormatsResource
    {
        public List<BookRelatedFormatResource> Formats { get; set; } = new List<BookRelatedFormatResource>();
    }

    public class BookRelatedFormatResource
    {
        public int BookId { get; set; }
        public string MediaType { get; set; }
        public string Title { get; set; }
        public string Narrator { get; set; }
        public string Disambiguation { get; set; }
    }
}
