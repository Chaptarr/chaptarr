using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Books.Commands
{
    /// <summary>
    /// Backfills the MatchingTitle column for all editions.
    /// Uses StringSuperNormalizer.ComputeMatchingTitle() to ensure consistent normalization.
    /// This command should be run once after migration 009, then automatically on startup
    /// if any editions are missing MatchingTitle values.
    /// </summary>
    public class BackfillMatchingTitlesCommand : Command
    {
        public override bool SendUpdatesToClient => false;
        public override bool RequiresDiskAccess => false;
        public override bool IsExclusive => true; // Exclusive to prevent concurrent writes

        public override string CompletionMessage => "MatchingTitle backfill completed";
    }
}
