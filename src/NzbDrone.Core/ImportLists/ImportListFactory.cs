using System;
using System.Collections.Generic;
using System.Linq;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.ImportLists.Hardcover.Library;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.ThingiProvider;

namespace NzbDrone.Core.ImportLists
{
    public interface IImportListFactory : IProviderFactory<IImportList, ImportListDefinition>
    {
        List<IImportList> AutomaticAddEnabled(bool filterBlockedImportLists = true);
    }

    public class ImportListFactory : ProviderFactory<IImportList, ImportListDefinition>, IImportListFactory
    {
        private readonly IImportListStatusService _importListStatusService;
        private readonly IImportListRepository _importListRepository;
        private readonly IHardcoverIdentityService _hardcoverIdentityService;
        private readonly IConfigService _configService;
        private readonly Logger _logger;

        public ImportListFactory(IImportListStatusService importListStatusService,
                              IImportListRepository providerRepository,
                              IEnumerable<IImportList> providers,
                              IServiceProvider container,
                              IEventAggregator eventAggregator,
                              IHardcoverIdentityService hardcoverIdentityService,
                              IConfigService configService,
                              Logger logger)
            : base(providerRepository, providers, container, eventAggregator, logger)
        {
            _importListStatusService = importListStatusService;
            _importListRepository = providerRepository;
            _hardcoverIdentityService = hardcoverIdentityService;
            _configService = configService;
            _logger = logger;
        }

        protected override List<ImportListDefinition> Active()
        {
            return base.Active().Where(c => c.Enable).ToList();
        }

        public override void SetProviderCharacteristics(IImportList provider, ImportListDefinition definition)
        {
            base.SetProviderCharacteristics(provider, definition);

            definition.ListType = provider.ListType;
            definition.MinRefreshInterval = provider.MinRefreshInterval;

            // Ensure Hardcover identity is available for UI display even when the list uses the global token.
            TryApplyHardcoverGlobalIdentity(definition);
        }

        public List<IImportList> AutomaticAddEnabled(bool filterBlockedImportLists = true)
        {
            var enabledImportLists = GetAvailableProviders().Where(n => ((ImportListDefinition)n.Definition).EnableAutomaticAdd);

            if (filterBlockedImportLists)
            {
                return FilterBlockedImportLists(enabledImportLists).ToList();
            }

            return enabledImportLists.ToList();
        }

        public override ImportListDefinition Create(ImportListDefinition definition)
        {
            TryUpdateHardcoverCachedIdentity(definition, existing: null);
            return base.Create(definition);
        }

        public override void Update(ImportListDefinition definition)
        {
            ImportListDefinition existing = null;
            if (definition?.Id > 0)
            {
                existing = _importListRepository.Find(definition.Id);
            }

            TryUpdateHardcoverCachedIdentity(definition, existing);
            base.Update(definition);
        }

        private IEnumerable<IImportList> FilterBlockedImportLists(IEnumerable<IImportList> importLists)
        {
            var blockedImportLists = _importListStatusService.GetBlockedProviders().ToDictionary(v => v.ProviderId, v => v);

            foreach (var importList in importLists)
            {
                if (blockedImportLists.TryGetValue(importList.Definition.Id, out var blockedImportListStatus))
                {
                    _logger.Debug("Temporarily ignoring import list {0} till {1} due to recent failures.", importList.Definition.Name, blockedImportListStatus.DisabledTill.Value.ToLocalTime());
                    continue;
                }

                yield return importList;
            }
        }

        private void TryUpdateHardcoverCachedIdentity(ImportListDefinition definition, ImportListDefinition existing)
        {
            try
            {
                if (definition?.Implementation != nameof(HardcoverLibraryImportList))
                {
                    return;
                }

                if (definition.Settings is not HardcoverLibraryImportListSettings settings)
                {
                    return;
                }

                var newToken = NormalizeApiToken(settings.ApiToken);

                if (newToken.IsNullOrWhiteSpace())
                {
                    settings.CachedUsername = _configService.HardcoverUsername ?? string.Empty;
                    settings.CachedAvatarUrl = _configService.HardcoverUserImageUrl ?? string.Empty;
                    return;
                }

                var existingToken = NormalizeApiToken((existing?.Settings as HardcoverLibraryImportListSettings)?.ApiToken);
                var tokenChanged = !existingToken.Equals(newToken, StringComparison.Ordinal);

                // Only fetch identity when the token changes or when cache is missing.
                if (!tokenChanged && settings.CachedUsername.IsNotNullOrWhiteSpace())
                {
                    return;
                }

                if (_hardcoverIdentityService.TryGetIdentity(settings.BaseUrl, newToken, out var identity))
                {
                    settings.CachedUsername = identity.Username ?? string.Empty;
                    settings.CachedAvatarUrl = identity.AvatarUrl ?? string.Empty;
                }
                else if (tokenChanged)
                {
                    // Avoid showing stale identity for a new token we couldn't validate/fetch.
                    settings.CachedUsername = string.Empty;
                    settings.CachedAvatarUrl = string.Empty;
                }
            }
            catch (Exception ex)
            {
                // Best-effort only: never block save/update on identity fetch issues.
                _logger.Debug(ex, "Failed to update cached Hardcover identity for import list '{0}'", definition?.Name);
            }
        }

        private void TryApplyHardcoverGlobalIdentity(ImportListDefinition definition)
        {
            try
            {
                if (definition?.Implementation != nameof(HardcoverLibraryImportList))
                {
                    return;
                }

                if (definition.Settings is not HardcoverLibraryImportListSettings settings)
                {
                    return;
                }

                // When no per-list token is configured, the provider uses the global Hardcover token.
                // Populate cached identity from global config so the UI can show which account this list belongs to.
                if (NormalizeApiToken(settings.ApiToken).IsNullOrWhiteSpace())
                {
                    settings.CachedUsername = _configService.HardcoverUsername ?? string.Empty;
                    settings.CachedAvatarUrl = _configService.HardcoverUserImageUrl ?? string.Empty;
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to apply global Hardcover identity for import list '{0}'", definition?.Name);
            }
        }

        private static string NormalizeApiToken(string token)
        {
            if (token.IsNullOrWhiteSpace())
            {
                return string.Empty;
            }

            token = token.Trim();

            if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                token = token.Substring("Bearer ".Length);
            }

            return token.Trim();
        }

        public override ValidationResult Test(ImportListDefinition definition)
        {
            var result = base.Test(definition);

            if (definition.Id == 0)
            {
                return result;
            }

            if (result == null || result.IsValid)
            {
                _importListStatusService.RecordSuccess(definition.Id);
            }
            else
            {
                _importListStatusService.RecordFailure(definition.Id);
            }

            return result;
        }
    }
}
