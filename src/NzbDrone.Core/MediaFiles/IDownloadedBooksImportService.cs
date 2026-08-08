using System.Collections.Generic;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles
{
    public interface IDownloadedBooksImportService
    {
        List<ImportResult> ProcessPath(string path, ImportMode importMode = ImportMode.Auto, Author author = null, DownloadClientItem downloadClientItem = null, RemoteBook remoteBook = null, bool requireDefaultRootFolderForMissingAuthors = false);
        List<ImportResult> ProcessFolder(string path, ImportMode importMode = ImportMode.Auto, Author author = null, DownloadClientItem downloadClientItem = null, RemoteBook remoteBook = null, bool requireDefaultRootFolderForMissingAuthors = false);
        List<ImportResult> ProcessFile(string path, ImportMode importMode = ImportMode.Auto, Author author = null, DownloadClientItem downloadClientItem = null, RemoteBook remoteBook = null, bool requireDefaultRootFolderForMissingAuthors = false);
    }
}
