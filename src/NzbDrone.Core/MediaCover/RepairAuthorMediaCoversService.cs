using System;
using System.Linq;
using System.Diagnostics;
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
        private readonly IEventAggregator _eventAggregator;

        public RepairAuthorMediaCoversService(
            IAuthorService authorService,
            IMapCoversToLocal mediaCoverService,
            IDiskProvider diskProvider,
            IManageCommandQueue commandQueueManager,
            Logger logger,
            IEventAggregator eventAggregator)
        {
            _authorService = authorService;
            _mediaCoverService = mediaCoverService;
            _diskProvider = diskProvider;
            _commandQueueManager = commandQueueManager;
            _logger = logger;
            _eventAggregator = eventAggregator;
        }

        public void Execute(RepairAuthorMediaCoversCommand message)
        {
            var timer = Stopwatch.StartNew();
            var changedAuthors = 0;
            var failures = 0;
            var candidates = _authorService.GetAllAuthors()
                .Where(author => NeedsLocalRendition(author) || HasKnownPlaceholder(author))
                .OrderBy(author => author.Id)
                .ToList();

            if (candidates.Count == 0)
            {
                _logger.Debug("[AUTHOR-COVER-REPAIR] No missing local author covers found");
            }

            if (candidates.Count > 0) _logger.Info("[AUTHOR-COVER-REPAIR] Repairing local covers for all {0} missing authors", candidates.Count);

            foreach (var candidate in candidates)
            {
                try
                {
                    var author = _authorService.GetExistingAuthors(new[] { candidate.Id }).SingleOrDefault();
                    if (author == null) continue;
                    var changed = false;
                    if (NeedsLocalRendition(author))
                    {
                        changed = _mediaCoverService.EnsureAuthorCovers(author);
                    }

                    changed |= (_authorService?.RemoveKnownPlaceholderImages(author) ?? false);
                    if (changed)
                    {
                        changedAuthors++;
                        _eventAggregator.PublishEvent(new MediaCoversUpdatedEvent(author));
                    }
                }
                catch (Exception ex)
                {
                    failures++;
                    // A failed provider must not prevent later authors from getting a repair attempt.
                    _logger.Warn(ex, "[AUTHOR-COVER-REPAIR] Failed to repair local cover for author {0}: {1}", candidate.Id, candidate.Name);
                }
            }
            _logger.Info("[AUTHOR-COVER-REPAIR] {0} candidates, {1} changed, {2} failures in {3} ms",
                candidates.Count, changedAuthors, failures, timer.ElapsedMilliseconds);
            _mediaCoverService.RepairBookCovers();
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

            var warnedUnreadable = false;
            return coverGroups.Any(group => group.All(cover => !HasHonestLocalRendition(author, cover, ref warnedUnreadable)));
        }

        private static bool HasKnownPlaceholder(Author author)
        {
            return author?.Images?.Any(cover => cover != null &&
                MediaCoverRendition.IsKnownPlaceholderImageUrl(cover.Url)) == true;
        }

        private bool HasHonestLocalRendition(Author author, MediaCover cover, ref bool warnedUnreadable)
        {
            var getPath = new Func<int?, string>(height => _mediaCoverService.GetCoverPath(
                author.Id,
                MediaCoverEntity.Author,
                cover.CoverType,
                cover.Extension,
                height));
            var matches = _mediaCoverService.AuthorCoverMatchesRemoteUrl(author.Id, cover.CoverType, cover.Url, out var unreadable);
            if (unreadable)
            {
                if (!warnedUnreadable) _logger.Warn("Cannot read the cover receipt for author {0} ({1}); keeping its photos", author.Name, author.Id);
                warnedUnreadable = true;
                return true; // Not evidence that repair is needed; retry the receipt next run.
            }
            return matches &&
                   MediaCoverRendition.HasAllGeneratedRenditions(getPath, _diskProvider, cover.CoverType);
        }
    }
}
