using System.Collections.Generic;
using System.Linq;
using Chaptarr.Http.REST;

namespace Chaptarr.Api.V1.Books
{
    public class ConvertBookResource : RestResource
    {
        public int AuthorId { get; set; }
        public int BookId { get; set; }
        public int EditionId { get; set; }
        public int BookFileId { get; set; }
        public string BookTitle { get; set; }
        public string Path { get; set; }
        public long Size { get; set; }
        public string SourceQuality { get; set; }
        public string TargetQuality { get; set; }
        public bool CanConvert { get; set; }
        public string Reason { get; set; }
    }

    public static class ConvertBookResourceMapper
    {
        public static ConvertBookResource ToResource(this NzbDrone.Core.MediaFiles.BookFileConversionPreview model)
        {
            if (model == null)
            {
                return null;
            }

            return new ConvertBookResource
            {
                Id = model.BookFileId,
                AuthorId = model.AuthorId,
                BookId = model.BookId,
                EditionId = model.EditionId,
                BookFileId = model.BookFileId,
                BookTitle = model.BookTitle,
                Path = model.Path,
                Size = model.Size,
                SourceQuality = model.SourceQuality,
                TargetQuality = model.TargetQuality,
                CanConvert = model.CanConvert,
                Reason = model.Reason
            };
        }

        public static List<ConvertBookResource> ToResource(this IEnumerable<NzbDrone.Core.MediaFiles.BookFileConversionPreview> models)
        {
            return models.Select(ToResource).ToList();
        }
    }
}
