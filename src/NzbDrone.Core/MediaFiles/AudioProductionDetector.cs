using System;
using System.Collections.Generic;
using System.Linq;

namespace NzbDrone.Core.MediaFiles
{
    public static class AudioProductionDetector
    {
        private static readonly HashSet<string> ExactCastLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "cast"
        };

        public static bool IsDramatizedOrFullCast(bool isGraphicAudio, string audioProductionType, params string[] fields)
        {
            return IsDramatizedOrFullCast(isGraphicAudio, audioProductionType, fields?.AsEnumerable());
        }

        public static bool IsDramatizedOrFullCast(bool isGraphicAudio, string audioProductionType, IEnumerable<string> fields)
        {
            if (isGraphicAudio)
            {
                return true;
            }

            if (IsDramatizedProductionType(audioProductionType))
            {
                return true;
            }

            return ContainsIndicator(fields);
        }

        public static bool IsDramatizedOrFullCast(IDictionary<string, List<string>> tags)
        {
            return ContainsIndicator(Flatten(tags));
        }

        public static IEnumerable<string> Flatten(IDictionary<string, List<string>> tags)
        {
            if (tags == null)
            {
                return Enumerable.Empty<string>();
            }

            return tags
                .Where(kvp => kvp.Value != null)
                .SelectMany(kvp => kvp.Value.Append(kvp.Key))
                .Where(value => !string.IsNullOrWhiteSpace(value));
        }

        public static bool ContainsIndicator(IEnumerable<string> fields)
        {
            if (fields == null)
            {
                return false;
            }

            foreach (var field in fields)
            {
                if (string.IsNullOrWhiteSpace(field))
                {
                    continue;
                }

                if (ExactCastLabels.Contains(field.Trim()))
                {
                    return true;
                }

                var normalizedField = Normalize(field);

                foreach (var indicator in AudioProductionConstants.GraphicAudioIndicators)
                {
                    if (normalizedField.Contains(Normalize(indicator)))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsDramatizedProductionType(string audioProductionType)
        {
            if (string.IsNullOrWhiteSpace(audioProductionType))
            {
                return false;
            }

            var normalized = Normalize(audioProductionType);
            return ContainsIndicator(new[] { audioProductionType }) ||
                   normalized.Contains("performance");
        }

        private static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var chars = value
                .ToLowerInvariant()
                .Where(char.IsLetterOrDigit)
                .ToArray();

            return new string(chars);
        }
    }
}
