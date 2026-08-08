using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Chaptarr.Http.Middleware;
using Chaptarr.Http.REST;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaCover;
using Swashbuckle.AspNetCore.Annotations;

namespace Chaptarr.Api.V1.Books
{
    public class EditionChapterResource
    {
        public string Title { get; set; }
        public int StartOffsetMs { get; set; }
        public int StartOffsetSec { get; set; }
        public int LengthMs { get; set; }
    }

    public class EditionResource : RestResource
    {
        public int BookId { get; set; }
        public string ForeignEditionId { get; set; }
        public string TitleSlug { get; set; }
        public string Isbn13 { get; set; }
        public string Isbn10 { get; set; }
        public string Asin { get; set; }
        public List<string> Asins { get; set; }
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public string Language { get; set; }
        public string Overview { get; set; }
        public string Format { get; set; }
        public bool IsEbook { get; set; }
        public string Disambiguation { get; set; }
        public string Publisher { get; set; }
        public int PageCount { get; set; }
        public DateTime? ReleaseDate { get; set; }
        public List<MediaCover> Images { get; set; }
        public List<Links> Links { get; set; }
        public Ratings Ratings { get; set; }
        public bool Monitored { get; set; }
        public bool ManualAdd { get; set; }
        public long? GoodreadsEditionId { get; set; }
        public string HardcoverEditionId { get; set; }
        public string OpenLibraryEditionId { get; set; }
        public int? ReadingFormatId { get; set; }
        public string EditionFormat { get; set; }
        public string EditionInfo { get; set; }
        public string AudibleASIN { get; set; }
        public string GoogleBooksEditionId { get; set; }
        public int BookFileCount { get; set; }
        public bool MonitoredByAnotherAudiobookBook { get; set; }
        public string RemoteCover { get; set; }
        public string Narrator { get; set; }
        public List<string> NarratorNames { get; set; }
        public int? DurationSeconds { get; set; }
        public int? ChapterCount { get; set; }
        public bool HasChapters { get; set; }
        public int? ReviewCount { get; set; }
        public List<EditionChapterResource> Chapters { get; set; }

        //Hiding this so people don't think its usable (only used to set the initial state)
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        [SwaggerIgnore]
        public bool Grabbed { get; set; }
    }

    public static class EditionResourceMapper
    {
        public static EditionResource ToResource(this Edition model)
        {
            return model.ToResource(null);
        }

        public static EditionResource ToResource(this Edition model, ReadarrFacadeContext facadeContext)
        {
            if (model == null)
            {
                return null;
            }

            return new EditionResource
            {
                Id = model.Id,
                BookId = model.BookId,
                ForeignEditionId = ReadarrFacadeEditionIdentity.BuildForeignEditionId(model, facadeContext),
                TitleSlug = model.TitleSlug,
                Isbn13 = model.Isbn13,
                Isbn10 = model.Isbn10,
                Asin = model.Asin,
                Asins = model.Asins?.ToList() ?? new List<string>(),
                Title = model.Title,
                Subtitle = model.Subtitle,
                Language = model.Language,
                Overview = model.Overview,
                Format = model.Format,
                IsEbook = model.IsEbook,
                Disambiguation = model.Disambiguation,
                Publisher = model.Publisher,
                PageCount = model.PageCount,
                ReleaseDate = model.ReleaseDate,
                Images = model.Images?.Select(image => new MediaCover(image.CoverType, image.Url)
                {
                    Hash = image.Hash
                }).ToList() ?? new List<MediaCover>(),
                Links = model.Links,
                Ratings = model.Ratings,
                Monitored = model.Monitored,
                ManualAdd = model.ManualAdd,
                GoodreadsEditionId = model.GoodreadsEditionId,
                HardcoverEditionId = model.HardcoverEditionId,
                OpenLibraryEditionId = model.OpenLibraryEditionId,
                ReadingFormatId = model.ReadingFormatId,
                EditionFormat = model.EditionFormat,
                EditionInfo = model.EditionInfo,
                AudibleASIN = model.AudibleASIN,
                GoogleBooksEditionId = model.GoogleBooksEditionId,
                BookFileCount = model.BookFiles?.Count ?? 0,
                Narrator = model.Narrator,
                NarratorNames = model.NarratorNames ?? new List<string>(),
                DurationSeconds = model.DurationSeconds,
                ChapterCount = model.ChapterCount,
                HasChapters = model.HasChapters,
                ReviewCount = model.ReviewCount,
                Chapters = model.Chapters?.Select(c => new EditionChapterResource
                {
                    Title = c?.Title,
                    StartOffsetMs = c?.StartOffsetMs ?? 0,
                    StartOffsetSec = c?.StartOffsetSec ?? 0,
                    LengthMs = c?.LengthMs ?? 0
                }).ToList() ?? new List<EditionChapterResource>()
            };
        }

        public static Edition ToModel(this EditionResource resource)
        {
            return resource.ToModel(null);
        }

        public static Edition ToModel(this EditionResource resource, ReadarrFacadeContext facadeContext)
        {
            if (resource == null)
            {
                return null;
            }

            var foreignEditionId = resource.ForeignEditionId;
            var goodreadsEditionId = resource.GoodreadsEditionId;
            var hardcoverEditionId = resource.HardcoverEditionId;

            if (facadeContext?.IsGoodreads == true &&
                !string.IsNullOrWhiteSpace(foreignEditionId) &&
                long.TryParse(foreignEditionId.Trim(), out var bareGoodreadsEditionId))
            {
                goodreadsEditionId ??= bareGoodreadsEditionId;
                foreignEditionId = "gr:" + bareGoodreadsEditionId;
            }
            else if (facadeContext?.IsHardcover == true &&
                     !string.IsNullOrWhiteSpace(foreignEditionId) &&
                     long.TryParse(foreignEditionId.Trim(), out _))
            {
                hardcoverEditionId ??= foreignEditionId.Trim();
                foreignEditionId = "hc:edition:" + foreignEditionId.Trim();
            }

            return new Edition
            {
                Id = resource.Id,
                BookId = resource.BookId,
                ForeignEditionId = foreignEditionId,
                TitleSlug = resource.TitleSlug,
                Isbn13 = resource.Isbn13,
                Isbn10 = resource.Isbn10,
                Asin = resource.Asin,
                Asins = resource.Asins?.ToList() ?? new List<string>(),
                Title = resource.Title,
                Subtitle = resource.Subtitle,
                Language = resource.Language,
                Overview = resource.Overview,
                Format = resource.Format,
                IsEbook = resource.IsEbook,
                Disambiguation = resource.Disambiguation,
                Publisher = resource.Publisher,
                PageCount = resource.PageCount,
                ReleaseDate = resource.ReleaseDate,
                Images = resource.Images,
                Links = resource.Links,
                Ratings = resource.Ratings,
                Monitored = resource.Monitored,
                ManualAdd = resource.ManualAdd,
                GoodreadsEditionId = goodreadsEditionId,
                HardcoverEditionId = hardcoverEditionId,
                OpenLibraryEditionId = resource.OpenLibraryEditionId,
                ReadingFormatId = resource.ReadingFormatId,
                EditionFormat = resource.EditionFormat,
                EditionInfo = resource.EditionInfo,
                AudibleASIN = resource.AudibleASIN,
                GoogleBooksEditionId = resource.GoogleBooksEditionId,
                Narrator = resource.Narrator,
                NarratorNames = resource.NarratorNames ?? new List<string>(),
                DurationSeconds = resource.DurationSeconds,
                ChapterCount = resource.ChapterCount,
                HasChapters = resource.HasChapters,
                ReviewCount = resource.ReviewCount,
                Chapters = resource.Chapters?.Select(c => new EditionChapter
                {
                    Title = c?.Title,
                    StartOffsetMs = c?.StartOffsetMs ?? 0,
                    StartOffsetSec = c?.StartOffsetSec ?? 0,
                    LengthMs = c?.LengthMs ?? 0
                }).ToList() ?? new List<EditionChapter>()
            };
        }

        public static List<EditionResource> ToResource(this IEnumerable<Edition> models)
        {
            return models?.Select(ToResource).ToList();
        }

        public static List<EditionResource> ToResource(this IEnumerable<Edition> models, ReadarrFacadeContext facadeContext)
        {
            if (models == null)
            {
                return null;
            }

            return ReadarrFacadeEditionIdentity
                .FilterAddressableEditions(models, facadeContext, "edition response")
                .Select(model => model.ToResource(facadeContext))
                .ToList();
        }

        public static List<Edition> ToModel(this IEnumerable<EditionResource> resources)
        {
            return resources.Select(ToModel).ToList();
        }

        public static List<Edition> ToModel(this IEnumerable<EditionResource> resources, ReadarrFacadeContext facadeContext)
        {
            return resources.Select(resource => resource.ToModel(facadeContext)).ToList();
        }

    }
}
