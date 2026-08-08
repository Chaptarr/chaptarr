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
                    file => file.Part,
                    (file, value) => file.Part = value,
                    (file, value) => file.PartCount = value);
            }
        }

        public static IReadOnlyDictionary<string, (int Part, int PartCount)> BuildPathAssignmentsByEdition(
            IEnumerable<(string Path, int? EditionId)> files,
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
                    .OrderBy(file => file.Path ?? string.Empty, NaturalSortComparer.Instance)
                    .ToList();

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
            Func<T, int> getPart,
            Action<T, int> setPart,
            Action<T, int> setPartCount)
        {
            var audioFiles = groupFiles
                .Where(isAudiobook)
                .OrderBy(getSortKey, NaturalSortComparer.Instance)
                .ToList();

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
    }
}
