using System;
using NUnit.Framework;
using NzbDrone.Core.MediaCover.Commands;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.Messaging.Commands;

namespace Chaptarr.Core.Test.Commands
{
    [TestFixture]
    public class CommandQueueCoverRepairFixture
    {
        [Test]
        public void author_cover_repair_should_not_block_an_unmapped_file_refresh()
        {
            var queue = new CommandQueue();
            queue.Add(BuildModel(new RepairAuthorMediaCoversCommand(), CommandStatus.Started, CommandPriority.Low, DateTime.UtcNow.AddSeconds(-1)));
            queue.Add(BuildModel(new RefreshUnmappedFilesCommand(), CommandStatus.Queued, CommandPriority.Normal, DateTime.UtcNow));

            var found = queue.TryGet(out var selected);

            Assert.That(found, Is.True);
            Assert.That(selected.Body, Is.TypeOf<RefreshUnmappedFilesCommand>());
            Assert.That(selected.Status, Is.EqualTo(CommandStatus.Started));
        }

        [Test]
        public void media_library_disk_commands_should_remain_serialized()
        {
            var queue = new CommandQueue();
            queue.Add(BuildModel(new RefreshUnmappedFilesCommand(), CommandStatus.Started, CommandPriority.Normal, DateTime.UtcNow.AddSeconds(-1)));
            queue.Add(BuildModel(new RetryUnmappedMatchCommand(), CommandStatus.Queued, CommandPriority.Normal, DateTime.UtcNow));

            var found = queue.TryGet(out var selected);

            Assert.That(found, Is.False);
            Assert.That(selected, Is.Null);
        }

        private static CommandModel BuildModel(
            Command command,
            CommandStatus status,
            CommandPriority priority,
            DateTime queuedAt)
        {
            return new CommandModel
            {
                Id = command is RefreshUnmappedFilesCommand ? 2 : 1,
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
