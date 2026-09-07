using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.Clients.Direct;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.ThingiProvider.Status;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class DownloadMonitoringInternalDirectFixture
    {
        private sealed class RecordingEventAggregator : IEventAggregator
        {
            public List<IEvent> Events { get; } = new();

            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
                Events.Add(@event);
            }
        }

        private sealed class StubDownloadClientStatusService : IDownloadClientStatusService
        {
            public List<DownloadClientStatus> GetBlockedProviders() => new();
            public void RecordSuccess(int providerId) { }
            public void RecordFailure(int providerId, TimeSpan minimumBackOff = default) { }
            public void RecordConnectionFailure(int providerId) { }
        }

        private class ThrowingProxy<T> : DispatchProxy where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == "Push" || targetMethod?.Name == "PushMany")
                {
                    return null;
                }

                if (targetMethod?.Name == "Check" || targetMethod?.Name == "RecordSuccess" || targetMethod?.Name == "RecordFailure")
                {
                    return null;
                }

                if (targetMethod?.Name == "get_EnableCompletedDownloadHandling")
                {
                    return true;
                }

                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private sealed class StubDownloadClientFactory : IDownloadClientFactory
        {
            private readonly List<IDownloadClient> _clients;

            public StubDownloadClientFactory(IEnumerable<IDownloadClient> clients)
            {
                _clients = clients.ToList();
            }

            public List<IDownloadClient> GetAvailableProviders() => _clients.ToList();
            public List<DownloadClientDefinition> All() => _clients.Select(c => (DownloadClientDefinition)c.Definition).ToList();
            public List<IDownloadClient> DownloadHandlingEnabled(bool filterBlockedClients = true) => _clients.ToList();
            public bool Exists(int id) => _clients.Any(c => c.Definition.Id == id);
            public DownloadClientDefinition Find(int id) => All().SingleOrDefault(d => d.Id == id);
            public DownloadClientDefinition Get(int id) => All().SingleOrDefault(d => d.Id == id);
            public IEnumerable<DownloadClientDefinition> Get(IEnumerable<int> ids) => All().Where(d => ids.Contains(d.Id));
            public DownloadClientDefinition Create(DownloadClientDefinition definition) => throw new NotImplementedException();
            public void Update(DownloadClientDefinition definition) => throw new NotImplementedException();
            public IEnumerable<DownloadClientDefinition> Update(IEnumerable<DownloadClientDefinition> definitions) => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
            public void Delete(IEnumerable<int> ids) => throw new NotImplementedException();
            public IEnumerable<DownloadClientDefinition> GetDefaultDefinitions() => Enumerable.Empty<DownloadClientDefinition>();
            public IEnumerable<DownloadClientDefinition> GetPresetDefinitions(DownloadClientDefinition providerDefinition) => Enumerable.Empty<DownloadClientDefinition>();
            public void SetProviderCharacteristics(DownloadClientDefinition definition) => throw new NotImplementedException();
            public void SetProviderCharacteristics(IDownloadClient provider, DownloadClientDefinition definition) => throw new NotImplementedException();
            public IDownloadClient GetInstance(DownloadClientDefinition definition) => _clients.Single(c => c.Definition.Id == definition.Id);
            public FluentValidation.Results.ValidationResult Test(DownloadClientDefinition definition) => new();
            public object RequestAction(DownloadClientDefinition definition, string action, IDictionary<string, string> query) => null;
            public List<DownloadClientDefinition> AllForTag(int tagId) => new();
        }

        private sealed class StubTrackedDownloadService : ITrackedDownloadService
        {
            public List<TrackedDownload> TrackedDownloads { get; } = new();

            public TrackedDownload Find(string downloadId) => TrackedDownloads.FirstOrDefault(t => t.DownloadItem?.DownloadId == downloadId);
            public void StopTracking(string downloadId) { }
            public void StopTracking(List<string> downloadIds) { }

            public TrackedDownload TrackDownload(DownloadClientDefinition downloadClient, DownloadClientItem downloadItem)
            {
                var tracked = new TrackedDownload
                {
                    DownloadClient = downloadClient.Id,
                    DownloadItem = downloadItem,
                    Protocol = downloadClient.Protocol,
                    IsTrackable = true,
                    State = TrackedDownloadState.Downloading
                };
                TrackedDownloads.Add(tracked);
                return tracked;
            }

            public List<TrackedDownload> GetTrackedDownloads() => TrackedDownloads.ToList();
            public void UpdateTrackable(List<TrackedDownload> trackedDownloads) { }
        }

        private sealed class StubDownloadClient : IDownloadClient
        {
            private readonly List<DownloadClientItem> _items;

            public StubDownloadClient(int id, DownloadProtocol protocol, List<DownloadClientItem> items)
            {
                _items = items;
                Definition = new DownloadClientDefinition
                {
                    Id = id,
                    Name = protocol == DownloadProtocol.Direct ? "Direct Download" : protocol.ToString(),
                    ImplementationName = "StubClient",
                    Enable = true,
                    Protocol = protocol,
                    RemoveCompletedDownloads = true,
                    RemoveFailedDownloads = true,
                    Settings = new DirectDownloadClientSettings { StagingFolder = "/tmp/test-staging" }
                };
            }

            public string Name => ((DownloadClientDefinition)Definition).Name;
            public Type ConfigContract => typeof(DirectDownloadClientSettings);
            public ProviderMessage Message => null;
            public IEnumerable<ProviderDefinition> DefaultDefinitions => Enumerable.Empty<ProviderDefinition>();
            public ProviderDefinition Definition { get; set; }
            public DownloadProtocol Protocol => ((DownloadClientDefinition)Definition).Protocol;
            public FluentValidation.Results.ValidationResult Test() => new();
            public object RequestAction(string action, IDictionary<string, string> query) => null;
            public System.Threading.Tasks.Task<string> Download(RemoteBook remoteBook, IIndexer indexer) => throw new NotImplementedException();
            public IEnumerable<DownloadClientItem> GetItems() => _items;
            public DownloadClientItem GetImportItem(DownloadClientItem item, DownloadClientItem previousImportAttempt) => item;
            public void RemoveItem(DownloadClientItem item, bool deleteData) { }
            public DownloadClientInfo GetStatus() => new();
            public void MarkItemAsImported(DownloadClientItem downloadClientItem) { }
        }

        private sealed class StubInternalDirectClientProvider : IInternalDirectClientProvider
        {
            private readonly IDownloadClient _client;

            public StubInternalDirectClientProvider(IDownloadClient client)
            {
                _client = client;
            }

            public IDownloadClient GetClient() => _client;
        }

        private DownloadMonitoringService CreateMonitoringService(
            IDownloadClientFactory factory,
            StubTrackedDownloadService trackedService,
            RecordingEventAggregator eventAggregator,
            IInternalDirectClientProvider internalProvider = null)
        {
            return new DownloadMonitoringService(
                new StubDownloadClientStatusService(),
                factory,
                eventAggregator,
                DispatchProxy.Create<IManageCommandQueue, ThrowingProxy<IManageCommandQueue>>(),
                DispatchProxy.Create<NzbDrone.Core.Configuration.IConfigService, ThrowingProxy<NzbDrone.Core.Configuration.IConfigService>>(),
                DispatchProxy.Create<IFailedDownloadService, ThrowingProxy<IFailedDownloadService>>(),
                DispatchProxy.Create<ICompletedDownloadService, ThrowingProxy<ICompletedDownloadService>>(),
                trackedService,
                LogManager.GetCurrentClassLogger(),
                internalProvider);
        }

        [Test]
        public void should_include_internal_direct_client_items_in_monitored_downloads()
        {
            var directItem = new DownloadClientItem
            {
                DownloadId = "hp-philosopher-stone",
                Title = "J.K. Rowling - Harry Potter and the Philosopher's Stone [azw3]",
                Status = DownloadItemStatus.Downloading,
                TotalSize = 1024000,
                RemainingSize = 512000,
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Id = -1,
                    Name = "Direct Download",
                    Protocol = DownloadProtocol.Direct
                }
            };

            var internalClient = new StubDownloadClient(-1, DownloadProtocol.Direct, new List<DownloadClientItem> { directItem });
            var internalProvider = new StubInternalDirectClientProvider(internalClient);
            var factory = new StubDownloadClientFactory(Array.Empty<IDownloadClient>());
            var trackedService = new StubTrackedDownloadService();
            var eventAggregator = new RecordingEventAggregator();

            var monitoringService = CreateMonitoringService(factory, trackedService, eventAggregator, internalProvider);

            monitoringService.Execute(new RefreshMonitoredDownloadsCommand());

            Assert.That(trackedService.TrackedDownloads, Has.Count.EqualTo(1),
                "Internal Direct client items must appear in monitored downloads even with zero user-configured clients.");
            Assert.That(trackedService.TrackedDownloads[0].DownloadItem.DownloadId, Is.EqualTo("hp-philosopher-stone"));
            Assert.That(trackedService.TrackedDownloads[0].Protocol, Is.EqualTo(DownloadProtocol.Direct));
            Assert.That(trackedService.TrackedDownloads[0].DownloadClient, Is.EqualTo(-1));

            var refreshedEvent = eventAggregator.Events.OfType<TrackedDownloadRefreshedEvent>().FirstOrDefault();
            Assert.That(refreshedEvent, Is.Not.Null, "TrackedDownloadRefreshedEvent must be published.");
            Assert.That(refreshedEvent.TrackedDownloads, Has.Count.EqualTo(1));
        }

        [Test]
        public void should_not_duplicate_internal_direct_client_when_user_direct_client_exists()
        {
            var userDirectClient = new StubDownloadClient(5, DownloadProtocol.Direct, new List<DownloadClientItem>());
            var factory = new StubDownloadClientFactory(new IDownloadClient[] { userDirectClient });
            var trackedService = new StubTrackedDownloadService();
            var eventAggregator = new RecordingEventAggregator();

            var internalClient = new StubDownloadClient(-1, DownloadProtocol.Direct, new List<DownloadClientItem>
            {
                new()
                {
                    DownloadId = "should-not-appear",
                    Title = "Should Not Appear",
                    Status = DownloadItemStatus.Downloading,
                    DownloadClientInfo = new DownloadClientItemClientInfo { Id = -1, Name = "Direct Download", Protocol = DownloadProtocol.Direct }
                }
            });
            var internalProvider = new StubInternalDirectClientProvider(internalClient);

            var monitoringService = CreateMonitoringService(factory, trackedService, eventAggregator, internalProvider);

            monitoringService.Execute(new RefreshMonitoredDownloadsCommand());

            Assert.That(trackedService.TrackedDownloads, Is.Empty,
                "When a user-configured Direct client exists, the internal client should NOT be added.");
        }

        [Test]
        public void should_include_internal_direct_client_alongside_non_direct_user_clients()
        {
            var usenetItem = new DownloadClientItem
            {
                DownloadId = "usenet-1",
                Title = "Some Book [MP3]",
                Status = DownloadItemStatus.Downloading,
                DownloadClientInfo = new DownloadClientItemClientInfo { Id = 2, Name = "SABnzbd", Protocol = DownloadProtocol.Usenet }
            };

            var usenetClient = new StubDownloadClient(2, DownloadProtocol.Usenet, new List<DownloadClientItem> { usenetItem });

            var directItem = new DownloadClientItem
            {
                DownloadId = "direct-ebook",
                Title = "Some Book [epub]",
                Status = DownloadItemStatus.Downloading,
                DownloadClientInfo = new DownloadClientItemClientInfo { Id = -1, Name = "Direct Download", Protocol = DownloadProtocol.Direct }
            };
            var internalClient = new StubDownloadClient(-1, DownloadProtocol.Direct, new List<DownloadClientItem> { directItem });
            var internalProvider = new StubInternalDirectClientProvider(internalClient);

            var factory = new StubDownloadClientFactory(new IDownloadClient[] { usenetClient });
            var trackedService = new StubTrackedDownloadService();
            var eventAggregator = new RecordingEventAggregator();

            var monitoringService = CreateMonitoringService(factory, trackedService, eventAggregator, internalProvider);

            monitoringService.Execute(new RefreshMonitoredDownloadsCommand());

            Assert.That(trackedService.TrackedDownloads, Has.Count.EqualTo(2),
                "Both the Usenet client item and the internal Direct client item should appear in monitored downloads.");
            Assert.That(trackedService.TrackedDownloads.Any(t => t.DownloadItem.DownloadId == "usenet-1"), Is.True);
            Assert.That(trackedService.TrackedDownloads.Any(t => t.DownloadItem.DownloadId == "direct-ebook"), Is.True);
        }
    }
}
