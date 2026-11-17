using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Composition;
using NzbDrone.Core.Messaging.Commands;

namespace Chaptarr.Core.Test.Commands
{
    [TestFixture]
    public class CommandQueueManagerProgressFixture
    {
        private class RepositoryProxy : DispatchProxy
        {
            public int SetFieldsCalls { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == "SetFields")
                {
                    SetFieldsCalls++;
                }

                return null;
            }
        }

        private RepositoryProxy _repository;
        private CommandQueueManager _manager;
        private CommandModel _command;

        [SetUp]
        public void SetUp()
        {
            var proxy = DispatchProxy.Create<ICommandRepository, RepositoryProxy>();
            _repository = (RepositoryProxy)proxy;
            _manager = new CommandQueueManager(proxy, null, new KnownTypes(), LogManager.GetCurrentClassLogger());
            _command = new CommandModel { Id = 42, Name = "RefreshAuthor" };
        }

        [Test]
        public void progress_messages_should_only_hit_the_database_once_per_interval()
        {
            for (var i = 0; i < 200; i++)
            {
                _manager.SetProgressMessage(_command, $"Checking Info for Book {i}");
            }

            // One write normally; two if the machine stalls past the 1s window mid-loop. Never 200.
            Assert.That(_repository.SetFieldsCalls, Is.InRange(1, 2), "first message persists, the rest fall inside the interval");
        }

        [Test]
        public void progress_messages_should_always_update_the_in_memory_command()
        {
            _manager.SetProgressMessage(_command, "Checking Info for Book 1");
            _manager.SetProgressMessage(_command, "Checking Info for Book 2");
            _manager.SetProgressMessage(_command, "Checking Info for Book 3");

            Assert.That(_command.Message, Is.EqualTo("Checking Info for Book 3"));
            Assert.That(_command.LastProgressAt, Is.Not.Null);
        }

        [Test]
        public void set_message_should_always_persist_and_reset_the_progress_throttle()
        {
            _manager.SetProgressMessage(_command, "Checking Info for Book 1");
            _manager.SetProgressMessage(_command, "Checking Info for Book 2");

            // Relative counts, so a stall past the throttle window earlier in the test cannot break them.
            var before = _repository.SetFieldsCalls;
            _manager.SetMessage(_command, "Completed");
            Assert.That(_repository.SetFieldsCalls, Is.EqualTo(before + 1), "explicit messages are never throttled");
            Assert.That(_command.Message, Is.EqualTo("Completed"));

            before = _repository.SetFieldsCalls;
            _manager.SetProgressMessage(_command, "Checking Info for Book 3");
            Assert.That(_repository.SetFieldsCalls, Is.EqualTo(before + 1), "a finished command's next progress message is not held back by stale throttle state");
        }

        [Test]
        public void progress_throttling_should_be_tracked_per_command()
        {
            var other = new CommandModel { Id = 43, Name = "RefreshAuthor" };

            _manager.SetProgressMessage(_command, "a");
            _manager.SetProgressMessage(other, "b");

            Assert.That(_repository.SetFieldsCalls, Is.EqualTo(2), "each command gets its own first persist");
        }
    }
}
