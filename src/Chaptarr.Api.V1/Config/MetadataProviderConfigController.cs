using System.Collections.Generic;
using System.Linq;
using Chaptarr.Http;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MetadataSource.BookInfo;
using NzbDrone.Http.REST.Attributes;

namespace Chaptarr.Api.V1.Config
{
    [V1ApiController("config/metadataprovider")]
    public class MetadataProviderConfigController : ConfigController<MetadataProviderConfigResource>
    {
        public MetadataProviderConfigController(IConfigService configService)
            : base(configService)
        {
        }

        protected override MetadataProviderConfigResource ToResource(IConfigService model)
        {
            return MetadataProviderConfigResourceMapper.ToResource(model);
        }

        [RestPutById]
        public override ActionResult<MetadataProviderConfigResource> SaveConfig([FromBody] MetadataProviderConfigResource resource)
        {
            var legacySourceValues = new[]
            {
                resource.EnableGoogleBooks,
                resource.EnableLoc,
                resource.EnableGutendex,
                resource.EnableInternetArchive,
                resource.EnableNdl,
                resource.EnableEuropeana,
                resource.EnableApifyGoodreads
            };
            var runtimeSourceValues = new[] { resource.EnableOpenLibrary, resource.EnableHardcover, resource.EnableMetadataApi };
            var sources = new List<string>();

            AddSource(resource.EnableGoogleBooks, "googlebooks", sources);
            AddSource(resource.EnableLoc, "loc", sources);
            AddSource(resource.EnableGutendex, "gutendex", sources);
            AddSource(resource.EnableInternetArchive, "internetarchive", sources);
            AddSource(resource.EnableNdl, "ndl", sources);
            AddSource(resource.EnableEuropeana, "europeana", sources);
            AddSource(resource.EnableApifyGoodreads, "apify-goodreads", sources);
            AddSource(resource.EnableOpenLibrary, AdditionalMetadataSources.OpenLibrary, sources);
            AddSource(resource.EnableHardcover, AdditionalMetadataSources.Hardcover, sources);
            AddSource(resource.EnableMetadataApi, AdditionalMetadataSources.MetadataApi, sources);

            var dictionary = new Dictionary<string, object>
            {
                ["AudibleMarketplace"] = resource.AudibleMarketplace,
                ["WriteAudioTags"] = resource.WriteAudioTags,
                ["ScrubAudioTags"] = resource.ScrubAudioTags,
                ["WriteBookTags"] = resource.WriteBookTags,
                ["UpdateCovers"] = resource.UpdateCovers,
                ["EmbedMetadata"] = resource.EmbedMetadata
            };

            var fieldSourcePreferences = new Dictionary<string, string>
            {
                ["MetadataTitleSourcePreference"] = resource.MetadataTitleSourcePreference,
                ["MetadataDescriptionSourcePreference"] = resource.MetadataDescriptionSourcePreference,
                ["MetadataPublisherSourcePreference"] = resource.MetadataPublisherSourcePreference,
                ["MetadataLanguageSourcePreference"] = resource.MetadataLanguageSourcePreference,
                ["MetadataReleaseDateSourcePreference"] = resource.MetadataReleaseDateSourcePreference,
                ["MetadataPageCountSourcePreference"] = resource.MetadataPageCountSourcePreference,
                ["MetadataCoverSourcePreference"] = resource.MetadataCoverSourcePreference,
                ["MetadataAuthorImageSourcePreference"] = resource.MetadataAuthorImageSourcePreference,
                ["MetadataGenresSourcePreference"] = resource.MetadataGenresSourcePreference
            };

            foreach (var preference in fieldSourcePreferences)
            {
                if (preference.Value == null)
                {
                    continue;
                }

                if (!AdditionalMetadataSources.IsValidFieldPreference(preference.Value))
                {
                    return BadRequest("Metadata field source preference is not supported.");
                }

                dictionary[preference.Key] = preference.Value;
            }

            // Older clients do not send these fields. Keep their saved selection.
            if (runtimeSourceValues.Any(value => value.HasValue))
            {
                dictionary["MetadataCatalogSources"] = string.Join(",", sources);
            }
            else if (!_configService.IsDefined("MetadataCatalogSources") && legacySourceValues.Any(value => value.HasValue))
            {
                dictionary["AdditionalMetadataSources"] = string.Join(",", sources);
            }

            SaveSecret(dictionary, "GoogleBooksApiKey", resource.GoogleBooksApiKey, resource.ClearGoogleBooksApiKey);
            SaveSecret(dictionary, "EuropeanaApiKey", resource.EuropeanaApiKey, resource.ClearEuropeanaApiKey);
            SaveSecret(dictionary, "ApifyToken", resource.ApifyToken, resource.ClearApifyToken);

            if (!resource.HardcoverAuthFromEnvironment)
            {
                if (resource.ClearHardcoverAuth)
                {
                    dictionary["HardcoverApiToken"] = string.Empty;
                }
                else if (!string.IsNullOrWhiteSpace(resource.HardcoverAuth))
                {
                    dictionary["HardcoverApiToken"] = NormalizeHardcoverToken(resource.HardcoverAuth);
                }
            }

            if (!resource.ApifyGoodreadsActorFromEnvironment)
            {
                SaveSecret(dictionary, "ApifyGoodreadsActor", resource.ApifyGoodreadsActor, resource.ClearApifyGoodreadsActor);
            }

            if (resource.ApifyGoodreadsInputTemplate != null && !resource.ApifyGoodreadsInputTemplateFromEnvironment)
            {
                dictionary["ApifyGoodreadsInputTemplate"] = resource.ApifyGoodreadsInputTemplate;
            }

            if (resource.OpenLibraryContactEmail != null && !resource.OpenLibraryContactEmailFromEnvironment)
            {
                dictionary["OpenLibraryContactEmail"] = resource.OpenLibraryContactEmail.Trim();
            }

            _configService.SaveConfigDictionary(dictionary);

            var updatedResource = ToResource(_configService);
            updatedResource.Id = 1;
            return Accepted(updatedResource);
        }

        private static void AddSource(bool? enabled, string source, ICollection<string> sources)
        {
            if (enabled == true)
            {
                sources.Add(source);
            }
        }

        private static void SaveSecret(
            IDictionary<string, object> dictionary,
            string configKey,
            string inputValue,
            bool clear)
        {
            if (clear)
            {
                dictionary[configKey] = string.Empty;
            }
            else if (!string.IsNullOrWhiteSpace(inputValue))
            {
                dictionary[configKey] = inputValue;
            }
        }

        private static string NormalizeHardcoverToken(string value)
        {
            var token = value.Trim();
            return token.StartsWith("Bearer ", global::System.StringComparison.OrdinalIgnoreCase)
                ? token.Substring(7).Trim()
                : token;
        }
    }
}
