using System.Collections.Generic;
using System.Linq;
using Chaptarr.Http.REST;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.Books;
using NzbDrone.Core.MetadataSource;

namespace Chaptarr.Api.V1.Series
{
    public class SeriesResource : RestResource
    {
        public string ForeignSeriesId { get; set; }
        public string LocalSeriesId { get; set; }
        public string Title { get; set; }
        public string TitleSlug { get; set; }
        public string Description { get; set; }
        public int WorkCount { get; set; }
        public int PrimaryWorkCount { get; set; }
        public string SeriesType { get; set; }
        public int? ParentSeriesId { get; set; }
        public string MediaType { get; set; }
        public string Narrator { get; set; }
        public List<SeriesBookLinkResource> Links { get; set; }
        public List<MediaCover> Images { get; set; }
        public List<SeriesBookResource> Books { get; set; }
    }

    public static class SeriesResourceMapper
    {
        public static SeriesResource ToResource(this NzbDrone.Core.Books.Series model)
        {
            if (model == null)
            {
                return null;
            }

            var images = new List<MediaCover>();

            // Extract cover images from the first 3 books in the series
            if (model.Books?.Count > 0)
            {
                var bookCovers = model.Books
                    .Where(book => book.Editions?.Any(e => e.Images?.Any(img => img.CoverType == MediaCoverTypes.Cover) == true) == true)
                    .SelectMany(book => book.Editions
                        .Where(e => e.Images?.Any(img => img.CoverType == MediaCoverTypes.Cover) == true)
                        .Select(e => e.Images.First(img => img.CoverType == MediaCoverTypes.Cover)))
                    .Take(3)
                    .ToList();

                if (bookCovers.Any())
                {
                    images = CloneImages(bookCovers);
                }
            }

            // If we don't have Books populated (common for search results), fall back to SeriesBooks cover URLs.
            if (!images.Any() && model.SeriesBooks?.Any(b => !string.IsNullOrWhiteSpace(b.CoverUrl)) == true)
            {
                images = model.SeriesBooks
                    .Where(b => !string.IsNullOrWhiteSpace(b.CoverUrl))
                    .Take(3)
                    .Select(b => new MediaCover
                    {
                        CoverType = MediaCoverTypes.Cover,
                        Url = b.CoverUrl
                    })
                    .ToList();
            }

            // If Books collection is null but LinkItems exist, use LinkItems to populate Books
            var books = model.Books;
            if ((books == null || !books.Any()) && model.LinkItems?.Any() == true)
            {
                books = model.LinkItems
                    .Where(l => l.Book?.IsLoaded == true)
                    .Select(l => l.Book.Value)
                    .Where(b => b != null)
                    .ToList();
            }

            var foreignSeriesId = GetPreferredSeriesProviderId(model);

            return new SeriesResource
            {
                Id = model.Id,
                ForeignSeriesId = foreignSeriesId,
                LocalSeriesId = model.Id.ToString(),
                Title = model.DisplayTitle,
                TitleSlug = model.TitleSlug,
                Description = model.Description,
                WorkCount = model.WorkCount,
                PrimaryWorkCount = model.PrimaryWorkCount,
                SeriesType = model.SeriesType,
                ParentSeriesId = model.ParentSeriesId,
                MediaType = model.MediaType == BookMediaType.Audiobook ? "audiobook" : "ebook",
                Narrator = model.Narrator,
                Links = model.LinkItems?.ToResource() ?? new List<SeriesBookLinkResource>(),
                Images = images,
                Books = books?.Select(b => new SeriesBookResource
                {
                    ForeignBookId = BookEditionIdentity.GetCanonicalWorkProviderIds(b).FirstOrDefault(),
                    Title = b.Title,
                    AuthorName = b.Author?.Name,
                    ReleaseDate = b.ReleaseDate,
                    Position = model.LinkItems?.FirstOrDefault(l => l.BookId == b.Id)?.Position ?? "",
                    Images = CloneImages(b.Editions?.FirstOrDefault()?.Images),
                    Ratings = b.Ratings
                }).ToList() ?? new List<SeriesBookResource>()
            };
        }

        public static List<SeriesResource> ToResource(this IEnumerable<NzbDrone.Core.Books.Series> models)
        {
            return models?.Select(ToResource).ToList();
        }

        private static List<MediaCover> CloneImages(IEnumerable<MediaCover> images)
        {
            return images?.Select(image => new MediaCover(image.CoverType, image.Url)
            {
                Hash = image.Hash
            }).ToList() ?? new List<MediaCover>();
        }

        private static string GetPreferredSeriesProviderId(NzbDrone.Core.Books.Series series)
        {
            return NormalizeSeriesProviderId(series?.GoodreadsSeriesId, "gr") ??
                   NormalizeSeriesProviderId(series?.AmazonSeriesAsin, "az") ??
                   NormalizeSeriesProviderId(series?.HardcoverSeriesId, "hc") ??
                   NormalizeSeriesProviderId(series?.OpenLibrarySeriesId, "ol");
        }

        private static string NormalizeSeriesProviderId(string providerId, string defaultPrefix)
        {
            return string.IsNullOrWhiteSpace(providerId)
                ? null
                : ProviderIdHelper.Normalize(providerId, defaultPrefix);
        }
    }
}
