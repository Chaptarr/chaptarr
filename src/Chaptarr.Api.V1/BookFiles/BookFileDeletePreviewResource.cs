using System.Collections.Generic;
using NzbDrone.Core.MediaFiles.BookImport.Manual;

namespace Chaptarr.Api.V1.BookFiles
{
    public class BookFileDeletePreviewResource
    {
        public List<ManualImportReplacementFile> Files { get; set; }
        public bool HasCalibreFiles { get; set; }
    }
}
