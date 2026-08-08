using System;
using System.Collections.Generic;
using System.Linq;

namespace NzbDrone.Core.ThingiProvider
{
    public static class ProviderConfigTypeCache
    {
        private static readonly IReadOnlyDictionary<string, Type> TypesByName = BuildTypesByName();

        public static Type Find(string configContract)
        {
            if (string.IsNullOrWhiteSpace(configContract))
            {
                return null;
            }

            TypesByName.TryGetValue(configContract, out var type);
            return type;
        }

        private static IReadOnlyDictionary<string, Type> BuildTypesByName()
        {
            var configInterfaceType = typeof(IProviderConfig);

            return configInterfaceType.Assembly.GetTypes()
                .Where(type => configInterfaceType.IsAssignableFrom(type) &&
                               type.IsClass &&
                               !type.IsAbstract)
                .ToDictionary(type => type.Name, type => type, StringComparer.OrdinalIgnoreCase);
        }
    }
}

