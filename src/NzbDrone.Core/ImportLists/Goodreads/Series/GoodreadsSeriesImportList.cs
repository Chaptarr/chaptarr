using System;
using System.Collections.Generic;
using System.Net;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Tags;

namespace NzbDrone.Core.ImportLists.Goodreads
{
    public class GoodreadsSeriesImportList : ImportListBase<GoodreadsSeriesImportListSettings>
    {
        private readonly IProvideSeriesInfo _seriesInfo;
        private readonly Lazy<IQualityProfileService> _qualityProfileService;
        private readonly Lazy<IMetadataProfileService> _metadataProfileService;
        private readonly Lazy<ITagService> _tagService;
        private readonly IRootFolderService _rootFolderService;
        private readonly IRootFolderSettingsResolver _rootFolderSettingsResolver;

        public override string Name => "Goodreads Series";
        public override ImportListType ListType => ImportListType.Goodreads;
        public override TimeSpan MinRefreshInterval => TimeSpan.FromHours(12);

        public GoodreadsSeriesImportList(IProvideSeriesInfo seriesInfo,
            IImportListStatusService importListStatusService,
            IConfigService configService,
            IParsingService parsingService,
            Lazy<IQualityProfileService> qualityProfileService,
            Lazy<IMetadataProfileService> metadataProfileService,
            Lazy<ITagService> tagService,
            IRootFolderService rootFolderService,
            IRootFolderSettingsResolver rootFolderSettingsResolver,
            Logger logger)
            : base(importListStatusService, configService, parsingService, logger)
        {
            _seriesInfo = seriesInfo;
            _qualityProfileService = qualityProfileService;
            _metadataProfileService = metadataProfileService;
            _tagService = tagService;
            _rootFolderService = rootFolderService;
            _rootFolderSettingsResolver = rootFolderSettingsResolver;
        }

        public override IList<ImportListItemInfo> Fetch()
        {
            var result = new List<ImportListItemInfo>();
            var seenSourceBooks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var series = _seriesInfo.GetSeriesInfo(Settings.SeriesId);

                foreach (var work in series.Works)
                {
                    var item = new ImportListItemInfo
                    {
                        BookGoodreadsId = $"gr:{work.Id}",
                        Book = work.OriginalTitle,
                        EditionGoodreadsId = $"gr:{work.BestBook.Id}",
                        Author = work.BestBook.AuthorName,
                        AuthorGoodreadsId = $"gr:{work.BestBook.AuthorId}"
                    };

                    GoodreadsImportListLimit.TryAdd(result, item, seenSourceBooks, Settings.ImportLimit);

                    if (GoodreadsImportListLimit.HasReached(result.Count, Settings.ImportLimit))
                    {
                        _logger.Info("Goodreads Series import list '{0}' reached import limit of {1}; remaining Goodreads items will be skipped.",
                            Definition?.Name ?? Name, Settings.ImportLimit);
                        break;
                    }
                }

                _importListStatusService.RecordSuccess(Definition.Id);
            }
            catch
            {
                _importListStatusService.RecordFailure(Definition.Id);
            }

            return CleanupListItems(result);
        }

        protected override void Test(List<ValidationFailure> failures)
        {
            failures.AddIfNotNull(TestConnection());
            failures.AddRange(GoodreadsDualMediaImportListActions.TestRootFolderConfig(Settings, _rootFolderService, _rootFolderSettingsResolver));
        }

        public override object RequestAction(string action, IDictionary<string, string> query)
        {
            return GoodreadsDualMediaImportListActions.HandleRequestAction(action, _qualityProfileService, _metadataProfileService, _tagService)
                ?? base.RequestAction(action, query);
        }

        private ValidationFailure TestConnection()
        {
            try
            {
                _seriesInfo.GetSeriesInfo(Settings.SeriesId);
                return null;
            }
            catch (HttpException e)
            {
                _logger.Warn(e, "Goodreads API Error");
                if (e.Response.StatusCode == HttpStatusCode.NotFound)
                {
                    return new ValidationFailure(nameof(Settings.SeriesId), $"Series {Settings.SeriesId} not found");
                }

                return new ValidationFailure(nameof(Settings.SeriesId), $"Could not get series data");
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Unable to connect to Goodreads");

                return new ValidationFailure(string.Empty, "Unable to connect to import list, check the log for more details");
            }
        }
    }
}
