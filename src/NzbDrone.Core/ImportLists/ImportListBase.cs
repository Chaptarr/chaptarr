using System;
using System.Collections.Generic;
using System.Linq;
using FluentValidation.Results;
using NLog;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
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

        public abstract string Name { get; }

        public abstract ImportListType ListType { get; }

        public abstract TimeSpan MinRefreshInterval { get; }

        public ImportListBase(IImportListStatusService importListStatusService, IConfigService configService, IParsingService parsingService, Logger logger)
        {
            _importListStatusService = importListStatusService;
            _configService = configService;
            _parsingService = parsingService;
            _logger = logger;
        }

        public Type ConfigContract => typeof(TSettings);

        public virtual ProviderMessage Message => null;

        public virtual IEnumerable<ProviderDefinition> DefaultDefinitions
        {
            get
            {
                var config = (IProviderConfig)new TSettings();

                yield return new ImportListDefinition
                {
                    Name = GetType().Name,
                    EnableAutomaticAdd = config.Validate().IsValid,
                    Implementation = GetType().Name,
                    Settings = config
                };
            }
        }

        public virtual ProviderDefinition Definition { get; set; }

        public virtual object RequestAction(string action, IDictionary<string, string> query)
        {
            return null;
        }

        protected TSettings Settings => (TSettings)Definition.Settings;

        public abstract IList<ImportListItemInfo> Fetch();

        public virtual void CommitState()
        {
        }

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
