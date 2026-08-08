using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chaptarr.Http.REST;
using NzbDrone.Core.Profiles.Metadata;

namespace Chaptarr.Api.V1.Profiles.Metadata
{
    public class MetadataProfileResource : RestResource
    {
        public string Name { get; set; }
        public int ProfileType { get; set; }
        public double MinPopularity { get; set; }
        public bool SkipMissingDate { get; set; }
        public bool SkipMissingIsbn { get; set; }
        public bool SkipPartsAndSets { get; set; }
        public bool SkipSeriesSecondary { get; set; }
        public bool SkipMissingIdentifierOmnibus { get; set; }
        public bool SkipOmnibus { get; set; }
        public bool SkipMissingAsin { get; set; }

        // This property can receive either a string or an array from the frontend
        [JsonConverter(typeof(AllowedLanguagesConverter))]
        public string AllowedLanguages { get; set; }
        public int MinPages { get; set; }
        public List<string> Ignored { get; set; }

        public MetadataProfileResource()
        {
            Ignored = new List<string>();
            AllowedLanguages = string.Empty;
        }
    }

    // Custom converter to handle both string and array formats
    public class AllowedLanguagesConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.StartArray)
            {
                var languages = new List<string>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        languages.Add(reader.GetString());
                    }
                }

                return string.Join(",", languages);
            }
            else if (reader.TokenType == JsonTokenType.String)
            {
                return reader.GetString();
            }
            else if (reader.TokenType == JsonTokenType.Null)
            {
                return string.Empty;
            }

            throw new JsonException($"Unexpected token type {reader.TokenType} when parsing AllowedLanguages");
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value);
        }
    }

    public static class MetadataProfileResourceMapper
    {
        public static MetadataProfileResource ToResource(this MetadataProfile model)
        {
            if (model == null)
            {
                return null;
            }

            return new MetadataProfileResource
            {
                Id = model.Id,
                Name = model.Name,
                ProfileType = (int)model.ProfileType,
                MinPopularity = model.MinPopularity,
                SkipMissingDate = model.SkipMissingDate,
                SkipMissingIsbn = model.SkipMissingIsbn,
                SkipPartsAndSets = model.SkipPartsAndSets,
                SkipSeriesSecondary = model.SkipSeriesSecondary,
                SkipMissingIdentifierOmnibus = model.SkipMissingIdentifierOmnibus,
                SkipOmnibus = model.SkipOmnibus,
                SkipMissingAsin = model.SkipMissingAsin,
                AllowedLanguages = model.AllowedLanguages,
                MinPages = model.MinPages,
                Ignored = model.Ignored
            };
        }

        public static MetadataProfile ToModel(this MetadataProfileResource resource)
        {
            if (resource == null)
            {
                return null;
            }

            if (!Enum.IsDefined(typeof(MetadataProfileType), resource.ProfileType))
            {
                throw new BadRequestException("Profile type must be General, Audiobook, or Ebook");
            }

            return new MetadataProfile
            {
                Id = resource.Id,
                Name = resource.Name,
                ProfileType = (MetadataProfileType)resource.ProfileType,
                MinPopularity = resource.MinPopularity,
                SkipMissingDate = resource.SkipMissingDate,
                SkipMissingIsbn = resource.SkipMissingIsbn,
                SkipPartsAndSets = resource.SkipPartsAndSets,
                SkipSeriesSecondary = resource.SkipSeriesSecondary,
                SkipMissingIdentifierOmnibus = resource.SkipMissingIdentifierOmnibus,
                SkipOmnibus = resource.SkipOmnibus,
                SkipMissingAsin = resource.SkipMissingAsin,
                AllowedLanguages = resource.AllowedLanguages,
                MinPages = resource.MinPages,
                Ignored = resource.Ignored ?? new List<string>()
            };
        }

        public static List<MetadataProfileResource> ToResource(this IEnumerable<MetadataProfile> models)
        {
            return models.Select(ToResource).ToList();
        }
    }
}
