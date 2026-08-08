using System;
using System.Collections.Generic;

namespace NzbDrone.Core.Books
{
    public sealed class ProviderUrlMap : Dictionary<string, string>
    {
        public ProviderUrlMap()
            : base(StringComparer.OrdinalIgnoreCase)
        {
        }

        public ProviderUrlMap(IDictionary<string, string> values)
            : this()
        {
            if (values == null || values.Count == 0)
            {
                return;
            }

            foreach (var kvp in values)
            {
                SetNormalized(kvp.Key, kvp.Value);
            }
        }

        public void SetNormalized(string key, string url)
        {
            key = key?.Trim();
            url = url?.Trim();

            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            this[key.ToLowerInvariant()] = url;
        }
    }
}

