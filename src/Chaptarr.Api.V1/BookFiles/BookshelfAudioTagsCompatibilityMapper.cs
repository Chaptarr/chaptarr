using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Api.V1.BookFiles
{
    internal static class BookshelfAudioTagsCompatibilityMapper
    {
        private static readonly Regex TrackNumberSeparator = new(@"[-,;\s]+", RegexOptions.Compiled);

        public static ParsedTrackInfo ToParsedTrackInfo(BookFile file, IDictionary<string, List<string>> tags)
        {
            var mediaInfo = file?.MediaInfo;
            return ToParsedTrackInfo(tags, file?.Quality, mediaInfo, file?.DurationSeconds);
        }

        public static ParsedTrackInfo ToParsedTrackInfo(
            IDictionary<string, List<string>> tags,
            NzbDrone.Core.Qualities.QualityModel quality,
            MediaInfoModel mediaInfo,
            int? durationSeconds = null)
        {
            var title = HasKey(tags, BookshelfAudioTagFields.Title)
                ? FirstIncludingEmpty(tags, BookshelfAudioTagFields.Title)
                : FirstIncludingEmpty(tags, "TITLE") ?? First(tags, "TITLESORT", "TITLE_SORT", "TITLE SORT");
            var book = HasKey(tags, BookshelfAudioTagFields.Book)
                ? FirstIncludingEmpty(tags, BookshelfAudioTagFields.Book)
                : FirstIncludingEmpty(tags, "ALBUM") ?? First(tags, "ALBUMSORT", "ALBUM_SORT", "ALBUM SORT");
            var bookTitle = string.IsNullOrWhiteSpace(book) ? title : book;

            return new ParsedTrackInfo
            {
                Title = title,
                CleanTitle = title?.CleanTrackTitle(),
                Authors = Values(tags, "ALBUMARTIST", "ALBUM ARTIST", "ARTIST", "AUTHORS", "AUTHOR")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                BookTitle = bookTitle,
                SeriesTitle = First(tags, "SERIES", "SERIES_TITLE"),
                SeriesIndex = First(tags, "SERIESNUMBER", "SERIESINDEX", "PARTNUMBER"),
                Isbn = First(tags, "ISBN"),
                Asin = First(tags, "ASIN", "AUDIBLE_ASIN"),
                GoodreadsId = First(tags, "GOODREADS_ID", "GOODREADSID", "GOODREADS"),
                AuthorMBId = First(tags, "MUSICBRAINZ_ALBUMARTISTID", "MUSICBRAINZ_ALBUMARTIST_ID", "MUSICBRAINZ_ARTISTID"),
                BookMBId = First(tags, "MUSICBRAINZ_ALBUMID", "MUSICBRAINZ_ALBUM_ID"),
                ReleaseMBId = First(tags, "MUSICBRAINZ_RELEASEGROUPID", "MUSICBRAINZ_RELEASE_GROUP_ID"),
                RecordingMBId = First(tags, "MUSICBRAINZ_RECORDINGID", "MUSICBRAINZ_RECORDING_ID"),
                TrackMBId = First(tags, "MUSICBRAINZ_TRACKID", "MUSICBRAINZ_TRACK_ID"),
                DiscNumber = ParseInt(First(tags, "DISCNUMBER", "DISC")),
                DiscCount = ParseInt(First(tags, "TOTALDISCS", "DISCTOTAL", "DISCCOUNT")),
                Country = ParseCountry(First(tags, "COUNTRY")),
                Year = ParseYear(First(tags, "DATE", "YEAR")),
                Narrator = First(tags, "NARRATOR"),
                Publisher = First(tags, "PUBLISHER"),
                Label = First(tags, "PUBLISHER", "LABEL", "ORGANIZATION"),
                Source = First(tags, "MEDIA", "SOURCE"),
                CatalogNumber = First(tags, "CATALOGNUMBER", "CATALOG_NUMBER"),
                Disambiguation = First(tags, "DISAMBIGUATION"),
                Duration = mediaInfo?.Duration ?? (durationSeconds.HasValue ? TimeSpan.FromSeconds(durationSeconds.Value) : TimeSpan.Zero),
                Quality = quality,
                MediaInfo = mediaInfo,
                TrackNumbers = ParseTrackNumbers(Values(tags, "TRACKNUMBER", "TRACK", "TRACKNUMBERS")),
                Language = First(tags, "LANGUAGE"),
                ReleaseGroup = First(tags, "RELEASEGROUP", "RELEASE_GROUP"),
                ReleaseHash = First(tags, "RELEASEHASH", "RELEASE_HASH")
            };
        }

        private static IEnumerable<string> Values(IDictionary<string, List<string>> tags, params string[] keys)
        {
            if (tags == null)
            {
                return Enumerable.Empty<string>();
            }

            return keys.SelectMany(key => tags
                .Where(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                .SelectMany(pair => pair.Value ?? new List<string>()))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim());
        }

        private static string First(IDictionary<string, List<string>> tags, params string[] keys)
        {
            return Values(tags, keys).FirstOrDefault();
        }

        private static string FirstIncludingEmpty(IDictionary<string, List<string>> tags, params string[] keys)
        {
            if (tags == null)
            {
                return null;
            }

            foreach (var key in keys)
            {
                var pair = tags.FirstOrDefault(entry => string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase));
                if (pair.Key != null && pair.Value?.Count > 0)
                {
                    return pair.Value[0];
                }
            }

            return null;
        }

        private static bool HasKey(IDictionary<string, List<string>> tags, string key)
        {
            return tags?.Keys.Any(candidate => string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase)) == true;
        }

        private static int ParseInt(string value)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
                ? result
                : 0;
        }

        private static uint ParseYear(string value)
        {
            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date))
            {
                return (uint)date.Year;
            }

            var yearText = value?.Trim();
            return yearText?.Length >= 4 && uint.TryParse(yearText.Substring(0, 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var year)
                ? year
                : 0;
        }

        private static IsoCountry ParseCountry(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var country = value.Trim();
            return country.Length == 2
                ? new IsoCountry(country.ToUpperInvariant(), null)
                : new IsoCountry(null, country);
        }

        private static int[] ParseTrackNumbers(IEnumerable<string> values)
        {
            var result = new List<int>();
            foreach (var value in values)
            {
                // A single tag may use "track/total". The old ParsedTrackInfo contract
                // reports track numbers, not the total-track count.
                var track = value.Split('/')[0];
                foreach (var part in TrackNumberSeparator.Split(track))
                {
                    if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number > 0)
                    {
                        result.Add(number);
                    }
                }
            }

            return result.Distinct().ToArray();
        }
    }
}
