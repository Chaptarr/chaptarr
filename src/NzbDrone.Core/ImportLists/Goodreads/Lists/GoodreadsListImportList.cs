using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.Goodreads;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Tags;

namespace NzbDrone.Core.ImportLists.Goodreads
{
    public class GoodreadsListImportList : ImportListBase<GoodreadsListImportListSettings>
    {
        private const int MaxPages = 100;
        private const int ParallelPageFetches = 5;

        private readonly IProvideListInfo _listInfo;
        private readonly Lazy<IQualityProfileService> _qualityProfileService;
        private readonly Lazy<IMetadataProfileService> _metadataProfileService;
        private readonly Lazy<ITagService> _tagService;
        private readonly IRootFolderService _rootFolderService;
        private readonly IRootFolderSettingsResolver _rootFolderSettingsResolver;

        public override string Name => "Goodreads List";
        public override ImportListType ListType => ImportListType.Goodreads;
        public override TimeSpan MinRefreshInterval => TimeSpan.FromHours(12);

        public GoodreadsListImportList(IProvideListInfo listInfo,
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
            _listInfo = listInfo;
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
                var firstPage = FetchPageResource(1);
                var firstPageItems = MapPage(firstPage);

                if (firstPageItems.Any() && AddPageItems(result, firstPageItems, seenSourceBooks))
                {
                    _importListStatusService.RecordSuccess(Definition.Id);
                    return CleanupListItems(result);
                }

                if ((firstPage?.ListBooksCount ?? 0) <= 0)
                {
                    FetchRemainingPagesUntilEmpty(result, seenSourceBooks);
                }
                else
                {
                    FetchRemainingPagesInParallel(result, seenSourceBooks, GetTotalPages(firstPage));
                }

                _importListStatusService.RecordSuccess(Definition.Id);
            }
            catch
            {
                _importListStatusService.RecordFailure(Definition.Id);
            }

            return CleanupListItems(result);
        }

        private void FetchRemainingPagesInParallel(List<ImportListItemInfo> result, HashSet<string> seenSourceBooks, int totalPages)
        {
            if (totalPages <= 1)
            {
                return;
            }

            for (var pageStart = 2; pageStart <= totalPages; pageStart += ParallelPageFetches)
            {
                var pageCount = Math.Min(ParallelPageFetches, totalPages - pageStart + 1);
                var pages = Enumerable.Range(pageStart, pageCount).ToList();
                var pageResults = new ConcurrentDictionary<int, List<ImportListItemInfo>>();

                Parallel.ForEach(pages,
                    new ParallelOptions { MaxDegreeOfParallelism = ParallelPageFetches },
                    page => pageResults[page] = FetchPage(page));

                var reachedLimit = false;
                foreach (var page in pages.OrderBy(x => x))
                {
                    if (pageResults.TryGetValue(page, out var pageItems) &&
                        AddPageItems(result, pageItems, seenSourceBooks))
                    {
                        reachedLimit = true;
                        break;
                    }
                }

                if (reachedLimit)
                {
                    break;
                }
            }
        }

        private void FetchRemainingPagesUntilEmpty(List<ImportListItemInfo> result, HashSet<string> seenSourceBooks)
        {
            for (var page = 2; page <= MaxPages; page++)
            {
                var pageItems = FetchPage(page);

                if (!pageItems.Any() || AddPageItems(result, pageItems, seenSourceBooks))
                {
                    break;
                }
            }
        }

        private int GetTotalPages(ListResource firstPage)
        {
            if (firstPage == null)
            {
                return 1;
            }

            var perPage = firstPage.PerPage;

            if (perPage <= 0)
            {
                perPage = Math.Max(firstPage.Books?.Count ?? 0, 1);
            }

            var totalBooks = firstPage.ListBooksCount;

            if (totalBooks <= 0)
            {
                return firstPage.Books?.Any() == true ? 1 : 0;
            }

            // Goodreads returns page 100 for larger page numbers, so keep the inherited ceiling.
            return Math.Min(MaxPages, (int)Math.Ceiling(totalBooks / (double)perPage));
        }

        private bool AddPageItems(List<ImportListItemInfo> result, IEnumerable<ImportListItemInfo> pageItems, HashSet<string> seenSourceBooks)
        {
            foreach (var item in pageItems)
            {
                GoodreadsImportListLimit.TryAdd(result, item, seenSourceBooks, Settings.ImportLimit);

                if (GoodreadsImportListLimit.HasReached(result.Count, Settings.ImportLimit))
                {
                    _logger.Info("Goodreads List import list '{0}' reached import limit of {1}; remaining Goodreads items will be skipped.",
                        Definition?.Name ?? Name, Settings.ImportLimit);
                    return true;
                }
            }

            return false;
        }

        private List<ImportListItemInfo> FetchPage(int page)
        {
            return MapPage(FetchPageResource(page));
        }

        private ListResource FetchPageResource(int page)
        {
            return _listInfo.GetListInfo(Settings.ListId, page);
        }

        private List<ImportListItemInfo> MapPage(ListResource list)
        {
            var result = new List<ImportListItemInfo>();

            foreach (var book in list?.Books ?? new List<BookResource>())
            {
                var author = book.Authors.FirstOrDefault();

                result.Add(new ImportListItemInfo
                {
                    BookGoodreadsId = $"gr:{book.Work.Id}",
                    Book = book.Work.OriginalTitle,
                    EditionGoodreadsId = $"gr:{book.Id}",
                    Author = author?.Name,
                    AuthorGoodreadsId = author != null ? $"gr:{author.Id}" : null
                });
            }

            return result;
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
                _listInfo.GetListInfo(Settings.ListId, 1);
                return null;
            }
            catch (HttpException e)
            {
                _logger.Warn(e, "Goodreads API Error");
                if (e.Response.StatusCode == HttpStatusCode.NotFound)
                {
                    return new ValidationFailure(nameof(Settings.ListId), $"List {Settings.ListId} not found");
                }

                return new ValidationFailure(nameof(Settings.ListId), $"Could not get list data");
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Unable to connect to Goodreads");

                return new ValidationFailure(string.Empty, "Unable to connect to import list, check the log for more details");
            }
        }
    }
}
