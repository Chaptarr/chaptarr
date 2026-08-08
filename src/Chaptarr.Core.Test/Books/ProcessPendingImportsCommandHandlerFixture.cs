using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class ProcessPendingImportsCommandHandlerFixture
    {
        private sealed class StubPendingAuthorImportService : IPendingAuthorImportService
        {
            public Queue<List<PendingAuthorImport>> DueResponses { get; } = new();
            public int CleanupCount { get; private set; }
            public List<int> DeletedIds { get; } = new();

            public Task<int> EnqueueAsync(string providerId, MonitoringConfig config, string sourceApplication) => throw new NotImplementedException();
            public List<PendingAuthorImport> GetAll() => throw new NotImplementedException();
            public List<PendingAuthorImport> GetDueForProcessing(int limit = 10) => DueResponses.Count > 0 ? DueResponses.Dequeue() : new List<PendingAuthorImport>();
            public PendingAuthorImport GetByProviderId(string providerId) => throw new NotImplementedException();

            public void UpdateStatus(PendingAuthorImport item, PendingImportStatus status, string error)
            {
                item.OverallStatus = status;
                item.LastError = error;
            }

            public void ScheduleRetry(PendingAuthorImport item, string error) => throw new NotImplementedException();
            public void Cancel(int id) => throw new NotImplementedException();
            public void RetryNow(int id) => throw new NotImplementedException();
            public void Delete(int id) => DeletedIds.Add(id);
            public void CleanupOldCompleted() => CleanupCount++;
        }

        private class AuthorServiceProxy : DispatchProxy
        {
            public Author ExistingAuthor { get; set; } = new Author { Id = 42, Name = "Existing Author" };

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IAuthorService.FindByProviderId))
                {
                    return ExistingAuthor;
                }

                throw new NotImplementedException($"Test proxy does not implement IAuthorService.{targetMethod?.Name}");
            }
        }

        private class TerminalAuthorLibraryProxy : DispatchProxy
        {
            public AuthorTerminalException Terminal { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IAuthorLibraryService.AddAuthorAsync))
                {
                    return Task.FromException<Author>(Terminal);
                }

                throw new NotImplementedException($"Test proxy does not implement IAuthorLibraryService.{targetMethod?.Name}");
            }
        }
        private class ThrowingProxy<T> : DispatchProxy where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private sealed class RecordingCommandQueue : IManageCommandQueue
        {
            public List<Command> Pushed { get; } = new();

            public List<CommandModel> PushMany<TCommand>(List<TCommand> commands) where TCommand : Command => throw new NotImplementedException();

            public CommandModel Push<TCommand>(TCommand command, CommandPriority priority = CommandPriority.Normal, CommandTrigger trigger = CommandTrigger.Unspecified) where TCommand : Command
            {
                Pushed.Add(command);
                return new CommandModel { Name = command?.Name };
            }

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

        private sealed class RecordingEventAggregator : IEventAggregator
        {
            public List<IEvent> Events { get; } = new();

            public void PublishEvent<TEvent>(TEvent @event) where TEvent : class, IEvent
            {
                Events.Add(@event);
            }
        }

        private static PendingAuthorImport Pending(int id)
        {
            return new PendingAuthorImport
            {
                Id = id,
                ProviderId = $"gr:{id}",
                AuthorName = $"Author {id}",
                OverallStatus = PendingImportStatus.Pending,
                AudiobookStatus = PendingImportStatus.NotRequested,
                EbookStatus = PendingImportStatus.Pending,
                SearchForMissingBooks = false
            };
        }

        private static ProcessPendingImportsCommandHandler BuildHandler(
            StubPendingAuthorImportService pendingImportService,
            RecordingCommandQueue commandQueue,
            RecordingEventAggregator eventAggregator,
            bool existingAuthor = true,
            AuthorTerminalException terminal = null)
        {
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)authorService).ExistingAuthor = existingAuthor
                ? new Author { Id = 42, Name = "Existing Author" }
                : null;

            IAuthorLibraryService authorLibraryService;
            if (terminal == null)
            {
                authorLibraryService = DispatchProxy.Create<IAuthorLibraryService, ThrowingProxy<IAuthorLibraryService>>();
            }
            else
            {
                authorLibraryService = DispatchProxy.Create<IAuthorLibraryService, TerminalAuthorLibraryProxy>();
                ((TerminalAuthorLibraryProxy)authorLibraryService).Terminal = terminal;
            }

            return new ProcessPendingImportsCommandHandler(
                pendingImportService,
                authorLibraryService,
                authorService,
                DispatchProxy.Create<IBookService, ThrowingProxy<IBookService>>(),
                commandQueue,
                eventAggregator,
                LogManager.GetCurrentClassLogger());
        }
        [Test]
        public void should_not_emit_import_complete_while_continue_drain_has_more_due_items()
        {
            var pendingImportService = new StubPendingAuthorImportService();
            pendingImportService.DueResponses.Enqueue(new List<PendingAuthorImport> { Pending(1) });
            pendingImportService.DueResponses.Enqueue(new List<PendingAuthorImport> { Pending(2) });

            var commandQueue = new RecordingCommandQueue();
            var eventAggregator = new RecordingEventAggregator();
            var handler = BuildHandler(pendingImportService, commandQueue, eventAggregator);

            handler.Execute(new ProcessPendingImportsCommand { ContinueUntilEmpty = true, BatchSize = 1 });

            Assert.That(eventAggregator.Events.OfType<ImportStageProgressEvent>().Any(e => e.Stage == ImportStage.ImportComplete), Is.False);
            var continuation = commandQueue.Pushed.OfType<ProcessPendingImportsCommand>().Single();
            Assert.That(continuation.ContinueUntilEmpty, Is.True);
            Assert.That(continuation.Continuation, Is.EqualTo(1));
            Assert.That(pendingImportService.CleanupCount, Is.EqualTo(1));
        }

        [Test]
        public void should_emit_import_complete_on_final_continue_drain_batch()
        {
            var pendingImportService = new StubPendingAuthorImportService();
            pendingImportService.DueResponses.Enqueue(new List<PendingAuthorImport> { Pending(1) });
            pendingImportService.DueResponses.Enqueue(new List<PendingAuthorImport>());

            var commandQueue = new RecordingCommandQueue();
            var eventAggregator = new RecordingEventAggregator();
            var handler = BuildHandler(pendingImportService, commandQueue, eventAggregator);

            handler.Execute(new ProcessPendingImportsCommand { ContinueUntilEmpty = true, BatchSize = 1, Continuation = 1 });

            Assert.That(commandQueue.Pushed.OfType<ProcessPendingImportsCommand>(), Is.Empty);
            Assert.That(eventAggregator.Events.OfType<ImportStageProgressEvent>().Count(e => e.Stage == ImportStage.ImportComplete), Is.EqualTo(1));
            Assert.That(pendingImportService.CleanupCount, Is.EqualTo(0));
        }

        [Test]
        public void should_fail_declared_terminal_without_scheduling_an_automatic_retry()
        {
            var pendingImportService = new StubPendingAuthorImportService();
            var pending = Pending(99);
            pendingImportService.DueResponses.Enqueue(new List<PendingAuthorImport> { pending });

            var commandQueue = new RecordingCommandQueue();
            var eventAggregator = new RecordingEventAggregator();
            var terminal = new AuthorTerminalException(
                "author_identity_ambiguous",
                pending.ProviderId,
                pending.ProviderId,
                "Identity evidence is ambiguous.",
                reopenable: true);
            var handler = BuildHandler(
                pendingImportService,
                commandQueue,
                eventAggregator,
                existingAuthor: false,
                terminal: terminal);

            handler.Execute(new ProcessPendingImportsCommand());

            Assert.That(pending.OverallStatus, Is.EqualTo(PendingImportStatus.Failed));
            Assert.That(pending.LastError, Does.Contain("author_identity_ambiguous"));
            Assert.That(eventAggregator.Events.OfType<PendingAuthorImportFailedEvent>().Count(), Is.EqualTo(1));
        }

        [Test]
        public void should_stop_automatic_retry_for_a_typed_never_served_not_found()
        {
            var pendingImportService = new StubPendingAuthorImportService();
            var pending = Pending(100);
            pendingImportService.DueResponses.Enqueue(new List<PendingAuthorImport> { pending });

            var commandQueue = new RecordingCommandQueue();
            var eventAggregator = new RecordingEventAggregator();
            var terminal = new AuthorTerminalException(
                "author_provider_record_missing",
                pending.ProviderId,
                pending.ProviderId,
                "The provider no longer has this author record.",
                reopenable: true);
            var handler = BuildHandler(
                pendingImportService,
                commandQueue,
                eventAggregator,
                existingAuthor: false,
                terminal: terminal);

            handler.Execute(new ProcessPendingImportsCommand());

            Assert.That(pending.OverallStatus, Is.EqualTo(PendingImportStatus.Failed));
            Assert.That(pending.LastError, Does.Contain("author_provider_record_missing"));
            Assert.That(eventAggregator.Events.OfType<PendingAuthorImportFailedEvent>().Count(), Is.EqualTo(1));
        }
    }
}
