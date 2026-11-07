using System.Collections.Generic;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Books.Commands
{
    public class RefreshBookCommand : Command
    {
        public int? BookId { get; set; }

        // A legacy BookshelfNG work can map to both Chaptarr format rows. Keep
        // the RefreshBook command contract while allowing its handler to refresh
        // every row represented by the legacy work ID.
        public List<int> BookIds { get; set; }

        public RefreshBookCommand()
        {
        }

        public RefreshBookCommand(int? bookId)
        {
            BookId = bookId;
        }

        public override bool SendUpdatesToClient => true;

        public override bool UpdateScheduledTask => !BookId.HasValue && (BookIds == null || BookIds.Count == 0);

        public override string CompletionMessage => "Completed";
    }
}
