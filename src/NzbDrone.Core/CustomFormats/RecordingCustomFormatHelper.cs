using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Common;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.CustomFormats
{
    public static class RecordingCustomFormatHelper
    {
        public static List<List<LocalBook>> GroupByRecording(IEnumerable<LocalBook> files)
        {
            return files.Where(file => file != null)
                .GroupBy(file => (file.Book?.Id, file.Book?.MediaType, file.Edition?.Id,
                    GeneratedPath: file.IsGeneratedConversion ? file.Path : null,
                    Unresolved: file.Book?.Id > 0 && file.Edition?.Id > 0 ? null : file))
                .Select(group => group.ToList()).ToList();
        }

        public static List<List<LocalBook>> GroupForScoring(IEnumerable<LocalBook> files)
        {
            return GroupByRecording(files).SelectMany(group =>
                group[0].Book?.MediaType == NzbDrone.Core.Books.BookMediaType.Audiobook
                    ? new[] { group }
                    : group.Select(file => new List<LocalBook> { file })).ToList();
        }

        public static List<List<BookFile>> GroupStoredForScoring(IEnumerable<BookFile> files, NzbDrone.Core.Books.Book book = null)
        {
            var present = files.Where(file => file != null).ToList();
            if (book != null)
            {
                return book.MediaType == NzbDrone.Core.Books.BookMediaType.Audiobook
                    ? (present.Count == 0 ? new List<List<BookFile>>() : new List<List<BookFile>> { present })
                    : present.Select(file => new List<BookFile> { file }).ToList();
            }

            return present.GroupBy(file =>
                (BookId: file.Edition?.BookId ?? 0, Media: file.Edition?.Book?.MediaType,
                    Separate: file.Edition?.BookId > 0 && file.Edition?.Book?.MediaType == NzbDrone.Core.Books.BookMediaType.Audiobook
                        ? null : file))
                .Select(group => group.ToList()).ToList();
        }

        public static BookFile CreateStoredInput(IReadOnlyCollection<BookFile> files,
            NzbDrone.Core.Books.Author author = null, NzbDrone.Core.Books.Edition edition = null, NzbDrone.Core.Books.Book book = null)
        {
            var first = files.First();
            var stored = first.CloneForImport();
            author ??= first.Author;
            edition ??= first.Edition;
            book ??= edition?.Book;
            if (files.Count > 1)
            {
                var input = CreateInput(files.Select(file => ToLocal(file, author, edition, book)).ToList());
                stored.Size = input.Size;
                stored.AllTags = input.RawTags.AllTags;
                stored.Narrator = input.Narrator;
                stored.IsGraphicAudio = input.IsGraphicAudio;
            }

            stored.Author = author;
            stored.Edition = edition;
            return stored;
        }

        private static LocalBook ToLocal(BookFile file, NzbDrone.Core.Books.Author author,
            NzbDrone.Core.Books.Edition edition, NzbDrone.Core.Books.Book book) => new LocalBook
        {
            Path = file.Path, Size = file.Size, Quality = file.Quality, Part = file.Part, PartCount = file.PartCount,
            Book = book, Edition = edition, Author = author,
            RawTags = new RawFileTags { AllTags = file.AllTags }, Narrator = file.Narrator,
            IsGraphicAudio = file.IsGraphicAudio, AudioProductionOverride = file.AudioProductionOverride,
            AudioProductionType = file.AudioProductionType, SceneName = file.SceneName,
            ReleaseGroup = file.ReleaseGroup, IndexerFlags = file.IndexerFlags, MatchProvenance = file.MatchProvenance
        };

        public static List<LocalBook> GetCompletedParts(IReadOnlyCollection<LocalBook> selected,
            IEnumerable<BookFile> storedFiles, string downloadId, IReadOnlyDictionary<int, List<string>> previousSources,
            IEnumerable<LocalBook> observedCompleted = null, IReadOnlySet<int> excludedFileIds = null)
        {
            if (selected.Count == 0 || selected.Any(item => item.IsGeneratedConversion)) return new List<LocalBook>();
            var first = selected.First();
            var currentPaths = selected.SelectMany(TrackedDownloadAlreadyImported.GetSourcePaths).ToHashSet(PathEqualityComparer.Instance);
            var observed = (observedCompleted ?? Enumerable.Empty<LocalBook>()).Where(item => File.Exists(item.Path))
                .SelectMany(item => TrackedDownloadAlreadyImported.GetSourcePaths(item).Select(path => (Path: path, Item: item)))
                .ToLookup(source => source.Path, source => source.Item, PathEqualityComparer.Instance);
            var result = new List<LocalBook>();
            foreach (var file in storedFiles)
            {
                if (file.EditionId != first.Edition?.Id || file.Edition?.BookId != first.Book?.Id ||
                    !File.Exists(file.Path) || excludedFileIds?.Contains(file.Id) == true ||
                    !TrackedDownloadAlreadyImported.IsFileFromDownload(file, downloadId, previousSources, first.AlternativeCopyGrabDate)) continue;
                var paths = file.MatchProvenance?.ImportSourcePaths?.Count > 0 ? file.MatchProvenance.ImportSourcePaths
                    : previousSources?.GetValueOrDefault(file.Id) ?? new List<string>();
                if (paths.Count != 1 || currentPaths.Contains(paths[0])) continue;
                var item = observed[paths[0]].FirstOrDefault(item => item.Edition?.Id == file.EditionId)?.ShallowClone() ?? new LocalBook
                {
                    Path = paths[0], Edition = file.Edition, Book = first.Book, Author = first.Author, Size = file.Size,
                    Quality = file.Quality, Part = file.Part, PartCount = file.PartCount,
                    RawTags = new RawFileTags { AllTags = file.AllTags }, Narrator = file.Narrator,
                    IsGraphicAudio = file.IsGraphicAudio, AudioProductionOverride = file.AudioProductionOverride,
                    AudioProductionType = file.AudioProductionType, SceneName = file.SceneName,
                    ReleaseGroup = file.ReleaseGroup, IndexerFlags = file.IndexerFlags
                };
                result.Add(item);
            }
            return result;
        }

        public static LocalBook CreateInput(IReadOnlyCollection<LocalBook> parts, QualityModel effectiveQuality = null)
        {
            if (parts == null || parts.Count == 0)
            {
                throw new ArgumentException("A recording must contain at least one part", nameof(parts));
            }

            var input = parts.First().ShallowClone();
            input.Quality = effectiveQuality ?? input.Quality;
            input.Size = parts.Where(part => !string.IsNullOrWhiteSpace(part.Path))
                .DistinctBy(part => part.Path, PathEqualityComparer.Instance).Sum(part => part.Size)
                + parts.Where(part => string.IsNullOrWhiteSpace(part.Path)).Sum(part => part.Size);
            var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in parts)
            {
                foreach (var tag in part.RawTags?.AllTags ?? new Dictionary<string, List<string>>())
                {
                    if (!tags.TryGetValue(tag.Key, out var values))
                    {
                        values = new List<string>();
                        tags[tag.Key] = values;
                    }

                    foreach (var value in tag.Value ?? new List<string>())
                    {
                        if (!values.Contains(value, StringComparer.Ordinal)) values.Add(value);
                    }
                }
            }

            input.RawTags = new RawFileTags { AllTags = tags };
            input.Narrator = string.Join("; ", parts.Select(part => part.Narrator)
                .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase));
            input.IsGraphicAudio = parts.Any(AudioProductionDetector.IsDramatizedRecording);
            return input;
        }
    }
}
