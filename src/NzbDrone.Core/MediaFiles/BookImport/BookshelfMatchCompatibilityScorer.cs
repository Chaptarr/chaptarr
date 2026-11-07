using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser;

namespace NzbDrone.Core.MediaFiles.BookImport
{
    /// <summary>
    /// Reproduces BookshelfNG's weighted release-to-edition score for converted installs.
    /// The normal ChaptarrNG matcher still applies its stronger identity and ambiguity checks;
    /// this score restores the legacy minimum-percent acceptance gate after those checks pass.
    /// </summary>
    public static class BookshelfMatchCompatibilityScorer
    {
        private static readonly IReadOnlyDictionary<string, double> Weights = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["author"] = 3.0,
            ["book"] = 3.0,
            ["isbn"] = 10.0,
            ["isbn_missing"] = 0.1,
            ["asin"] = 10.0,
            ["asin_missing"] = 0.1,
            ["ebook_format"] = 0.1,
            ["audio_format"] = 0.1,
            ["wrong_format"] = 5.0,
            ["year"] = 1.0,
            ["language"] = 5.0,
            ["publisher"] = 0.5,
            ["narrator"] = 1.5,
            ["preferred_edition"] = 3.0
        };

        private static readonly Regex StripSeriesRegex = new Regex(@"\([^\)].+?\)$", RegexOptions.Compiled);
        private static readonly Regex CleanTitleCruft = new Regex(@"\((?:unabridged)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ValidAsinRegex = new Regex(@"^(B[\dA-Z]{9}|\d{9}[\dX])$", RegexOptions.Compiled);
        private static readonly Regex ValidIsbnRegex = new Regex(@"^(\d{9}[\dX]|\d{13})$", RegexOptions.Compiled);
        private static readonly Regex YearRegex = new Regex(@"\b(1\d{3}|2\d{3})\b", RegexOptions.Compiled);
        private static readonly string[] EbookFormats = { "Kindle Edition", "Nook", "ebook" };
        private static readonly string[] AudiobookFormats = { "Audiobook", "Audio CD", "Audio Cassette", "Audible Audio", "CD-ROM", "MP3 CD" };
        private static readonly string[] VariousAuthorTitles = { string.Empty, "various authors", "various", "va", "unknown" };
        private const string CalibreOverrideMarker = "BOOKSHELF_CALIBRE_OVERRIDE";
        private const string CalibreTitleOverride = "BOOKSHELF_CALIBRE_TITLE";
        private const string CalibreAuthorsOverride = "BOOKSHELF_CALIBRE_AUTHORS";
        private const string CalibreIsbnOverride = "BOOKSHELF_CALIBRE_ISBN";
        private const string CalibreAsinOverride = "BOOKSHELF_CALIBRE_ASIN";
        private const string CalibreGoodreadsOverride = "BOOKSHELF_CALIBRE_GOODREADS_ID";
        private const string BookshelfAudioBook = BookshelfAudioTagFields.Book;
        private const string BookshelfAudioTitle = BookshelfAudioTagFields.Title;

        internal static Dictionary<string, List<string>> ApplyCalibreMetadata(
            Dictionary<string, List<string>> sourceTags,
            string title,
            IEnumerable<string> authors,
            string isbn,
            string asin,
            string goodreadsId)
        {
            var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in sourceTags ?? new Dictionary<string, List<string>>())
            {
                tags[pair.Key] = pair.Value?.ToList() ?? new List<string>();
            }

            tags[CalibreOverrideMarker] = new List<string> { "true" };
            SetOverride(tags, CalibreTitleOverride, title);
            tags[CalibreAuthorsOverride] = (authors ?? Enumerable.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();
            SetOverride(tags, CalibreIsbnOverride, isbn);
            SetOverride(tags, CalibreAsinOverride, asin);
            SetOverride(tags, CalibreGoodreadsOverride, goodreadsId);
            return tags;
        }

        public static double GetMatchPercent(
            IReadOnlyList<Dictionary<string, List<string>>> trackTags,
            EditionFtsMatch candidate,
            Edition edition,
            Book book,
            BookMediaType mediaType,
            string preferredEditionTerms = null,
            IReadOnlyList<string> filePaths = null)
        {
            if (candidate == null && (edition == null || book == null))
            {
                return 0;
            }

            trackTags ??= Array.Empty<Dictionary<string, List<string>>>();
            var distance = new WeightedDistance();
            var fileAuthors = GetBookshelfRemoteAuthorSearchTerms(trackTags, mediaType, filePaths);
            var authorVariants = GetAuthorVariants(fileAuthors);
            var authorName = book?.Author?.Name ?? candidate?.AuthorName;
            distance.AddString("author", authorVariants, authorName);

            var fileTitle = GetMostCommonTitle(trackTags, mediaType, filePaths);
            var fileTitles = new[] { fileTitle, CleanTitleCruft.Replace(fileTitle ?? string.Empty, string.Empty) }
                .Where(value => value != null)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var editionTitle = edition?.Title ?? candidate?.EditionTitle;
            var titleOptions = new List<string>();
            if (!string.IsNullOrWhiteSpace(editionTitle))
            {
                titleOptions.Add(editionTitle);
                if (editionTitle.Contains("#"))
                {
                    titleOptions.Add(StripSeriesRegex.Replace(editionTitle, string.Empty));
                }

                var (mainTitle, _) = editionTitle.SplitBookTitle(authorName ?? string.Empty);
                if (!titleOptions.Contains(mainTitle))
                {
                    titleOptions.Add(mainTitle);
                }
            }

            foreach (var link in book?.SeriesLinks ?? new List<SeriesBookLink>())
            {
                var seriesTitle = link?.Series?.Value?.Title;
                if (string.IsNullOrWhiteSpace(seriesTitle))
                {
                    continue;
                }

                var position = link.Position;
                titleOptions.Add($"{seriesTitle} {position} {editionTitle}");
                titleOptions.Add($"{seriesTitle} Book {position} {editionTitle}");
                titleOptions.Add($"{editionTitle} {seriesTitle} {position}");
                titleOptions.Add($"{editionTitle} {seriesTitle} Book {position}");
            }

            if (titleOptions.Count == 0)
            {
                titleOptions.Add(candidate?.BookTitle ?? book?.Title ?? string.Empty);
            }
            distance.AddString("book", fileTitles, titleOptions);

            AddIdentifierDistance(
                distance,
                "isbn",
                "isbn_missing",
                GetMostCommonIdentifier(trackTags, "isbn", "isbn", "isbn13", "isbn10", "isbn-13", "isbn-10"),
                edition?.Isbn13,
                ValidIsbnRegex);
            AddIdentifierDistance(
                distance,
                "asin",
                "asin_missing",
                GetMostCommonIdentifier(trackTags, "asin", "asin", "audible_asin"),
                edition?.Asin,
                ValidAsinRegex);

            var localYear = GetMostCommonYear(trackTags, "date", "year");
            var releaseDate = edition?.ReleaseDate;
            if (localYear > 0 && releaseDate.HasValue)
            {
                var remoteYear = releaseDate.Value.Year;
                var difference = Math.Abs(localYear - remoteYear);
                var maximumDifference = Math.Abs(DateTime.Now.Year - remoteYear);
                distance.AddRatio("year", difference, maximumDifference);
            }

            var localLanguage = GetMostCommonSeparatedValue(trackTags, "language")?.CanonicalizeLanguage();
            var editionLanguage = edition?.Language?.CanonicalizeLanguage();
            if (!string.IsNullOrWhiteSpace(localLanguage) && !string.IsNullOrWhiteSpace(editionLanguage))
            {
                distance.AddBool("language", localLanguage != editionLanguage);
            }

            var localPublisher = GetMostCommonSeparatedValue(trackTags, "publisher");
            var editionPublisher = edition?.Publisher;
            if (!string.IsNullOrWhiteSpace(localPublisher) && !string.IsNullOrWhiteSpace(editionPublisher))
            {
                distance.AddString("publisher", localPublisher, editionPublisher);
            }

            if (mediaType == BookMediaType.Audiobook)
            {
                // BookshelfNG's AudioTag maps its explicit NARRATOR credit to FileTrackInfo.Narrator.
                // Performers are also included in the author list, so treating them as narrator
                // evidence would add a score penalty that the legacy matcher never applied.
                var fileNarrators = GetLegacyNarrators(trackTags);
                var editionNarrators = GetNarrators(new[] { edition?.Narrator });
                if (fileNarrators.Count > 0 && editionNarrators.Count > 0)
                {
                    distance.AddString("narrator", fileNarrators, editionNarrators);
                }
            }

            var format = edition?.Format;
            if (!string.IsNullOrWhiteSpace(format))
            {
                if (mediaType == BookMediaType.Ebook)
                {
                    distance.AddBool("ebook_format", !EbookFormats.Contains(format));
                    distance.AddBool("wrong_format", AudiobookFormats.Contains(format));
                }
                else
                {
                    distance.AddBool("audio_format", !AudiobookFormats.Contains(format));
                }
            }

            AddPreferredEditionDistance(distance, editionTitle, format, editionPublisher, preferredEditionTerms);
            return 100.0 * (1.0 - distance.NormalizedDistance());
        }

        internal static string GetBookshelfBookTitle(
            IReadOnlyList<Dictionary<string, List<string>>> trackTags,
            BookMediaType mediaType,
            IReadOnlyList<string> filePaths = null)
        {
            return GetMostCommonTitle(trackTags, mediaType, filePaths);
        }

        internal static List<string> GetBookshelfRemoteAuthorSearchTerms(
            IReadOnlyList<Dictionary<string, List<string>>> trackTags,
            BookMediaType mediaType,
            IReadOnlyList<string> filePaths = null)
        {
            var authorLists = trackTags?.Where(tags => tags != null)
                .Select(tags => GetLegacyAuthors(tags, mediaType))
                .ToList() ?? new List<List<string>>();

            var filenameEvidence = GetFilenameEvidence(trackTags, mediaType, filePaths);
            if (filenameEvidence.Count == authorLists.Count)
            {
                for (var index = 0; index < authorLists.Count; index++)
                {
                    if (authorLists[index].Count == 0 && !string.IsNullOrWhiteSpace(filenameEvidence[index].Author))
                    {
                        authorLists[index] = new List<string> { filenameEvidence[index].Author };
                    }
                }
            }

            var authorTitles = authorLists.Select(authors => authors.FirstOrDefault()).ToList();
            if (IsVariousAuthors(authorTitles))
            {
                return new List<string> { "Various Authors" };
            }

            return GetMostCommonList(authorLists);
        }

        private static void AddIdentifierDistance(WeightedDistance distance, string key, string missingKey, string localValue, string editionValue, Regex valid)
        {
            var local = ValidIdentifier(localValue, valid);
            if (!string.IsNullOrWhiteSpace(local) && string.Equals(local, editionValue, StringComparison.Ordinal))
            {
                distance.Add(key, 0);
            }
            else if (!string.IsNullOrWhiteSpace(local) || !string.IsNullOrWhiteSpace(editionValue))
            {
                distance.Add(missingKey, 1);
            }
        }

        private static string ValidIdentifier(string value, Regex valid)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var normalizedForValidation = value.Trim().Replace("-", string.Empty).ToUpperInvariant();
            return valid.IsMatch(normalizedForValidation) ? value : null;
        }

        private static int? ParseYear(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var match = YearRegex.Match(value);
            return match.Success && int.TryParse(match.Groups[1].Value, out var year) ? year : (int?)null;
        }

        private static string GetMostCommon(IReadOnlyList<Dictionary<string, List<string>>> trackTags, params string[] names)
        {
            var values = GetTrackValues(trackTags, names);
            return values.GroupBy(value => value)
                .OrderByDescending(group => group.Count())
                .Select(group => group.First())
                .FirstOrDefault();
        }

        private static string GetMostCommonIdentifier(
            IReadOnlyList<Dictionary<string, List<string>>> trackTags,
            string calibreOverrideName,
            params string[] names)
        {
            var overrideName = "BOOKSHELF_CALIBRE_" + calibreOverrideName.ToUpperInvariant();
            var values = (trackTags ?? Array.Empty<Dictionary<string, List<string>>>())
                .Where(tags => tags != null)
                .Select(tags => HasCalibreOverride(tags)
                    ? GetFirstValue(tags, overrideName)
                    : GetFirstValue(tags, names))
                .ToList();

            return values.GroupBy(value => value)
                .OrderByDescending(group => group.Count())
                .Select(group => group.First())
                .FirstOrDefault();
        }

        private static string GetMostCommonSeparatedValue(IReadOnlyList<Dictionary<string, List<string>>> trackTags, params string[] names)
        {
            var values = GetTrackValues(trackTags, names)
                .Select(value => value?.Split(';').FirstOrDefault())
                .ToList();
            return values.GroupBy(value => value)
                .OrderByDescending(group => group.Count())
                .Select(group => group.First())
                .FirstOrDefault();
        }

        private static List<string> GetMostCommonList(IReadOnlyList<Dictionary<string, List<string>>> trackTags, BookMediaType mediaType)
        {
            if (trackTags == null || trackTags.Count == 0)
            {
                return new List<string>();
            }

            var perTrack = trackTags
                .Where(tags => tags != null)
                .Select(tags => GetLegacyAuthors(tags, mediaType))
                .ToList();

            return GetMostCommonList(perTrack);
        }

        private static List<string> GetMostCommonList(IReadOnlyList<List<string>> perTrack)
        {
            return perTrack
                .GroupBy(values => string.Join(", ", values))
                .OrderByDescending(group => group.Count())
                .Select(group => group.First())
                .FirstOrDefault() ?? new List<string>();
        }

        private static List<string> GetLegacyAuthors(Dictionary<string, List<string>> tags, BookMediaType mediaType)
        {
            if (HasCalibreOverride(tags))
            {
                return GetValues(tags, CalibreAuthorsOverride);
            }

            if (mediaType == BookMediaType.Ebook)
            {
                return GetValues(tags, "author", "authors")
                    .SelectMany(SplitList)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
            }

            var bookAuthors = GetValues(tags, "albumartist", "album artist", "albumartists");
            if (bookAuthors.Count == 0)
            {
                bookAuthors = GetValues(tags, "albumartistsort", "albumartist_sort", "album artist sort");
            }

            var performers = GetValues(tags, "artist", "performers");
            if (performers.Count == 0)
            {
                performers = GetValues(tags, "performersort", "artistsort", "artist_sort", "performers_sort");
            }

            return bookAuthors.Concat(performers).Distinct().ToList();
        }

        private static bool IsVariousAuthors(IReadOnlyList<string> authorTitles)
        {
            if (authorTitles == null || authorTitles.Count == 0)
            {
                return false;
            }

            var groups = authorTitles.GroupBy(value => value).OrderByDescending(group => group.Count()).ToList();
            var distinctCount = groups.Count;
            var mostCommonCount = groups[0].Count();
            var mostCommonEntry = groups[0].Key;

            foreach (var group in groups.Skip(1))
            {
                if (!string.IsNullOrWhiteSpace(mostCommonEntry) &&
                    !string.IsNullOrWhiteSpace(group.Key) &&
                    GetLevenshteinCoefficient(mostCommonEntry, group.Key) > 0.9)
                {
                    distinctCount--;
                    mostCommonCount += group.Count();
                }
            }

            if (distinctCount > 1 &&
                (distinctCount / (double)authorTitles.Count > 0.75 ||
                 mostCommonCount / (double)authorTitles.Count < 0.25))
            {
                return true;
            }

            return VariousAuthorTitles.Contains(mostCommonEntry, StringComparer.OrdinalIgnoreCase);
        }

        private static double GetLevenshteinCoefficient(string first, string second)
        {
            return 1.0 - ((double)first.LevenshteinDistance(second) / Math.Max(first.Length, second.Length));
        }

        private static string GetMostCommonTitle(
            IReadOnlyList<Dictionary<string, List<string>>> trackTags,
            BookMediaType mediaType,
            IReadOnlyList<string> filePaths = null)
        {
            var tagSets = trackTags?.Where(tags => tags != null).ToList() ?? new List<Dictionary<string, List<string>>>();
            var titles = tagSets.Select(tags => GetLegacyTrackTitle(tags, mediaType)).ToList();
            var filenameEvidence = GetFilenameEvidence(trackTags, mediaType, filePaths);
            if (filenameEvidence.Count == titles.Count)
            {
                for (var index = 0; index < titles.Count; index++)
                {
                    if (string.IsNullOrWhiteSpace(titles[index]))
                    {
                        titles[index] = filenameEvidence[index].Title;
                    }
                }
            }

            return titles.GroupBy(value => value)
                .OrderByDescending(group => group.Count())
                .Select(group => group.First())
                .FirstOrDefault();
        }

        private static string GetLegacyTrackTitle(Dictionary<string, List<string>> tags, BookMediaType mediaType)
        {
            return mediaType == BookMediaType.Audiobook
                ? GetLegacyAudioTitle(tags)
                : HasCalibreOverride(tags)
                    ? GetFirstValue(tags, CalibreTitleOverride)
                    : GetFirstValue(tags, "title", "booktitle", "book title");
        }

        private static IReadOnlyList<BookshelfMatchCompatibilityFilenameParser.TrackEvidence> GetFilenameEvidence(
            IReadOnlyList<Dictionary<string, List<string>>> trackTags,
            BookMediaType mediaType,
            IReadOnlyList<string> filePaths)
        {
            if (filePaths == null || filePaths.Count == 0)
            {
                return Array.Empty<BookshelfMatchCompatibilityFilenameParser.TrackEvidence>();
            }

            var tagSets = trackTags?.Where(tags => tags != null).ToList() ?? new List<Dictionary<string, List<string>>>();
            if (tagSets.Count != filePaths.Count)
            {
                return Array.Empty<BookshelfMatchCompatibilityFilenameParser.TrackEvidence>();
            }

            var allowUnstructuredSingleFileTitle = tagSets.Count == 1 &&
                string.IsNullOrWhiteSpace(GetLegacyTrackTitle(tagSets[0], mediaType)) &&
                GetLegacyAuthors(tagSets[0], mediaType).Count == 0;
            return BookshelfMatchCompatibilityFilenameParser.Parse(filePaths, allowUnstructuredSingleFileTitle);
        }

        private static string GetLegacyAudioTitle(Dictionary<string, List<string>> tags)
        {
            if (HasCalibreOverride(tags))
            {
                return GetFirstValue(tags, CalibreTitleOverride);
            }

            if (HasTag(tags, BookshelfAudioBook) || HasTag(tags, BookshelfAudioTitle))
            {
                var exactBookTitle = GetFirstValue(tags, BookshelfAudioBook);
                return !string.IsNullOrWhiteSpace(exactBookTitle)
                    ? exactBookTitle
                    : GetFirstValue(tags, BookshelfAudioTitle);
            }

            var bookTitle = GetFirstValue(tags, "album")
                ?? GetFirstValue(tags, "album_sort", "albumsort", "album sort");
            if (!string.IsNullOrWhiteSpace(bookTitle))
            {
                return bookTitle;
            }

            return GetFirstValue(tags, "title", "title_sort", "titlesort", "title sort");
        }

        private static bool HasTag(Dictionary<string, List<string>> tags, string name)
        {
            return tags?.Keys.Any(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) == true;
        }

        private static int GetMostCommonYear(IReadOnlyList<Dictionary<string, List<string>>> trackTags, params string[] names)
        {
            var years = trackTags?.Where(tags => tags != null)
                .Select(tags => ParseYear(GetFirstValue(tags, names)) ?? 0)
                .ToList() ?? new List<int>();

            return years.GroupBy(year => year)
                .OrderByDescending(group => group.Count())
                .Select(group => group.Key)
                .FirstOrDefault();
        }

        private static List<string> GetTrackValues(IReadOnlyList<Dictionary<string, List<string>>> trackTags, params string[] names)
        {
            return trackTags?.Where(tags => tags != null)
                .Select(tags => GetFirstValue(tags, names))
                .ToList() ?? new List<string>();
        }

        private static string GetFirstValue(Dictionary<string, List<string>> tags, params string[] names)
        {
            if (tags == null || names == null)
            {
                return null;
            }

            foreach (var name in names)
            {
                var pair = tags.FirstOrDefault(value => string.Equals(value.Key, name, StringComparison.OrdinalIgnoreCase));
                var value = pair.Value?.FirstOrDefault();
                if (value != null)
                {
                    return value;
                }
            }

            return null;
        }

        internal static string GetCalibreGoodreadsId(Dictionary<string, List<string>> tags)
        {
            return HasCalibreOverride(tags) ? GetFirstValue(tags, CalibreGoodreadsOverride) : null;
        }

        internal static bool HasCalibreOverride(Dictionary<string, List<string>> tags)
        {
            return tags?.ContainsKey(CalibreOverrideMarker) == true;
        }

        private static void SetOverride(Dictionary<string, List<string>> tags, string name, string value)
        {
            tags[name] = string.IsNullOrWhiteSpace(value) ? new List<string>() : new List<string> { value };
        }

        private static List<string> GetValues(IReadOnlyList<Dictionary<string, List<string>>> trackTags, params string[] names)
        {
            return trackTags?.Where(tags => tags != null)
                .SelectMany(tags => GetValues(tags, names))
                .ToList() ?? new List<string>();
        }

        private static List<string> GetValues(Dictionary<string, List<string>> tags, params string[] names)
        {
            var nameSet = new HashSet<string>(names ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            return tags?.Where(pair => nameSet.Contains(pair.Key))
                .SelectMany(pair => pair.Value ?? new List<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList() ?? new List<string>();
        }

        private static List<string> SplitList(string value)
        {
            return (value ?? string.Empty)
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToList();
        }

        private static List<string> GetAuthorVariants(List<string> fileAuthors)
        {
            var authors = new List<string>(fileAuthors);
            if (fileAuthors.Count == 1)
            {
                authors.AddRange(SplitAuthor(fileAuthors[0]));
            }

            foreach (var author in fileAuthors)
            {
                if (author.Contains(','))
                {
                    var split = author.Split(',', 2).Select(value => value.Trim()).ToArray();
                    if (split[0].IndexOf(' ') < 0)
                    {
                        authors.Add(string.Join(" ", split.Reverse()));
                    }
                }
            }

            return authors;
        }

        private static List<string> SplitAuthor(string input)
        {
            foreach (var separator in new[] { ';', '/' })
            {
                if (input.Contains(separator))
                {
                    return input.Split(separator).Select(value => value.Trim()).ToList();
                }
            }

            foreach (var separator in new[] { " and ", " & " })
            {
                if (input.Contains(separator))
                {
                    var result = new List<string>();
                    foreach (var value in input.Split(new[] { separator }, StringSplitOptions.None).Select(value => value.Trim()))
                    {
                        var split = SplitAuthor(value);
                        if (split.Any())
                        {
                            result.AddRange(split);
                        }
                        else
                        {
                            result.Add(value);
                        }
                    }

                    return result;
                }
            }

            if (input.Contains(','))
            {
                var split = input.Split(',').Select(value => value.Trim()).ToList();
                if (split[0].Contains(' '))
                {
                    return split;
                }
            }

            return new List<string>();
        }

        private static List<string> GetNarrators(IEnumerable<string> values)
        {
            return (values ?? Enumerable.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .SelectMany(value => value.Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                .Select(value => value.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<string> GetLegacyNarrators(IReadOnlyList<Dictionary<string, List<string>>> trackTags)
        {
            var values = new List<string>();
            foreach (var tags in trackTags ?? Array.Empty<Dictionary<string, List<string>>>())
            {
                values.AddRange(GetLegacyNarratorValues(tags));
            }

            return GetNarrators(values);
        }

        private static List<string> GetLegacyNarratorValues(Dictionary<string, List<string>> tags)
        {
            // Match AudioTag's container precedence. In Xiph files, PERFORMER is the
            // narrator credit and NARRATOR is only its fallback.
            if (HasTagContainer(tags, "ID3v2:"))
            {
                return GetValues(tags, "ID3v2:TXXX:NARRATOR");
            }

            if (HasTagContainer(tags, "XIPH:"))
            {
                var performers = GetValues(tags, "XIPH:PERFORMER");
                return performers.Count > 0 ? performers : GetValues(tags, "XIPH:NARRATOR");
            }

            if (HasTagContainer(tags, "APE:"))
            {
                return GetValues(tags, "APE:Narrator");
            }

            if (HasTagContainer(tags, "ASF:"))
            {
                return GetValues(tags, "ASF:WM/Narrator");
            }

            if (HasTagContainer(tags, "MP4:"))
            {
                return GetValues(tags, "MP4:----:com.apple.iTunes:NARRATOR", "----:com.apple.iTunes:NARRATOR");
            }

            if (HasTagContainer(tags, "AUDIBLE:"))
            {
                return GetValues(tags, "AUDIBLE:NARRATOR");
            }

            return GetValues(tags, "NARRATOR", "NARRATORS");
        }

        private static bool HasTagContainer(Dictionary<string, List<string>> tags, string prefix)
        {
            return tags?.Keys.Any(key => key?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true) == true;
        }

        private static void AddPreferredEditionDistance(WeightedDistance distance, string title, string format, string publisher, string preferredEditionTerms)
        {
            if (string.IsNullOrWhiteSpace(preferredEditionTerms))
            {
                return;
            }

            var terms = preferredEditionTerms
                .Split(new[] { '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim())
                .Select(value =>
                {
                    var separator = value.IndexOf('|');
                    return separator <= 0 || separator == value.Length - 1 ||
                           !int.TryParse(value.Substring(0, separator).Trim(), out var weight) || weight <= 0
                        ? null
                        : new { Weight = weight, Term = value.Substring(separator + 1).Trim() };
                })
                .Where(value => value != null && !string.IsNullOrWhiteSpace(value.Term))
                .ToList();

            if (terms.Count == 0)
            {
                return;
            }

            var text = $"{title} {format} {publisher}";
            var maximumWeight = terms.Max(value => value.Weight);
            var matchingWeight = terms
                .Where(value => text.IndexOf(value.Term, StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(value => value.Weight)
                .DefaultIfEmpty(0)
                .Max();
            distance.Add("preferred_edition", matchingWeight > 0 ? 1.0 - matchingWeight / (double)maximumWeight : 1.0);
        }

        private sealed class WeightedDistance
        {
            private readonly Dictionary<string, List<double>> _penalties = new Dictionary<string, List<double>>(StringComparer.Ordinal);

            public void Add(string key, double penalty)
            {
                if (!_penalties.TryGetValue(key, out var values))
                {
                    values = new List<double>();
                    _penalties[key] = values;
                }

                values.Add(penalty);
            }

            public void AddBool(string key, bool mismatch) => Add(key, mismatch ? 1.0 : 0.0);

            public void AddRatio(string key, int difference, int maximumDifference)
            {
                Add(key, maximumDifference > 0 ? Math.Max(Math.Min(difference, maximumDifference), 0.0) / maximumDifference : 0.0);
            }

            public void AddString(string key, string value, string target) => Add(key, StringDistance(value, target));

            public void AddString(string key, IReadOnlyCollection<string> values, string target) =>
                Add(key, values.Count == 0 ? StringDistance(string.Empty, target) : values.Min(value => StringDistance(value, target)));

            public void AddString(string key, IReadOnlyCollection<string> values, IReadOnlyCollection<string> targets) =>
                Add(key, targets.Count == 0
                    ? StringDistance(values.FirstOrDefault() ?? string.Empty, string.Empty)
                    : values.Count == 0
                        ? StringDistance(string.Empty, targets.FirstOrDefault() ?? string.Empty)
                        : values.Min(value => targets.Min(target => StringDistance(value, target))));

            public double NormalizedDistance()
            {
                var maximum = _penalties.Sum(pair => pair.Value.Count * Weights[pair.Key]);
                if (maximum <= 0)
                {
                    return 0;
                }

                var raw = _penalties.Sum(pair => pair.Value.Sum() * Weights[pair.Key]);
                return raw / maximum;
            }
        }

        private static double StringDistance(string value, string target)
        {
            var cleanValue = Clean(value);
            var cleanTarget = Clean(target);
            if (string.IsNullOrWhiteSpace(cleanValue) && !string.IsNullOrWhiteSpace(cleanTarget))
            {
                return 1.0;
            }
            if (string.IsNullOrWhiteSpace(cleanValue) && string.IsNullOrWhiteSpace(cleanTarget))
            {
                return 0.0;
            }

            return (double)cleanValue.LevenshteinDistance(cleanTarget) / Math.Max(cleanValue.Length, cleanTarget.Length);
        }

        private static string Clean(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return string.Empty;
            }

            var characters = input.ToLower().RemoveAccent().ToCharArray();
            return new string(Array.FindAll(characters, char.IsLetterOrDigit));
        }
    }
}
