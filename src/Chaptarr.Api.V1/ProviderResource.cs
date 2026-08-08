using System;
using System.Collections.Generic;
using Chaptarr.Http.ClientSchema;
using Chaptarr.Http.REST;
using NzbDrone.Core.ThingiProvider;

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
}
