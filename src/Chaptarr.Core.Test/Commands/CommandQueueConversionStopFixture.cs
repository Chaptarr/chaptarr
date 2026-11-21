using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.MediaFiles.BookImport.Manual;
using NzbDrone.Core.Messaging.Commands;

namespace Chaptarr.Core.Test.Commands
{
    [TestFixture]
    public class CommandQueueConversionStopFixture
    {
        [TestCase(false)]
        [TestCase(true)]
        public void manual_import_and_retry_should_wait_for_each_other_across_disk_groups(bool manualStarted)
        {
            var queue = new CommandQueue(_ => 4);
            var manual = new ManualImportCommand();
            var retry = new RetryFailedImportCommand { DownloadId = "constructed-download" };
            var running = new CommandModel { Id = 1, Body = manualStarted ? manual : retry, Status = CommandStatus.Started };
            var waiting = new CommandModel { Id = 2, Body = manualStarted ? retry : manual, Status = CommandStatus.Queued };
            queue.Add(running);
            queue.Add(waiting);
            Assert.That(queue.TryGet(out _), Is.False);
            running.Status = CommandStatus.Completed;
            Assert.That(queue.TryGet(out var selected), Is.True);
            Assert.That(selected, Is.SameAs(waiting));
        }
    }
}
