using System.Linq;
using System.Collections.Generic;
using NzbDrone.Core.Datastore;
using System.Threading.Tasks;
using Chaptarr.Http;
using Chaptarr.Http.REST;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Pending;

namespace Chaptarr.Api.V1.Queue
{
    [V1ApiController("queue")]
    public class QueueActionController : Controller
    {
        private readonly IPendingReleaseService _pendingReleaseService;
        private readonly IProcessDownloadDecisions _downloadDecisionProcessor;

        public QueueActionController(IPendingReleaseService pendingReleaseService,
                                     IProcessDownloadDecisions downloadDecisionProcessor)
        {
            _pendingReleaseService = pendingReleaseService;
            _downloadDecisionProcessor = downloadDecisionProcessor;
        }

        [HttpPost("grab/{id:int}")]
        public async Task<object> Grab(int id)
        {
            var pendingRelease = _pendingReleaseService.FindPendingQueueItem(id);

            ValidatePendingRelease(pendingRelease);

            await _downloadDecisionProcessor.ProcessSelectedDecisions(new() { pendingRelease.RemoteBook });

            return new { };
        }

        private static void ValidatePendingRelease(NzbDrone.Core.Queue.Queue pendingRelease)
        {
            if (pendingRelease == null)
            {
                throw new NotFoundException();
            }

            if (pendingRelease.RemoteBook?.Books?.Any(book => book?.Id > 0) != true)
            {
                throw new BadRequestException("Cannot grab this pending release until a library book is identified.");
            }

            if (!pendingRelease.RemoteBook.AlternativeCopies && pendingRelease.TargetBookIds.Any(id => id > 0) &&
                !pendingRelease.TargetBookIds.Where(id => id > 0).ToHashSet()
                    .SetEquals(pendingRelease.RemoteBook.Books.Select(book => book.Id)))
            {
                throw new BadRequestException("Cannot grab this pending release until all captured books are available.");
            }
        }

        [HttpPost("grab/bulk")]
        [Consumes("application/json")]
        public async Task<object> Grab([FromBody] QueueBulkResource resource)
        {
            var pendingById = _pendingReleaseService.GetPendingQueue().ToLookup(item => item.Id);
            var selected = new List<NzbDrone.Core.Parser.Model.RemoteBook>();
            foreach (var id in resource.Ids.Distinct())
            {
                var matches = pendingById[id].Take(2).ToList();
                if (matches.Count > 1)
                {
                    throw new ModelConflictException(typeof(NzbDrone.Core.Queue.Queue), id, "matches multiple pending items; no item was selected");
                }

                var pendingRelease = matches.FirstOrDefault();
                ValidatePendingRelease(pendingRelease);
                selected.Add(pendingRelease.RemoteBook);
            }

            await _downloadDecisionProcessor.ProcessSelectedDecisions(selected);

            return new { };
        }
    }
}
