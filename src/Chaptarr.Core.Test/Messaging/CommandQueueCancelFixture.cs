using System;
using System.Collections.Generic;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common;
using NzbDrone.Common.Composition;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Messaging
{
    [TestFixture]
    public class CommandQueueCancelFixture
    {
        private class Repository : DispatchProxy
        {
            private readonly List<CommandModel> _commands = new();

            protected override object Invoke(MethodInfo method, object[] args)
            {
                switch (method.Name)
                {
                    case nameof(ICommandRepository.Insert):
                        var model = (CommandModel)args[0];
                        model.Id = _commands.Count + 1;
                        _commands.Add(model);
                        return model;
                    case nameof(ICommandRepository.Get):
                        return _commands.Find(c => c.Id == (int)args[0]);
                    case nameof(ICommandRepository.End):
                        return null;
                }

                throw new NotImplementedException(method.Name);
            }
        }

        private class Events : DispatchProxy
        {
            public List<IEvent> Published { get; } = new();

            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (method.Name == nameof(IEventAggregator.PublishEvent))
                {
                    Published.Add((IEvent)args[0]);
                    return null;
                }

                throw new NotImplementedException(method.Name);
            }
        }

        private class Services : DispatchProxy
        {
            public object EventAggregator { get; set; }

            protected override object Invoke(MethodInfo method, object[] args) =>
                method.Name == nameof(IServiceFactory.Build) && method.IsGenericMethod && method.GetGenericArguments()[0] == typeof(IEventAggregator)
                    ? EventAggregator
                    : throw new NotImplementedException(method.Name);
        }

        [Test]
        public void cancelling_a_queued_command_publishes_that_it_never_ran()
        {
            var events = DispatchProxy.Create<IEventAggregator, Events>();
            var services = DispatchProxy.Create<IServiceFactory, Services>();
            ((Services)(object)services).EventAggregator = events;
            var queue = new CommandQueueManager(DispatchProxy.Create<ICommandRepository, Repository>(), services,
                new KnownTypes(new List<Type> { typeof(LibraryConversionProgressCommand) }), LogManager.GetCurrentClassLogger());

            var queued = queue.Push(new LibraryConversionProgressCommand());
            queue.Cancel(queued.Id);

            var cancelled = (CommandCancelledEvent)((Events)(object)events).Published.Find(e => e is CommandCancelledEvent);
            Assert.That(cancelled, Is.Not.Null);
            Assert.That(cancelled.WasQueued, Is.True);
            Assert.That(cancelled.Command.Body, Is.InstanceOf<LibraryConversionProgressCommand>());
            Assert.That(queued.Status, Is.EqualTo(CommandStatus.Cancelled));
        }

        [Test]
        public void cancelling_still_works_without_a_service_factory()
        {
            var queue = new CommandQueueManager(DispatchProxy.Create<ICommandRepository, Repository>(), null,
                new KnownTypes(new List<Type> { typeof(LibraryConversionProgressCommand) }), LogManager.GetCurrentClassLogger());

            var queued = queue.Push(new LibraryConversionProgressCommand());

            Assert.DoesNotThrow(() => queue.Cancel(queued.Id));
            Assert.That(queued.Status, Is.EqualTo(CommandStatus.Cancelled));
        }
    }
}
