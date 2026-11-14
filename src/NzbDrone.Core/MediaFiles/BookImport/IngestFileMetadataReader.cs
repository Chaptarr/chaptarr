using System;
using System.Collections.Generic;
using System.IO;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles.TagExtraction;

namespace NzbDrone.Core.MediaFiles.BookImport
{
    internal static class IngestFileMetadataReader
    {
        internal static (Dictionary<string, List<string>> Tags, int? DurationSeconds, bool ExtractionFailed) Read(
            IngestQueueItem item, IMetadataTagService metadata, IDiskProvider disk)
        {
            var tags = BookImportSerializationHelper.SafeDeserializeTags(item.TagsJson);
            var duration = item.DurationSeconds;
            var needsDuration = MediaFileExtensions.AudioExtensions.Contains(Path.GetExtension(item.Path) ?? string.Empty) &&
                                !MediaDuration.HasDuration(duration);
            if (tags?.Count > 0 && !needsDuration) return (tags, duration, false);
            try
            {
                var info = metadata == null || string.IsNullOrWhiteSpace(item.Path) ? null : disk?.GetFileInfo(item.Path);
                if (info?.Exists == true)
                {
                    var extracted = metadata.ReadAllTagsAndDuration(info);
                    if (extracted.Tags?.Count > 0)
                    {
                        tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                        foreach (var pair in extracted.Tags)
                        {
                            if (!string.IsNullOrWhiteSpace(pair.Key)) tags[pair.Key] = pair.Value ?? new List<string>();
                        }
                    }
                    duration = extracted.DurationSeconds ?? duration;
                }
            }
            catch (TagExtractionException)
            {
                return (tags, duration, true);
            }
            catch
            {
                // Retain this file's staged metadata when a fresh read is unavailable.
            }
            return (tags, duration, false);
        }
    }
}
