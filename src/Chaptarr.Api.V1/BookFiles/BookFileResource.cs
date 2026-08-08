using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Http.REST;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport.Services;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Api.V1.BookFiles
{
    public class BookFileResource : RestResource
    {
        public int AuthorId { get; set; }
        public int BookId { get; set; }
        public string Path { get; set; }
        public long Size { get; set; }
        public DateTime DateAdded { get; set; }
        public QualityModel Quality { get; set; }
        public int QualityWeight { get; set; }
        public int? IndexerFlags { get; set; }
        public MediaInfoResource MediaInfo { get; set; }
        public string MediaType { get; set; }
        public string Narrator { get; set; }
        public Dictionary<string, List<string>> Tags { get; set; }
        public MatchProvenance MatchProvenance { get; set; }
        public string ImportUnitKey { get; set; }
        public string ImportUnitRoot { get; set; }

        public bool QualityCutoffNotMet { get; set; }
        // Removed: ParsedTrackInfo AudioTags (field-agnostic import no longer exposes tags here)
    }

    public static class BookFileResourceMapper
    {
        private static int QualityWeight(QualityModel quality)
        {
            if (quality == null)
            {
                return 0;
            }

            var qualityWeight = Quality.DefaultQualityDefinitions.Single(q => q.Quality == quality.Quality).Weight;
            qualityWeight += quality.Revision.Real * 10;
            qualityWeight += quality.Revision.Version;
            return qualityWeight;
        }

        public static BookFileResource ToResource(this BookFile model)
        {
            if (model == null)
            {
                return null;
            }

            return new BookFileResource
            {
                Id = model.Id,
                BookId = model.Edition?.BookId ?? 0,
                Path = model.Path,
                Size = model.Size,
                DateAdded = model.DateAdded,
                Quality = model.Quality,
                QualityWeight = QualityWeight(model.Quality),
                MediaInfo = model.MediaInfo.ToResource(),
                MediaType = model.MediaType,
                Narrator = model.Narrator,
                MatchProvenance = model.MatchProvenance,
                ImportUnitKey = model.EditionId == 0 ? BookImportUnitGroupingService.BuildFallbackUnitKey(model) : null,
                ImportUnitRoot = model.EditionId == 0 ? BookImportUnitGroupingService.GetFallbackUnitRoot(model.Path) : null
            };
        }

        public static BookFileResource ToResource(this BookFile model, NzbDrone.Core.Books.Author author, IUpgradableSpecification upgradableSpecification)
        {
            if (model == null)
            {
                return null;
            }

            return new BookFileResource
            {
                Id = model.Id,

                AuthorId = author.Id,
                BookId = model.Edition?.BookId ?? 0,
                Path = model.Path,
                Size = model.Size,
                DateAdded = model.DateAdded,
                Quality = model.Quality,
                QualityWeight = QualityWeight(model.Quality),
                MediaInfo = model.MediaInfo.ToResource(),
                MediaType = model.MediaType,
                Narrator = model.Narrator,
                MatchProvenance = model.MatchProvenance,
                QualityCutoffNotMet = author.GetQualityProfileForQuality(model.Quality.Quality) != null ? upgradableSpecification.QualityCutoffNotMet(author.GetQualityProfileForQuality(model.Quality.Quality), model.Quality) : false,
                IndexerFlags = (int)model.IndexerFlags,
                ImportUnitKey = model.EditionId == 0 ? BookImportUnitGroupingService.BuildFallbackUnitKey(model) : null,
                ImportUnitRoot = model.EditionId == 0 ? BookImportUnitGroupingService.GetFallbackUnitRoot(model.Path) : null
            };
        }
    }
}
