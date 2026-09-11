using System;
using System.Collections.Generic;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Composition;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;

namespace Chaptarr.Core.Test.Messaging
{
    [TestFixture]
    public class CommandQueueTriggerFixture
    {
        private class RecordingRepository : DispatchProxy
        {
            public List<CommandModel> Inserted { get; } = new();

            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (method.Name == nameof(ICommandRepository.Insert))
                {
                    var model = (CommandModel)args[0];
                    model.Id = Inserted.Count + 1;
                    Inserted.Add(model);
                    return model;
                }

                throw new NotImplementedException(method.Name);
            }
        }

        private CommandQueueManager _queue;
        private RecordingRepository _repository;

        [SetUp]
        public void SetUp()
        {
            var repository = DispatchProxy.Create<ICommandRepository, RecordingRepository>();
            _repository = (RecordingRepository)(object)repository;
            _queue = new CommandQueueManager(repository, null,
                new KnownTypes(new List<Type> { typeof(BookSearchCommand) }), LogManager.GetCurrentClassLogger());
        }

        [Test]
        public void explicit_trigger_should_reach_the_persisted_command_body(
            [Values] CommandTrigger bodyTrigger, [Values] CommandTrigger queueTrigger)
        {
            var command = new BookSearchCommand(new List<int> { 1 }) { Trigger = bodyTrigger };
            var queued = _queue.Push(command, trigger: queueTrigger);

            Assert.That(queued.Body.Trigger, Is.EqualTo(queueTrigger == CommandTrigger.Unspecified ? bodyTrigger : queueTrigger));
            Assert.That(queued.Trigger, Is.EqualTo(queueTrigger));
            Assert.That(_repository.Inserted, Is.EqualTo(new[] { queued }));
        }

        [Test]
        public void omitted_trigger_should_preserve_a_constructor_set_trigger()
        {
            var queued = _queue.Push(new BulkSyncFormatMonitoringCommand());

            Assert.That(queued.Body.Trigger, Is.EqualTo(CommandTrigger.Manual));
        }

        [Test]
        public void duplicate_should_keep_the_first_commands_trigger([Values] CommandTrigger firstTrigger)
        {
            var first = _queue.Push(new BookSearchCommand(new List<int> { 1 }), trigger: firstTrigger);
            var second = _queue.Push(new BookSearchCommand(new List<int> { 1 }), trigger: CommandTrigger.Manual);

            Assert.That(second, Is.SameAs(first));
            Assert.That(second.Trigger, Is.EqualTo(firstTrigger));
            Assert.That(second.Body.Trigger, Is.EqualTo(firstTrigger));
            Assert.That(_repository.Inserted, Has.Count.EqualTo(1));
        }

        [Test]
        public void by_name_scheduler_push_should_preserve_scheduled_trigger()
        {
            var queued = _queue.Push(nameof(BookSearchCommand), null, null, trigger: CommandTrigger.Scheduled);

            Assert.That(queued.Trigger, Is.EqualTo(CommandTrigger.Scheduled));
            Assert.That(queued.Body.Trigger, Is.EqualTo(CommandTrigger.Scheduled));
        }
    }
}
