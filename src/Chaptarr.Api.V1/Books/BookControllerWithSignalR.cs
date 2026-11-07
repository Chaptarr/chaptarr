using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.Author;
using Chaptarr.Http.Middleware;
using Chaptarr.Http.REST;
using NzbDrone.Core.AuthorStats;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.MediaCover;
using NzbDrone.SignalR;

namespace Chaptarr.Api.V1.Books
{
    public abstract class BookControllerWithSignalR : RestControllerWithSignalR<BookResource, Book>
    {
        protected readonly IBookService _bookService;
        protected readonly ISeriesBookLinkService _seriesBookLinkService;
        protected readonly IAuthorStatisticsService _authorStatisticsService;
        protected readonly IUpgradableSpecification _qualityUpgradableSpecification;
        protected readonly IMapCoversToLocal _coverMapper;
        private readonly IBookshelfLegacyIdentityMapService _bookshelfLegacyIdentityMapService;
        private readonly IEditionService _editionService;

        protected BookControllerWithSignalR(IBookService bookService,
                                        ISeriesBookLinkService seriesBookLinkService,
                                        IAuthorStatisticsService authorStatisticsService,
                                        IMapCoversToLocal coverMapper,
                                        IUpgradableSpecification qualityUpgradableSpecification,
                                        IBroadcastSignalRMessage signalRBroadcaster,
                                        IBookshelfLegacyIdentityMapService bookshelfLegacyIdentityMapService = null,
                                        IEditionService editionService = null)
            : base(signalRBroadcaster)
        {
            _bookService = bookService;
            _seriesBookLinkService = seriesBookLinkService;
            _authorStatisticsService = authorStatisticsService;
            _coverMapper = coverMapper;
            _qualityUpgradableSpecification = qualityUpgradableSpecification;
            _bookshelfLegacyIdentityMapService = bookshelfLegacyIdentityMapService;
            _editionService = editionService;
        }

        protected override BookResource GetResourceById(int id)
        {
            var book = _bookService.GetBook(id);
            var resource = MapToResource(book, true);
            return resource;
        }

        protected override bool ShouldBroadcastSyncForResourceId(int id)
        {
            if (HttpContext?.GetReadarrFacadeContext()?.IsBookshelfCompatibility == true)
            {
                return true;
            }

            return _bookshelfLegacyIdentityMapService?.GetMappingsByTargetIds("Book", new[] { id }).Count > 0;
        }

        protected override bool ShouldBroadcastSyncForResource(BookResource resource)
        {
            return HttpContext?.GetReadarrFacadeContext()?.IsBookshelfCompatibility == true ||
                   base.ShouldBroadcastSyncForResource(resource);
        }

        protected override BookResource GetResourceByIdForBroadcast(int id)
        {
            var book = _bookService.GetBook(id);
            var resource = MapToResource(book, false);
            return resource;
        }

        protected BookResource MapToResource(Book book, bool includeAuthor)
        {
            var stats = GetAuthorStatistics(book.AuthorId)
                ?.BookStatistics
                ?.FirstOrDefault(x => x.BookId == book.Id);

            var resource = book.ToResource(new BookResourceMappingOptions
            {
                IncludeAuthor = includeAuthor,
                IncludeOverview = true,
                IncludeLinks = true,
                Statistics = stats,
                FacadeContext = HttpContext?.GetReadarrFacadeContext()
            });

            MapCoversToLocal(resource);

            ProjectLegacyAuthorMetadataIdentity(resource, book.AuthorId);
            ProjectLegacyBookshelfResource(resource);

            return resource;
        }

        protected List<BookResource> MapToResource(List<Book> books, bool includeAuthor)
        {
            return MapToResource(books, includeAuthor, null, includeOverview: true, includeLinks: true);
        }

        protected List<BookResource> MapToResource(List<Book> books,
                                                   bool includeAuthor,
                                                   IReadOnlyDictionary<int, BookStatistics> statsByBookId,
                                                   bool includeOverview,
                                                   bool includeLinks)
        {
            var facadeContext = HttpContext?.GetReadarrFacadeContext();
            var seriesLinks = _seriesBookLinkService.GetLinksByBook(books.Select(x => x.Id).ToList())
                .GroupBy(x => x.BookId)
                .ToDictionary(x => x.Key, y => y.ToList());

            foreach (var book in books)
            {
                if (seriesLinks.TryGetValue(book.Id, out var links))
                {
                    book.SeriesLinks = links;
                }
                else
                {
                    book.SeriesLinks = new List<SeriesBookLink>();
                }
            }

            var resolvedStatsByBookId = statsByBookId ?? BuildBookStatisticsById(GetAuthorStatistics());

            // Convert books to resources. Statistics are passed before mapping so narrator gating can
            // use file counts even when lean list endpoints intentionally skip BookFiles hydration.
            var result = books.Select(b =>
            {
                resolvedStatsByBookId.TryGetValue(b.Id, out var stats);
                return b.ToResource(new BookResourceMappingOptions
                {
                    IncludeAuthor = includeAuthor,
                    IncludeOverview = includeOverview,
                    IncludeLinks = includeLinks,
                    Statistics = stats,
                    FacadeContext = facadeContext
                });
            }).ToList();
            BookResourceMapper.WarnFacadeIdentityGaps(result, facadeContext, "book response");

            MapCoversToLocal(result.ToArray());

            return ProjectLegacyBookshelfResources(books, result, resolvedStatsByBookId, facadeContext);

        }

        private void ProjectLegacyBookshelfResource(BookResource resource)
        {
            var facadeContext = HttpContext?.GetReadarrFacadeContext();
            if (resource == null || facadeContext?.IsBookshelfCompatibility != true || _bookshelfLegacyIdentityMapService == null)
            {
                return;
            }

            var mapping = _bookshelfLegacyIdentityMapService
                .GetMappingsByTargetIds("Book", new[] { resource.Id })
                .FirstOrDefault();

            if (mapping == null)
            {
                return;
            }

            var sourceWorkBookIds = _bookshelfLegacyIdentityMapService.ResolveBookIds(new[] { mapping.SourceId });
            var sourceWorkBooks = _bookService.GetBooks(sourceWorkBookIds)
                .Where(book => book != null)
                .ToList();
            resource.Id = mapping.SourceId;
            resource.LocalBookId = mapping.SourceId.ToString(global::System.Globalization.CultureInfo.InvariantCulture);
            ProjectLegacyBookshelfCoverImageUrls(resource, mapping.SourceId);
            resource.MediaType = null;
            resource.AudiobookMonitored = null;
            resource.EbookMonitored = null;
            resource.LocalAudiobookBooks = null;
            resource.LocalEbookBooks = null;
            if (sourceWorkBooks.Count > 0)
            {
                resource.Monitored = sourceWorkBooks.Any(book => book.IsMonitored());
                resource.HasFiles = sourceWorkBooks.Any(book => book.HasFiles) || resource.HasFiles;
            }

            if (sourceWorkBooks.Count > 1)
            {
                var targetIds = sourceWorkBooks.Select(book => book.Id).ToHashSet();
                var stats = GetAuthorStatistics(sourceWorkBooks[0].AuthorId)
                    ?.BookStatistics
                    ?.Where(value => targetIds.Contains(value.BookId))
                    .ToList() ?? new List<BookStatistics>();
                if (stats.Count > 0)
                {
                    resource.Statistics = CombineLegacyBookStatistics(stats).ToResource(bookshelfCompatibility: true);
                    resource.HasFiles = resource.Statistics.BookFileCount > 0;
                }
            }
        }

        private List<BookResource> ProjectLegacyBookshelfResources(
            List<Book> books,
            List<BookResource> resources,
            IReadOnlyDictionary<int, BookStatistics> statisticsByBookId,
            ReadarrFacadeContext facadeContext)
        {
            if (facadeContext?.IsBookshelfCompatibility != true ||
                _bookshelfLegacyIdentityMapService == null ||
                books == null || resources == null || books.Count == 0 || resources.Count == 0)
            {
                return resources;
            }

            var mappingsByTargetId = _bookshelfLegacyIdentityMapService
                .GetMappingsByTargetIds("Book", books.Select(book => book.Id))
                .GroupBy(mapping => mapping.TargetId)
                .ToDictionary(group => group.Key, group => group.First());
            var legacyMetadataIdByAuthorId = _bookshelfLegacyIdentityMapService
                .GetMappingsByTargetIds("AuthorMetadata", books.Select(book => book.AuthorId))
                .GroupBy(mapping => mapping.TargetId)
                .Where(group => group.Select(mapping => mapping.SourceId).Distinct().Count() == 1)
                .ToDictionary(group => group.Key, group => group.First().SourceId);
            var resourcesByTargetId = resources.ToDictionary(resource => resource.Id);
            var rowsByLegacyId = new Dictionary<int, List<Book>>();
            var legacySelector = new BookshelfLegacyBookSelector(
                _editionService?.GetEditionsByBook(books.Select(book => book.Id)) ?? new List<Edition>());

            foreach (var book in books)
            {
                if (mappingsByTargetId.TryGetValue(book.Id, out var mapping))
                {
                    if (!rowsByLegacyId.TryGetValue(mapping.SourceId, out var rows))
                    {
                        rows = new List<Book>();
                        rowsByLegacyId.Add(mapping.SourceId, rows);
                    }

                    rows.Add(book);
                }
            }

            var resourceByLegacyId = new Dictionary<int, BookResource>();
            var legacyIdsByTargetId = mappingsByTargetId.Values.ToDictionary(mapping => mapping.TargetId, mapping => mapping.SourceId);

            foreach (var (legacyId, rows) in rowsByLegacyId)
            {
                var primaryBookId = legacySelector.Select(rows);
                if (!resourcesByTargetId.TryGetValue(primaryBookId, out var resource))
                {
                    continue;
                }

                ProjectLegacyAuthorMetadataIdentity(resource, rows[0].AuthorId, legacyMetadataIdByAuthorId);
                resource.Id = legacyId;
                resource.LocalBookId = legacyId.ToString(global::System.Globalization.CultureInfo.InvariantCulture);
                ProjectLegacyBookshelfCoverImageUrls(resource, legacyId);
                resource.MediaType = null;
                resource.AudiobookMonitored = null;
                resource.EbookMonitored = null;
                resource.LocalAudiobookBooks = null;
                resource.LocalEbookBooks = null;
                resource.Monitored = rows.Any(book => book.IsMonitored());

                var stats = rows
                    .Select(book => statisticsByBookId != null && statisticsByBookId.TryGetValue(book.Id, out var value) ? value : null)
                    .Where(value => value != null)
                    .ToList();
                if (stats.Count > 0)
                {
                    resource.Statistics = CombineLegacyBookStatistics(stats).ToResource(bookshelfCompatibility: true);
                    resource.HasFiles = resource.Statistics.BookFileCount > 0;
                }

                resourceByLegacyId[legacyId] = resource;
            }

            var projected = new List<BookResource>();
            var emittedLegacyIds = new HashSet<int>();
            foreach (var book in books)
            {
                if (legacyIdsByTargetId.TryGetValue(book.Id, out var legacyId))
                {
                    if (emittedLegacyIds.Add(legacyId) && resourceByLegacyId.TryGetValue(legacyId, out var resource))
                    {
                        projected.Add(resource);
                    }

                    continue;
                }

                if (resourcesByTargetId.TryGetValue(book.Id, out var unprojectedResource))
                {
                    ProjectLegacyAuthorMetadataIdentity(unprojectedResource, book.AuthorId, legacyMetadataIdByAuthorId);
                    projected.Add(unprojectedResource);
                }
            }

            return projected;
        }

        private void ProjectLegacyBookshelfCoverImageUrls(BookResource resource, int legacyBookId)
        {
            if (resource != null)
            {
                _coverMapper.ConvertToBookshelfLocalUrls(legacyBookId, MediaCoverEntity.Book, resource.Images);
            }
        }

        private void ProjectLegacyAuthorMetadataIdentity(BookResource resource, int authorId)
        {
            var facadeContext = HttpContext?.GetReadarrFacadeContext();
            if (resource == null || facadeContext?.IsBookshelfCompatibility != true || _bookshelfLegacyIdentityMapService == null)
            {
                return;
            }

            var legacyMetadataIdByAuthorId = _bookshelfLegacyIdentityMapService
                .GetMappingsByTargetIds("AuthorMetadata", new[] { authorId })
                .GroupBy(mapping => mapping.TargetId)
                .Where(group => group.Select(mapping => mapping.SourceId).Distinct().Count() == 1)
                .ToDictionary(group => group.Key, group => group.First().SourceId);
            ProjectLegacyAuthorMetadataIdentity(resource, authorId, legacyMetadataIdByAuthorId);
        }

        private static void ProjectLegacyAuthorMetadataIdentity(
            BookResource resource,
            int authorId,
            IReadOnlyDictionary<int, int> legacyMetadataIdByAuthorId)
        {
            if (resource == null || legacyMetadataIdByAuthorId == null ||
                !legacyMetadataIdByAuthorId.TryGetValue(authorId, out var legacyMetadataId))
            {
                return;
            }

            resource.AuthorMetadataId = legacyMetadataId;
            if (resource.Author != null)
            {
                resource.Author.AuthorMetadataId = legacyMetadataId;
            }
        }

        private static BookStatistics CombineLegacyBookStatistics(IReadOnlyCollection<BookStatistics> statistics)
        {
            return new BookStatistics
            {
                BookFileCount = statistics.Sum(value => value.BookFileCount),
                EbookFileCount = statistics.Sum(value => value.EbookFileCount),
                AudiobookFileCount = statistics.Sum(value => value.AudiobookFileCount),
                BookCount = statistics.Max(value => value.BookCount),
                AvailableBookCount = statistics.Max(value => value.AvailableBookCount),
                TotalBookCount = statistics.Max(value => value.TotalBookCount),
                SizeOnDisk = statistics.Sum(value => value.SizeOnDisk),
                EbookSizeOnDisk = statistics.Sum(value => value.EbookSizeOnDisk),
                AudiobookSizeOnDisk = statistics.Sum(value => value.AudiobookSizeOnDisk)
            };
        }

        protected static IReadOnlyDictionary<int, BookStatistics> BuildBookStatisticsById(IEnumerable<AuthorStatistics> authorStatistics)
        {
            return authorStatistics?
                .SelectMany(x => x.BookStatistics ?? new List<BookStatistics>())
                .GroupBy(x => x.BookId)
                .ToDictionary(x => x.Key, x => x.First())
                ?? new Dictionary<int, BookStatistics>();
        }

        private void FetchAndLinkBookStatistics(BookResource resource)
        {
            LinkAuthorStatistics(resource, GetAuthorStatistics(resource.AuthorId));
        }

        protected List<AuthorStatistics> GetAuthorStatistics(string mediaType = null)
        {
            if (string.IsNullOrWhiteSpace(mediaType) &&
                HttpContext?.GetReadarrFacadeContext()?.IsBookshelfCompatibility == true)
            {
                return _authorStatisticsService.BookshelfAuthorStatistics();
            }

            return _authorStatisticsService.AuthorStatistics(mediaType);
        }

        private AuthorStatistics GetAuthorStatistics(int authorId)
        {
            if (HttpContext?.GetReadarrFacadeContext()?.IsBookshelfCompatibility == true)
            {
                return _authorStatisticsService.BookshelfAuthorStatistics(authorId);
            }

            return _authorStatisticsService.AuthorStatistics(authorId);
        }

        private void LinkAuthorStatistics(List<BookResource> resources, List<AuthorStatistics> authorStatistics)
        {
            var bookStatsDict = authorStatistics.SelectMany(x => x.BookStatistics).ToDictionary(x => x.BookId);

            foreach (var book in resources)
            {
                // For multi-edition resources, statistics are already set in ToResourceForEdition
                if (book.Statistics == null && bookStatsDict.TryGetValue(book.Id, out var stats))
                {
                    book.Statistics = stats.ToResource(HttpContext?.GetReadarrFacadeContext()?.IsBookshelfCompatibility == true);
                }

                if (book.Statistics != null)
                {
                    book.HasFiles = book.Statistics.BookFileCount > 0;
                }
            }
        }

        private void LinkAuthorStatistics(BookResource resource, AuthorStatistics authorStatistics)
        {
            if (authorStatistics?.BookStatistics != null)
            {
                var dictBookStats = authorStatistics.BookStatistics.ToDictionary(v => v.BookId);

                resource.Statistics = dictBookStats.GetValueOrDefault(resource.Id).ToResource(HttpContext?.GetReadarrFacadeContext()?.IsBookshelfCompatibility == true);
                if (resource.Statistics != null)
                {
                    resource.HasFiles = resource.Statistics.BookFileCount > 0;
                }
            }
        }

        private void MapCoversToLocal(params BookResource[] books)
        {
            if (HttpContext?.GetReadarrFacadeContext()?.IsBookshelfCompatibility == true)
            {
                return;
            }

            foreach (var bookResource in books)
            {
                _coverMapper.ConvertToLocalUrls(bookResource.Id, MediaCoverEntity.Book, bookResource.Images);
            }
        }
    }
}
