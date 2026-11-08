using System.Collections.Generic;

namespace Chaptarr.Api.V1.ManualImport
{
    public class ManualImportReplacementRequest
    {
        public List<ManualImportReplacementSelection> Files { get; set; }
    }

    public class ManualImportReplacementSelection
    {
        public string Path { get; set; }
        public int BookId { get; set; }
    }
}
