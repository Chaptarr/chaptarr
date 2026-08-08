using System.Collections.Generic;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.MediaFiles.BookImport.Manual
{
    public class ManualImportItem : ModelBase
    {
        public ManualImportItem()
        {
            CustomFormats = new List<CustomFormat>();
            Rejections = new List<Rejection>();
            Warnings = new List<string>();
        }

        public string Path { get; set; }
        public string Name { get; set; }
        public long Size { get; set; }
        public Author Author { get; set; }
        public Book Book { get; set; }
        public Edition Edition { get; set; }
        public QualityModel Quality { get; set; }
        public string ReleaseGroup { get; set; }
        public string DownloadId { get; set; }
        public List<CustomFormat> CustomFormats { get; set; }
        public int IndexerFlags { get; set; }
        public IEnumerable<Rejection> Rejections { get; set; }
        public List<string> Warnings { get; set; }
        public Dictionary<string, List<string>> Tags { get; set; }
        public bool AdditionalFile { get; set; }
        public bool ReplaceExistingFiles { get; set; }
        public bool DisableReleaseSwitching { get; set; }

        // Suggest-only metadata (no DB side effects). Used to show a best-guess for new authors on initial load.
        public string SuggestedForeignAuthorId { get; set; }
        public string SuggestedAuthorName { get; set; }
        public string SuggestedForeignBookId { get; set; }
        public string SuggestedBookTitle { get; set; }
        public string SuggestedForeignEditionId { get; set; }
        public string SuggestedEditionTitle { get; set; }
    }
}
