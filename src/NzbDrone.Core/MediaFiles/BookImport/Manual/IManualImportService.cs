using System.Collections.Generic;
using System.Threading;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles.BookImport.Manual;

namespace NzbDrone.Core.MediaFiles.BookImport.Manual
{
    public interface IManualImportService
    {
        List<ManualImportItem> GetMediaFiles(string folder, string downloadId, Author author, FilterFilesType filter, bool replaceExistingFiles, CancellationToken cancellationToken = default, IReadOnlyCollection<string> exactPaths = null);
        List<ManualImportItem> UpdateItems(List<ManualImportItem> items);
    }
}
