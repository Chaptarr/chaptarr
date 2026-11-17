using System.Collections.Generic;
using System.Linq;
using System;
using NzbDrone.Core.Messaging.Commands;
using NLog;

namespace NzbDrone.Core.MediaCover.Commands
{
    public class DownloadBookMediaCommand : Command
    {
        public int BookId { get; set; }
        public List<int> BookIds { get; set; } = new List<int>();
        public bool ForceDownload { get; set; }

        public DownloadBookMediaCommand()
        {
        }

        public DownloadBookMediaCommand(int bookId, bool forceDownload = false)
        {
            BookId = bookId;
            ForceDownload = forceDownload;
        }

        public DownloadBookMediaCommand(IEnumerable<int> bookIds)
        {
            BookIds = bookIds?.Where(id => id > 0).Distinct().ToList() ?? new List<int>();
        }

        public static IEnumerable<DownloadBookMediaCommand> ForBooks(IEnumerable<int> bookIds) =>
            bookIds.Where(id => id > 0).Distinct().Chunk(DeferredCoverService.BookBatchSize)
                .Select(page => new DownloadBookMediaCommand(page));

        // Saved selections must reach the writer even during imports (which defer HTTP only).
        // Reuse the existing minute retry if a queue page cannot be saved; later pages still run.
        public static void NotifyBookSelectionsChanged(IEnumerable<int> bookIds,
            IManageCommandQueue queue, IDeferredCoverService deferred, Logger logger)
        {
            foreach (var command in ForBooks(bookIds))
            {
                try
                {
                    if (queue.Push(command, priority: CommandPriority.Low)?.Status == CommandStatus.Started)
                    {
                        deferred.RequeueBooksForCoverDownload(command.BookIds);
                    }
                }
                catch (Exception ex)
                {
                    deferred.RequeueBooksForCoverDownload(command.BookIds);
                    logger.Warn(ex, "Unable to queue covers for {0} changed book selections; retained for retry", command.BookIds.Count);
                }
            }
        }

        public override bool SendUpdatesToClient => true;
    }
}
