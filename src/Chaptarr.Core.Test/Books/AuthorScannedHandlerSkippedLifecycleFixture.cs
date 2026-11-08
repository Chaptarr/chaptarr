using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
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
        public void rejected_initial_search_admission_retains_the_durable_request_until_an_accepted_retry()
        {
            var options = new AddAuthorOptions { Monitor = null, SearchForMissingBooks = true };
            var author = new Author { Id = 7, Name = "New Author", AddOptions = options };
            var queue = new StubCommandQueueManager { RejectSearch = true };
            var events = new StubEventAggregator();
            var subject = new AuthorScannedHandler(
                DispatchProxy.Create<IBookMonitoredService, BookMonitoredServiceProxy>(),
                DispatchProxy.Create<IAuthorService, AuthorServiceProxy>(), queue,
                DispatchProxy.Create<IBookAddedService, BookAddedServiceProxy>(), events,
                LogManager.GetCurrentClassLogger(),
                Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IBookService>((_, _) => null));

            subject.Handle(new AuthorScannedEvent(author));
            Assert.That(author.AddOptions, Is.SameAs(options));
            Assert.That(queue.Pushed.OfType<MissingBookSearchCommand>(), Is.Empty);
            Assert.That(events.PublishedEvents.OfType<AuthorScanCompletedEvent>().Count(), Is.EqualTo(1));
            queue.RejectSearch = false;
            subject.Handle(new AuthorScannedEvent(author));
            Assert.That(author.AddOptions, Is.SameAs(options));
            Assert.That(queue.Pushed.OfType<MissingBookSearchCommand>(), Is.Empty,
                "An immediate duplicate scan event must respect the persisted setup retry delay");
            options.InitialFormatSyncNotBefore = DateTime.UtcNow.AddSeconds(-1);
            subject.Handle(new AuthorScannedEvent(author));
            Assert.That(author.AddOptions, Is.Null);
            Assert.That(queue.Pushed.OfType<MissingBookSearchCommand>().Count(), Is.EqualTo(1));
        }

        [TestCase(true, false, false, "audiobook")]
        [TestCase(false, true, false, "ebook")]
        [TestCase(true, true, false, null)]
        [TestCase(true, false, true, null)]
        [TestCase(false, false, false, null)]
        public void initial_search_keeps_its_saved_media_scope(bool audio, bool ebook, bool independent, string expected)
        {
            var options = new AddAuthorOptions
            {
                SearchForMissingBooks = true,
                AudiobookSetupSearchForMissingBooks = audio,
                EbookSetupSearchForMissingBooks = ebook,
                IndependentSearchForMissingBooks = independent
            };
            var author = new Author { Id = 7, AddOptions = options };
            var queue = new StubCommandQueueManager();
            var subject = new AuthorScannedHandler(
                DispatchProxy.Create<IBookMonitoredService, BookMonitoredServiceProxy>(),
                DispatchProxy.Create<IAuthorService, AuthorServiceProxy>(), queue,
                DispatchProxy.Create<IBookAddedService, BookAddedServiceProxy>(), new StubEventAggregator(),
                LogManager.GetCurrentClassLogger(),
                Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IBookService>((_, _) => null));
            subject.Handle(new AuthorScannedEvent(author));
            Assert.That(queue.Pushed.OfType<MissingBookSearchCommand>().Single().MediaType, Is.EqualTo(expected));
            Assert.That(author.AddOptions, Is.Null);
        }

        [TestCase(null)]
        [TestCase(MonitorTypes.None)]
        [TestCase(MonitorTypes.All)]
        public void no_folder_evidence_skip_runs_the_normal_author_scan_completion_lifecycle(MonitorTypes? mode)
        {
            var options = new AddAuthorOptions { SearchForMissingBooks = true };
            if (mode.HasValue) options.Monitor = mode;
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
                LogManager.GetLogger(nameof(AuthorScannedHandlerSkippedLifecycleFixture)), Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IBookService>((method, args) => { queue.Pushed.Add(new BulkSyncFormatMonitoringCommand((List<int>)args[0])); return null; }));

            subject.Handle(new AuthorScanSkippedEvent(author, AuthorScanSkippedReason.NoFolderEvidence));

            var monitored = (BookMonitoredServiceProxy)(object)monitoredService;
            Assert.That(monitored.Author, mode.HasValue ? Is.SameAs(author) : Is.Null);
            Assert.That(monitored.Options, mode.HasValue ? Is.SameAs(options) : Is.Null);

            var authorProxy = (AuthorServiceProxy)(object)authorService;
            Assert.That(authorProxy.RemovedAddOptionsFor, Is.SameAs(author));
            Assert.That(author.AddOptions, Is.Null);

            var missingSearch = queue.Pushed.OfType<MissingBookSearchCommand>().Single();
            Assert.That(missingSearch.AuthorId, Is.EqualTo(author.Id));

            Assert.That(((BookAddedServiceProxy)(object)bookAddedService).AuthorIds, Is.EqualTo(new[] { author.Id }));
            var completed = events.PublishedEvents.OfType<AuthorScanCompletedEvent>().Single();
            Assert.That(completed.Author, Is.SameAs(author));
        }

        [Test]
        public void media_specific_initial_modes_should_be_applied_once_after_the_scan_without_changing_the_author_gate()
        {
            var author = new Author
            {
                Id = 7,
                Name = "New Author",
                AudiobookMonitored = false,
                EbookMonitored = true,
                AddOptions = new AddAuthorOptions
                {
                    AudiobookMonitor = MonitorTypes.Missing,
                    EbookMonitor = MonitorTypes.Existing
                }
            };
            var monitoredService = DispatchProxy.Create<IBookMonitoredService, BookMonitoredServiceProxy>();
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            var subject = new AuthorScannedHandler(
                monitoredService,
                authorService,
                new StubCommandQueueManager(),
                DispatchProxy.Create<IBookAddedService, BookAddedServiceProxy>(),
                new StubEventAggregator(),
                LogManager.GetLogger(nameof(AuthorScannedHandlerSkippedLifecycleFixture)), Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IBookService>((_, _) => throw new NotSupportedException()));

            subject.Handle(new AuthorScannedEvent(author));

            var calls = ((BookMonitoredServiceProxy)(object)monitoredService).Calls;
            Assert.Multiple(() =>
            {
                Assert.That(calls, Has.Count.EqualTo(2));
                Assert.That(calls[0].Options.Monitor, Is.EqualTo(MonitorTypes.Missing));
                Assert.That(calls[0].Options.MediaType, Is.EqualTo(BookMediaType.Audiobook));
                Assert.That(calls[1].Options.Monitor, Is.EqualTo(MonitorTypes.Existing));
                Assert.That(calls[1].Options.MediaType, Is.EqualTo(BookMediaType.Ebook));
                Assert.That(author.AudiobookMonitored, Is.False);
                Assert.That(author.EbookMonitored, Is.True);
                Assert.That(author.AddOptions, Is.Null);
            });
        }

        [Test]
        public void initial_scan_should_reconcile_format_sync_after_media_defaults()
        {
            var author = new Author
            {
                Id = 7,
                Name = "New Author",
                SyncMonitoredAcrossFormats = true,
                AddOptions = new AddAuthorOptions
                {
                    AudiobookMonitor = MonitorTypes.All,
                    EbookMonitor = MonitorTypes.None
                }
            };
            var queue = new StubCommandQueueManager();
            var monitoredService = DispatchProxy.Create<IBookMonitoredService, BookMonitoredServiceProxy>();
            var subject = new AuthorScannedHandler(
                monitoredService,
                DispatchProxy.Create<IAuthorService, AuthorServiceProxy>(),
                queue,
                DispatchProxy.Create<IBookAddedService, BookAddedServiceProxy>(),
                new StubEventAggregator(),
                LogManager.GetLogger(nameof(AuthorScannedHandlerSkippedLifecycleFixture)), Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IBookService>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(IBookService.SyncMonitoredAcrossFormats)));
                    Assert.That(((BookMonitoredServiceProxy)(object)monitoredService).Calls, Has.Count.EqualTo(2));
                    queue.Pushed.Add(new BulkSyncFormatMonitoringCommand((List<int>)args[0]));
                    return null;
                }));

            subject.Handle(new AuthorScannedEvent(author));

            var reconciliation = queue.Pushed.OfType<BulkSyncFormatMonitoringCommand>().Single();
            Assert.That(reconciliation.AuthorIds, Is.EqualTo(new[] { author.Id }));
        }

        [Test]
        public void importing_ebook_side_with_none_should_leave_audiobook_side_untouched_and_reconcile_once()
        {
            var author = new Author
            {
                Id = 7,
                Name = "Existing Author",
                AudiobookMonitored = true,
                EbookMonitored = false,
                SyncMonitoredAcrossFormats = true,
                AddOptions = new AddAuthorOptions { EbookMonitor = MonitorTypes.None }
            };
            var monitoredService = DispatchProxy.Create<IBookMonitoredService, BookMonitoredServiceProxy>();
            var queue = new StubCommandQueueManager();
            var subject = new AuthorScannedHandler(
                monitoredService,
                DispatchProxy.Create<IAuthorService, AuthorServiceProxy>(),
                queue,
                DispatchProxy.Create<IBookAddedService, BookAddedServiceProxy>(),
                new StubEventAggregator(),
                LogManager.GetLogger(nameof(AuthorScannedHandlerSkippedLifecycleFixture)), Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IBookService>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(IBookService.SyncMonitoredAcrossFormats)));
                    Assert.That(((BookMonitoredServiceProxy)(object)monitoredService).Calls, Has.Count.EqualTo(1));
                    queue.Pushed.Add(new BulkSyncFormatMonitoringCommand((List<int>)args[0]));
                    return null;
                }));

            subject.Handle(new AuthorScannedEvent(author));

            var call = ((BookMonitoredServiceProxy)(object)monitoredService).Calls.Single();
            Assert.Multiple(() =>
            {
                Assert.That(call.Options.MediaType, Is.EqualTo(BookMediaType.Ebook));
                Assert.That(call.Options.Monitor, Is.EqualTo(MonitorTypes.None));
                Assert.That(queue.Pushed.OfType<BulkSyncFormatMonitoringCommand>().Single().AuthorIds,
                    Is.EqualTo(new[] { author.Id }));
                Assert.That(author.AudiobookMonitored, Is.True);
                Assert.That(author.EbookMonitored, Is.False);
            });
        }

        [TestCase("monitor")]
        [TestCase("search")]
        [TestCase("clear")]
        public void one_failed_setup_backs_off_without_blocking_other_authors(string failingStep)
        {
            var authors = new List<Author>
            {
                new() { Id = 1, Name = "First", AddOptions = new AddAuthorOptions { Monitor = MonitorTypes.All, SearchForMissingBooks = true } },
                new() { Id = 2, Name = "Second", AddOptions = new AddAuthorOptions { Monitor = MonitorTypes.All, SearchForMissingBooks = true } }
            };
            var saved = new Dictionary<int, AddAuthorOptions>();
            var monitoring = new List<int>();
            var fail = true;
            var service = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IAuthorService>((method, args) =>
            {
                if (method.Name == nameof(IAuthorService.GetAuthors)) return authors;
                Assert.That(method.Name, Is.EqualTo(nameof(IAuthorService.SetAddOptions)));
                var author = (Author)args[0];
                if (fail && failingStep == "clear" && author.Id == 1 && author.AddOptions == null)
                    throw new InvalidOperationException("Setup persistence unavailable");
                saved[author.Id] = author.AddOptions;
                return null;
            });
            var queue = new StubCommandQueueManager { BeforePush = command =>
            {
                if (fail && failingStep == "search" && command is MissingBookSearchCommand search && search.AuthorId == 1)
                    throw new InvalidOperationException("Search queue unavailable");
            } };
            var subject = new AuthorScannedHandler(
                Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IBookMonitoredService>((_, args) =>
                {
                    var author = (Author)args[0];
                    monitoring.Add(author.Id);
                    if (fail && failingStep == "monitor" && author.Id == 1) throw new InvalidOperationException("Monitoring unavailable");
                    return null;
                }), service, queue, DispatchProxy.Create<IBookAddedService, BookAddedServiceProxy>(),
                new StubEventAggregator(), LogManager.GetCurrentClassLogger(),
                Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IBookService>((_, _) => null));
            var command = new BulkSyncFormatMonitoringCommand(new List<int> { 1, 2 }) { InitialSetupAttempt = 1 };
            subject.Execute(command);
            Assert.That(authors[1].AddOptions, Is.Null);
            Assert.That(saved[2], Is.Null);
            Assert.That(saved[1].InitialFormatSyncFailureCount, Is.EqualTo(1));
            Assert.That(saved[1].InitialFormatSyncNotBefore, Is.GreaterThan(DateTime.UtcNow));
            var calls = monitoring.Count;
            subject.Execute(command);
            Assert.That(monitoring, Has.Count.EqualTo(calls));
            Assert.That(saved[1].InitialFormatSyncFailureCount, Is.EqualTo(1));
            fail = false;
            authors[0].AddOptions = saved[1];
            authors[0].AddOptions.InitialFormatSyncNotBefore = DateTime.UtcNow.AddSeconds(-1);
            subject.Execute(command);
            Assert.That(authors[0].AddOptions, Is.Null);
            Assert.That(saved[1], Is.Null);
            if (failingStep != "monitor") Assert.That(monitoring.Count(id => id == 1), Is.EqualTo(1));
        }

        public class BookMonitoredServiceProxy : DispatchProxy
        {
            public Author Author { get; private set; }
            public MonitoringOptions Options { get; private set; }
            public List<(Author Author, MonitoringOptions Options)> Calls { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IBookMonitoredService.SetBookMonitoredStatus))
                {
                    Author = (Author)args[0];
                    Options = (MonitoringOptions)args[1];
                    Calls.Add((Author, Options));
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
                if (targetMethod?.Name == nameof(IAuthorService.SetAddOptions))
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
            public bool RejectSearch { get; set; }
            public Action<Command> BeforePush { get; set; }
            public List<Command> Pushed { get; } = new();

            public CommandModel Push<TCommand>(TCommand command, CommandPriority priority = CommandPriority.Normal, CommandTrigger trigger = CommandTrigger.Unspecified)
                where TCommand : Command
            {
                BeforePush?.Invoke(command);
                if (RejectSearch && command is MissingBookSearchCommand) return null;
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
            public bool CancelIfQueued(int id) => throw new NotImplementedException();
            public void Pause(int id) => throw new NotImplementedException();
            public void Resume(int id) => throw new NotImplementedException();
            public void CleanCommands() => throw new NotImplementedException();
            public CancellationToken GetCancellationToken(int commandId) => throw new NotImplementedException();
            public void RegisterCancellationToken(int commandId, CancellationTokenSource cancellationTokenSource) => throw new NotImplementedException();
            public void UnregisterCancellationToken(int commandId) => throw new NotImplementedException();
        }
    }
}
