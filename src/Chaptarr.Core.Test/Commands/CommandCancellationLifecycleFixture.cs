using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Composition;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Commands
{
    public class CommandCancellationLifecycleFixture
    {
        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void unsupported_running_command_is_rejected_but_queued_command_can_be_cancelled(bool postgres, bool started)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                var repository = new CommandRepository(db, Proxy<IEventAggregator>((_, _) => null));
                var manager = new CommandQueueManager(repository, null, new KnownTypes(new List<Type> { typeof(BookSearchCommand) }), LogManager.GetCurrentClassLogger());
                var command = manager.Push(new BookSearchCommand(new List<int> { 10 }));
                if (started)
                {
                    using var enumerator = manager.Queue(CancellationToken.None).GetEnumerator();
                    Assert.That(enumerator.MoveNext(), Is.True);
                    manager.Start(command);
                    var exception = Assert.Throws<NzbDroneClientException>(() => manager.Cancel(command.Id));
                    Assert.That(exception.Message, Is.EqualTo("This task can't be cancelled once it has started."));
                    Assert.That(repository.Get(command.Id).Status, Is.EqualTo(CommandStatus.Started));
                }
                else
                {
                    manager.Cancel(command.Id);
                    Assert.That(repository.Get(command.Id).Status, Is.EqualTo(CommandStatus.Cancelled));
                }
            });
        }

        [TestCase(false, false, false)]
        [TestCase(true, false, false)]
        [TestCase(false, true, false)]
        [TestCase(true, true, false)]
        [TestCase(false, false, true)]
        [TestCase(true, false, true)]
        public void cancellation_survives_late_registration_completion_and_failure(bool postgres, bool registerAfterCancel, bool fail)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                var repository = new CommandRepository(db, Proxy<IEventAggregator>((_, _) => null));
                var manager = new CommandQueueManager(repository, null, new KnownTypes(new List<Type> { typeof(AuthorSearchCommand) }), LogManager.GetCurrentClassLogger());
                var command = manager.Push(new AuthorSearchCommand { AuthorId = 10 });
                using var enumerator = manager.Queue(CancellationToken.None).GetEnumerator();
                Assert.That(enumerator.MoveNext(), Is.True);
                var cancellation = new CancellationTokenSource();
                if (!registerAfterCancel) manager.RegisterCancellationToken(command.Id, cancellation);
                manager.Cancel(command.Id);
                if (registerAfterCancel) manager.RegisterCancellationToken(command.Id, cancellation);
                Assert.That(cancellation.IsCancellationRequested, Is.True);
                manager.Start(command);
                if (fail) manager.Fail(command, "Failed", new InvalidOperationException("Search failed after cancellation"));
                else manager.Complete(command, "Completed");
                manager.UnregisterCancellationToken(command.Id);

                Assert.Multiple(() =>
                {
                    Assert.That(repository.Get(command.Id).Status, Is.EqualTo(CommandStatus.Cancelled));
                    Assert.That(repository.Get(command.Id).Message, Is.EqualTo("Cancelled by user"));
                    Assert.That(manager.GetCancellationToken(command.Id).CanBeCanceled, Is.False);
                    Assert.Throws<ObjectDisposedException>(() => { _ = cancellation.Token; });
                });
            });
        }

        [Test]
        public void cancellation_support_and_token_aware_handlers_must_agree_in_both_directions()
        {
            var types = new[] { typeof(Command).Assembly, typeof(Chaptarr.Api.V1.Indexers.GroupedAuthorSearchService).Assembly }
                .SelectMany(assembly => assembly.GetTypes()).Where(type => !type.IsAbstract).ToList();
            var optedIn = types.Where(type => typeof(Command).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) != null)
                .Where(type => ((Command)Activator.CreateInstance(type)).SupportsCancellation).ToHashSet();
            var handlers = types.SelectMany(type => type.GetInterfaces()
                .Where(contract => contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IExecute<>))
                .Select(contract => new { Handler = type, Command = contract.GetGenericArguments()[0] })).ToList();
            var tokenAware = handlers.Where(pair => pair.Handler.GetMethod("Execute", new[] { pair.Command, typeof(CancellationToken) }) != null)
                .Select(pair => pair.Command).ToHashSet();
            Assert.That(optedIn, Is.Not.Empty);
            Assert.That(tokenAware, Is.EquivalentTo(optedIn), "A token-aware handler must opt in, and an opted-in command must have a token-aware handler");
            foreach (var pair in handlers.Where(pair => optedIn.Contains(pair.Command)))
            {
                Assert.That(pair.Handler.GetMethod("Execute", new[] { pair.Command, typeof(CancellationToken) }), Is.Not.Null, pair.Handler.FullName);
            }
        }
    }
}
