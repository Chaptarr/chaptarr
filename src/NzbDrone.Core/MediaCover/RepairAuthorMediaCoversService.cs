using System;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.MediaCover.Commands;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.MediaCover
{
    public class RepairAuthorMediaCoversService : IExecute<RepairAuthorMediaCoversCommand>, IHandle<ApplicationStartedEvent>
    {
        private readonly IAuthorService _authorService;
        private readonly IMapCoversToLocal _mediaCoverService;
        private readonly IDiskProvider _diskProvider;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly Logger _logger;

        public RepairAuthorMediaCoversService(
            IAuthorService authorService,
            IMapCoversToLocal mediaCoverService,
            IDiskProvider diskProvider,
            IManageCommandQueue commandQueueManager,
            Logger logger)
        {
            _authorService = authorService;
            _mediaCoverService = mediaCoverService;
            _diskProvider = diskProvider;
            _commandQueueManager = commandQueueManager;
            _logger = logger;
        }

        public void Execute(RepairAuthorMediaCoversCommand message)
        {
            var candidates = _authorService.GetAllAuthors()
                .Where(author => NeedsLocalRendition(author) || HasKnownPlaceholder(author))
                .OrderBy(author => author.Id)
                .ToList();

            if (candidates.Count == 0)
            {
                _logger.Debug("[AUTHOR-COVER-REPAIR] No missing local author covers found");
                return;
            }

            _logger.Info("[AUTHOR-COVER-REPAIR] Repairing local covers for all {0} missing authors", candidates.Count);

            foreach (var author in candidates)
            {
                try
                {
                    if (NeedsLocalRendition(author))
                    {
                        _mediaCoverService.EnsureAuthorCovers(author);
                    }

                    if (HasKnownPlaceholder(author))
                    {
                        author.Images = author.Images
                            .Where(cover => cover != null && !MediaCoverRendition.IsKnownPlaceholderImageUrl(cover.Url))
                            .ToList();
                        _authorService.UpdateAuthor(author);
                    }
                }
                catch (Exception ex)
                {
                    // A failed provider must not prevent later authors from getting a repair attempt.
                    _logger.Warn(ex, "[AUTHOR-COVER-REPAIR] Failed to repair local cover for author {0}: {1}", author.Id, author.Name);
                }
            }
        }

        public void Handle(ApplicationStartedEvent message)
        {
            // Existing installations need the repair immediately after upgrading, not one
            // scheduler interval later. One low-priority command processes the entire missing
            // cohort; healthy authors are skipped on subsequent starts.
            _commandQueueManager.Push(new RepairAuthorMediaCoversCommand(), CommandPriority.Low);
        }

        private bool NeedsLocalRendition(Author author)
        {
            var coverGroups = MediaCoverRendition.SelectCandidates(author?.Images)
                .GroupBy(cover => cover.CoverType)
                .ToList();

            if (coverGroups.Count == 0)
            {
                return false;
            }

            return coverGroups.Any(group => group.All(cover => !HasHonestLocalRendition(author, cover)));
        }

        private static bool HasKnownPlaceholder(Author author)
        {
            return author?.Images?.Any(cover => cover != null &&
                MediaCoverRendition.IsKnownPlaceholderImageUrl(cover.Url)) == true;
        }

        private bool HasHonestLocalRendition(Author author, MediaCover cover)
        {
            var getPath = new Func<int?, string>(height => _mediaCoverService.GetCoverPath(
                author.Id,
                MediaCoverEntity.Author,
                cover.CoverType,
                cover.Extension,
                height));
            var identityPath = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(getPath(null)),
                MediaCoverRendition.GetAuthorCoverIdentityFileName(cover.CoverType));

            return MediaCoverRendition.StoredRemoteUrlMatches(identityPath, cover.Url, _diskProvider) &&
                   MediaCoverRendition.HasAllGeneratedRenditions(getPath, _diskProvider, cover.CoverType);
        }
    }
}
