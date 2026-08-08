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
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Indexers.Newznab
{
    public class Newznab : HttpIndexerBase<NewznabSettings>, INarratorMetadataProvider
    {
        private readonly INewznabCapabilitiesProvider _capabilitiesProvider;
        private readonly ICacheManager _cacheManager;
        private NewznabNarratorMetadataClient _narratorMetadataClient;

        public override string Name => "Newznab";

        public override DownloadProtocol Protocol => DownloadProtocol.Usenet;
        public override int PageSize => GetProviderPageSize();

        public Newznab(INewznabCapabilitiesProvider capabilitiesProvider, IIndexerHttpClientFactory httpClientFactory, IIndexerStatusService indexerStatusService, IConfigService configService, IParsingService parsingService, ICacheManager cacheManager, Logger logger)
            : base(httpClientFactory, indexerStatusService, configService, parsingService, logger)
        {
            _capabilitiesProvider = capabilitiesProvider;
            _cacheManager = cacheManager;
        }

        public override IIndexerRequestGenerator GetRequestGenerator()
        {
            return new NewznabRequestGenerator(_capabilitiesProvider)
            {
                PageSize = PageSize,
                Settings = Settings,
                ProxyId = (Definition as IndexerDefinition)?.ProxyId
            };
        }

        public override IParseIndexerResponse GetParser()
        {
            return new NewznabRssParser();
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

        public override IEnumerable<ProviderDefinition> DefaultDefinitions
        {
            get
            {
                yield return GetDefinition("DOGnzb", GetSettings("https://api.dognzb.cr"));
                yield return GetDefinition("DrunkenSlug", GetSettings("https://drunkenslug.com"));
                // NZB.su API is hosted at api.nzb.life (api.nzb.su redirects).
                yield return GetDefinition("Nzb.su", GetSettings("https://api.nzb.life"));
                yield return GetDefinition("NZBCat", GetSettings("https://nzb.cat"));
                yield return GetDefinition("NZBFinder.ws", GetSettings("https://nzbfinder.ws"));
                yield return GetDefinition("NZBgeek", GetSettings("https://api.nzbgeek.info"));
                yield return GetDefinition("nzbplanet.net", GetSettings("https://api.nzbplanet.net"));
                yield return GetDefinition("NinjaCentral", GetSettings("https://ninjacentral.co.za"));
                yield return GetDefinition("SimplyNZBs", GetSettings("https://simplynzbs.com"));
                yield return GetDefinition("Tabula Rasa", GetSettings("https://www.tabula-rasa.pw", apiPath: @"/api/v1/api"));
                yield return GetDefinition("Usenet Crawler", GetSettings("https://www.usenet-crawler.com"));
            }
        }

        private IndexerDefinition GetDefinition(string name, NewznabSettings settings)
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

        private NewznabSettings GetSettings(string url, string apiPath = null, int[] categories = null)
        {
            var settings = new NewznabSettings { BaseUrl = url };

            if (categories != null)
            {
                settings.Categories = categories;
            }

            if (apiPath.IsNotNullOrWhiteSpace())
            {
                settings.ApiPath = apiPath;
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

                if (capabilities.SupportedTvSearchParameters != null &&
                    new[] { "q", "tvdbid", "rid" }.Any(v => capabilities.SupportedTvSearchParameters.Contains(v)) &&
                    new[] { "season", "ep" }.All(v => capabilities.SupportedTvSearchParameters.Contains(v)))
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

        public override object RequestAction(string action, IDictionary<string, string> query)
        {
            if (action == "newznabCategories")
            {
                List<NewznabCategory> categories = null;
                try
                {
                    if (Settings.BaseUrl.IsNotNullOrWhiteSpace() && Settings.ApiPath.IsNotNullOrWhiteSpace())
                    {
                        categories = _capabilitiesProvider.GetCapabilities(Settings, (Definition as IndexerDefinition)?.ProxyId).Categories;
                    }
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
