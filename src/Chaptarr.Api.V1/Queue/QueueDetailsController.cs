using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Http;
using Chaptarr.Http.Middleware;
using Chaptarr.Http.REST;
using Chaptarr.Api.V1.MediaTypes;
using NzbDrone.Core.Books;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Datastore.Events;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Queue;
using NzbDrone.SignalR;

namespace Chaptarr.Api.V1.Queue
{
    [V1ApiController("queue/details")]
    public class QueueDetailsController : RestControllerWithSignalR<QueueResource, NzbDrone.Core.Queue.Queue>,
                               IHandle<QueueUpdatedEvent>, IHandle<PendingReleasesUpdatedEvent>
    {
        private readonly IQueueService _queueService;
        private readonly IPendingReleaseService _pendingReleaseService;
        private readonly IBookshelfLegacyIdentityMapService _legacyIdentityMapService;

        public QueueDetailsController(
            IBroadcastSignalRMessage broadcastSignalRMessage,
            IQueueService queueService,
            IPendingReleaseService pendingReleaseService,
            IBookshelfLegacyIdentityMapService legacyIdentityMapService = null)
            : base(broadcastSignalRMessage)
        {
            _queueService = queueService;
            _pendingReleaseService = pendingReleaseService;
            _legacyIdentityMapService = legacyIdentityMapService;
        }

        [NonAction]
        public override ActionResult<QueueResource> GetResourceByIdWithErrorHandler(int id)
        {
            return base.GetResourceByIdWithErrorHandler(id);
        }

        protected override QueueResource GetResourceById(int id)
        {
            throw new NotImplementedException();
        }

        [HttpGet]
        public List<QueueResource> GetQueue(int? authorId, [FromQuery] List<int> bookIds, bool includeAuthor = false, bool includeBook = true, [FromQuery] string mediaType = null)
        {
            var effectiveMediaType = mediaType ?? HttpContext?.GetReadarrFacadeContext()?.MediaType;
            var resolvedBookIds = HttpContext.ResolveBookshelfLegacyBookIds(
                _legacyIdentityMapService,
                bookIds,
                MediaTypeParameterParser.ParseOptional(effectiveMediaType));
            var queue = QueueMediaTypeFilter.FilterByMediaType(_queueService.GetQueue(), effectiveMediaType);
            var pending = QueueMediaTypeFilter.FilterByMediaType(_pendingReleaseService.GetPendingQueue(), effectiveMediaType);
            var fullQueue = queue.Concat(pending);

            if (authorId.HasValue)
            {
                return MapToResources(fullQueue.Where(q => q.Author?.Id == authorId.Value), includeAuthor, includeBook);
            }

            if (resolvedBookIds.Any())
            {
                return MapToResources(fullQueue.Where(q => q.Book != null && resolvedBookIds.Contains(q.Book.Id)), includeAuthor, includeBook);
            }

            return MapToResources(fullQueue, includeAuthor, includeBook);
        }

        private List<QueueResource> MapToResources(IEnumerable<NzbDrone.Core.Queue.Queue> queue, bool includeAuthor, bool includeBook)
        {
            var resources = queue.ToResource(includeAuthor, includeBook, HttpContext.GetReadarrFacadeContext());
            if (HttpContext?.GetReadarrFacadeContext()?.IsBookshelfCompatibility != true || _legacyIdentityMapService == null)
            {
                return resources;
            }

            var mappingsByTargetId = _legacyIdentityMapService
                .GetMappingsByTargetIds("Book", resources
                    .Where(resource => resource != null)
                    .Select(resource => resource.Book?.Id ?? resource.BookId.GetValueOrDefault()))
                .GroupBy(mapping => mapping.TargetId)
                .ToDictionary(group => group.Key, group => group.First());
            var legacyMetadataIdByAuthorId = _legacyIdentityMapService
                .GetMappingsByTargetIds("AuthorMetadata", resources
                    .Where(resource => resource?.Book != null)
                    .Select(resource => resource.Book.AuthorId))
                .GroupBy(mapping => mapping.TargetId)
                .Where(group => group.Select(mapping => mapping.SourceId).Distinct().Count() == 1)
                .ToDictionary(group => group.Key, group => group.First().SourceId);

            foreach (var resource in resources.Where(resource => resource != null))
            {
                if (resource.Book != null && legacyMetadataIdByAuthorId.TryGetValue(resource.Book.AuthorId, out var legacyMetadataId))
                {
                    resource.Book.AuthorMetadataId = legacyMetadataId;
                    if (resource.Book.Author != null)
                    {
                        resource.Book.Author.AuthorMetadataId = legacyMetadataId;
                    }
                }

                var targetBookId = resource.Book?.Id ?? resource.BookId.GetValueOrDefault();
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

            return resources;
        }

        [NonAction]
        public void Handle(QueueUpdatedEvent message)
        {
            BroadcastResourceChange(ModelAction.Sync);
        }

        [NonAction]
        public void Handle(PendingReleasesUpdatedEvent message)
        {
            BroadcastResourceChange(ModelAction.Sync);
        }
    }
}
