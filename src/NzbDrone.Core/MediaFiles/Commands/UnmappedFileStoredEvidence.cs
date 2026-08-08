using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using NLog;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.MediaFiles.Commands
{
    internal sealed class UnmappedFileStoredEvidenceResult
    {
        public Dictionary<string, List<string>> Tags { get; set; }
        public int? DurationSeconds { get; set; }
        public bool FileChanged { get; set; }
        public bool Mutated { get; set; }
    }

    internal static class UnmappedFileStoredEvidence
    {
        internal static bool TryRefreshIfNeeded(
            BookFile file,
            IFileInfo fileInfo,
            IMetadataTagService metadataTagService,
            Logger logger,
            string logPrefix,
            out UnmappedFileStoredEvidenceResult result)
        {
            result = BuildStoredResult(file, fileInfo);

            if (file == null || fileInfo == null)
            {
                return false;
            }

            var needsEvidenceRead = result.FileChanged ||
                                    result.Tags.Count == 0 ||
                                    IsAudioFile(file.Path) && !MediaDuration.HasDuration(result.DurationSeconds);

            if (!needsEvidenceRead)
            {
                result.Mutated = BackfillQualityAndMediaType(file);
                return true;
            }

            try
            {
                var (freshTags, freshDurationSeconds) = metadataTagService.ReadAllTagsAndDuration(fileInfo);
                var refreshedTags = CloneStoredTags(freshTags);

                if (result.FileChanged || refreshedTags.Any())
                {
                    result.Tags = refreshedTags;
                }

                if (MediaDuration.HasDuration(freshDurationSeconds) || result.FileChanged)
                {
                    result.DurationSeconds = freshDurationSeconds;
                }

                file.Size = fileInfo.Length;
                file.Modified = MediaFileFreshness.GetLastWriteUtc(fileInfo);
                file.AllTags = result.Tags;
                file.DurationSeconds = result.DurationSeconds;
                file.MediaInfo = MediaDuration.ApplyToMediaInfo(file.MediaInfo, result.DurationSeconds);
                BackfillQualityAndMediaType(file);

                result.Mutated = true;

                logger.Debug("{0} Refreshed stored evidence for '{1}' (changed={2}, tags={3}, duration={4})",
                    logPrefix,
                    file.Path,
                    result.FileChanged,
                    result.Tags.Count,
                    result.DurationSeconds?.ToString() ?? "null");

                return true;
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "{0} Failed to refresh stored evidence for '{1}'", logPrefix, file.Path);
                return false;
            }
        }

        internal static QualityModel ResolveQuality(BookFile file, string path)
        {
            if (file?.Quality != null)
            {
                return file.Quality;
            }

            return new QualityModel
            {
                Quality = MediaFileExtensions.GetQualityForExtension(Path.GetExtension(path))
            };
        }

        private static UnmappedFileStoredEvidenceResult BuildStoredResult(BookFile file, IFileInfo fileInfo)
        {
            var result = new UnmappedFileStoredEvidenceResult
            {
                Tags = CloneStoredTags(file?.AllTags),
                DurationSeconds = MediaDuration.GetStoredDurationSeconds(file),
                FileChanged = MediaFileFreshness.HasChanged(file, fileInfo)
            };

            return result;
        }

        private static bool BackfillQualityAndMediaType(BookFile file)
        {
            var changed = false;

            if (file.Quality == null)
            {
                file.Quality = ResolveQuality(file, file.Path);
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(file.MediaType))
            {
                file.MediaType = BookFile.DetermineMediaType(file.Quality);
                changed = true;
            }

            return changed;
        }

        private static Dictionary<string, List<string>> CloneStoredTags(Dictionary<string, List<string>> tags)
        {
            if (tags == null || tags.Count == 0)
            {
                return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            }

            return tags.ToDictionary(
                pair => pair.Key,
                pair => pair.Value?.ToList() ?? new List<string>(),
                StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsAudioFile(string path)
        {
            return MediaFileExtensions.AudioExtensions.Contains(Path.GetExtension(path));
        }
    }
}
