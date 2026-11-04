using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.Author;
using Chaptarr.Api.V1.Books;
using Chaptarr.Api.V1.MediaTypes;
using Chaptarr.Http;
using Chaptarr.Http.Extensions;
using Chaptarr.Http.Middleware;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Download;
using NzbDrone.Core.History;

namespace Chaptarr.Api.V1.History
{
    [V1ApiController]
    public class HistoryController : Controller
    {
        private readonly IHistoryService _historyService;
        private readonly ICustomFormatCalculationService _formatCalculator;
        private readonly IUpgradableSpecification _upgradableSpecification;
        private readonly IFailedDownloadService _failedDownloadService;
        private readonly IAuthorService _authorService;
        private readonly IBookshelfLegacyIdentityMapService _legacyIdentityMapService;

        public HistoryController(IHistoryService historyService,
                             ICustomFormatCalculationService formatCalculator,
                             IUpgradableSpecification upgradableSpecification,
                             IFailedDownloadService failedDownloadService,
                             IAuthorService authorService,
                             IBookshelfLegacyIdentityMapService legacyIdentityMapService = null)
        {
            _historyService = historyService;
            _formatCalculator = formatCalculator;
            _upgradableSpecification = upgradableSpecification;
            _failedDownloadService = failedDownloadService;
            _authorService = authorService;
            _legacyIdentityMapService = legacyIdentityMapService;
        }

        protected HistoryResource MapToResource(EntityHistory model, bool includeAuthor, bool includeBook)
        {
            var resource = model.ToResource(_formatCalculator);

            if (includeAuthor)
            {
                resource.Author = model.Author != null ? model.Author.ToResource(HttpContext.GetReadarrFacadeContext()) : null;
            }

            if (includeBook)
            {
                resource.Book = model.Book != null ? model.Book.ToResource(new BookResourceMappingOptions { FacadeContext = HttpContext.GetReadarrFacadeContext() }) : null;
            }

            if (model.Author != null)
            {
                var qualityProfile = model.Author.GetQualityProfileForQuality(resource.Quality.Quality);
                resource.QualityCutoffNotMet = qualityProfile != null ? _upgradableSpecification.QualityCutoffNotMet(qualityProfile, resource.Quality) : false;
            }

            if (HttpContext?.GetReadarrFacadeContext()?.IsBookshelfCompatibility == true)
            {
                HistoryResourceMapper.ApplyBookshelfCompatibility(resource);
            }

            return resource;
        }

        private void ProjectLegacyBookIdentities(IEnumerable<HistoryResource> resources)
        {
            var historyResources = resources?.Where(resource => resource != null).ToList();
            if (historyResources == null || historyResources.Count == 0 ||
                HttpContext?.GetReadarrFacadeContext()?.IsBookshelfCompatibility != true ||
                _legacyIdentityMapService == null)
            {
                return;
            }

            var mappingsByTargetId = _legacyIdentityMapService
                .GetMappingsByTargetIds("Book", historyResources.Select(resource => resource.Book?.Id ?? resource.BookId))
                .GroupBy(mapping => mapping.TargetId)
                .ToDictionary(group => group.Key, group => group.First());
            var legacyMetadataIdByAuthorId = _legacyIdentityMapService
                .GetMappingsByTargetIds("AuthorMetadata", historyResources
                    .Where(resource => resource.Book != null)
                    .Select(resource => resource.Book.AuthorId))
                .GroupBy(mapping => mapping.TargetId)
                .Where(group => group.Select(mapping => mapping.SourceId).Distinct().Count() == 1)
                .ToDictionary(group => group.Key, group => group.First().SourceId);

            foreach (var resource in historyResources)
            {
                if (resource.Book != null && legacyMetadataIdByAuthorId.TryGetValue(resource.Book.AuthorId, out var legacyMetadataId))
                {
                    resource.Book.AuthorMetadataId = legacyMetadataId;
                    if (resource.Book.Author != null)
                    {
                        resource.Book.Author.AuthorMetadataId = legacyMetadataId;
                    }
                }

                var targetBookId = resource.Book?.Id ?? resource.BookId;
                if (!mappingsByTargetId.TryGetValue(targetBookId, out var mapping))
                {
                    continue;
                }

                resource.BookId = mapping.SourceId;
                if (resource.Book != null)
                {
                    resource.Book.Id = mapping.SourceId;
                    resource.Book.LocalBookId = mapping.SourceId.ToString(global::System.Globalization.CultureInfo.InvariantCulture);
                    resource.Book.MediaType = null;
                    resource.Book.AudiobookMonitored = null;
                    resource.Book.EbookMonitored = null;
                    resource.Book.LocalAudiobookBooks = null;
                    resource.Book.LocalEbookBooks = null;
                }
            }
        }

        [HttpGet]
        [Produces("application/json")]
        public PagingResource<HistoryResource> GetHistory([FromQuery] PagingRequestResource paging, bool includeAuthor, bool includeBook, [FromQuery(Name = "eventType")] int[] eventTypes, int? bookId, string downloadId, [FromQuery] string mediaType = null)
        {
            mediaType ??= HttpContext?.GetReadarrFacadeContext()?.MediaType;
            var parsedMediaType = MediaTypeParameterParser.ParseOptional(mediaType);
            var pagingResource = new PagingResource<HistoryResource>(paging);
            var pagingSpec = pagingResource.MapToPagingSpec<HistoryResource, EntityHistory>("date", SortDirection.Descending);

            if (eventTypes != null && eventTypes.Any())
            {
                pagingSpec.FilterExpressions.Add(v => eventTypes.Contains((int)v.EventType));
            }

            if (bookId.HasValue)
            {
                var resolvedBookIds = HttpContext.ResolveBookshelfLegacyBookIds(_legacyIdentityMapService, new[] { bookId.Value }, parsedMediaType);
                var bookIds = resolvedBookIds.ToArray();
                pagingSpec.FilterExpressions.Add(h => bookIds.Contains(h.BookId));
            }

            if (downloadId.IsNotNullOrWhiteSpace())
            {
                pagingSpec.FilterExpressions.Add(h => h.DownloadId == downloadId);
            }

            var history = pagingSpec.ApplyToPage(spec => _historyService.Paged(spec, parsedMediaType), h => MapToResource(h, includeAuthor, includeBook));
            ProjectLegacyBookIdentities(history.Records);
            return history;
        }

        [HttpGet("since")]
        public List<HistoryResource> GetHistorySince(DateTime date, EntityHistoryEventType? eventType = null, bool includeAuthor = false, bool includeBook = false, [FromQuery] string mediaType = null)
        {
            var history = FilterByMediaType(_historyService.Since(date, eventType), mediaType)
                .Select(h => MapToResource(h, includeAuthor, includeBook))
                .ToList();
            ProjectLegacyBookIdentities(history);
            return history;
        }

        [HttpGet("author")]
        public List<HistoryResource> GetAuthorHistory(int authorId, int? bookId = null, EntityHistoryEventType? eventType = null, bool includeAuthor = false, bool includeBook = false, [FromQuery] string mediaType = null)
        {
            mediaType ??= HttpContext?.GetReadarrFacadeContext()?.MediaType;
            var author = _authorService.GetAuthor(authorId);

            if (bookId.HasValue)
            {
                var parsedMediaType = MediaTypeParameterParser.ParseOptional(mediaType);
                var resolvedBookIds = HttpContext.ResolveBookshelfLegacyBookIds(_legacyIdentityMapService, new[] { bookId.Value }, parsedMediaType);
                var history = resolvedBookIds
                    .SelectMany(resolvedBookId => _historyService.GetByBook(resolvedBookId, eventType) ?? new List<EntityHistory>())
                    .GroupBy(entry => entry.Id)
                    .Select(group => group.First());
                var resources = FilterByMediaType(history, mediaType).Select(h =>
                {
                    h.Author = author;

                    return MapToResource(h, includeAuthor, includeBook);
                }).ToList();
                ProjectLegacyBookIdentities(resources);
                return resources;
            }

            var authorHistory = FilterByMediaType(_historyService.GetByAuthor(authorId, eventType), mediaType).Select(h =>
            {
                h.Author = author;

                return MapToResource(h, includeAuthor, includeBook);
            }).ToList();
            ProjectLegacyBookIdentities(authorHistory);
            return authorHistory;
        }

        private static IEnumerable<EntityHistory> FilterByMediaType(IEnumerable<EntityHistory> history, string mediaType)
        {
            var parsedMediaType = MediaTypeParameterParser.ParseOptional(mediaType);
            if (!parsedMediaType.HasValue)
            {
                return history;
            }

            return history.Where(h => h?.Book != null && h.Book.MediaType == parsedMediaType.Value);
        }

        [HttpPost("failed/{id}")]
        public object MarkAsFailed([FromRoute] int id)
        {
            _failedDownloadService.MarkAsFailed(id);
            return new { };
        }
    }
}
