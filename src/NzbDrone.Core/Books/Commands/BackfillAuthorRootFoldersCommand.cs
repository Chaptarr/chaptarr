using System.Collections.Generic;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Books.Commands
{
    /// <summary>
    /// Fills the blank audiobook or ebook root folder of authors that have only one of the two,
    /// using the effective default root folder, then queues metadata refreshes in small batches.
    /// </summary>
    public class BackfillAuthorRootFoldersCommand : Command
    {
        public override string CompletionMessage => "Author root folder backfill completed";

        public BackfillAuthorRootFoldersCommand()
        {
            Trigger = CommandTrigger.Manual;
        }

        /// <summary>
        /// Authors whose refresh is still outstanding. Null on the initial run; set when the
        /// command re-queues itself to continue with the next batch.
        /// </summary>
        public List<int> PendingRefreshAuthorIds { get; set; }

        public override bool SendUpdatesToClient => true;
        public override bool UpdateScheduledTask => false;
        public override bool IsLongRunning => true;
    }
}
