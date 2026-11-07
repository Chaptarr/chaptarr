using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.MediaFiles.TagExtraction
{
    public interface ITagExtractionService
    {
        Dictionary<string, List<string>> ExtractTags(string path);
        (Dictionary<string, List<string>> Tags, int? DurationSeconds) ExtractTagsAndDuration(string path);
        TagExtractionResult ExtractTagsWithResult(string path);
        TagExtractionResult ExtractTagsAndDurationWithResult(string path);
    }

    public class TagExtractionService : ITagExtractionService
    {
        private readonly IEnumerable<ITagExtractor> _extractors;
        private readonly IAudioDurationResolver _durationResolver;
        private readonly Logger _logger;

        public TagExtractionService(
            IEnumerable<ITagExtractor> extractors,
            IAudioDurationResolver durationResolver,
            Logger logger)
        {
            _extractors = extractors;
            _durationResolver = durationResolver;
            _logger = logger;
        }

        public Dictionary<string, List<string>> ExtractTags(string path)
        {
            return RequireSuccess(ExtractTagsWithResult(path), path).Tags;
        }

        public (Dictionary<string, List<string>> Tags, int? DurationSeconds) ExtractTagsAndDuration(string path)
        {
            var result = RequireSuccess(ExtractTagsAndDurationWithResult(path), path);
            return (result.Tags, result.DurationSeconds);
        }

        public TagExtractionResult ExtractTagsWithResult(string path)
        {
            return Extract(path, includeDuration: false);
        }

        public TagExtractionResult ExtractTagsAndDurationWithResult(string path)
        {
            return Extract(path, includeDuration: true);
        }

        private TagExtractionResult Extract(string path, bool includeDuration)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var isMp3 = includeDuration && Mp3DurationReader.IsMp3Path(path);
            int? durationSeconds = null;
            AudioDurationSource? mp3DurationSource = null;

            if (isMp3)
            {
                var resolution = _durationResolver.ResolveMp3(path);
                durationSeconds = MediaDuration.FromTimeSpan(resolution.Duration);
                if (resolution.HasDuration)
                {
                    mp3DurationSource = resolution.Source;
                }
            }

            TagExtractionResult bestNonEvidence = null;
            Exception lastError = null;
            var attempted = 0;

            foreach (var extractor in _extractors.OrderBy(extractor => extractor.Priority))
            {
                // Availability is deliberately checked just-in-time. In the normal TagLibSharp-success
                // path this avoids even probing the external FFprobe process.
                if (!SafeAvailable(extractor))
                {
                    continue;
                }

                attempted++;
                try
                {
                    Dictionary<string, List<string>> tags;
                    int? extractedDuration = null;

                    if (includeDuration && !isMp3 && extractor is ITagExtractorWithDuration withDuration)
                    {
                        (tags, extractedDuration) = withDuration.ExtractTagsAndDuration(path);
                    }
                    else
                    {
                        tags = extractor.ExtractTags(path);
                    }

                    tags ??= new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                    PreserveBookshelfAudioTagFields(bestNonEvidence?.Tags, tags);
                    RemoveEncodeKeys(tags);
                    TagCanonicalizer.AddCanonicalKeys(tags);
                    EnsureBookshelfAudioTagFields(path, tags);

                    if (!durationSeconds.HasValue && extractedDuration.HasValue && extractedDuration.Value > 0)
                    {
                        durationSeconds = extractedDuration;
                    }

                    var disposition = TagExtractionResult.Classify(tags);
                    var current = new TagExtractionResult(tags, durationSeconds, disposition, extractor.Name);
                    if (disposition == TagExtractionDisposition.Evidence)
                    {
                        sw.Stop();
                        LogResult(current, path, sw.ElapsedMilliseconds, mp3DurationSource);
                        return current;
                    }

                    if (bestNonEvidence == null ||
                        (bestNonEvidence.Disposition == TagExtractionDisposition.Tagless && disposition == TagExtractionDisposition.NoisyOnly))
                    {
                        bestNonEvidence = current;
                    }

                    _logger.Debug("[TAG-EXTRACT] {0} produced {1} metadata for '{2}', trying next",
                        extractor.Name,
                        disposition == TagExtractionDisposition.NoisyOnly ? "excluded-only" : "no",
                        System.IO.Path.GetFileName(path));
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    _logger.Warn(ex, "[TAG-EXTRACT] {0} failed for '{1}', trying next if possible", extractor.Name, path);
                }
            }

            sw.Stop();
            if (bestNonEvidence != null)
            {
                var result = new TagExtractionResult(
                    bestNonEvidence.Tags,
                    durationSeconds,
                    bestNonEvidence.Disposition,
                    bestNonEvidence.Extractor);
                LogResult(result, path, sw.ElapsedMilliseconds, mp3DurationSource);
                return result;
            }

            var failure = new TagExtractionResult(
                new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase),
                durationSeconds,
                TagExtractionDisposition.Failed,
                error: lastError ?? new InvalidOperationException(attempted == 0 ? "No tag extractors available" : "All tag extractors failed"));

            _logger.Warn("[TAG-EXTRACT] {0} for '{1}' after {2} extractor attempt(s) in {3}ms",
                TagExtractionResult.FailureReason,
                path,
                attempted,
                sw.ElapsedMilliseconds);
            return failure;
        }

        private void LogResult(TagExtractionResult result, string path, long elapsedMilliseconds, AudioDurationSource? mp3DurationSource)
        {
            _logger.Debug("[TAG-EXTRACT] {0} returned {1} ({2} keys) from '{3}' in {4}ms (duration={5})",
                result.Extractor,
                result.Disposition,
                result.Tags.Count,
                System.IO.Path.GetFileName(path),
                elapsedMilliseconds,
                result.DurationSeconds.HasValue
                    ? $"{result.DurationSeconds.Value}s{(mp3DurationSource.HasValue ? $" ({mp3DurationSource.Value})" : string.Empty)}"
                    : "null");
        }

        private static TagExtractionResult RequireSuccess(TagExtractionResult result, string path)
        {
            if (result?.Succeeded == true)
            {
                return result;
            }

            throw new TagExtractionException(path, result?.Error);
        }

        private static void PreserveBookshelfAudioTagFields(
            IReadOnlyDictionary<string, List<string>> earlierTags,
            IDictionary<string, List<string>> laterTags)
        {
            if (earlierTags == null || laterTags == null)
            {
                return;
            }

            foreach (var key in new[] { BookshelfAudioTagFields.Book, BookshelfAudioTagFields.Title })
            {
                var entry = earlierTags.FirstOrDefault(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase));
                if (entry.Key != null && !laterTags.Keys.Any(candidate => string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase)))
                {
                    laterTags[key] = entry.Value?.ToList() ?? new List<string> { string.Empty };
                }
            }
        }

        private static void EnsureBookshelfAudioTagFields(string path, IDictionary<string, List<string>> tags)
        {
            if (tags == null ||
                !MediaFileExtensions.AudioExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            if (!tags.Keys.Any(key => string.Equals(key, BookshelfAudioTagFields.Book, StringComparison.OrdinalIgnoreCase)))
            {
                tags[BookshelfAudioTagFields.Book] = new List<string>
                {
                    GetFirstTagValue(tags, "ALBUM", "ALBUMSORT", "ALBUM_SORT", "ALBUM SORT", "ID3v2:TSOA", "MP4:soal", "XIPH:ALBUMSORT") ?? string.Empty
                };
            }

            if (!tags.Keys.Any(key => string.Equals(key, BookshelfAudioTagFields.Title, StringComparison.OrdinalIgnoreCase)))
            {
                tags[BookshelfAudioTagFields.Title] = new List<string>
                {
                    GetFirstTagValue(tags, "TITLE", "TITLESORT", "TITLE_SORT", "TITLE SORT", "ID3v2:TSOT", "MP4:sonm", "XIPH:TITLESORT") ?? string.Empty
                };
            }
        }

        private static string GetFirstTagValue(IDictionary<string, List<string>> tags, params string[] names)
        {
            foreach (var name in names)
            {
                var pair = tags.FirstOrDefault(entry => string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase));
                var value = pair.Value?.FirstOrDefault();
                if (value != null)
                {
                    return value;
                }
            }

            return null;
        }

        // Remove any tag entries whose key contains "encode" (case-insensitive)
        private void RemoveEncodeKeys(Dictionary<string, List<string>> tags)
        {
            if (tags == null || tags.Count == 0) return;
            try
            {
                var toRemove = new List<string>();
                foreach (var key in tags.Keys)
                {
                    if (string.IsNullOrWhiteSpace(key)) continue;
                    // Remove encoder-related and special diagnostic keys (e.g., __UNSUPPORTED__, __FORMAT__)
                    if (key.IndexOf("encode", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                        key.StartsWith("__", System.StringComparison.Ordinal))
                    {
                        toRemove.Add(key);
                    }
                }
                if (toRemove.Count > 0)
                {
                    foreach (var k in toRemove) tags.Remove(k);
                }
            }
            catch (Exception ex)
            {
                _logger.Trace(ex, "[TAG-EXTRACT] Failed to remove encode keys from tags");
            }
        }

        private static bool SafeAvailable(ITagExtractor extractor)
        {
            try { return extractor.IsAvailable; } catch { return false; }
        }
    }
}
