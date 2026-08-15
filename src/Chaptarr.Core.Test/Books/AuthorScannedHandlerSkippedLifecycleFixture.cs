using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class AuthorScannedHandlerSkippedLifecycleFixture
    {
        [Test]
        public void no_folder_evidence_skip_runs_the_normal_author_scan_completion_lifecycle()
        {
            var options = new AddAuthorOptions { SearchForMissingBooks = true };
            var author = new Author { Id = 7, Name = "New Author", AddOptions = options };
            var monitoredService = DispatchProxy.Create<IBookMonitoredService, BookMonitoredServiceProxy>();
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            var bookAddedService = DispatchProxy.Create<IBookAddedService, BookAddedServiceProxy>();
            var queue = new StubCommandQueueManager();
            var events = new StubEventAggregator();
            var subject = new AuthorScannedHandler(
                monitoredService,
                authorService,
                queue,
                bookAddedService,
                events,
                LogManager.GetLogger(nameof(AuthorScannedHandlerSkippedLifecycleFixture)));

            subject.Handle(new AuthorScanSkippedEvent(author, AuthorScanSkippedReason.NoFolderEvidence));

            var monitored = (BookMonitoredServiceProxy)(object)monitoredService;
            Assert.That(monitored.Author, Is.SameAs(author));
            Assert.That(monitored.Options, Is.SameAs(options));

            var authorProxy = (AuthorServiceProxy)(object)authorService;
            Assert.That(authorProxy.RemovedAddOptionsFor, Is.SameAs(author));
            Assert.That(author.AddOptions, Is.Null);

            var missingSearch = queue.Pushed.OfType<MissingBookSearchCommand>().Single();
            Assert.That(missingSearch.AuthorId, Is.EqualTo(author.Id));

            Assert.That(((BookAddedServiceProxy)(object)bookAddedService).AuthorIds, Is.EqualTo(new[] { author.Id }));
            var completed = events.PublishedEvents.OfType<AuthorScanCompletedEvent>().Single();
            Assert.That(completed.Author, Is.SameAs(author));
        }

        public class BookMonitoredServiceProxy : DispatchProxy
        {
            public Author Author { get; private set; }
            public MonitoringOptions Options { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IBookMonitoredService.SetBookMonitoredStatus))
                {
                    Author = (Author)args[0];
                    Options = (MonitoringOptions)args[1];
                    return null;
                }

                throw new NotImplementedException();
            }
        }

        public class AuthorServiceProxy : DispatchProxy
        {
            public Author RemovedAddOptionsFor { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IAuthorService.RemoveAddOptions))
                {
                    RemovedAddOptionsFor = (Author)args[0];
                    return null;
                }

                throw new NotImplementedException();
            }
        }

        public class BookAddedServiceProxy : DispatchProxy
        {
            public List<int> AuthorIds { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IBookAddedService.SearchForRecentlyAdded))
                {
                    AuthorIds.Add((int)args[0]);
                    return null;
                }

                throw new NotImplementedException();
            }
        }

        private sealed class StubEventAggregator : IEventAggregator
        {
            public List<IEvent> PublishedEvents { get; } = new();

            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
                PublishedEvents.Add(@event);
            }
        }

        private sealed class StubCommandQueueManager : IManageCommandQueue
        {
            public List<Command> Pushed { get; } = new();

            public CommandModel Push<TCommand>(TCommand command, CommandPriority priority = CommandPriority.Normal, CommandTrigger trigger = CommandTrigger.Unspecified)
                where TCommand : Command
            {
                Pushed.Add(command);
                return new CommandModel { Name = command.Name, Body = command, Priority = priority, Trigger = trigger };
            }

            public List<CommandModel> PushMany<TCommand>(List<TCommand> commands) where TCommand : Command => throw new NotImplementedException();
            public CommandModel Push(string commandName, DateTime? lastExecutionTime, DateTime? lastStartTime, CommandPriority priority = CommandPriority.Normal, CommandTrigger trigger = CommandTrigger.Unspecified) => throw new NotImplementedException();
            public IEnumerable<CommandModel> Queue(CancellationToken cancellationToken) => throw new NotImplementedException();
            public List<CommandModel> All() => throw new NotImplementedException();
            public CommandModel Get(int id) => throw new NotImplementedException();
            public List<CommandModel> GetStarted() => throw new NotImplementedException();
            public void SetMessage(CommandModel command, string message) => throw new NotImplementedException();
            public void TouchProgress(CommandModel command) => throw new NotImplementedException();
            public void SetResult(CommandModel command, CommandResult result) => throw new NotImplementedException();
            public void Start(CommandModel command) => throw new NotImplementedException();
            public void Complete(CommandModel command, string message) => throw new NotImplementedException();
            public void Fail(CommandModel command, string message, Exception e) => throw new NotImplementedException();
            public void Requeue() => throw new NotImplementedException();
            public void Cancel(int id) => throw new NotImplementedException();
            public void Pause(int id) => throw new NotImplementedException();
            public void Resume(int id) => throw new NotImplementedException();
            public void CleanCommands() => throw new NotImplementedException();
            public CancellationToken GetCancellationToken(int commandId) => throw new NotImplementedException();
            public void RegisterCancellationToken(int commandId, CancellationTokenSource cancellationTokenSource) => throw new NotImplementedException();
            public void UnregisterCancellationToken(int commandId) => throw new NotImplementedException();
        }
    }
}
