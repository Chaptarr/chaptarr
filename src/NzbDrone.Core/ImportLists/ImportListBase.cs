using System;
using System.Collections.Generic;
using System.Linq;
using FluentValidation.Results;
using NLog;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Tags;
using NzbDrone.Core.ThingiProvider;

namespace NzbDrone.Core.ImportLists
{
    public abstract class ImportListBase<TSettings> : IImportList
        where TSettings : IImportListSettings, new()
    {
        protected readonly IImportListStatusService _importListStatusService;
        protected readonly IConfigService _configService;
        protected readonly IParsingService _parsingService;
        protected readonly Logger _logger;
        private readonly Lazy<IQualityProfileService> _qualityProfileService;
        private readonly Lazy<IMetadataProfileService> _metadataProfileService;
        private readonly Lazy<ITagService> _tagService;
        private readonly IRootFolderService _rootFolderService;
        private readonly IRootFolderSettingsResolver _rootFolderSettingsResolver;

        public abstract string Name { get; }

        public abstract ImportListType ListType { get; }

        public abstract TimeSpan MinRefreshInterval { get; }

        public ImportListBase(IImportListStatusService importListStatusService,
            IConfigService configService,
            IParsingService parsingService,
            Lazy<IQualityProfileService> qualityProfileService,
            Lazy<IMetadataProfileService> metadataProfileService,
            Lazy<ITagService> tagService,
            IRootFolderService rootFolderService,
            IRootFolderSettingsResolver rootFolderSettingsResolver,
            Logger logger)
        {
            _importListStatusService = importListStatusService;
            _configService = configService;
            _parsingService = parsingService;
            _logger = logger;
            _qualityProfileService = qualityProfileService;
            _metadataProfileService = metadataProfileService;
            _tagService = tagService;
            _rootFolderService = rootFolderService;
            _rootFolderSettingsResolver = rootFolderSettingsResolver;
        }

        public Type ConfigContract => typeof(TSettings);

        public virtual ProviderMessage Message => null;

        public virtual IEnumerable<ProviderDefinition> DefaultDefinitions
        {
            get
            {
                var config = (IProviderConfig)new TSettings();

                var definition = new ImportListDefinition
                {
                    Name = GetType().Name,
                    EnableAutomaticAdd = config.Validate().IsValid,
                    ShouldMonitor = ImportListMonitorType.SpecificBook,
                    ShouldMonitorExisting = true,
                    MonitoringOptions = new ImportListMonitoringOptions(),
                    Implementation = GetType().Name,
                    Settings = config
                };

                definition.EnableAutomaticAdd &= DualMediaImportListActions.ValidateCreationSettings(definition).Count == 0;
                yield return definition;
            }
        }

        public virtual ProviderDefinition Definition { get; set; }

        public virtual object RequestAction(string action, IDictionary<string, string> query)
        {
            return DualMediaImportListActions.HandleRequestAction(action, _qualityProfileService, _metadataProfileService, _tagService);
        }

        protected TSettings Settings => (TSettings)Definition.Settings;

        public abstract IList<ImportListItemInfo> Fetch();

        private static object BuildDedupeKey(ImportListItemInfo item)
        {
            if (item == null)
            {
                return null;
            }

            var authorProviderId = ImportListProviderIdHelper.Normalize(item.AuthorProviderId, "gr");
            var bookProviderId = ImportListProviderIdHelper.Normalize(item.BookProviderId, "gr");
            var editionProviderId = ImportListProviderIdHelper.Normalize(item.EditionProviderId, "gr");

            var fallbackAuthor = item.Author?.Trim();
            var fallbackBook = item.Book?.Trim();

            // Preserve edition + format fidelity (multiple editions or multi-format scenarios).
            return new
            {
                Author = authorProviderId ?? fallbackAuthor,
                Book = bookProviderId ?? editionProviderId ?? fallbackBook,
                Edition = editionProviderId,
                item.EditionMediaType,
                item.HardcoverReadingFormatId
            };
        }

        protected virtual IList<ImportListItemInfo> CleanupListItems(IEnumerable<ImportListItemInfo> releases)
        {
            var result = releases
                .Where(r => r != null)
                .DistinctBy(BuildDedupeKey)
                .ToList();

            result.ForEach(c =>
            {
                c.ImportListId = Definition.Id;
                c.ImportList = Definition.Name;
            });

            return result;
        }

        public ValidationResult Test()
        {
            var failures = new List<ValidationFailure>();

            try
            {
                Test(failures);
                var definition = (ImportListDefinition)Definition;
                var creationFailures = DualMediaImportListActions.ValidateCreationSettings(definition);
                if (creationFailures.Count == 0)
                {
                    creationFailures.AddRange(DualMediaImportListActions.ValidateCreationProfiles(definition, _qualityProfileService, _metadataProfileService));
                }

                failures.AddRange(creationFailures);
                if (creationFailures.Count == 0)
                {
                    failures.AddRange(DualMediaImportListActions.TestRootFolderConfig(definition, _rootFolderService, _rootFolderSettingsResolver));
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Test aborted due to exception");
                failures.Add(new ValidationFailure(string.Empty, "Test was aborted due to an error: " + ex.Message));
            }

            return new ValidationResult(failures);
        }

        protected abstract void Test(List<ValidationFailure> failures);

        public override string ToString()
        {
            return Definition.Name;
        }
    }
}
