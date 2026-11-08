using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentValidation.Results;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore.Events;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.ThingiProvider.Status;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class DownloadMonitoringServiceManualRetryFixture
    {
        private sealed class RecordingEventAggregator : IEventAggregator
        {
            public List<IEvent> Events { get; } = new();
            public Action<IEvent> Published { get; set; }

            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
                Events.Add(@event);
                Published?.Invoke(@event);
            }
        }

        private sealed class StubDownloadClientStatusService : IDownloadClientStatusService
        {
            public List<int> SuccessfulProviderIds { get; } = new();
            public List<int> FailedProviderIds { get; } = new();

            public List<DownloadClientStatus> BlockedProviders { get; } = new();
            public List<DownloadClientStatus> GetBlockedProviders() => BlockedProviders;

            public void RecordSuccess(int providerId)
            {
                SuccessfulProviderIds.Add(providerId);
            }

            public void RecordFailure(int providerId, TimeSpan minimumBackOff = default)
            {
                FailedProviderIds.Add(providerId);
            }

            public void RecordConnectionFailure(int providerId)
            {
                FailedProviderIds.Add(providerId);
            }
        }

        private sealed class RecordingCommandQueue : IManageCommandQueue
        {
            public List<CommandModel> PushedCommands { get; } = new();

            public List<CommandModel> PushMany<TCommand>(List<TCommand> commands) where TCommand : Command
            {
                return commands.Select(command => Push(command)).ToList();
            }

            public CommandModel Push<TCommand>(TCommand command, CommandPriority priority = CommandPriority.Normal, CommandTrigger trigger = CommandTrigger.Unspecified) where TCommand : Command
            {
                var model = new CommandModel
                {
                    Name = command.Name,
                    Body = command,
                    Priority = priority,
                    Trigger = trigger,
                    Status = CommandStatus.Queued
                };

                PushedCommands.Add(model);
                return model;
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
            public bool CancelIfQueued(int id) => throw new NotImplementedException();
            public void Pause(int id) => throw new NotImplementedException();
            public void Resume(int id) => throw new NotImplementedException();
            public void CleanCommands() => throw new NotImplementedException();
            public CancellationToken GetCancellationToken(int commandId) => throw new NotImplementedException();
            public void RegisterCancellationToken(int commandId, CancellationTokenSource cancellationTokenSource) => throw new NotImplementedException();
            public void UnregisterCancellationToken(int commandId) => throw new NotImplementedException();
        }

        private sealed class StubTrackedDownloadService : ITrackedDownloadService
        {
            private readonly Dictionary<string, TrackedDownload> _trackedDownloads;

            public StubTrackedDownloadService(params TrackedDownload[] trackedDownloads)
            {
                _trackedDownloads = trackedDownloads.ToDictionary(download => download.DownloadItem.DownloadId, StringComparer.OrdinalIgnoreCase);
            }

            public List<List<TrackedDownload>> UpdateTrackableCalls { get; } = new();
            public Exception TrackFailure { get; set; }

            public TrackedDownload Find(string downloadId)
            {
                _trackedDownloads.TryGetValue(downloadId, out var trackedDownload);
                return trackedDownload;
            }

            public void StopTracking(string downloadId)
            {
                _trackedDownloads.Remove(downloadId);
            }

            public void StopTracking(List<string> downloadIds)
            {
                foreach (var downloadId in downloadIds)
                {
                    _trackedDownloads.Remove(downloadId);
                }
            }

            public TrackedDownload TrackDownload(DownloadClientDefinition downloadClient, DownloadClientItem downloadItem)
            {
                if (TrackFailure != null) throw TrackFailure;
                if (_trackedDownloads.TryGetValue(downloadItem.DownloadId, out var existingItem) && existingItem.State != TrackedDownloadState.Downloading)
                {
                    existingItem.DownloadItem = downloadItem;
                    existingItem.IsTrackable = true;
                    return existingItem;
                }

                var trackedDownload = new TrackedDownload
                {
                    DownloadClient = downloadClient.Id,
                    DownloadItem = downloadItem,
                    State = existingItem?.State ?? TrackedDownloadState.Downloading,
                    IsTrackable = true
                };

                _trackedDownloads[downloadItem.DownloadId] = trackedDownload;
                return trackedDownload;
            }

            public List<TrackedDownload> GetTrackedDownloads()
            {
                return _trackedDownloads.Values.ToList();
            }

            public void UpdateTrackable(List<TrackedDownload> trackedDownloads)
            {
                var currentIds = trackedDownloads.Select(download => download.DownloadItem.DownloadId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                UpdateTrackableCalls.Add(trackedDownloads.ToList());

                foreach (var trackedDownload in _trackedDownloads.Values)
                {
                    trackedDownload.IsTrackable = currentIds.Contains(trackedDownload.DownloadItem.DownloadId);
                }
            }
        }

        private sealed class StubFailedDownloadService : IFailedDownloadService
        {
            public List<string> CheckedDownloadIds { get; } = new();
            public List<string> ProcessedDownloadIds { get; } = new();

            public void MarkAsFailed(int historyId, bool skipRedownload = false) => throw new NotImplementedException();
            public void MarkAsFailed(string downloadId, bool skipRedownload = false) => throw new NotImplementedException();

            public void Check(TrackedDownload trackedDownload)
            {
                CheckedDownloadIds.Add(trackedDownload.DownloadItem.DownloadId);
            }

            public void ProcessFailed(TrackedDownload trackedDownload)
            {
                ProcessedDownloadIds.Add(trackedDownload.DownloadItem.DownloadId);
            }
        }

        private sealed class StubCompletedDownloadService : ICompletedDownloadService
        {
            public List<string> CheckedDownloadIds { get; } = new();
            public List<string> ImportedDownloadIds { get; } = new();

            public void Check(TrackedDownload trackedDownload)
            {
                CheckedDownloadIds.Add(trackedDownload.DownloadItem.DownloadId);
            }

            public void Import(TrackedDownload trackedDownload)
            {
                ImportedDownloadIds.Add(trackedDownload.DownloadItem.DownloadId);
            }

            public bool CanRemoveDownload(TrackedDownload trackedDownload, DownloadCleanupContext cleanup = null) => throw new NotImplementedException();

            public bool VerifyImport(TrackedDownload trackedDownload, List<ImportResult> importResults) => throw new NotImplementedException();
        }

        private sealed class StubDownloadClient : IDownloadClient, IRecoverDownloadClientOperations
        {
            public IEnumerable<DownloadClientItem> Items { get; set; } = Array.Empty<DownloadClientItem>();

            public DownloadProtocol Protocol => (Definition as DownloadClientDefinition)?.Protocol ?? DownloadProtocol.Torrent;
            public string Name => Definition?.Name;
            public Type ConfigContract => typeof(DownloadClientDefinition);
            public ProviderMessage Message => null;
            public IEnumerable<ProviderDefinition> DefaultDefinitions => Enumerable.Empty<ProviderDefinition>();
            public ProviderDefinition Definition { get; set; }

            public Task<string> Download(RemoteBook remoteBook, IIndexer indexer) => throw new NotImplementedException();
            public int PollCount { get; private set; }
            public int RecoveryCount { get; private set; }
            public void RecoverPendingOperations() => RecoveryCount++;
            public bool PollFails { get; set; }
            public IEnumerable<DownloadClientItem> GetItems()
            {
                PollCount++;
                return PollFails ? throw new InvalidOperationException("Client offline") : Items;
            }
            public DownloadClientItem GetImportItem(DownloadClientItem item, DownloadClientItem previousImportAttempt) => item;
            public void RemoveItem(DownloadClientItem item, bool deleteData) => throw new NotImplementedException();
            public DownloadClientInfo GetStatus() => null;
            public void MarkItemAsImported(DownloadClientItem downloadClientItem) { }
            public ValidationResult Test() => new();
            public object RequestAction(string stage, IDictionary<string, string> query) => null;
        }

        private class DownloadClientFactoryProxy : DispatchProxy
        {
            public List<IDownloadClient> Clients { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IDownloadClientFactory.DownloadHandlingEnabled))
                {
                    return Clients;
                }

                throw new NotImplementedException($"Test proxy does not implement IDownloadClientFactory.{targetMethod?.Name}");
            }
        }

        private class ConfigServiceProxy : DispatchProxy
        {
            public bool EnableCompletedDownloadHandlingValue { get; set; } = true;

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == "get_EnableCompletedDownloadHandling")
                {
                    return EnableCompletedDownloadHandlingValue;
                }

                throw new NotImplementedException($"Test proxy does not implement IConfigService.{targetMethod?.Name}");
            }
        }

        [Test]
        public void failed_poll_must_preserve_known_queue_until_successful_poll_confirms_removal()
        {
            var tracked = CreateTrackedDownload("outage-download", TrackedDownloadState.ImportBlocked, DownloadItemStatus.Completed);
            var tracking = new StubTrackedDownloadService(tracked);
            var factory = CreateDownloadClientFactory(tracked.DownloadItem);
            var client = (StubDownloadClient)((DownloadClientFactoryProxy)(object)factory).Clients.Single();
            var config = CreateConfigService(true);
            var service = CreateSubject(tracking, factory, config, out var events, out _, out _, out _, out var status);
            tracked.RemoteBook.ParsedBookInfo = new ParsedBookInfo { Quality = new NzbDrone.Core.Qualities.QualityModel(NzbDrone.Core.Qualities.Quality.M4B) };
            var queue = new NzbDrone.Core.Queue.QueueService(events,
                Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<NzbDrone.Core.History.IHistoryService>((method, args) => new List<NzbDrone.Core.History.EntityHistory>()),
                Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<NzbDrone.Core.CustomFormats.ICustomFormatCalculationService>((method, args) => new List<NzbDrone.Core.CustomFormats.CustomFormat>()));
            events.Published = message => { if (message is TrackedDownloadRefreshedEvent refreshed) queue.Handle(refreshed); };
            var specification = new NzbDrone.Core.DecisionEngine.Specifications.QueueSpecification(queue,
                new NzbDrone.Core.DecisionEngine.Specifications.UpgradableSpecification(config, LogManager.GetCurrentClassLogger()),
                null, config, LogManager.GetCurrentClassLogger());
            service.Execute(new RefreshMonitoredDownloadsCommand());
            client.PollFails = true;
            service.Execute(new RefreshMonitoredDownloadsCommand());
            Assert.That(tracked.IsTrackable, Is.True, "A failed poll does not prove a download was removed");
            Assert.That(status.FailedProviderIds, Is.EqualTo(new[] { 1 }));
            Assert.That(queue.GetQueue(), Has.Count.EqualTo(1));
            Assert.That(specification.IsSatisfiedBy(tracked.RemoteBook, null).Accepted, Is.False);
            client.PollFails = false;
            client.Items = Array.Empty<DownloadClientItem>();
            service.Execute(new RefreshMonitoredDownloadsCommand());
            Assert.That(tracked.IsTrackable, Is.False, "A reachable client confirmed removal");
            Assert.That(queue.GetQueue(), Is.Empty);
            Assert.That(specification.IsSatisfiedBy(tracked.RemoteBook, null).Accepted, Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void client_backoff_retains_queue_without_reporting_removal_evidence(bool firstPollSucceeded)
        {
            var tracked = CreateTrackedDownload("backoff-download", TrackedDownloadState.ImportBlocked, DownloadItemStatus.Completed);
            var factory = CreateDownloadClientFactory(tracked.DownloadItem);
            var client = (StubDownloadClient)((DownloadClientFactoryProxy)(object)factory).Clients.Single();
            var service = CreateSubject(new StubTrackedDownloadService(tracked), factory, CreateConfigService(true),
                out var events, out _, out _, out _, out var status);
            if (firstPollSucceeded) service.Execute(new RefreshMonitoredDownloadsCommand());
            var polls = client.PollCount;
            status.BlockedProviders.Add(new DownloadClientStatus { ProviderId = 1 });
            service.Execute(new RefreshMonitoredDownloadsCommand());
            Assert.That(client.PollCount, Is.EqualTo(polls), "Backoff must still suppress network requests");
            Assert.That(client.RecoveryCount, Is.EqualTo(polls), "Backoff must also suppress recovery calls");
            Assert.That(tracked.IsTrackable, Is.True);
            var refresh = events.Events.OfType<TrackedDownloadRefreshedEvent>().Last();
            Assert.That(refresh.TrackedDownloads, Does.Contain(tracked));
            Assert.That(refresh.ReportedDownloadIds, Is.Empty);
        }

        [Test]
        public void item_processing_failure_keeps_known_item_until_recovery()
        {
            var tracked = CreateTrackedDownload("database-outage", TrackedDownloadState.ImportBlocked, DownloadItemStatus.Completed);
            var tracking = new StubTrackedDownloadService(tracked) { TrackFailure = new InvalidOperationException("Database unavailable") };
            var service = CreateSubject(tracking, CreateDownloadClientFactory(tracked.DownloadItem), CreateConfigService(true),
                out var events, out _, out _, out _, out _);
            service.Execute(new RefreshMonitoredDownloadsCommand());
            Assert.That(tracked.IsTrackable, Is.True);
            Assert.That(events.Events.OfType<TrackedDownloadRefreshedEvent>().Last().TrackedDownloads, Does.Contain(tracked));
            tracking.TrackFailure = null;
            service.Execute(new RefreshMonitoredDownloadsCommand());
            Assert.That(tracked.IsTrackable, Is.True);
            Assert.That(events.Events.OfType<TrackedDownloadRefreshedEvent>().Last().TrackedDownloads, Does.Contain(tracked));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task unavailable_client_at_startup_does_not_hold_unrelated_grab(bool inBackoff)
        {
            var factory = CreateDownloadClientFactory();
            var clients = ((DownloadClientFactoryProxy)(object)factory).Clients;
            clients[0].Definition.Name = "SABnzbd";
            ((DownloadClientDefinition)clients[0].Definition).Protocol = DownloadProtocol.Usenet;
            var offline = new StubDownloadClient
            {
                Definition = new DownloadClientDefinition { Id = 2, Name = "Deluge", Protocol = DownloadProtocol.Torrent },
                PollFails = true
            };
            clients.Add(offline);
            var service = CreateSubject(new StubTrackedDownloadService(), factory, CreateConfigService(true),
                out var events, out _, out _, out _, out var status);
            if (inBackoff) status.BlockedProviders.Add(new DownloadClientStatus { ProviderId = 2 });
            var queue = new NzbDrone.Core.Queue.QueueService(events,
                Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<NzbDrone.Core.History.IHistoryService>((method, args) => new List<NzbDrone.Core.History.EntityHistory>()), null);
            queue.Handle(new TrackedDownloadRefreshedEvent(new()));
            events.Published = message => { if (message is TrackedDownloadRefreshedEvent refreshed) queue.Handle(refreshed); };
            service.Execute(new RefreshMonitoredDownloadsCommand());
            Assert.That(status.SuccessfulProviderIds, Is.EqualTo(new[] { 1 }));
            Assert.That(offline.PollCount, Is.EqualTo(inBackoff ? 0 : 1));
            Assert.That(offline.RecoveryCount, Is.EqualTo(offline.PollCount));
            Assert.That(events.Events.OfType<TrackedDownloadRefreshedEvent>().Last().ReportedDownloadIds.Keys, Is.EqualTo(new[] { 1 }));

            var downloads = new List<RemoteBook>();
            var transport = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IDownloadService>((method, args) =>
            {
                downloads.Add((RemoteBook)args[0]);
                return Task.CompletedTask;
            });
            var prioritizer = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IPrioritizeDownloadDecision>((method, args) => args[0]);
            var pendingCount = 0;
            var pending = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<NzbDrone.Core.Download.Pending.IPendingReleaseService>((method, args) =>
            {
                pendingCount += ((System.Collections.ICollection)args[0]).Count;
                return null;
            });
            var processor = new ProcessDownloadDecisions(transport, prioritizer, pending,
                new NzbDrone.Core.DecisionEngine.Specifications.QueueSpecification(queue, null, null, null, LogManager.GetCurrentClassLogger()),
                LogManager.GetCurrentClassLogger());
            var book = new RemoteBook
            {
                Author = new Author { Id = 38, Name = "Frank Herbert" },
                Books = new List<Book> { new Book { Id = 17, MediaType = BookMediaType.Ebook } },
                Release = new ReleaseInfo { Title = "Frank Herbert - Dune", DownloadProtocol = DownloadProtocol.Usenet },
                ParsedBookInfo = new ParsedBookInfo { Quality = new NzbDrone.Core.Qualities.QualityModel(NzbDrone.Core.Qualities.Quality.EPUB) }
            };
            var result = await processor.ProcessDecisions(new List<NzbDrone.Core.DecisionEngine.DownloadDecision> { new(book) });
            Assert.That(result.Grabbed, Has.Count.EqualTo(1));
            Assert.That(result.Pending, Is.Empty);
            Assert.That(pendingCount, Is.Zero);
            Assert.That(downloads, Is.EqualTo(new[] { book }));
        }

        [Test]
        public void only_successful_polls_report_authoritative_download_ids()
        {
            var factory = CreateDownloadClientFactory();
            var client = (StubDownloadClient)((DownloadClientFactoryProxy)(object)factory).Clients.Single();
            var service = CreateSubject(new StubTrackedDownloadService(), factory, CreateConfigService(true),
                out var events, out _, out _, out _, out _);
            client.PollFails = true;
            service.Execute(new RefreshMonitoredDownloadsCommand());
            Assert.That(events.Events.OfType<TrackedDownloadRefreshedEvent>().Last().ReportedDownloadIds, Is.Empty);
            client.PollFails = false;
            service.Execute(new RefreshMonitoredDownloadsCommand());
            Assert.That(events.Events.OfType<TrackedDownloadRefreshedEvent>().Last().ReportedDownloadIds[1], Is.Empty);
            service.Handle(new DownloadsProcessedEvent());
            Assert.That(events.Events.OfType<TrackedDownloadRefreshedEvent>().Last().ReportedDownloadIds, Is.Null);
            client.PollFails = true;
            service.Execute(new RefreshMonitoredDownloadsCommand());
            Assert.That(events.Events.OfType<TrackedDownloadRefreshedEvent>().Last().ReportedDownloadIds, Is.Empty);
        }

        [Test]
        public void should_not_rearm_completed_import_blocked_downloads_on_manual_refresh()
        {
            var trackedDownload = CreateTrackedDownload("download-1", TrackedDownloadState.ImportBlocked, DownloadItemStatus.Completed);
            var trackedDownloadService = new StubTrackedDownloadService(trackedDownload);
            var subject = CreateSubject(
                trackedDownloadService,
                CreateDownloadClientFactory(CreateClientItem("download-1", DownloadItemStatus.Completed)),
                CreateConfigService(enableCompletedDownloadHandling: true),
                out var eventAggregator,
                out var commandQueue,
                out var failedDownloadService,
                out var completedDownloadService,
                out var statusService);

            subject.Execute(new RefreshMonitoredDownloadsCommand { Trigger = CommandTrigger.Manual });

            Assert.That(trackedDownload.State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
            Assert.That(commandQueue.PushedCommands.Select(command => command.Name), Does.Contain("ProcessMonitoredDownloads"));
            Assert.That(commandQueue.PushedCommands.Single().Trigger, Is.EqualTo(CommandTrigger.Manual));
            Assert.That(((ProcessMonitoredDownloadsCommand)commandQueue.PushedCommands.Single().Body).Trigger, Is.EqualTo(CommandTrigger.Manual));
            Assert.That(eventAggregator.Events, Has.Some.InstanceOf<TrackedDownloadRefreshedEvent>());
            Assert.That(failedDownloadService.CheckedDownloadIds, Is.Empty);
            Assert.That(completedDownloadService.CheckedDownloadIds, Is.Empty);
            Assert.That(statusService.SuccessfulProviderIds, Does.Contain(1));
            Assert.That(trackedDownloadService.UpdateTrackableCalls, Has.Count.EqualTo(1));
            Assert.That(trackedDownloadService.UpdateTrackableCalls[0].Single().State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
        }

        [Test]
        public void should_not_rearm_completed_import_blocked_downloads_on_scheduled_refresh()
        {
            var trackedDownload = CreateTrackedDownload("download-2", TrackedDownloadState.ImportBlocked, DownloadItemStatus.Completed);
            var trackedDownloadService = new StubTrackedDownloadService(trackedDownload);
            var subject = CreateSubject(
                trackedDownloadService,
                CreateDownloadClientFactory(CreateClientItem("download-2", DownloadItemStatus.Completed)),
                CreateConfigService(enableCompletedDownloadHandling: true),
                out _,
                out var commandQueue,
                out _,
                out _,
                out _);

            subject.Execute(new RefreshMonitoredDownloadsCommand { Trigger = CommandTrigger.Scheduled });

            Assert.That(trackedDownload.State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
            Assert.That(commandQueue.PushedCommands.Single().Trigger, Is.EqualTo(CommandTrigger.Scheduled));
            Assert.That(((ProcessMonitoredDownloadsCommand)commandQueue.PushedCommands.Single().Body).Trigger, Is.EqualTo(CommandTrigger.Scheduled));
        }

        [Test]
        public void should_not_rearm_non_completed_import_blocked_downloads_on_manual_refresh()
        {
            var trackedDownload = CreateTrackedDownload("download-3", TrackedDownloadState.ImportBlocked, DownloadItemStatus.Downloading);
            var trackedDownloadService = new StubTrackedDownloadService(trackedDownload);
            var subject = CreateSubject(
                trackedDownloadService,
                CreateDownloadClientFactory(CreateClientItem("download-3", DownloadItemStatus.Downloading)),
                CreateConfigService(enableCompletedDownloadHandling: true),
                out _,
                out _,
                out _,
                out _,
                out _);

            subject.Execute(new RefreshMonitoredDownloadsCommand { Trigger = CommandTrigger.Manual });

            Assert.That(trackedDownload.State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
        }

        [Test]
        public void should_not_rearm_completed_import_blocked_downloads_when_completed_download_handling_is_disabled()
        {
            var trackedDownload = CreateTrackedDownload("download-4", TrackedDownloadState.ImportBlocked, DownloadItemStatus.Completed);
            var trackedDownloadService = new StubTrackedDownloadService(trackedDownload);
            var subject = CreateSubject(
                trackedDownloadService,
                CreateDownloadClientFactory(CreateClientItem("download-4", DownloadItemStatus.Completed)),
                CreateConfigService(enableCompletedDownloadHandling: false),
                out _,
                out _,
                out _,
                out _,
                out _);

            subject.Execute(new RefreshMonitoredDownloadsCommand { Trigger = CommandTrigger.Manual });

            Assert.That(trackedDownload.State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
            Assert.That(trackedDownload.IsTrackable, Is.False);
        }

        [Test]
        public void should_queue_high_priority_refresh_when_remote_path_mapping_changes()
        {
            var trackedDownloadService = new StubTrackedDownloadService();
            var subject = CreateSubject(
                trackedDownloadService,
                CreateDownloadClientFactory(),
                CreateConfigService(enableCompletedDownloadHandling: true),
                out _,
                out var commandQueue,
                out _,
                out _,
                out _);

            subject.Handle(new ModelEvent<RemotePathMapping>(new RemotePathMapping
            {
                Id = 5,
                Host = "192.168.1.10",
                RemotePath = "/data/",
                LocalPath = "/downloads/"
            }, ModelAction.Updated));

            Assert.That(commandQueue.PushedCommands, Has.Count.EqualTo(1));
            Assert.That(commandQueue.PushedCommands.Single().Name, Is.EqualTo("RefreshMonitoredDownloads"));
            Assert.That(commandQueue.PushedCommands.Single().Priority, Is.EqualTo(CommandPriority.High));
        }

        private static DownloadMonitoringService CreateSubject(
            StubTrackedDownloadService trackedDownloadService,
            IDownloadClientFactory downloadClientFactory,
            IConfigService configService,
            out RecordingEventAggregator eventAggregator,
            out RecordingCommandQueue commandQueue,
            out StubFailedDownloadService failedDownloadService,
            out StubCompletedDownloadService completedDownloadService,
            out StubDownloadClientStatusService statusService)
        {
            eventAggregator = new RecordingEventAggregator();
            commandQueue = new RecordingCommandQueue();
            failedDownloadService = new StubFailedDownloadService();
            completedDownloadService = new StubCompletedDownloadService();
            statusService = new StubDownloadClientStatusService();

            return new DownloadMonitoringService(
                statusService,
                downloadClientFactory,
                eventAggregator,
                commandQueue,
                configService,
                failedDownloadService,
                completedDownloadService,
                trackedDownloadService,
                LogManager.GetCurrentClassLogger());
        }

        private static IDownloadClientFactory CreateDownloadClientFactory(params DownloadClientItem[] items)
        {
            var factory = DispatchProxy.Create<IDownloadClientFactory, DownloadClientFactoryProxy>();
            ((DownloadClientFactoryProxy)(object)factory).Clients = new List<IDownloadClient>
            {
                new StubDownloadClient
                {
                    Definition = new DownloadClientDefinition
                    {
                        Id = 1,
                        Name = "qBittorrent",
                        Protocol = DownloadProtocol.Torrent
                    },
                    Items = items
                }
            };

            return factory;
        }

        private static IConfigService CreateConfigService(bool enableCompletedDownloadHandling)
        {
            var config = DispatchProxy.Create<IConfigService, ConfigServiceProxy>();
            ((ConfigServiceProxy)(object)config).EnableCompletedDownloadHandlingValue = enableCompletedDownloadHandling;
            return config;
        }

        private static TrackedDownload CreateTrackedDownload(string downloadId, TrackedDownloadState state, DownloadItemStatus status)
        {
            return new TrackedDownload
            {
                DownloadClient = 1,
                DownloadItem = CreateClientItem(downloadId, status),
                State = state,
                IsTrackable = true,
                RemoteBook = new RemoteBook
                {
                    Author = new Author { Id = 38, Name = "Brian Herbert" },
                    Books = new List<Book>
                    {
                        new Book { Id = 1565, AuthorId = 38, Title = "House Harkonnen", MediaType = BookMediaType.Audiobook }
                    }
                }
            };
        }

        private static DownloadClientItem CreateClientItem(string downloadId, DownloadItemStatus status)
        {
            return new DownloadClientItem
            {
                DownloadId = downloadId,
                Title = $"Tracked {downloadId}",
                Status = status,
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Id = 1,
                    Name = "qBittorrent",
                    Protocol = DownloadProtocol.Torrent
                }
            };
        }
    }
}
