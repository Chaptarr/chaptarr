using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Download;
using NzbDrone.Core.Messaging.Commands;

namespace Chaptarr.Core.Test.Commands
{
    [TestFixture]
    public class CommandQueueManagerCancelFixture
    {
        private class RepoProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                var type = targetMethod.ReturnType;
                return type == typeof(void) || !type.IsValueType ? null : Activator.CreateInstance(type);
            }
        }

        private static CommandQueueManager BuildManager()
        {
            var repo = DispatchProxy.Create<ICommandRepository, RepoProxy>();
            return new CommandQueueManager(repo, null, null, LogManager.GetCurrentClassLogger());
        }

        // A running command whose handler does not observe cancellation used to be relabelled Cancelled immediately,
        // which hid a worker thread that was still busy (it dropped out of the started list and out of disk access
        // group accounting). The command must stay Started until the executor sees the handler exit.
        [Test]
        public void cancelling_a_running_command_should_keep_it_started_and_signal_the_token()
        {
            var manager = BuildManager();
            var model = manager.Push(new ProcessMonitoredDownloadsCommand());
            using var stop = new CancellationTokenSource();
            var running = manager.Queue(stop.Token).First();
            using var handlerToken = new CancellationTokenSource();
            manager.RegisterCancellationToken(running.Id, handlerToken);

            manager.Cancel(running.Id);

            Assert.That(running.Status, Is.EqualTo(CommandStatus.Started));
            Assert.That(running.Message, Is.EqualTo("Cancelling"));
            Assert.That(handlerToken.IsCancellationRequested, Is.True);
            Assert.That(model, Is.SameAs(running));
        }

        [Test]
        public void cancelling_twice_should_not_relabel_a_still_running_command()
        {
            var manager = BuildManager();
            manager.Push(new ProcessMonitoredDownloadsCommand());
            using var stop = new CancellationTokenSource();
            var running = manager.Queue(stop.Token).First();
            using var handlerToken = new CancellationTokenSource();
            manager.RegisterCancellationToken(running.Id, handlerToken);

            manager.Cancel(running.Id);
            manager.Cancel(running.Id);

            Assert.That(running.Status, Is.EqualTo(CommandStatus.Started));
        }

        [Test]
        public void cancelling_a_queued_command_should_still_cancel_it_immediately()
        {
            var manager = BuildManager();
            var queued = manager.Push(new ProcessMonitoredDownloadsCommand());

            manager.Cancel(queued.Id);

            Assert.That(queued.Status, Is.EqualTo(CommandStatus.Cancelled));
        }
    }
}
