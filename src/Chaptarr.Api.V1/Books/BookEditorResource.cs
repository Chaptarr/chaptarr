using System.Collections.Generic;

namespace Chaptarr.Api.V1.Books
{
    public class BookEditorResource
    {
        public List<int> BookIds { get; set; }
        public bool? Monitored { get; set; }
        public bool? DeleteFiles { get; set; }
        public bool? AddImportListExclusion { get; set; }
        public string MediaType { get; set; } // "audiobook" or "ebook" - optional for media-specific monitoring
    }
}
