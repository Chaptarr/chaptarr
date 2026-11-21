using System.Collections.Generic;

namespace Chaptarr.Api.V1.BookFiles
{
    public class BookFileDeletePreviewRequest
    {
        public List<int> BookFileIds { get; set; }
        public List<int> BookIds { get; set; }
    }
}
