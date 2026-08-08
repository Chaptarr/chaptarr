using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.MediaFiles.TagExtraction
{
    public class FfprobeTagExtractor : ITagExtractorWithDuration
    {
        private static readonly HashSet<string> ExcludedKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            // Container/technical metadata that pollutes matching
            "major_brand",
            "minor_version",
            "compatible_brands",
            "encoder",
            "creation_time"
        };

        private readonly IExternalToolsService _externalTools;
        private readonly Lazy<bool> _isAvailable;

        public FfprobeTagExtractor(IExternalToolsService externalTools)
        {
            _externalTools = externalTools;
            _isAvailable = new Lazy<bool>(() =>
            {
                try
                {
                    return _externalTools.IsFFprobeAvailable();
                }
                catch
                {
                    return false;
                }
            });
        }

        public bool IsAvailable => _isAvailable.Value;

        public int Priority => 3;
        public string Name => "FFprobe";

        public Dictionary<string, List<string>> ExtractTags(string path)
        {
            return ExtractTagsAndDuration(path).Tags;
        }

        public (Dictionary<string, List<string>> Tags, int? DurationSeconds) ExtractTagsAndDuration(string path)
        {
            var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            int? durationSeconds = null;

            if (string.IsNullOrWhiteSpace(path))
            {
                return (tags, durationSeconds);
            }

            var json = _externalTools.ExecuteFFprobe(
                new[]
                {
                    "-v", "quiet",
                    "-print_format", "json",
                    "-show_format",
                    path
                },
                timeoutMs: 20000);

            if (string.IsNullOrWhiteSpace(json))
            {
                throw new InvalidDataException("FFprobe returned no metadata output.");
            }

            try
            {
                using var document = JsonDocument.Parse(json);

                if (!document.RootElement.TryGetProperty("format", out var formatElement))
                {
                    throw new InvalidDataException("FFprobe output did not contain a format object.");
                }

                if (formatElement.TryGetProperty("duration", out var durationElement))
                {
                    var durationText = durationElement.ValueKind switch
                    {
                        JsonValueKind.String => durationElement.GetString(),
                        JsonValueKind.Number => durationElement.ToString(),
                        _ => null
                    };

                    if (!string.IsNullOrWhiteSpace(durationText) &&
                        double.TryParse(durationText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) &&
                        seconds > 0)
                    {
                        durationSeconds = (int)Math.Round(seconds);
                    }
                }

                if (!formatElement.TryGetProperty("tags", out var tagsElement) || tagsElement.ValueKind != JsonValueKind.Object)
                {
                    return (tags, durationSeconds);
                }

                foreach (var property in tagsElement.EnumerateObject())
                {
                    var key = property.Name?.Trim();
                    if (string.IsNullOrWhiteSpace(key) || ExcludedKeys.Contains(key))
                    {
                        continue;
                    }

                    switch (property.Value.ValueKind)
                    {
                        case JsonValueKind.String:
                            Add(tags, key, property.Value.GetString());
                            break;
                        case JsonValueKind.Number:
                            Add(tags, key, property.Value.ToString());
                            break;
                        case JsonValueKind.True:
                        case JsonValueKind.False:
                            Add(tags, key, property.Value.GetBoolean().ToString());
                            break;
                        case JsonValueKind.Array:
                            foreach (var element in property.Value.EnumerateArray())
                            {
                                if (element.ValueKind == JsonValueKind.String)
                                {
                                    Add(tags, key, element.GetString());
                                }
                                else if (element.ValueKind == JsonValueKind.Number)
                                {
                                    Add(tags, key, element.ToString());
                                }
                                else if (element.ValueKind == JsonValueKind.True || element.ValueKind == JsonValueKind.False)
                                {
                                    Add(tags, key, element.GetBoolean().ToString());
                                }
                            }
                            break;
                        default:
                            break;
                    }
                }
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("FFprobe returned invalid metadata JSON.", ex);
            }

            return (tags, durationSeconds);
        }

        private static void Add(Dictionary<string, List<string>> dict, string key, string value)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var trimmed = value.Trim();
            if (trimmed.Length == 0)
            {
                return;
            }

            if (!dict.TryGetValue(key, out var list))
            {
                list = new List<string>();
                dict[key] = list;
            }

            if (!list.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(trimmed);
            }
        }
    }
}
