using System;
using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.MediaCover.Commands;
using NzbDrone.Core.Messaging.Commands;

namespace Chaptarr.Core.Test.Commands
{
    [TestFixture]
    public class BulkSyncFormatMonitoringCommandFixture
    {
        [TestCase(CommandStatus.Completed)]
        [TestCase(CommandStatus.Failed)]
        [TestCase(CommandStatus.Cancelled)]
        public void overlapping_sync_waits_for_the_running_sync_to_finish(CommandStatus terminalStatus)
        {
            var queue = new CommandQueue();
            var running = Model(1, new BulkSyncFormatMonitoringCommand(new List<int> { 1 }), CommandStatus.Started);
            var waiting = Model(2, new BulkSyncFormatMonitoringCommand(new List<int> { 2 }) { InitialSetupAttempt = 1 }, CommandStatus.Queued);
            queue.Add(running);
            queue.Add(waiting);

            Assert.That(queue.TryGet(out var blocked), Is.False);
            Assert.That(blocked, Is.Null);
            Assert.That(waiting.Status, Is.EqualTo(CommandStatus.Queued));

            running.Status = terminalStatus;
            Assert.That(queue.TryGet(out var selected), Is.True);
            Assert.That(selected, Is.SameAs(waiting));
            Assert.That(selected.Status, Is.EqualTo(CommandStatus.Started));
        }

        [Test]
        public void running_sync_allows_other_command_types_to_start()
        {
            var queue = new CommandQueue();
            queue.Add(Model(1, new BulkSyncFormatMonitoringCommand(), CommandStatus.Started));
            var other = Model(2, new RepairAuthorMediaCoversCommand(), CommandStatus.Queued);
            queue.Add(other);

            Assert.That(queue.TryGet(out var selected), Is.True);
            Assert.That(selected, Is.SameAs(other));
        }

        private static CommandModel Model(int id, Command body, CommandStatus status) => new()
        {
            Id = id,
            Name = body.Name,
            Body = body,
            Status = status,
            Priority = CommandPriority.Normal,
            QueuedAt = DateTime.UtcNow,
            StartedAt = status == CommandStatus.Started ? DateTime.UtcNow : null
        };
    }
}
