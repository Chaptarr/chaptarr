using System.Collections.Generic;
using System.IO.Abstractions;
using System.Threading;
using System.Threading.Tasks;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.MediaFiles
{
    public interface IDiskScanService
    {
        void Scan(Author author);
        void ScanRootFolder(string path, AuthorScanMode mode = AuthorScanMode.All, CancellationToken cancellationToken = default);
        Task ScanRootFolderAsync(string path, AuthorScanMode mode = AuthorScanMode.All, CancellationToken cancellationToken = default);

        // Utility methods for file operations
        IFileInfo[] GetBookFiles(string path, bool allDirectories = true);
        string[] GetNonBookFiles(string path, bool allDirectories = true);
        List<IFileInfo> FilterFiles(string basePath, IEnumerable<IFileInfo> files);
        List<string> FilterPaths(string basePath, IEnumerable<string> paths);
    }

    public enum AuthorScanMode
    {
        All,
        NewFiles,
        MissingFiles
    }
}
