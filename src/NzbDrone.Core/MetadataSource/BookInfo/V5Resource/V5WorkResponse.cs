using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NzbDrone.Core.MetadataSource.BookInfo.V5Resource
{
    public class V5WorkResponse
    {
        public V5.V5Book Work { get; set; }
        public List<V5.V5Edition> Editions { get; set; } = new List<V5.V5Edition>();
        public List<V5Author> Authors { get; set; } = new List<V5Author>();
        public List<V5Series> Series { get; set; } = new List<V5Series>();
        public V5Stats Stats { get; set; }
    }

    public class V5Author
    {
        public string Name { get; set; }
        public string Biography { get; set; }
        public string BirthDate { get; set; }
        public string DeathDate { get; set; }
        public string ImageUrl { get; set; }
        [JsonProperty("providerIds")]
        public Dictionary<string, object> LegacyProviderIds { get; set; } = new Dictionary<string, object>();
        [JsonProperty("providerIdsAll")]
        public Dictionary<string, List<string>> ProviderIdsAll { get; set; } = new Dictionary<string, List<string>>();
        [JsonIgnore]
        public Dictionary<string, object> ProviderIds => ProviderIdMapCoercion.ToObjectMap(ProviderIdsAll, LegacyProviderIds);
        public string Role { get; set; }
    }

    public class V5Series
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public double? Position { get; set; }
        [JsonProperty("providerIds")]
        public Dictionary<string, object> LegacyProviderIds { get; set; } = new Dictionary<string, object>();
        [JsonProperty("providerIdsAll")]
        public Dictionary<string, List<string>> ProviderIdsAll { get; set; } = new Dictionary<string, List<string>>();
        [JsonIgnore]
        public Dictionary<string, object> ProviderIds => ProviderIdMapCoercion.ToObjectMap(ProviderIdsAll, LegacyProviderIds);
    }

    public class V5Stats
    {
        public int? TotalEditions { get; set; }
        public int? AudiobookEditions { get; set; }
        public int? EbookEditions { get; set; }
        public int? PhysicalEditions { get; set; }
        public int? LanguageCount { get; set; }
        public List<string> Languages { get; set; } = new List<string>();
        public int? EarliestPublication { get; set; }
        public int? LatestPublication { get; set; }
        public double? AverageRating { get; set; }
        public int? TotalRatings { get; set; }
    }

    internal static class ProviderIdMapCoercion
    {
        public static Dictionary<string, List<string>> ToListMap(
            Dictionary<string, List<string>> providerIdsAll,
            Dictionary<string, object> legacyProviderIds)
        {
            if (HasAnyValue(providerIdsAll))
            {
                return providerIdsAll;
            }

            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (legacyProviderIds == null)
            {
                return result;
            }

            foreach (var providerId in legacyProviderIds)
            {
                result[providerId.Key] = EnumerateProviderValues(providerId.Value).ToList();
            }

            return result;
        }

        public static Dictionary<string, object> ToObjectMap(
            Dictionary<string, List<string>> providerIdsAll,
            Dictionary<string, object> legacyProviderIds)
        {
            if (!HasAnyValue(providerIdsAll))
            {
                return legacyProviderIds ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            }

            return providerIdsAll.ToDictionary(
                kvp => kvp.Key,
                kvp => (object)(kvp.Value ?? new List<string>()),
                StringComparer.OrdinalIgnoreCase);
        }

        private static bool HasAnyValue(Dictionary<string, List<string>> providerIdsAll)
        {
            return providerIdsAll?.Values.Any(values => values?.Any(value => !string.IsNullOrWhiteSpace(value)) == true) == true;
        }

        private static IEnumerable<string> EnumerateProviderValues(object raw)
        {
            switch (raw)
            {
                case null:
                    yield break;
                case string value when !string.IsNullOrWhiteSpace(value):
                    yield return value.Trim();
                    yield break;
                case int value:
                    yield return value.ToString();
                    yield break;
                case long value:
                    yield return value.ToString();
                    yield break;
                case JValue value when !string.IsNullOrWhiteSpace(value.ToString()):
                    yield return value.ToString().Trim();
                    yield break;
                case JArray values:
                    foreach (var value in values)
                    {
                        var coerced = value?.ToString();
                        if (!string.IsNullOrWhiteSpace(coerced))
                        {
                            yield return coerced.Trim();
                        }
                    }
                    yield break;
                case IEnumerable<object> values:
                    foreach (var value in values)
                    {
                        var coerced = value?.ToString();
                        if (!string.IsNullOrWhiteSpace(coerced))
                        {
                            yield return coerced.Trim();
                        }
                    }
                    yield break;
                default:
                    yield break;
            }
        }
    }
}
