using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Parser.Model
{
    /// <summary>
    /// Legacy BookshelfNG response shape for the <c>bookfile.audioTags</c> API field.
    /// New code uses the richer, field-agnostic tag dictionary on BookFileResource.
    /// </summary>
    public class ParsedTrackInfo
    {
        public string Title { get; set; }
        public string CleanTitle { get; set; }
        public List<string> Authors { get; set; } = new();
        public string AuthorTitle => Authors.FirstOrDefault();
        public string BookTitle { get; set; }
        public string SeriesTitle { get; set; }
        public string SeriesIndex { get; set; }
        public string Isbn { get; set; }
        public string Asin { get; set; }
        public string GoodreadsId { get; set; }
        public string AuthorMBId { get; set; }
        public string BookMBId { get; set; }
        public string ReleaseMBId { get; set; }
        public string RecordingMBId { get; set; }
        public string TrackMBId { get; set; }
        public int DiscNumber { get; set; }
        public int DiscCount { get; set; }
        public IsoCountry Country { get; set; }
        public uint Year { get; set; }
        public string Narrator { get; set; }
        public string Publisher { get; set; }
        public string Label { get; set; }
        public string Source { get; set; }
        public string CatalogNumber { get; set; }
        public string Disambiguation { get; set; }
        public TimeSpan Duration { get; set; }
        public QualityModel Quality { get; set; }
        public MediaInfoModel MediaInfo { get; set; }
        public int[] TrackNumbers { get; set; } = Array.Empty<int>();
        public string Language { get; set; }
        public string ReleaseGroup { get; set; }
        public string ReleaseHash { get; set; }
    }
}
