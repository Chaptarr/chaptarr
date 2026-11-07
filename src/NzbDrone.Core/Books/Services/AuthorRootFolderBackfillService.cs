using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.Books
{
    public class AuthorRootFolderBackfillResult
    {
        /// <summary>Authors with exactly one of the two root folders set.</summary>
        public int Candidates { get; set; }

        /// <summary>Authors that received (or, in a dry run, would receive) a root folder.</summary>
        public int Updated { get; set; }

        /// <summary>Authors whose missing side was left alone because its settings were set by hand.</summary>
        public int SkippedManuallyOverridden { get; set; }

        /// <summary>Authors left alone because no single default root folder could be determined.</summary>
        public int SkippedNoDefault { get; set; }

        /// <summary>Authors whose missing side was left alone because it has no quality profile to pair with a root folder.</summary>
        public int SkippedNoProfile { get; set; }

        public List<int> UpdatedAuthorIds { get; set; } = new();
    }

    public interface IBackfillAuthorRootFolders
    {
        AuthorRootFolderBackfillResult Backfill(bool dryRun);
    }

    public class AuthorRootFolderBackfillService : IBackfillAuthorRootFolders, IExecute<BackfillAuthorRootFoldersCommand>
    {
        internal const int RefreshBatchSize = 25;

        private readonly IAuthorService _authorService;
        private readonly IRootFolderService _rootFolderService;
        private readonly IConfigService _configService;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly Logger _logger;

        public AuthorRootFolderBackfillService(IAuthorService authorService,
                                               IRootFolderService rootFolderService,
                                               IConfigService configService,
                                               IManageCommandQueue commandQueueManager,
                                               Logger logger)
        {
            _authorService = authorService;
            _rootFolderService = rootFolderService;
            _configService = configService;
            _commandQueueManager = commandQueueManager;
            _logger = logger;
        }

        public AuthorRootFolderBackfillResult Backfill(bool dryRun)
        {
            var result = new AuthorRootFolderBackfillResult();
            var rootFolders = _rootFolderService.All();

            RootFolderDefaultResolver.TryGetEffectiveDefaultRootFolder(rootFolders, FolderType.Audiobook, _configService.DefaultAudiobookRootFolderPath, out var audiobookDefault, out _);
            RootFolderDefaultResolver.TryGetEffectiveDefaultRootFolder(rootFolders, FolderType.Ebook, _configService.DefaultEbookRootFolderPath, out var ebookDefault, out _);

            var changed = new List<Author>();

            foreach (var author in _authorService.GetAllAuthors())
            {
                var hasAudiobook = author.AudiobookRootFolderPath.IsNotNullOrWhiteSpace();
                var hasEbook = author.EbookRootFolderPath.IsNotNullOrWhiteSpace();

                if (hasAudiobook == hasEbook)
                {
                    continue;
                }

                result.Candidates++;

                var missingAudiobook = !hasAudiobook;
                var overridden = missingAudiobook ? author.AudiobookSettingsManuallyOverridden : author.EbookSettingsManuallyOverridden;

                if (overridden)
                {
                    result.SkippedManuallyOverridden++;
                    continue;
                }

                var missingSideProfile = missingAudiobook ? author.AudiobookQualityProfileId : author.EbookQualityProfileId;

                if (!missingSideProfile.HasValue || missingSideProfile.Value <= 0)
                {
                    result.SkippedNoProfile++;
                    continue;
                }

                var defaultRoot = missingAudiobook ? audiobookDefault : ebookDefault;

                if (defaultRoot == null)
                {
                    result.SkippedNoDefault++;
                    continue;
                }

                if (!dryRun)
                {
                    if (missingAudiobook)
                    {
                        author.AudiobookRootFolderPath = defaultRoot.Path;
                    }
                    else
                    {
                        author.EbookRootFolderPath = defaultRoot.Path;
                    }

                    changed.Add(author);
                }

                result.Updated++;
                result.UpdatedAuthorIds.Add(author.Id);
            }

            if (!dryRun && changed.Any())
            {
                // Paths are assigned in place; existing files and author folders are not moved.
                _authorService.UpdateAuthors(changed, true);
            }

            return result;
        }

        public void Execute(BackfillAuthorRootFoldersCommand message)
        {
            var pending = message.PendingRefreshAuthorIds;

            if (pending == null)
            {
                var result = Backfill(false);

                _logger.Info("Root folder backfill: {0} candidate author(s), {1} updated, {2} skipped (manual settings), {3} skipped (no single default), {4} skipped (no quality profile)",
                    result.Candidates,
                    result.Updated,
                    result.SkippedManuallyOverridden,
                    result.SkippedNoDefault,
                    result.SkippedNoProfile);

                pending = result.UpdatedAuthorIds;
            }

            if (!pending.Any())
            {
                return;
            }

            // Newly attached roots need a forced local refresh. Queue it a batch at a time and let a
            // follow-up command carry the remainder so a large library does not flood the queue.
            var batch = pending.Take(RefreshBatchSize).ToList();
            var remaining = pending.Skip(RefreshBatchSize).ToList();

            _commandQueueManager.PushMany(batch
                .Select(id => new RefreshAuthorCommand(id, refreshMetadata: true, rescanFolders: false, forceRefresh: true))
                .ToList());

            if (remaining.Any())
            {
                _commandQueueManager.Push(new BackfillAuthorRootFoldersCommand { PendingRefreshAuthorIds = remaining });
            }
        }
    }
}
