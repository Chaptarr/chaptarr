using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.Messaging.Commands;

namespace Chaptarr.Core.Test.Commands
{
    // The command executor thread count is configurable (CHAPTARR_COMMAND_THREADS), so the queue's exclusivity
    // rules must hold no matter how many consumers race on TryGet.
    [TestFixture]
    public class CommandQueueConcurrentConsumersFixture
    {
        private const int Consumers = 10;

        [Test]
        public void disk_access_commands_should_stay_serialized_with_many_consumers()
        {
            var queue = new CommandQueue();
            for (var i = 0; i < Consumers; i++)
            {
                queue.Add(Build(i + 1, new RefreshUnmappedFilesCommand()));
            }

            var started = RaceConsumers(queue);

            Assert.That(started.Count, Is.EqualTo(1));
        }

        [Test]
        public void type_exclusive_commands_should_not_run_twice_with_many_consumers()
        {
            var queue = new CommandQueue();
            for (var i = 0; i < Consumers; i++)
            {
                queue.Add(Build(i + 1, new RefreshAuthorCommand()));
            }

            var started = RaceConsumers(queue);

            Assert.That(started.Count, Is.EqualTo(1));
        }

        [Test]
        public void per_author_refreshes_should_run_concurrently_and_hold_every_consumer()
        {
            // Documents the assumption behind per-refresh state (#218, #181): per-author refreshes are neither
            // disk-access nor type exclusive, so all consumers can be busy with them at once.
            var queue = new CommandQueue();
            for (var i = 0; i < Consumers; i++)
            {
                queue.Add(Build(i + 1, new RefreshAuthorCommand(i + 1)));
            }

            var started = RaceConsumers(queue);

            Assert.That(started.Count, Is.EqualTo(Consumers));
        }

        private static List<CommandModel> RaceConsumers(CommandQueue queue)
        {
            var started = new List<CommandModel>();
            var gate = new object();

            Parallel.For(0, Consumers, new ParallelOptions { MaxDegreeOfParallelism = Consumers }, _ =>
            {
                if (queue.TryGet(out var item) && item != null)
                {
                    lock (gate)
                    {
                        started.Add(item);
                    }
                }
            });

            return started;
        }

        private static CommandModel Build(int id, Command command)
        {
            return new CommandModel
            {
                Id = id,
                Name = command.Name,
                Body = command,
                Priority = CommandPriority.Normal,
                Status = CommandStatus.Queued,
                QueuedAt = DateTime.UtcNow.AddSeconds(-Consumers + id)
            };
        }
    }
}
