using System.Collections.Generic;
using System.Threading.Tasks;
using NzbDrone.Core.Authors;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.MediaFiles.BookImport
{
    /// <summary>
    /// Interface for orchestrating the book import process.
    /// Implemented by ImportOrchestratorV2 (active implementation).
    /// </summary>
    public interface IImportOrchestrator
    {
        Task<OrchestratorImportResult> ProcessFilesAsync(string path, RootFolder rootFolder = null, int? commandId = null, IReadOnlyCollection<string> forceStagePaths = null, FilterFilesType filter = FilterFilesType.Known);
    }

    /// <summary>
    /// Result of orchestrated import operation containing imported files, unmapped files, and errors.
    /// </summary>
    public class OrchestratorImportResult
    {
        public List<ImportedFile> ImportedFiles { get; set; } = new List<ImportedFile>();
        public List<UnmappedFile> UnmappedFiles { get; set; } = new List<UnmappedFile>();
        public List<FailedFile> FailedFiles { get; set; } = new List<FailedFile>();
        public List<string> AddedAuthors { get; set; } = new List<string>();
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> ScannedFilePaths { get; set; } = new List<string>();
        public bool CleanupSafe { get; set; }
    }

    /// <summary>
    /// Represents a successfully imported file.
    /// </summary>
    public class ImportedFile
    {
        public string FilePath { get; set; }
        public int BookId { get; set; }
        public string BookTitle { get; set; }
        public string AuthorName { get; set; }
    }

    /// <summary>
    /// Represents a file that could not be matched to a book in the library.
    /// </summary>
    public class UnmappedFile
    {
        public string FilePath { get; set; }
        public string Reason { get; set; }
        public AuthorSuggestion SuggestedAuthor { get; set; }
    }

    /// <summary>
    /// Represents a file that was matched but could not be applied to the library database.
    /// The file can still be persisted visibly as EditionId=0 without relabeling its import history as Unmapped.
    /// </summary>
    public class FailedFile
    {
        public string FilePath { get; set; }
        public string Reason { get; set; }
    }
}
