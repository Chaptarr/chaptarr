using System.Collections.Generic;
using System.IO.Abstractions;
using System.Threading;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles.BookImport.Identification
{
    // Contracts used by manual/interactive import flows.
    public interface IMakeImportDecision 
    {
        List<ImportDecision<LocalBook>> GetImportDecisions(List<IFileInfo> bookFiles, IdentificationOverrides idOverrides, ImportDecisionMakerInfo itemInfo, ImportDecisionMakerConfig config, CancellationToken cancellationToken = default);
    }

    public class IdentificationOverrides
    {
        public Author Author { get; set; }
        public Book Book { get; set; }
        public Edition Edition { get; set; }
    }

    public class ImportDecisionMakerInfo
    {
        public DownloadClientItem DownloadClientItem { get; set; }
        public ParsedBookInfo ParsedBookInfo { get; set; }
    }

    public class ImportDecisionMakerConfig
    {
        public FilterFilesType Filter { get; set; }
        public bool NewDownload { get; set; }
        public bool SingleRelease { get; set; }
        public bool IncludeExisting { get; set; }
        public bool AddNewAuthors { get; set; }
        public bool KeepAllEditions { get; set; }
    }
}
