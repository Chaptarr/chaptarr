using System;
using System.Collections.Concurrent;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.MediaFiles
{
    public class UnmappedFilesRefreshService : IHandle<AuthorScannedEvent>
    {
        private static readonly TimeSpan RefreshCooldown = TimeSpan.FromMinutes(30);
        private static readonly ConcurrentDictionary<int, string> AttemptedSignatures = new ConcurrentDictionary<int, string>();

        private readonly IMediaFileService _mediaFileService;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly Logger _logger;

        public UnmappedFilesRefreshService(IMediaFileService mediaFileService,
                                           IManageCommandQueue commandQueueManager,
                                           Logger logger)
        {
            _mediaFileService = mediaFileService;
            _commandQueueManager = commandQueueManager;
            _logger = logger;
        }

        public void Handle(AuthorScannedEvent message)
        {
            var author = message.Author;

            if (author == null || author.Id <= 0 || author.Path.IsNullOrWhiteSpace())
            {
                return;
            }

            // A refresh rescans on completion; without the cooldown this would loop.
            if (author.LastInfoSync.HasValue && DateTime.UtcNow - author.LastInfoSync.Value < RefreshCooldown)
            {
                return;
            }

            var unmapped = _mediaFileService.GetFilesWithBasePath(author.Path)
                ?.Where(f => f != null && f.EditionId == 0)
                .ToList();

            if (unmapped == null || unmapped.Count == 0)
            {
                return;
            }

            var signature = string.Join("|", unmapped.Select(f => f.Path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase));

            // Already retried exactly these files; do not hammer the metadata server on every scan.
            if (AttemptedSignatures.TryGetValue(author.Id, out var previousSignature) && previousSignature == signature)
            {
                return;
            }

            AttemptedSignatures[author.Id] = signature;

            _logger.Info("Author {0} has {1} unmapped file(s); queueing a refresh to rebuild missing catalog entries", author.Name, unmapped.Count);
            _commandQueueManager.Push(new RefreshAuthorCommand { AuthorId = author.Id });
        }
    }
}
