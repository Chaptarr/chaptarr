using System;
using System.Collections.Generic;
using System.Linq;

namespace NzbDrone.Core.MediaFiles.TagExtraction
{
    public enum TagExtractionDisposition
    {
        Evidence,
        NoisyOnly,
        Tagless,
        Failed
    }

    public sealed class TagExtractionResult
    {
        public const string FailureReason = "TAG_EXTRACTION_FAILED";
        public const string EvidenceStorageValue = "evidence";
        public const string NoisyOnlyStorageValue = "noisy_only";
        public const string TaglessStorageValue = "tagless";

        public TagExtractionResult(
            Dictionary<string, List<string>> tags,
            int? durationSeconds,
            TagExtractionDisposition disposition,
            string extractor = null,
            Exception error = null)
        {
            Tags = tags ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            DurationSeconds = durationSeconds;
            Disposition = disposition;
            Extractor = extractor;
            Error = error;
        }

        public Dictionary<string, List<string>> Tags { get; }
        public int? DurationSeconds { get; }
        public TagExtractionDisposition Disposition { get; }
        public string Extractor { get; }
        public Exception Error { get; }

        public bool Succeeded => Disposition != TagExtractionDisposition.Failed;

        public static TagExtractionDisposition Classify(Dictionary<string, List<string>> tags)
        {
            if (!HasAnyValues(tags))
            {
                return TagExtractionDisposition.Tagless;
            }

            return tags.Any(pair =>
                    !TagExclusionPolicy.IsExcludedFromMatching(pair.Key) &&
                    pair.Value != null &&
                    pair.Value.Any(value => !string.IsNullOrWhiteSpace(value)))
                ? TagExtractionDisposition.Evidence
                : TagExtractionDisposition.NoisyOnly;
        }

        public static string ToStorageValue(TagExtractionDisposition disposition)
        {
            return disposition switch
            {
                TagExtractionDisposition.Evidence => EvidenceStorageValue,
                TagExtractionDisposition.NoisyOnly => NoisyOnlyStorageValue,
                TagExtractionDisposition.Tagless => TaglessStorageValue,
                _ => null
            };
        }

        public static bool IsCacheableStorageValue(string value)
        {
            return value == EvidenceStorageValue || value == NoisyOnlyStorageValue || value == TaglessStorageValue;
        }

        public static bool TryParseStorageValue(string value, out TagExtractionDisposition disposition)
        {
            disposition = value?.Trim().ToLowerInvariant() switch
            {
                EvidenceStorageValue => TagExtractionDisposition.Evidence,
                NoisyOnlyStorageValue => TagExtractionDisposition.NoisyOnly,
                TaglessStorageValue => TagExtractionDisposition.Tagless,
                _ => TagExtractionDisposition.Failed
            };

            return disposition != TagExtractionDisposition.Failed;
        }

        private static bool HasAnyValues(Dictionary<string, List<string>> tags)
        {
            return tags != null &&
                   tags.Any(pair => pair.Value != null && pair.Value.Any(value => !string.IsNullOrWhiteSpace(value)));
        }
    }

    public sealed class TagExtractionException : Exception
    {
        public TagExtractionException(string path, Exception innerException = null)
            : base($"{TagExtractionResult.FailureReason}: Unable to read metadata from '{path}'.", innerException)
        {
            Path = path;
        }

        public string Path { get; }
        public string Reason => TagExtractionResult.FailureReason;
    }
}
