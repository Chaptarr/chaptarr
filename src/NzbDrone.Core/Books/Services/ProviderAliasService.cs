using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.MetadataSource;

namespace NzbDrone.Core.Books
{
    public interface IProviderAliasService
    {
        void ReplaceAliases(string entityType, int entityId, string scope, IEnumerable<string> providerIds);
        void DeleteAliases(string entityType, int entityId);
        List<int> FindBookIds(string scope, IEnumerable<string> providerIds);
        List<int> FindAuthorIds(IEnumerable<string> providerIds);
        List<(string Provider, string NormalizedProviderId)> NormalizeProviderIds(IEnumerable<string> providerIds);
    }

    public class ProviderAliasService : IProviderAliasService
    {
        private readonly IProviderAliasRepository _repository;

        public ProviderAliasService(IProviderAliasRepository repository)
        {
            _repository = repository;
        }

        public void ReplaceAliases(string entityType, int entityId, string scope, IEnumerable<string> providerIds)
        {
            if (entityId <= 0 || string.IsNullOrWhiteSpace(entityType) || string.IsNullOrWhiteSpace(scope))
            {
                return;
            }

            var now = DateTime.UtcNow;
            var aliases = NormalizeProviderIds(providerIds)
                .Select(alias => new ProviderAlias
                {
                    EntityType = entityType,
                    EntityId = entityId,
                    Scope = scope,
                    Provider = alias.Provider,
                    NormalizedProviderId = alias.NormalizedProviderId,
                    CreatedAt = now,
                    UpdatedAt = now
                })
                .ToList();

            _repository.ReplaceAliases(entityType, entityId, scope, aliases);
        }

        public void DeleteAliases(string entityType, int entityId)
        {
            if (entityId <= 0 || string.IsNullOrWhiteSpace(entityType))
            {
                return;
            }

            _repository.DeleteAliases(entityType, entityId);
        }

        public List<int> FindBookIds(string scope, IEnumerable<string> providerIds)
        {
            var aliases = NormalizeProviderIds(providerIds);
            return _repository.FindEntityIds("Book", scope, aliases);
        }

        public List<int> FindAuthorIds(IEnumerable<string> providerIds)
        {
            var aliases = NormalizeProviderIds(providerIds);
            return _repository.FindEntityIds("Author", "author", aliases);
        }

        public List<(string Provider, string NormalizedProviderId)> NormalizeProviderIds(IEnumerable<string> providerIds)
        {
            return providerIds?
                .Select(TryNormalize)
                .Where(alias => alias.HasValue)
                .Select(alias => alias.Value)
                .Distinct()
                .ToList() ?? new List<(string Provider, string NormalizedProviderId)>();
        }

        private static (string Provider, string NormalizedProviderId)? TryNormalize(string providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId))
            {
                return null;
            }

            var trimmed = providerId.Trim();
            if (ProviderIdHelper.ContainsUnsafeProviderAliasValue(trimmed))
            {
                return null;
            }

            var firstColon = trimmed.IndexOf(':');
            if (firstColon <= 0 || firstColon >= trimmed.Length - 1)
            {
                return null;
            }

            var provider = NormalizeProviderKey(trimmed.Substring(0, firstColon));
            if (string.IsNullOrWhiteSpace(provider))
            {
                return null;
            }

            var normalized = trimmed.Substring(firstColon + 1).Trim();
            if (string.IsNullOrWhiteSpace(normalized) ||
                ProviderIdHelper.ContainsUnsafeProviderAliasValue(normalized))
            {
                return null;
            }

            if (provider == "az")
            {
                normalized = normalized.ToUpperInvariant();
            }

            return (provider, normalized);
        }

        private static string NormalizeProviderKey(string provider)
        {
            switch ((provider ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "gr":
                case "goodreads":
                    return "gr";
                case "hc":
                case "hardcover":
                    return "hc";
                case "ol":
                case "openlibrary":
                case "open_library":
                    return "ol";
                case "gb":
                case "googlebooks":
                case "google_books":
                    return "gb";
                // Chaptarr has historically stored Amazon, Audible, and Audnexus ASIN-family IDs under az.
                // Entity type, media type, and alias scope carry the product/instance distinction.
                case "az":
                case "am":
                case "amazon":
                case "audible":
                case "audnexus":
                    return "az";
                default:
                    return null;
            }
        }
    }
}
