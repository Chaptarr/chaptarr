using System;
using System.Collections.Generic;
using Chaptarr.Api.V1.Author;
using Chaptarr.Api.V1.Books;
using Chaptarr.Api.V1.CustomFormats;
using Chaptarr.Http.REST;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.History;
using NzbDrone.Core.Books;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Api.V1.History
{
    public class HistoryResource : RestResource
    {
        public int BookId { get; set; }
        public int AuthorId { get; set; }
        public string SourceTitle { get; set; }
        public QualityModel Quality { get; set; }
        public List<CustomFormatResource> CustomFormats { get; set; }
        public int CustomFormatScore { get; set; }
        public bool QualityCutoffNotMet { get; set; }
        public DateTime Date { get; set; }
        public string DownloadId { get; set; }

        public EntityHistoryEventType EventType { get; set; }

        public Dictionary<string, string> Data { get; set; }

        public BookResource Book { get; set; }
        public AuthorResource Author { get; set; }
    }

    public static class HistoryResourceMapper
    {
        public static HistoryResource ToResource(this EntityHistory model, ICustomFormatCalculationService formatCalculator)
        {
            if (model == null)
            {
                return null;
            }

            var quality = ResolveDisplayQuality(model);
            var customFormats = model.Author == null
                ? new List<CustomFormat>()
                : formatCalculator.ParseCustomFormat(model, model.Author);
            var qualityProfile = model.Author?.GetQualityProfileForQuality(quality.Quality);
            var customFormatScore = qualityProfile?.CalculateCustomFormatScore(customFormats) ?? 0;

            return new HistoryResource
            {
                Id = model.Id,

                BookId = model.BookId,
                AuthorId = model.AuthorId,
                SourceTitle = model.SourceTitle,
                Quality = quality,
                CustomFormats = customFormats.ToResource(false),
                CustomFormatScore = customFormatScore,

                //QualityCutoffNotMet
                Date = model.Date,
                DownloadId = model.DownloadId,

                EventType = model.EventType,

                Data = model.Data

                //Episode
                //Series
            };
        }

        private static QualityModel ResolveDisplayQuality(EntityHistory model)
        {
            var quality = model.Quality ?? new QualityModel();

            if (quality.Quality == Quality.Unknown &&
                model.Book?.MediaType == BookMediaType.Audiobook)
            {
                return new QualityModel(Quality.UnknownAudio, quality.Revision);
            }

            return quality;
        }
    }
}
