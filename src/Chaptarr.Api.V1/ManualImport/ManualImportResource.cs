using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.Author;
using Chaptarr.Api.V1.Books;
using Chaptarr.Api.V1.CustomFormats;
using Chaptarr.Http.REST;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.MediaFiles.BookImport.Manual;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Api.V1.ManualImport
{
    public class ManualImportResource : RestResource
    {
        public string Path { get; set; }
        public string Name { get; set; }
        public long Size { get; set; }
        public AuthorResource Author { get; set; }
        public BookResource Book { get; set; }
        public EditionResource Edition { get; set; }
        public int? EditionId { get; set; }
        public string ForeignEditionId { get; set; }
        public string SuggestedForeignAuthorId { get; set; }
        public string SuggestedAuthorName { get; set; }
        public string SuggestedForeignBookId { get; set; }
        public string SuggestedBookTitle { get; set; }
        public string SuggestedForeignEditionId { get; set; }
        public string SuggestedEditionTitle { get; set; }
        public List<CustomFormatResource> CustomFormats { get; set; }
        public QualityModel Quality { get; set; }
        public string ReleaseGroup { get; set; }
        public int QualityWeight { get; set; }
        public string DownloadId { get; set; }
        public int IndexerFlags { get; set; }
        public IEnumerable<Rejection> Rejections { get; set; }
        public List<string> Warnings { get; set; }
        public Dictionary<string, List<string>> Tags { get; set; }
        public bool AdditionalFile { get; set; }
        public bool ReplaceExistingFiles { get; set; }
        public bool DisableReleaseSwitching { get; set; }
    }

    public static class ManualImportResourceMapper
    {
        public static ManualImportResource ToResource(this ManualImportItem model)
        {
            if (model == null)
            {
                return null;
            }

            // Best-guess preselection for the GUI (user can override before import).
            var suggestedEdition = model.Edition
                ?? model.Book?.Editions?.FirstOrDefault(x => x.Monitored)
                ?? model.Book?.Editions?.FirstOrDefault();
            var suggestedForeignEditionId = BookEditionIdentity.GetTrustedForeignEditionId(suggestedEdition)
                ?? model.SuggestedForeignEditionId;

            return new ManualImportResource
            {
                Id = model.Id,
                Path = model.Path,
                Name = model.Name,
                Size = model.Size,
                Author = model.Author?.ToResource(),
                Book = model.Book?.ToResource(),
                Edition = model.Edition?.ToResource(),
                EditionId = suggestedEdition?.Id > 0 ? suggestedEdition.Id : (int?)null,
                ForeignEditionId = suggestedForeignEditionId,
                SuggestedForeignAuthorId = model.SuggestedForeignAuthorId,
                SuggestedAuthorName = model.SuggestedAuthorName,
                SuggestedForeignBookId = model.SuggestedForeignBookId,
                SuggestedBookTitle = model.SuggestedBookTitle,
                SuggestedForeignEditionId = model.SuggestedForeignEditionId,
                CustomFormats = (model.CustomFormats ?? new List<NzbDrone.Core.CustomFormats.CustomFormat>()).ToResource(false),
                SuggestedEditionTitle = model.SuggestedEditionTitle,
                Quality = model.Quality,
                ReleaseGroup = model.ReleaseGroup,

                //QualityWeight
                DownloadId = model.DownloadId,
                IndexerFlags = model.IndexerFlags,
                Rejections = model.Rejections ?? new List<Rejection>(),
                Warnings = model.Warnings ?? new List<string>(),
                Tags = model.Tags ?? new Dictionary<string, List<string>>(),

                AdditionalFile = model.AdditionalFile,
                ReplaceExistingFiles = model.ReplaceExistingFiles,
                DisableReleaseSwitching = model.DisableReleaseSwitching
            };
        }

        public static List<ManualImportResource> ToResource(this IEnumerable<ManualImportItem> models)
        {
            return models.Select(ToResource).ToList();
        }
    }
}
