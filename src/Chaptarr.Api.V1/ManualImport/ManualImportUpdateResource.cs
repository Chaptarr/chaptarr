using System.Collections.Generic;
using Chaptarr.Http.REST;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Api.V1.ManualImport
{
    public class ManualImportUpdateResource : RestResource
    {
        public string Path { get; set; }
        public string Name { get; set; }
        public int? AuthorId { get; set; }
        public int? BookId { get; set; }
        public int? EditionId { get; set; }
        public string SuggestedForeignAuthorId { get; set; }
        public string SuggestedAuthorName { get; set; }
        public string SuggestedForeignBookId { get; set; }
        public string SuggestedBookTitle { get; set; }
        public string SuggestedForeignEditionId { get; set; }
        public string SuggestedEditionTitle { get; set; }
        public QualityModel Quality { get; set; }
        public string ReleaseGroup { get; set; }
        public int IndexerFlags { get; set; }
        public string DownloadId { get; set; }
        public bool AdditionalFile { get; set; }
        public bool ReplaceExistingFiles { get; set; }
        public bool DisableReleaseSwitching { get; set; }

        public IEnumerable<Rejection> Rejections { get; set; }
        public List<string> Warnings { get; set; }
    }
}
