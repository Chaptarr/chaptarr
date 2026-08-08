using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Newznab;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Indexers.Torznab
{
    public class Torznab : HttpIndexerBase<TorznabSettings>, INarratorMetadataProvider
    {
        private readonly INewznabCapabilitiesProvider _capabilitiesProvider;
        private readonly ICacheManager _cacheManager;
        private NewznabNarratorMetadataClient _narratorMetadataClient;

        public override string Name => "Torznab";

        public override DownloadProtocol Protocol => DownloadProtocol.Torrent;
        public override int PageSize => GetProviderPageSize();

        public Torznab(INewznabCapabilitiesProvider capabilitiesProvider, IIndexerHttpClientFactory httpClientFactory, IIndexerStatusService indexerStatusService, IConfigService configService, IParsingService parsingService, ICacheManager cacheManager, Logger logger)
            : base(httpClientFactory, indexerStatusService, configService, parsingService, logger)
        {
            _capabilitiesProvider = capabilitiesProvider;
            _cacheManager = cacheManager;
        }

        public override IIndexerRequestGenerator GetRequestGenerator()
        {
            return new TorznabRequestGenerator(_capabilitiesProvider)
            {
                PageSize = PageSize,
                Settings = Settings,
                ProxyId = (Definition as IndexerDefinition)?.ProxyId
            };
        }

        public override IParseIndexerResponse GetParser()
        {
            return new TorznabRssParser();
        }

        public bool CanProvideNarratorMetadata => Settings?.EnableNarratorMetadata == true;

        public bool TryPopulateNarratorMetadata(ReleaseInfo release)
        {
            if (!CanProvideNarratorMetadata)
            {
                return false;
            }

            _narratorMetadataClient ??= new NewznabNarratorMetadataClient(_httpClient, Settings, Definition?.Id ?? 0, RateLimit, _cacheManager, _logger);
            return _narratorMetadataClient.TryPopulate(release);
        }

        private IndexerDefinition GetDefinition(string name, TorznabSettings settings)
        {
            return new IndexerDefinition
            {
                EnableRss = false,
                EnableAutomaticSearch = false,
                EnableInteractiveSearch = false,
                Name = name,
                Implementation = GetType().Name,
                Settings = settings,
                Protocol = DownloadProtocol.Usenet,
                SupportsRss = SupportsRss,
                SupportsSearch = SupportsSearch
            };
        }

        private TorznabSettings GetSettings(string url, params int[] categories)
        {
            var settings = new TorznabSettings { BaseUrl = url };

            if (categories.Any())
            {
                settings.Categories = categories;
            }

            return settings;
        }

        protected override async Task Test(List<ValidationFailure> failures)
        {
            await base.Test(failures);

            if (failures.HasErrors())
            {
                return;
            }

            failures.AddIfNotNull(JackettAll());
            failures.AddIfNotNull(TestCapabilities());
        }

        protected virtual ValidationFailure TestCapabilities()
        {
            try
            {
                var capabilities = _capabilitiesProvider.GetCapabilities(Settings, (Definition as IndexerDefinition)?.ProxyId);

                if (capabilities.SupportedSearchParameters != null && capabilities.SupportedSearchParameters.Contains("q"))
                {
                    return null;
                }

                if (capabilities.SupportedBookSearchParameters != null &&
                    new[] { "author", "title" }.All(v => capabilities.SupportedBookSearchParameters.Contains(v)))
                {
                    return null;
                }

                return new ValidationFailure(string.Empty, "Indexer does not support required search parameters");
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Unable to connect to indexer: " + ex.Message);

                return new ValidationFailure(string.Empty, "Unable to connect to indexer, check the log for more details");
            }
        }

        protected virtual ValidationFailure JackettAll()
        {
            if (Settings.ApiPath.Contains("/torznab/all", StringComparison.InvariantCultureIgnoreCase) ||
                Settings.ApiPath.Contains("/api/v2.0/indexers/all/results/torznab", StringComparison.InvariantCultureIgnoreCase) ||
                Settings.BaseUrl.Contains("/torznab/all", StringComparison.InvariantCultureIgnoreCase) ||
                Settings.BaseUrl.Contains("/api/v2.0/indexers/all/results/torznab", StringComparison.InvariantCultureIgnoreCase))
            {
                return new NzbDroneValidationFailure("ApiPath", "Jackett's all endpoint is not supported, please add indexers individually")
                {
                    IsWarning = true,
                    DetailedDescription = "Jackett's all endpoint is not supported, please add indexers individually"
                };
            }

            return null;
        }

        public override object RequestAction(string action, IDictionary<string, string> query)
        {
            if (action == "newznabCategories")
            {
                List<NewznabCategory> categories = null;
                try
                {
                    categories = _capabilitiesProvider.GetCapabilities(Settings, (Definition as IndexerDefinition)?.ProxyId).Categories;
                }
                catch
                {
                    // Use default categories
                }

                return new
                {
                    options = NewznabCategoryFieldOptionsConverter.GetFieldSelectOptions(categories)
                };
            }

            return base.RequestAction(action, query);
        }

        private int GetProviderPageSize()
        {
            try
            {
                var capabilities = _capabilitiesProvider.GetCapabilities(Settings, (Definition as IndexerDefinition)?.ProxyId);
                return Math.Min(100, Math.Max(capabilities.DefaultPageSize, capabilities.MaxPageSize));
            }
            catch
            {
                return 100;
            }
        }
    }
}
