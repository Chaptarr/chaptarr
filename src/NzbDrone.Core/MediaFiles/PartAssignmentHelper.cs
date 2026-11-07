using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NzbDrone.Common;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.MediaFiles
{
    internal static class PartAssignmentHelper
    {
        public static void NormalizeBookFilesByEdition(IList<BookFile> files)
        {
            if (files == null || files.Count == 0)
            {
                return;
            }

            foreach (var editionGroup in files.GroupBy(f => f.EditionId))
            {
                NormalizeGroup(
                    editionGroup.ToList(),
                    file => IsAudiobookFile(file.Path, file.MediaType, file.Quality),
                    file => file.Path ?? string.Empty,
                    file => file.AllTags,
                    file => file.Part,
                    (file, value) => file.Part = value,
                    (file, value) => file.PartCount = value);
            }
        }

        public static void NormalizeLocalBooksByEdition(IList<LocalBook> files)
        {
            if (files == null || files.Count == 0)
            {
                return;
            }

            foreach (var editionGroup in files.GroupBy(GetLocalBookGroupingKey))
            {
                NormalizeGroup(
                    editionGroup.ToList(),
                    file => IsAudiobookFile(file.Path, null, file.Quality),
                    file => file.Path ?? string.Empty,
                    file => file.RawTags?.AllTags,
                    file => file.Part,
                    (file, value) => file.Part = value,
                    (file, value) => file.PartCount = value);
            }
        }

        public static IReadOnlyDictionary<string, (int Part, int PartCount)> BuildPathAssignmentsByEdition(
            IEnumerable<(string Path, int? EditionId)> files,
            int defaultEditionId)
        {
            return BuildPathAssignmentsByEdition(
                files?.Select(file => (file.Path, file.EditionId, (Dictionary<string, List<string>>)null)),
                defaultEditionId);
        }

        public static IReadOnlyDictionary<string, (int Part, int PartCount)> BuildPathAssignmentsByEdition(
            IEnumerable<(string Path, int? EditionId, Dictionary<string, List<string>> AllTags)> files,
            int defaultEditionId)
        {
            var assignments = new Dictionary<string, (int Part, int PartCount)>(StringComparer.OrdinalIgnoreCase);
            if (files == null)
            {
                return assignments;
            }

            foreach (var editionGroup in files.GroupBy(file => file.EditionId.GetValueOrDefault(defaultEditionId)))
            {
                var groupFiles = editionGroup.ToList();
                var audioFiles = groupFiles
                    .Where(file => IsAudiobookFile(file.Path))
                    .ToList();
                audioFiles = OrderAudioFiles(audioFiles, file => file.Path ?? string.Empty, file => file.AllTags);

                if (audioFiles.Count > 1)
                {
                    for (var index = 0; index < audioFiles.Count; index++)
                    {
                        assignments[audioFiles[index].Path] = (index + 1, audioFiles.Count);
                    }
                }
                else
                {
                    foreach (var audioFile in audioFiles)
                    {
                        assignments[audioFile.Path] = (1, 1);
                    }
                }

                foreach (var file in groupFiles.Where(file => !IsAudiobookFile(file.Path)))
                {
                    assignments[file.Path] = (1, 1);
                }
            }

            return assignments;
        }

        private static int GetLocalBookGroupingKey(LocalBook localBook)
        {
            if (localBook?.Edition?.Id > 0)
            {
                return localBook.Edition.Id;
            }

            if (localBook?.Book?.Id > 0)
            {
                return localBook.Book.Id;
            }

            return 0;
        }

        private static bool IsAudiobookFile(string path, string mediaType = null, QualityModel quality = null)
        {
            if (!string.IsNullOrWhiteSpace(mediaType))
            {
                return string.Equals(mediaType, "audiobook", StringComparison.OrdinalIgnoreCase);
            }

            var extension = Path.GetExtension(path ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(extension))
            {
                return MediaFileExtensions.AudioExtensions.Contains(extension);
            }

            return quality != null && string.Equals(BookFile.DetermineMediaType(quality), "audiobook", StringComparison.OrdinalIgnoreCase);
        }

        private static void NormalizeGroup<T>(
            List<T> groupFiles,
            Func<T, bool> isAudiobook,
            Func<T, string> getSortKey,
            Func<T, Dictionary<string, List<string>>> getTags,
            Func<T, int> getPart,
            Action<T, int> setPart,
            Action<T, int> setPartCount)
        {
            var audioFiles = OrderAudioFiles(groupFiles.Where(isAudiobook).ToList(), getSortKey, getTags);

            if (audioFiles.Count > 1)
            {
                var parts = audioFiles.Select(getPart).ToList();
                var hasMissing = parts.Any(part => part <= 0);
                var hasDuplicates = parts.Where(part => part > 0).GroupBy(part => part).Any(partGroup => partGroup.Count() > 1);

                if (hasMissing || hasDuplicates)
                {
                    var part = 1;
                    foreach (var file in audioFiles)
                    {
                        setPart(file, part++);
                    }
                }

                foreach (var file in audioFiles)
                {
                    setPartCount(file, audioFiles.Count);
                }
            }
            else
            {
                foreach (var file in audioFiles)
                {
                    if (getPart(file) <= 0)
                    {
                        setPart(file, 1);
                    }

                    setPartCount(file, 1);
                }
            }

            foreach (var file in groupFiles.Where(file => !isAudiobook(file)))
            {
                setPart(file, 1);
                setPartCount(file, 1);
            }
        }

        private static List<T> OrderAudioFiles<T>(
            List<T> files,
            Func<T, string> getSortKey,
            Func<T, Dictionary<string, List<string>>> getTags)
        {
            if (files.Count < 2 || getTags == null)
            {
                return files.OrderBy(getSortKey, NaturalSortComparer.Instance).ToList();
            }

            var trackNumbers = new List<(T File, int Disc, int Track)>();
            var identities = new HashSet<(int Disc, int Track)>();
            bool? hasDiscNumbers = null;
            foreach (var file in files)
            {
                var tags = getTags(file);
                if (!TryGetTagNumber(tags, out var track, "TRACKNUMBER", "TRACK", "TRCK") ||
                    !TryGetOptionalTagNumber(tags, out var disc, out var hasDiscNumber, "DISCNUMBER", "DISC", "TPOS"))
                {
                    return files.OrderBy(getSortKey, NaturalSortComparer.Instance).ToList();
                }

                if (hasDiscNumbers.HasValue && hasDiscNumbers.Value != hasDiscNumber)
                {
                    // A partial disc sequence cannot establish the intended order reliably.
                    return files.OrderBy(getSortKey, NaturalSortComparer.Instance).ToList();
                }

                hasDiscNumbers = hasDiscNumber;

                var identity = (disc, track);
                if (!identities.Add(identity))
                {
                    // Repeated track numbers without a unique disc/track identity are ambiguous.
                    // Keep the established natural-path order in that case.
                    return files.OrderBy(getSortKey, NaturalSortComparer.Instance).ToList();
                }

                trackNumbers.Add((file, disc, track));
            }

            return trackNumbers
                .OrderBy(item => item.Disc)
                .ThenBy(item => item.Track)
                .ThenBy(item => getSortKey(item.File), NaturalSortComparer.Instance)
                .Select(item => item.File)
                .ToList();
        }

        private static bool TryGetOptionalTagNumber(
            Dictionary<string, List<string>> tags,
            out int number,
            out bool hasNumber,
            params string[] keys)
        {
            number = 0;
            hasNumber = false;
            if (!TryGetTagNumber(tags, out var parsed, keys))
            {
                return tags == null || !HasAnyTag(tags, keys);
            }

            number = parsed;
            hasNumber = true;
            return true;
        }

        private static bool TryGetTagNumber(
            Dictionary<string, List<string>> tags,
            out int number,
            params string[] keys)
        {
            number = 0;
            if (tags == null)
            {
                return false;
            }

            foreach (var key in keys)
            {
                if (!tags.TryGetValue(key, out var values))
                {
                    var matching = tags.FirstOrDefault(tag => string.Equals(tag.Key, key, StringComparison.OrdinalIgnoreCase));
                    values = matching.Value;
                }

                if (values == null || values.Count == 0)
                {
                    continue;
                }

                if (values.Count != 1)
                {
                    return false;
                }

                var value = values[0]?.Trim();
                if (string.IsNullOrWhiteSpace(value))
                {
                    return false;
                }

                var separator = value.IndexOf('/');
                if (separator >= 0)
                {
                    value = value.Substring(0, separator).Trim();
                }

                return int.TryParse(value, out number) && number > 0;
            }

            return false;
        }

        private static bool HasAnyTag(Dictionary<string, List<string>> tags, params string[] keys)
        {
            return tags.Keys.Any(tagKey => keys.Any(key => string.Equals(tagKey, key, StringComparison.OrdinalIgnoreCase)));
        }
    }
}
