using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Books.Extensions;
using NzbDrone.Core.Books.Repositories;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Books.Commands
{
    /// <summary>
    /// Handler for BackfillMatchingTitlesCommand.
    /// Backfills MatchingTitle for all editions using the canonical StringSuperNormalizer.
    /// Also runs on startup to ensure new editions from migration 009 have MatchingTitle populated.
    /// </summary>
    public class BackfillMatchingTitlesCommandHandler : IExecute<BackfillMatchingTitlesCommand>, IHandle<ApplicationStartedEvent>
    {
        private readonly IEditionRepository _editionRepository;
        private readonly IEditionFtsRepository _editionFtsRepository;
        private readonly Logger _logger;
        private const int BatchSize = 500;

        public BackfillMatchingTitlesCommandHandler(
            IEditionRepository editionRepository,
            IEditionFtsRepository editionFtsRepository,
            Logger logger)
        {
            _editionRepository = editionRepository;
            _editionFtsRepository = editionFtsRepository;
            _logger = logger;
        }

        public void Execute(BackfillMatchingTitlesCommand message)
        {
            _logger.Debug("[MATCHING-TITLE-BACKFILL] Starting MatchingTitle backfill for all editions");

            var totalEditions = _editionRepository.Count();
            var missingCount = _editionRepository.CountMissingMatchingTitles();

            if (missingCount <= 0)
            {
                _logger.Debug("[MATCHING-TITLE-BACKFILL] All editions already have MatchingTitle populated");
                return;
            }

            _logger.Debug("[MATCHING-TITLE-BACKFILL] Found {0} editions needing MatchingTitle (out of {1} total)",
                missingCount, totalEditions);

            // Process in batches without loading the full table into memory
            var totalUpdated = 0;
            var afterId = 0;

            while (true)
            {
                var batch = _editionRepository.GetMissingMatchingTitles(afterId, BatchSize);
                if (!batch.Any())
                {
                    break;
                }

                // Compute MatchingTitle using canonical normalizer
                foreach (var edition in batch)
                {
                    edition.MatchingTitle = StringSuperNormalizer.ComputeMatchingTitle(edition.Title);
                }

                // Update batch
                _editionRepository.UpdateMany(batch);
                totalUpdated += batch.Count;
                afterId = batch.Last().Id;

                _logger.Debug("[MATCHING-TITLE-BACKFILL] Updated {0}/{1} editions",
                    totalUpdated, missingCount);
            }

            _logger.Debug("[MATCHING-TITLE-BACKFILL] Completed - updated {0} editions", totalUpdated);

            // Rebuild FTS index to include the new MatchingTitle values
            _logger.Debug("[MATCHING-TITLE-BACKFILL] Rebuilding FTS index to include MatchingTitle");
            _editionFtsRepository.RebuildIndex();
            _logger.Debug("[MATCHING-TITLE-BACKFILL] FTS index rebuilt");
        }

        public void Handle(ApplicationStartedEvent message)
        {
            // Check if any editions are missing MatchingTitle and backfill if needed
            // This handles the case where migration 009 ran but editions weren't backfilled yet
            var missingCount = _editionRepository.CountMissingMatchingTitles();

            if (missingCount > 0)
            {
                _logger.Debug("[MATCHING-TITLE-BACKFILL] Found {0} editions missing MatchingTitle on startup - running backfill",
                    missingCount);
                Execute(new BackfillMatchingTitlesCommand());
            }
            else
            {
                _logger.Debug("[MATCHING-TITLE-BACKFILL] All editions have MatchingTitle - no backfill needed");
            }
        }
    }
}
