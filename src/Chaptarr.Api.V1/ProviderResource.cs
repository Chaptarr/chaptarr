using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.DownloadClient;
using Chaptarr.Api.V1.Indexers;
using Chaptarr.Api.V1.Notifications;
using Chaptarr.Http.ClientSchema;
using Chaptarr.Http.REST;
using NzbDrone.Core.Indexers.DirectDownload;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Notifications.Plex.Server;

namespace Chaptarr.Api.V1
{
    public class ProviderResource<T> : RestResource
    {
        public string Name { get; set; }
        public List<Field> Fields { get; set; }
        public string ImplementationName { get; set; }
        public string Implementation { get; set; }

        // Prowlarr compatibility - Mousetrap uses this field to find MAM indexer
        // Prowlarr uses "MyAnonamouse" (lowercase 'a'), we use "MyAnonaMouse" (uppercase 'A')
        public string DefinitionName => Implementation == "MyAnonaMouse" ? "MyAnonamouse" : Implementation;
        public string ConfigContract { get; set; }
        public string InfoLink { get; set; }
        public ProviderMessage Message { get; set; }
        public HashSet<int> Tags { get; set; }

        public List<T> Presets { get; set; }
    }

    public class ProviderResourceMapper<TProviderResource, TProviderDefinition>
        where TProviderResource : ProviderResource<TProviderResource>, new()
        where TProviderDefinition : ProviderDefinition, new()
    {
        public virtual TProviderResource ToResource(TProviderDefinition definition)
        {
            // Ensure we have valid settings for schema generation
            var settings = definition.Settings;
            if (settings == null && !string.IsNullOrEmpty(definition.ConfigContract))
            {
                // Create default instance of settings if null
                var configContract = ProviderConfigTypeCache.Find(definition.ConfigContract);
                if (configContract != null)
                {
                    settings = (IProviderConfig)Activator.CreateInstance(configContract);
                }
            }

            return new TProviderResource
            {
                Id = definition.Id,

                Name = definition.Name,
                ImplementationName = definition.ImplementationName,
                Implementation = definition.Implementation,
                ConfigContract = definition.ConfigContract,
                Message = definition.Message,
                Tags = definition.Tags,
                Fields = settings != null ? SchemaBuilder.ToSchema(settings) : new List<Field>(),

                //Discord link for support
                InfoLink = string.Format("https://discord.gg/nqFGsGUug2",
                    definition.Implementation.ToLower())
            };
        }

        public virtual TProviderDefinition ToModel(TProviderResource resource)
        {
            if (resource == null)
            {
                return default(TProviderDefinition);
            }

            var definition = new TProviderDefinition
            {
                Id = resource.Id,

                Name = resource.Name,
                ImplementationName = resource.ImplementationName,
                Implementation = resource.Implementation,
                ConfigContract = resource.ConfigContract,
                Message = resource.Message,
                Tags = resource.Tags
            };

            var configContract = ProviderConfigTypeCache.Find(definition.ConfigContract);
            definition.Settings = configContract == null
                ? NullConfig.Instance
                : (IProviderConfig)SchemaBuilder.ReadFromSchema(resource.Fields ?? new List<Field>(), configContract);

            return definition;
        }
    }

    internal static class ProviderResourceCompatibilityProjection
    {
        public sealed class BookshelfPlexFields
        {
            [FieldDefinition(10, Label = "NotificationsSettingsUpdateLibrary", Type = FieldType.Checkbox)]
            public bool UpdateLibrary { get; set; }
        }

        public static void ApplyBookshelfLegacySettings<TProviderResource>(TProviderResource resource, IProviderConfig settings)
            where TProviderResource : ProviderResource<TProviderResource>
        {
            if (settings is PlexServerSettings plex)
            {
                var legacy = new BookshelfPlexFields { UpdateLibrary = plex.UpdateLibrary };
                SchemaBuilder.ReadFromSchema(resource.Fields ?? new List<Field>(), legacy);
                plex.UpdateLibrary = legacy.UpdateLibrary;
            }
        }

        public static TProviderResource ProjectBookshelfLegacyTemplateDefaults<TProviderResource>(TProviderResource resource)
            where TProviderResource : ProviderResource<TProviderResource>
        {
            if (resource == null || resource.Id != 0)
            {
                return resource;
            }

            if (resource is DownloadClientResource)
            {
                if (resource.Implementation is "TorrentDownloadStation" or "UsenetDownloadStation")
                {
                    resource.ImplementationName = "Download Station";
                }

                var message = resource.Implementation switch
                {
                    "TorrentDownloadStation" or "UsenetDownloadStation" => "Readarr is unable to connect to Download Station if 2-Factor Authentication is enabled on your DSM account",
                    "Flood" => "Readarr will handle automatic removal of torrents based on the current seed criteria in Settings -> Indexers",
                    "RTorrent" => "rTorrent will not pause torrents when they meet the seed criteria. Readarr will handle automatic removal of torrents based on the current seed criteria in Settings->Indexers only when Remove Completed is enabled.  After importing it will also set \"readarr_imported\" as an rTorrent view, which can be used in rTorrent scripts to customize behavior.",
                    _ => null
                };
                if (message != null && resource.Message != null && resource.Message.Type != ProviderMessageType.Error)
                {
                    // Definitions can share this object with native responses.
                    resource.Message = new ProviderMessage(message, resource.Message.Type);
                }

                // Public defaults from the predecessor template, never saved
                // credentials. Native constructors keep their hardened defaults.
                var publicPassword = resource.ConfigContract switch
                {
                    "DelugeSettings" => "deluge",
                    "NzbgetSettings" => "tegbzn6789",
                    _ => null
                };
                var category = resource.ConfigContract switch
                {
                    "DelugeSettings" or "QBittorrentSettings" or "RTorrentSettings" or "UTorrentSettings" => "readarr",
                    "NzbgetSettings" or "NzbVortexSettings" or "SabnzbdSettings" => "Readarr",
                    _ => null
                };
                foreach (var field in resource.Fields ?? new List<Field>())
                {
                    if (publicPassword != null && string.Equals(field.Name, "password", StringComparison.OrdinalIgnoreCase) &&
                        (field.Value == null || field.Value is string password && password.Length == 0))
                    {
                        field.Value = publicPassword;
                    }

                    if (category != null)
                    {
                        if (string.Equals(field.Name, "musicCategory", StringComparison.OrdinalIgnoreCase))
                        {
                            field.Value = category;
                        }
                        else if (string.Equals(field.Name, "audiobookCategory", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(field.Name, "ebookCategory", StringComparison.OrdinalIgnoreCase))
                        {
                            field.Value = null;
                        }
                    }
                    else if (resource.ConfigContract == "HadoukenSettings" && field.Name == "category")
                    {
                        field.Value = "readarr-music";
                    }
                    else if (resource.ConfigContract == "FloodSettings")
                    {
                        if (field.Name == "tags")
                        {
                            field.Value = new[] { "readarr" };
                        }
                        else if (field.Name is "audiobookTags" or "ebookTags")
                        {
                            field.Value = null;
                        }
                    }
                }

                return resource;
            }

            if (resource is NotificationResource notification && resource.ConfigContract == "PlexServerSettings")
            {
                foreach (var field in resource.Fields ?? new List<Field>())
                {
                    if (string.Equals(field.Name, "useSsl", StringComparison.OrdinalIgnoreCase))
                    {
                        field.Value = false;
                    }
                }

                notification.SupportsOnBookFileDelete = false;
                notification.SupportsOnBookFileDeleteForUpgrade = false;
                return resource;
            }

            // Legacy clients retain their known preset; requests resolve this
            // retired endpoint through the Newznab service handoff at runtime.
            if (resource is IndexerResource && resource.ConfigContract == "NewznabSettings" && resource.Name == "Nzb.su")
            {
                foreach (var field in resource.Fields ?? new List<Field>())
                {
                    if (string.Equals(field.Name, "baseUrl", StringComparison.OrdinalIgnoreCase) &&
                        field.Value is string url && url == "https://api.nzb.life")
                    {
                        field.Value = "https://api.nzb.su";
                    }
                }

                return resource;
            }

            if (resource is not IndexerResource indexer ||
                resource.ConfigContract != "MyAnonaMouseSettings")
            {
                return resource;
            }

            indexer.Enable = false;
            indexer.EnableRss = false;
            indexer.EnableAutomaticSearch = false;
            indexer.EnableInteractiveSearch = false;
            var defaults = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["baseUrl"] = "https://www.myanonamouse.net/",
                ["mamId"] = string.Empty,
                ["searchType"] = 0,
                ["searchInDescription"] = false,
                ["searchInSeries"] = false,
                ["searchInFilenames"] = false
            };
            foreach (var field in resource.Fields ?? new List<Field>())
            {
                if (defaults.TryGetValue(field.Name, out var value))
                {
                    field.Value = value;
                }
            }

            return resource;
        }

        public static TProviderResource ProjectBookshelfLegacyProviderIdentity<TProviderResource>(TProviderResource resource)
            where TProviderResource : ProviderResource<TProviderResource>
        {
            if (resource == null)
            {
                return null;
            }

            if (string.Equals(resource.Implementation, "ChaptarrImport", StringComparison.Ordinal))
            {
                resource.Implementation = "ReadarrImport";
                resource.ImplementationName = "Readarr";
                resource.ConfigContract = "ReadarrSettings";

                // The generated BookshelfNG template used the provider identity
                // as its default name. Keep saved instance names intact.
                if (resource.Id == 0)
                {
                    resource.Name = "ReadarrImport";
                }
            }
            else if (string.Equals(resource.Implementation, "LegacyAudiobookshelfMetadata", StringComparison.Ordinal))
            {
                resource.Implementation = "AudiobookshelfMetadata";
            }

            return resource;
        }

        public static TProviderResource ProjectBookshelfNativeProviderIdentity<TProviderResource>(TProviderResource resource)
            where TProviderResource : ProviderResource<TProviderResource>
        {
            if (resource == null)
            {
                return null;
            }

            if (string.Equals(resource.Implementation, "ReadarrImport", StringComparison.Ordinal))
            {
                resource.Implementation = "ChaptarrImport";
                resource.ImplementationName = "Chaptarr";
                resource.ConfigContract = "ChaptarrSettings";
            }
            else if (string.Equals(resource.Implementation, "AudiobookshelfMetadata", StringComparison.Ordinal))
            {
                resource.Implementation = "LegacyAudiobookshelfMetadata";
            }

            return resource;
        }

        public static TProviderResource ProjectBookshelfLegacyFields<TProviderResource>(TProviderResource resource, IProviderConfig settings = null)
            where TProviderResource : ProviderResource<TProviderResource>
        {
            if (settings is PlexServerSettings plex)
            {
                resource.Fields ??= new List<Field>();
                resource.Fields.RemoveAll(field => field.Name == "updateLibrary");
                resource.Fields.AddRange(SchemaBuilder.ToSchema(new BookshelfPlexFields { UpdateLibrary = plex.UpdateLibrary }));
            }

            if (resource?.ConfigContract != nameof(DirectDownloadSettings))
            {
                return resource;
            }

            var urls = resource.Fields?.FirstOrDefault(field =>
                string.Equals(field.Name, nameof(DirectDownloadSettings.Urls), StringComparison.OrdinalIgnoreCase));
            if (urls == null)
            {
                return resource;
            }

            // BookshelfNG exposed this setting as a tag field with an array value. ChaptarrNG
            // stores the same ordered URLs as newline-delimited text for its native textarea.
            urls.Type = "tag";
            if (urls.Value is string configuredUrls)
            {
                urls.Value = DirectDownloadSettings.NormalizeUrls(configuredUrls).ToList();
            }

            return resource;
        }
    }
}
