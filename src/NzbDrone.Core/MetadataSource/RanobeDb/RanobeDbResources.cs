using System.Collections.Generic;
using Newtonsoft.Json;

namespace NzbDrone.Core.MetadataSource.RanobeDb
{
    internal sealed class RanobeDbBooksResponse
    {
        [JsonProperty("books")]
        public List<RanobeDbBookSummary> Books { get; set; }

        [JsonProperty("count")]
        public string Count { get; set; }

        [JsonProperty("totalPages")]
        public int TotalPages { get; set; }
    }

    internal class RanobeDbBookSummary
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("lang")]
        public string Language { get; set; }

        [JsonProperty("romaji")]
        public string Romaji { get; set; }

        [JsonProperty("title_orig")]
        public string OriginalTitle { get; set; }

        [JsonProperty("romaji_orig")]
        public string OriginalRomaji { get; set; }

        [JsonProperty("c_release_date")]
        public int CurrentReleaseDate { get; set; }
    }

    internal sealed class RanobeDbBookResponse
    {
        [JsonProperty("book")]
        public RanobeDbBookDetail Book { get; set; }
    }

    internal sealed class RanobeDbBookDetail : RanobeDbBookSummary
    {
        [JsonProperty("hidden")]
        public bool Hidden { get; set; }

        [JsonProperty("editions")]
        public List<RanobeDbEdition> Editions { get; set; } = new List<RanobeDbEdition>();

        [JsonProperty("releases")]
        public List<RanobeDbRelease> Releases { get; set; } = new List<RanobeDbRelease>();

        [JsonProperty("publishers")]
        public List<RanobeDbPublisher> Publishers { get; set; } = new List<RanobeDbPublisher>();

        [JsonProperty("series")]
        public RanobeDbSeries Series { get; set; }
    }

    internal sealed class RanobeDbEdition
    {
        [JsonProperty("eid")]
        public int Id { get; set; }

        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("lang")]
        public string Language { get; set; }

        [JsonProperty("staff")]
        public List<RanobeDbStaffCredit> Staff { get; set; } = new List<RanobeDbStaffCredit>();
    }

    internal sealed class RanobeDbStaffCredit
    {
        [JsonProperty("role_type")]
        public string Role { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("romaji")]
        public string Romaji { get; set; }

        [JsonProperty("staff_id")]
        public int StaffId { get; set; }
    }

    internal sealed class RanobeDbRelease
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("lang")]
        public string Language { get; set; }

        [JsonProperty("format")]
        public string Format { get; set; }

        [JsonProperty("hidden")]
        public bool Hidden { get; set; }

        [JsonProperty("release_date")]
        public int ReleaseDate { get; set; }

        [JsonProperty("release_date_parsed")]
        public string ReleaseDateParsed { get; set; }

        [JsonProperty("isbn13")]
        public string Isbn13 { get; set; }

        [JsonProperty("pages")]
        public int? Pages { get; set; }

        [JsonProperty("website")]
        public string Website { get; set; }

        [JsonProperty("amazon")]
        public string Amazon { get; set; }

        [JsonProperty("bookwalker")]
        public string BookWalker { get; set; }

        [JsonProperty("rakuten")]
        public string Rakuten { get; set; }

        [JsonProperty("books")]
        public List<RanobeDbReleaseBook> Books { get; set; } = new List<RanobeDbReleaseBook>();
    }

    internal sealed class RanobeDbReleaseBook
    {
        [JsonProperty("id")]
        public int Id { get; set; }
    }

    internal sealed class RanobeDbReleaseResponse
    {
        [JsonProperty("release")]
        public RanobeDbRelease Release { get; set; }
    }

    internal sealed class RanobeDbPublisher
    {
        [JsonProperty("name")]
        public string Name { get; set; }
    }

    internal sealed class RanobeDbSeries
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("books")]
        public List<RanobeDbSeriesBook> Books { get; set; } = new List<RanobeDbSeriesBook>();

        [JsonProperty("tags")]
        public List<RanobeDbTag> Tags { get; set; } = new List<RanobeDbTag>();
    }

    internal sealed class RanobeDbSeriesBook
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("sort_order")]
        public int? SortOrder { get; set; }
    }

    internal sealed class RanobeDbTag
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("ttype")]
        public string Type { get; set; }
    }

    internal sealed class RanobeDbStaffResponse
    {
        [JsonProperty("staff")]
        public RanobeDbStaffDetail Staff { get; set; }
    }

    internal sealed class RanobeDbStaffDetail
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("romaji")]
        public string Romaji { get; set; }

        [JsonProperty("hidden")]
        public bool Hidden { get; set; }
    }
}
