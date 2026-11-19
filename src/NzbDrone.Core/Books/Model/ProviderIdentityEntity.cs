using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Equ;

namespace NzbDrone.Core.Books
{
    // The stored snapshot owns identity; ProviderAliasIndex is only its database projection.
    public abstract class ProviderIdentityEntity<T> : Entity<T>
        where T : ProviderIdentityEntity<T>
    {
        private string _providerIds;
        private HashSet<string> _storedIds;
        private HashSet<string> _incomingIds;

        [MemberwiseEqualityIgnore]
        [JsonIgnore]
        public string ProviderIds
        {
            get => _providerIds;
            set
            {
                _providerIds = value;
                _storedIds = null;
                _incomingIds = null;
                ProviderIdsChanged = false;
            }
        }

        [MemberwiseEqualityIgnore]
        [JsonIgnore]
        public bool ProviderIdsChanged { get; private set; }

        [MemberwiseEqualityIgnore]
        [JsonIgnore]
        public HashSet<string> RemoteProviderIds
        {
            get
            {
                if (_incomingIds != null) return _incomingIds;
                if (_storedIds == null && _providerIds != null)
                {
                    var scopes = JsonSerializer.Deserialize<Dictionary<string, string[]>>(_providerIds);
                    _storedIds = (scopes?.GetValueOrDefault(IdentityScope) ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                }
                return _storedIds;
            }
            set
            {
                _incomingIds = value;
                ProviderIdsChanged = value != null;
            }
        }

        protected abstract string IdentityScope { get; }
        protected abstract IEnumerable<string> GetIdentityIds();

        public override IEnumerable<string> PrepareForPersistence(IReadOnlyCollection<string> fields)
        {
            if (fields != null && !fields.Contains(nameof(ProviderIds))) yield break;
            if (Id != 0 && !ProviderIdsChanged) yield break;
            var ids = ProviderAliasService.NormalizeAliases(GetIdentityIds())
                .Select(id => $"{id.Provider}:{id.NormalizedProviderId}")
                .OrderBy(id => id, StringComparer.Ordinal).ToArray();
            var stored = _providerIds == null ? null : JsonSerializer.Deserialize<Dictionary<string, string[]>>(_providerIds);
            // PostgreSQL migration JSON has different spacing/order. Equal snapshots must
            // not rebuild the projection; legacy extra scopes still disappear on refresh.
            if (stored?.Count != 1 || !stored.TryGetValue(IdentityScope, out var previous) ||
                !ids.ToHashSet(StringComparer.Ordinal).SetEquals(previous))
                _providerIds = JsonSerializer.Serialize(new Dictionary<string, string[]> { [IdentityScope] = ids });
            _storedIds = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
            ProviderIdsChanged = true;
            yield return nameof(ProviderIds);
        }
    }
}
