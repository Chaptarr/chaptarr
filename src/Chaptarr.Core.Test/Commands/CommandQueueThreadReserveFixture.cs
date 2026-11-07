using System;
using NUnit.Framework;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Messaging.Commands;

namespace Chaptarr.Core.Test.Commands
{
    [TestFixture]
    public class CommandQueueThreadReserveFixture
    {
        [Test]
        public void should_skip_per_author_refresh_at_cap_and_start_other_work()
        {
            var queue = new CommandQueue(null, () => 3);
            queue.Add(Build(new RefreshAuthorCommand(1), CommandStatus.Started, CommandPriority.Normal, 1));
            queue.Add(Build(new RefreshAuthorCommand(2), CommandStatus.Started, CommandPriority.Normal, 2));
            queue.Add(Build(new RefreshAuthorCommand(3), CommandStatus.Queued, CommandPriority.High, 3));
            queue.Add(Build(new RssSyncCommand(), CommandStatus.Queued, CommandPriority.Low, 4));

            var found = queue.TryGet(out var selected);

            Assert.That(found, Is.True);
            Assert.That(selected.Body, Is.TypeOf<RssSyncCommand>());
        }

        [Test]
        public void should_start_per_author_refresh_below_cap()
        {
            var queue = new CommandQueue(null, () => 3);
            queue.Add(Build(new RefreshAuthorCommand(1), CommandStatus.Started, CommandPriority.Normal, 1));
            queue.Add(Build(new RefreshAuthorCommand(2), CommandStatus.Queued, CommandPriority.Normal, 2));

            Assert.That(queue.TryGet(out var selected), Is.True);
            Assert.That(selected.Id, Is.EqualTo(2));
        }

        [Test]
        public void should_still_allow_one_refresh_with_a_single_thread()
        {
            var queue = new CommandQueue(null, () => 1);
            queue.Add(Build(new RefreshAuthorCommand(1), CommandStatus.Queued, CommandPriority.Normal, 1));

            Assert.That(queue.TryGet(out _), Is.True);
        }

        [Test]
        public void should_not_cap_refreshes_without_a_thread_limit()
        {
            var queue = new CommandQueue();
            queue.Add(Build(new RefreshAuthorCommand(1), CommandStatus.Started, CommandPriority.Normal, 1));
            queue.Add(Build(new RefreshAuthorCommand(2), CommandStatus.Started, CommandPriority.Normal, 2));
            queue.Add(Build(new RefreshAuthorCommand(3), CommandStatus.Queued, CommandPriority.Normal, 3));

            Assert.That(queue.TryGet(out var selected), Is.True);
            Assert.That(selected.Id, Is.EqualTo(3));
        }

        private static CommandModel Build(Command command, CommandStatus status, CommandPriority priority, int id)
        {
            var queuedAt = DateTime.UtcNow.AddMinutes(-10 + id);

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
