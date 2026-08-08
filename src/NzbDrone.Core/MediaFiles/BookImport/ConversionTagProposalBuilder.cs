using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles.BookImport.Services;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles.BookImport
{
    internal static class ConversionTagProposalBuilder
    {
        private const int MaxManifestFiles = 50;
        private const int MaxManifestTagFieldsPerFile = 48;
        private const int MaxManifestValuesPerField = 4;
        private const int MaxManifestValueLength = 500;

        public static ConversionTagOptions BuildOptions(
            IEnumerable<LocalBook> sourceBooks,
            Book book,
            Author author,
            Edition edition,
            IContainmentValidator containmentValidator,
            string mode)
        {
            var sources = (sourceBooks ?? Enumerable.Empty<LocalBook>())
                .Where(source => source != null)
                .ToList();

            var normalizedMode = ConversionTagModes.Normalize(mode);
            var options = normalizedMode == ConversionTagModes.Clean
                ? BuildCleanOptions(sources, book, author, edition)
                : BuildPreserveOptions(sources, book, author, edition, containmentValidator);

            options.Mode = normalizedMode;
            RefreshManifestJson(options, sources);
            return options;
        }

        public static void RefreshManifestJson(ConversionTagOptions options, IEnumerable<LocalBook> sourceBooks)
        {
            if (options == null)
            {
                return;
            }

            var sources = (sourceBooks ?? Enumerable.Empty<LocalBook>())
                .Where(source => source != null)
                .ToList();

            options.ManifestJson = BuildManifestJson(sources, options);
        }

        private static ConversionTagOptions BuildPreserveOptions(
            IReadOnlyList<LocalBook> sources,
            Book book,
            Author author,
            Edition edition,
            IContainmentValidator containmentValidator)
        {
            var multipleFiles = sources.Count > 1;
            var tagSets = BuildTagSets(sources);
            var consensusTags = UnitTagConsensusBuilder.BuildConsensus(tagSets, sources.Count);
            var allTags = MergeTagSets(tagSets);
            var consensusEvidence = BuildEvidence(author, book, edition, consensusTags, containmentValidator);
            var allEvidence = BuildEvidence(author, book, edition, allTags, containmentValidator);

            var titleValue = PickEvidenceValue(consensusEvidence.BookTags);
            if (titleValue.IsNullOrWhiteSpace() && !multipleFiles)
            {
                titleValue = PickEvidenceValue(allEvidence.BookTags);
            }

            // A merged M4B has one title/album. If every source file has a different track title
            // and no common source field proves the book title, use the matched DB title for only
            // the merged-book identity while preserving all other source-derived tags exactly.
            if (titleValue.IsNullOrWhiteSpace() && multipleFiles)
            {
                titleValue = GetCanonicalTitle(book, edition);
            }

            var authorValue = PickEvidenceValue(consensusEvidence.AuthorTags) ?? PickEvidenceValue(allEvidence.AuthorTags);
            var narratorValue = PickEvidenceValue(consensusEvidence.NarratorTags) ?? PickEvidenceValue(allEvidence.NarratorTags);

            return new ConversionTagOptions
            {
                Name = titleValue,
                Album = titleValue,
                Artist = authorValue,
                AlbumArtist = authorValue,
                Writer = narratorValue,
                UseFilenamesAsChapters = multipleFiles
            };
        }

        private static ConversionTagOptions BuildCleanOptions(
            IReadOnlyList<LocalBook> sources,
            Book book,
            Author author,
            Edition edition)
        {
            var title = GetCanonicalTitle(book, edition);
            var narrator = GetCanonicalNarrator(book, edition);
            var year = GetCanonicalYear(book, edition);
            var genre = book?.Genres?.Any() == true ? string.Join("; ", book.Genres.Where(g => g.IsNotNullOrWhiteSpace()).Distinct(StringComparer.OrdinalIgnoreCase)) : null;
            var description = FirstNonEmpty(edition?.Overview, book?.Overview);

            return new ConversionTagOptions
            {
                Name = title,
                Album = title,
                Artist = author?.Name,
                AlbumArtist = author?.Name,
                Writer = narrator,
                Year = year,
                Genre = genre,
                Comment = Truncate(description, 4096),
                Copyright = FirstNonEmpty(edition?.Publisher, book?.Publisher),
                Series = book?.SeriesName,
                SeriesPart = book?.SeriesPosition,
                UseFilenamesAsChapters = sources.Count > 1,
                IgnoreSourceTags = true
            };
        }

        private static ExactMatchEvidence BuildEvidence(
            Author author,
            Book book,
            Edition edition,
            IDictionary<string, List<string>> tags,
            IContainmentValidator containmentValidator)
        {
            if (containmentValidator == null || tags == null || tags.Count == 0)
            {
                return new ExactMatchEvidence();
            }

            return ExactMatchEvidenceBuilder.Build(author?.Name, book?.Title, edition, tags, containmentValidator);
        }

        private static List<Dictionary<string, List<string>>> BuildTagSets(IEnumerable<LocalBook> sources)
        {
            return sources
                .Select(source => ToCaseInsensitiveTags(source.RawTags?.AllTags))
                .Where(tags => tags.Count > 0)
                .ToList();
        }

        private static Dictionary<string, List<string>> ToCaseInsensitiveTags(Dictionary<string, List<string>> tags)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (tags == null)
            {
                return result;
            }

            foreach (var kv in tags)
            {
                if (kv.Key.IsNullOrWhiteSpace() || kv.Value == null)
                {
                    continue;
                }

                var values = kv.Value
                    .Select(value => value?.Trim())
                    .Where(value => value.IsNotNullOrWhiteSpace())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (values.Count > 0)
                {
                    result[kv.Key] = values;
                }
            }

            return result;
        }

        private static Dictionary<string, List<string>> MergeTagSets(IEnumerable<Dictionary<string, List<string>>> tagSets)
        {
            return ExactMatchEvidenceBuilder.MergeTagSets(
                (tagSets ?? Enumerable.Empty<Dictionary<string, List<string>>>())
                .Select(tags => (IDictionary<string, List<string>>)tags)
                .ToArray());
        }

        private static string PickEvidenceValue(IDictionary<string, List<string>> evidenceTags)
        {
            return evidenceTags?
                .Where(kv => kv.Value != null && kv.Value.Count > 0)
                .SelectMany(kv => kv.Value.Select(value => value?.Trim()))
                .Where(value => value.IsNotNullOrWhiteSpace())
                .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key.Length)
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Key)
                .FirstOrDefault();
        }

        private static string GetCanonicalTitle(Book book, Edition edition)
        {
            return FirstNonEmpty(edition?.Title, book?.Title);
        }

        private static string GetCanonicalNarrator(Book book, Edition edition)
        {
            if (edition?.NarratorNames?.Any() == true)
            {
                return string.Join("; ", edition.NarratorNames.Where(n => n.IsNotNullOrWhiteSpace()).Distinct(StringComparer.OrdinalIgnoreCase));
            }

            return FirstNonEmpty(edition?.Narrator, book?.NarratorName, book?.Narrator);
        }

        private static string GetCanonicalYear(Book book, Edition edition)
        {
            var year = edition?.ReleaseDate?.Year ??
                       book?.ReleaseDate?.Year ??
                       book?.PublicationYear;

            return year.HasValue && year.Value > 0
                ? year.Value.ToString(CultureInfo.InvariantCulture)
                : null;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return values?.FirstOrDefault(value => value.IsNotNullOrWhiteSpace())?.Trim();
        }

        private static string Truncate(string value, int maxLength)
        {
            if (value.IsNullOrWhiteSpace())
            {
                return null;
            }

            value = value.Trim();
            return value.Length <= maxLength ? value : value.Substring(0, maxLength);
        }

        private static string BuildManifestJson(IReadOnlyList<LocalBook> sources, ConversionTagOptions options)
        {
            var manifest = new
            {
                mode = options.Mode,
                selected = new
                {
                    name = options.Name,
                    album = options.Album,
                    artist = options.Artist,
                    albumArtist = options.AlbumArtist,
                    writer = options.Writer,
                    year = options.Year,
                    genre = options.Genre,
                    comment = options.Comment,
                    copyright = options.Copyright,
                    series = options.Series,
                    seriesPart = options.SeriesPart,
                    useFilenamesAsChapters = options.UseFilenamesAsChapters,
                    ignoreSourceTags = options.IgnoreSourceTags,
                    providerChapterCount = options.ProviderChapterCount
                },
                sourceCount = sources.Count,
                sourcesTruncated = sources.Count > MaxManifestFiles,
                sources = sources
                    .Take(MaxManifestFiles)
                    .Select(source => new
                    {
                        path = source.Path,
                        tagFieldCount = source.RawTags?.AllTags?.Count ?? 0,
                        tagFieldsTruncated = (source.RawTags?.AllTags?.Count ?? 0) > MaxManifestTagFieldsPerFile,
                        tags = ClampTags(source.RawTags?.AllTags)
                    })
                    .ToList()
            };

            return JsonSerializer.Serialize(manifest);
        }

        private static Dictionary<string, List<string>> ClampTags(Dictionary<string, List<string>> tags)
        {
            var output = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (tags == null || tags.Count == 0)
            {
                return output;
            }

            foreach (var kv in tags
                         .Where(kv => kv.Key.IsNotNullOrWhiteSpace() && kv.Value != null)
                         .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                         .Take(MaxManifestTagFieldsPerFile))
            {
                var values = kv.Value
                    .Where(value => value.IsNotNullOrWhiteSpace())
                    .Take(MaxManifestValuesPerField)
                    .Select(value => Truncate(value, MaxManifestValueLength))
                    .Where(value => value.IsNotNullOrWhiteSpace())
                    .ToList();

                if (values.Count > 0)
                {
                    output[kv.Key] = values;
                }
            }

            return output;
        }
    }
}
