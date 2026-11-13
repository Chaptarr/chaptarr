using System;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.MediaFiles.BookImport.Manual;
using NzbDrone.Core.Messaging.Commands;

namespace Chaptarr.Core.Test.Commands
{
    [TestFixture]
    public class CommandQueueDiskAccessGroupFixture
    {
        // chaptarr #188: a command in a distinct DiskAccessGroup (e.g. RetryFailedImportCommand's
        // "downloadImport") must be able to start while a command in a different group (e.g.
        // ProcessMonitoredDownloadsCommand's implicit "default") is already running -- that's the
        // entire point of naming a separate group. A prior bug blocked a candidate whenever ANY
        // started disk command was in a different group, which serialized every group behind
        // whichever one happened to be running first -- observed live as RetryFailedImport and
        // ManualImport commands sitting "queued" for the full 15+ minutes of a ProcessMonitoredDownloads
        // sweep over a large library.
        [Test]
        public void command_in_a_different_disk_access_group_should_not_be_blocked()
        {
            var queue = new CommandQueue();
            queue.Add(BuildModel(new ProcessMonitoredDownloadsCommand(), CommandStatus.Started, CommandPriority.Normal, DateTime.UtcNow.AddMinutes(-5), id: 1));
            queue.Add(BuildModel(new RetryFailedImportCommand { DownloadId = "ABC" }, CommandStatus.Queued, CommandPriority.High, DateTime.UtcNow, id: 2));

            var found = queue.TryGet(out var selected);

            Assert.That(found, Is.True);
            Assert.That(selected.Body, Is.TypeOf<RetryFailedImportCommand>());
            Assert.That(selected.Status, Is.EqualTo(CommandStatus.Started));
        }

        // Same-group serialization must still hold: two commands that share a DiskAccessGroup (here,
        // both defaulting to "default") still respect that group's limit (1, absent an explicit
        // higher limit), same as before this fix. ManualImportCommand is the real-world example that
        // was starved by the bug: it shares "default" with ProcessMonitoredDownloadsCommand, so it's
        // still correctly serialized behind it (unlike RetryFailedImportCommand above).
        [Test]
        public void command_in_the_same_disk_access_group_should_still_be_blocked()
        {
            var queue = new CommandQueue();
            queue.Add(BuildModel(new ProcessMonitoredDownloadsCommand(), CommandStatus.Started, CommandPriority.Normal, DateTime.UtcNow.AddMinutes(-5), id: 1));
            queue.Add(BuildModel(new ManualImportCommand(), CommandStatus.Queued, CommandPriority.High, DateTime.UtcNow, id: 2));

            var found = queue.TryGet(out var selected);

            Assert.That(found, Is.False);
            Assert.That(selected, Is.Null);
        }

        private static CommandModel BuildModel(
            Command command,
            CommandStatus status,
            CommandPriority priority,
            DateTime queuedAt,
            int id)
        {
            return new CommandModel
            {
                Id = id,
                Name = command.Name,
                Body = command,
                Priority = priority,
                Status = status,
                QueuedAt = queuedAt,
                StartedAt = status == CommandStatus.Started ? queuedAt : null
            };
        }
    }
}
