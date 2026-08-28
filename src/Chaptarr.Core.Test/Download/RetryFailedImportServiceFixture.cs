using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentValidation.Results;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;
using NzbDrone.Core.ThingiProvider;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class RetryFailedImportServiceFixture
    {
        private sealed class StubTrackedDownloadService : ITrackedDownloadService
        {
            private readonly Dictionary<string, TrackedDownload> _trackedDownloads;

            public StubTrackedDownloadService(params TrackedDownload[] trackedDownloads)
            {
                _trackedDownloads = trackedDownloads.ToDictionary(download => download.DownloadItem.DownloadId, StringComparer.OrdinalIgnoreCase);
            }

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
                if (_trackedDownloads.TryGetValue(downloadItem.DownloadId, out var existing))
                {
                    existing.DownloadClient = downloadClient.Id;
                    existing.DownloadItem = downloadItem;
                    existing.IsTrackable = true;
                    return existing;
                }

                var trackedDownload = new TrackedDownload
                {
                    DownloadClient = downloadClient.Id,
                    DownloadItem = downloadItem,
                    State = TrackedDownloadState.Downloading,
                    IsTrackable = true
                };

                _trackedDownloads[downloadItem.DownloadId] = trackedDownload;
                return trackedDownload;
            }

            public List<TrackedDownload> GetTrackedDownloads()
            {
                return _trackedDownloads.Values.ToList();
            }

            public void UpdateTrackable(List<TrackedDownload> trackedDownloads) => throw new NotImplementedException();
        }

        private sealed class StubCompletedDownloadService : ICompletedDownloadService
        {
            private readonly TrackedDownloadState _stateAfterImport;

            public StubCompletedDownloadService(TrackedDownloadState stateAfterImport)
            {
                _stateAfterImport = stateAfterImport;
            }

            public List<string> ImportedDownloadIds { get; } = new();
            public List<TrackedDownloadState> StatesAtImportStart { get; } = new();
            public List<TrackedDownloadStatus> StatusesAtImportStart { get; } = new();
            public List<TrackedDownloadStatusMessage[]> StatusMessagesAtImportStart { get; } = new();
            public List<string> OutputPathsAtImportStart { get; } = new();
            public List<List<string>> FilePathsAtImportStart { get; } = new();

            public void Check(TrackedDownload trackedDownload) => throw new NotImplementedException();

            public void Import(TrackedDownload trackedDownload)
            {
                ImportedDownloadIds.Add(trackedDownload.DownloadItem.DownloadId);
                StatesAtImportStart.Add(trackedDownload.State);
                StatusesAtImportStart.Add(trackedDownload.Status);
                StatusMessagesAtImportStart.Add(trackedDownload.StatusMessages);
                OutputPathsAtImportStart.Add(trackedDownload.DownloadItem.OutputPath.FullPath);
                FilePathsAtImportStart.Add(trackedDownload.DownloadItem.FilePaths?.ToList());
                trackedDownload.State = _stateAfterImport;
            }

            public bool VerifyImport(TrackedDownload trackedDownload, List<ImportResult> importResults) => throw new NotImplementedException();
        }

        private sealed class RecordingConversionTrackingService : IConversionTrackingService
        {
            public List<string> ClearedDownloadIds { get; } = new();

            public void Start(string downloadId, int targetQualityId, string targetQualityName, string message = null) => throw new NotImplementedException();
            public void Progress(string downloadId, decimal? progress, string message = null) => throw new NotImplementedException();
            public void RegisterCancellation(string downloadId, CancellationTokenSource cancellationTokenSource) => throw new NotImplementedException();
            public bool Cancel(string downloadId) => throw new NotImplementedException();
            public void Cancelled(string downloadId, string message = null) => throw new NotImplementedException();
            public void Complete(string downloadId) => throw new NotImplementedException();
            public void Fail(string downloadId, string errorMessage) => throw new NotImplementedException();

            public void Clear(string downloadId)
            {
                ClearedDownloadIds.Add(downloadId);
            }

            public ConversionQueueStatus Get(string downloadId) => null;
        }

        private sealed class RecordingDownloadClientFileSnapshotService : IDownloadClientFileSnapshotService
        {
            public List<string> DeletedDownloadIds { get; } = new();

            public void CaptureClientList(DownloadClientItem item) => throw new NotImplementedException();
            public void CaptureCompletedOutput(DownloadClientItem item) => throw new NotImplementedException();
            public void ApplySnapshot(DownloadClientItem item) => throw new NotImplementedException();

            public void Delete(DownloadClientItem item)
            {
                if (item?.DownloadId.IsNotNullOrWhiteSpace() == true)
                {
                    DeletedDownloadIds.Add(item.DownloadId);
                }
            }
        }

        private sealed class RecordingCommandResultReporter : ICommandResultReporter
        {
            public List<CommandResult> Results { get; } = new();

            public void Report(CommandResult result)
            {
                Results.Add(result);
            }
        }

        private sealed class RecordingEventAggregator : IEventAggregator
        {
            public List<IEvent> Events { get; } = new();

            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
                Events.Add(@event);
            }
        }

        private sealed class StubDownloadClientProvider : IProvideDownloadClient
        {
            public IDownloadClient Client { get; set; }

            public IDownloadClient GetDownloadClient(DownloadProtocol downloadProtocol, BookMediaType mediaType, int indexerId = 0, bool filterBlockedClients = false, HashSet<int> tags = null) => throw new NotImplementedException();
            public IEnumerable<IDownloadClient> GetDownloadClients(bool filterBlockedClients = false) => throw new NotImplementedException();
            public IDownloadClient Get(int id) => Client != null && Client.Definition.Id == id ? Client : null;
        }

        private sealed class StubDownloadClient : IDownloadClient
        {
            public IEnumerable<DownloadClientItem> Items { get; set; } = Array.Empty<DownloadClientItem>();

            public DownloadProtocol Protocol => ((DownloadClientDefinition)Definition).Protocol;
            public string Name => Definition.Name;
            public Type ConfigContract => typeof(DownloadClientDefinition);
            public ProviderMessage Message => null;
            public IEnumerable<ProviderDefinition> DefaultDefinitions => Enumerable.Empty<ProviderDefinition>();
            public ProviderDefinition Definition { get; set; }

            public Task<string> Download(RemoteBook remoteBook, IIndexer indexer) => throw new NotImplementedException();
            public IEnumerable<DownloadClientItem> GetItems() => Items;
            public DownloadClientItem GetImportItem(DownloadClientItem item, DownloadClientItem previousImportAttempt) => item;
            public void RemoveItem(DownloadClientItem item, bool deleteData) => throw new NotImplementedException();
            public DownloadClientInfo GetStatus() => null;
            public void MarkItemAsImported(DownloadClientItem downloadClientItem) { }
            public ValidationResult Test() => new();
            public object RequestAction(string stage, IDictionary<string, string> query) => null;
        }

        private sealed class StubRemotePathMappingService : IRemotePathMappingService
        {
            public Func<int, string, OsPath, OsPath> Remap { get; set; } = (_, _, path) => path;

            public List<RemotePathMapping> All() => throw new NotImplementedException();
            public RemotePathMapping Add(RemotePathMapping mapping) => throw new NotImplementedException();
            public void Remove(int id) => throw new NotImplementedException();
            public RemotePathMapping Get(int id) => throw new NotImplementedException();
            public RemotePathMapping Update(RemotePathMapping mapping) => throw new NotImplementedException();
            public OsPath RemapRemoteToLocal(string host, OsPath remotePath) => Remap(0, host, remotePath);
            public OsPath RemapLocalToRemote(string host, OsPath localPath) => localPath;
            public OsPath RemapRemoteToLocal(int downloadClientId, string host, OsPath remotePath) => Remap(downloadClientId, host, remotePath);
            public OsPath RemapLocalToRemote(int downloadClientId, string host, OsPath localPath) => localPath;
            public RemotePathMappingTestResult Test(RemotePathMapping mapping) => throw new NotImplementedException();
        }

        [Test]
        public void should_retry_single_blocked_completed_download()
        {
            var trackedDownload = CreateTrackedDownload("download-1", TrackedDownloadState.ImportBlocked, DownloadItemStatus.Completed);
            var subject = CreateSubject(trackedDownload, TrackedDownloadState.Imported, out var completedDownloadService, out var conversionTrackingService, out var commandResultReporter, out var eventAggregator);

            subject.Execute(new RetryFailedImportCommand { DownloadId = "download-1" });

            Assert.That(completedDownloadService.StatesAtImportStart, Is.EqualTo(new[] { TrackedDownloadState.ImportPending }));
            Assert.That(completedDownloadService.ImportedDownloadIds, Is.EqualTo(new[] { "download-1" }));
            Assert.That(conversionTrackingService.ClearedDownloadIds, Is.EqualTo(new[] { "download-1" }));
            Assert.That(trackedDownload.State, Is.EqualTo(TrackedDownloadState.Imported));
            Assert.That(commandResultReporter.Results, Is.Empty);
            Assert.That(eventAggregator.Events.OfType<TrackedDownloadRefreshedEvent>().Count(), Is.EqualTo(2));
        }

        [Test]
        public void should_clear_stale_failure_message_before_retrying_import()
        {
            var trackedDownload = CreateTrackedDownload("download-1", TrackedDownloadState.ImportBlocked, DownloadItemStatus.Completed);
            trackedDownload.Warn(new TrackedDownloadStatusMessage("Tracked download-1", "Conversion skipped because the destination file already exists"));
            var subject = CreateSubject(trackedDownload, TrackedDownloadState.ImportBlocked, out var completedDownloadService, out _, out _, out _);

            subject.Execute(new RetryFailedImportCommand { DownloadId = "download-1" });

            Assert.That(completedDownloadService.StatesAtImportStart, Is.EqualTo(new[] { TrackedDownloadState.ImportPending }));
            Assert.That(completedDownloadService.StatusesAtImportStart, Is.EqualTo(new[] { TrackedDownloadStatus.Ok }));
            Assert.That(completedDownloadService.StatusMessagesAtImportStart.Single(), Is.Empty);
        }

        [Test]
        public void should_delete_persisted_file_snapshots_before_retrying_import()
        {
            var trackedDownload = CreateTrackedDownload("download-1", TrackedDownloadState.ImportBlocked, DownloadItemStatus.Completed);
            trackedDownload.ImportItem = CreateClientItem("download-1", DownloadItemStatus.Completed, "/downloads/book");
            var subject = CreateSubject(trackedDownload, TrackedDownloadState.Imported, out _, out _, out _, out _, out var snapshotService);

            subject.Execute(new RetryFailedImportCommand { DownloadId = "download-1" });

            Assert.That(snapshotService.DeletedDownloadIds, Is.EqualTo(new[] { "download-1", "download-1" }));
        }

        [Test]
        public void should_refresh_blocked_download_from_client_before_retrying_import()
        {
            var trackedDownload = CreateTrackedDownload("download-1", TrackedDownloadState.ImportBlocked, DownloadItemStatus.Completed, "/old/path");
            var currentItem = CreateClientItem("download-1", DownloadItemStatus.Completed, "/new/path");
            var client = CreateDownloadClient(currentItem);
            var subject = CreateSubject(
                trackedDownload,
                TrackedDownloadState.Imported,
                out var completedDownloadService,
                out _,
                out _,
                out _,
                downloadClientProvider: new StubDownloadClientProvider { Client = client });

            subject.Execute(new RetryFailedImportCommand { DownloadId = "download-1" });

            Assert.That(completedDownloadService.OutputPathsAtImportStart, Is.EqualTo(new[] { "/new/path" }));
        }

        [Test]
        public void should_remap_cached_output_path_and_clear_file_paths_when_client_no_longer_reports_download()
        {
            var trackedDownload = CreateTrackedDownload(
                "download-1",
                TrackedDownloadState.ImportBlocked,
                DownloadItemStatus.Completed,
                "/data/book",
                new List<string> { "/data/book/part1.mp3" });
            var client = CreateDownloadClient();
            var remotePathMappingService = new StubRemotePathMappingService
            {
                Remap = (_, _, path) => path.FullPath.StartsWith("/data/", StringComparison.Ordinal)
                    ? new OsPath("/downloads/" + path.FullPath.Substring("/data/".Length))
                    : path
            };
            var subject = CreateSubject(
                trackedDownload,
                TrackedDownloadState.Imported,
                out var completedDownloadService,
                out _,
                out _,
                out _,
                downloadClientProvider: new StubDownloadClientProvider { Client = client },
                remotePathMappingService: remotePathMappingService);

            subject.Execute(new RetryFailedImportCommand { DownloadId = "download-1" });

            Assert.That(completedDownloadService.OutputPathsAtImportStart, Is.EqualTo(new[] { "/downloads/book" }));
            Assert.That(completedDownloadService.FilePathsAtImportStart.Single(), Is.Null);
        }

        [Test]
        public void should_leave_cached_local_output_path_when_no_remote_mapping_matches()
        {
            var trackedDownload = CreateTrackedDownload("download-1", TrackedDownloadState.ImportBlocked, DownloadItemStatus.Completed, "/downloads/book");
            var client = CreateDownloadClient();
            var subject = CreateSubject(
                trackedDownload,
                TrackedDownloadState.Imported,
                out var completedDownloadService,
                out _,
                out _,
                out _,
                downloadClientProvider: new StubDownloadClientProvider { Client = client });

            subject.Execute(new RetryFailedImportCommand { DownloadId = "download-1" });

            Assert.That(completedDownloadService.OutputPathsAtImportStart, Is.EqualTo(new[] { "/downloads/book" }));
        }

        [Test]
        public void should_report_unsuccessful_when_retry_does_not_import()
        {
            var trackedDownload = CreateTrackedDownload("download-2", TrackedDownloadState.ImportBlocked, DownloadItemStatus.Completed);
            var subject = CreateSubject(trackedDownload, TrackedDownloadState.ImportBlocked, out _, out _, out var commandResultReporter, out _);

            subject.Execute(new RetryFailedImportCommand { DownloadId = "download-2" });

            Assert.That(commandResultReporter.Results, Is.EqualTo(new[] { CommandResult.Unsuccessful }));
        }

        [Test]
        public void should_retry_multiple_blocked_completed_downloads()
        {
            var firstDownload = CreateTrackedDownload("download-1", TrackedDownloadState.ImportBlocked, DownloadItemStatus.Completed);
            var secondDownload = CreateTrackedDownload("download-2", TrackedDownloadState.ImportBlocked, DownloadItemStatus.Completed);
            var subject = CreateSubject(firstDownload, TrackedDownloadState.Imported, out var completedDownloadService, out var conversionTrackingService, out var commandResultReporter, out var eventAggregator, new[] { secondDownload });

            subject.Execute(new RetryFailedImportCommand { DownloadIds = new List<string> { "download-1", "download-2" } });

            Assert.That(completedDownloadService.ImportedDownloadIds, Is.EqualTo(new[] { "download-1", "download-2" }));
            Assert.That(conversionTrackingService.ClearedDownloadIds, Is.EqualTo(new[] { "download-1", "download-2" }));
            Assert.That(commandResultReporter.Results, Is.Empty);
            Assert.That(eventAggregator.Events.OfType<TrackedDownloadRefreshedEvent>().Count(), Is.EqualTo(4));
        }

        [Test]
        public void should_continue_bulk_retry_when_one_download_is_stale()
        {
            var trackedDownload = CreateTrackedDownload("download-1", TrackedDownloadState.ImportBlocked, DownloadItemStatus.Completed);
            var subject = CreateSubject(trackedDownload, TrackedDownloadState.Imported, out var completedDownloadService, out _, out var commandResultReporter, out _);

            subject.Execute(new RetryFailedImportCommand { DownloadIds = new List<string> { "missing", "download-1" } });

            Assert.That(completedDownloadService.ImportedDownloadIds, Is.EqualTo(new[] { "download-1" }));
            Assert.That(commandResultReporter.Results, Is.EqualTo(new[] { CommandResult.Unsuccessful }));
        }

        [Test]
        public void should_reject_untracked_download()
        {
            var subject = CreateSubject(null, TrackedDownloadState.Imported, out _, out _, out _, out _);

            var ex = Assert.Throws<InvalidOperationException>(() => subject.Execute(new RetryFailedImportCommand { DownloadId = "missing" }));

            Assert.That(ex.Message, Does.Contain("not currently tracked"));
        }

        [Test]
        public void should_reject_download_that_is_not_import_blocked()
        {
            var trackedDownload = CreateTrackedDownload("download-3", TrackedDownloadState.ImportPending, DownloadItemStatus.Completed);
            var subject = CreateSubject(trackedDownload, TrackedDownloadState.Imported, out var completedDownloadService, out _, out _, out _);

            var ex = Assert.Throws<InvalidOperationException>(() => subject.Execute(new RetryFailedImportCommand { DownloadId = "download-3" }));

            Assert.That(ex.Message, Does.Contain("not in a blocked import state"));
            Assert.That(completedDownloadService.ImportedDownloadIds, Is.Empty);
        }

        [Test]
        public void should_reject_download_that_is_not_completed()
        {
            var trackedDownload = CreateTrackedDownload("download-4", TrackedDownloadState.ImportBlocked, DownloadItemStatus.Downloading);
            var subject = CreateSubject(trackedDownload, TrackedDownloadState.Imported, out var completedDownloadService, out _, out _, out _);

            var ex = Assert.Throws<InvalidOperationException>(() => subject.Execute(new RetryFailedImportCommand { DownloadId = "download-4" }));

            Assert.That(ex.Message, Does.Contain("not completed"));
            Assert.That(completedDownloadService.ImportedDownloadIds, Is.Empty);
        }

        private static RetryFailedImportService CreateSubject(
            TrackedDownload trackedDownload,
            TrackedDownloadState stateAfterImport,
            out StubCompletedDownloadService completedDownloadService,
            out RecordingConversionTrackingService conversionTrackingService,
            out RecordingCommandResultReporter commandResultReporter,
            out RecordingEventAggregator eventAggregator,
            TrackedDownload[] additionalTrackedDownloads = null,
            IProvideDownloadClient downloadClientProvider = null,
            IRemotePathMappingService remotePathMappingService = null)
        {
            return CreateSubject(
                trackedDownload,
                stateAfterImport,
                out completedDownloadService,
                out conversionTrackingService,
                out commandResultReporter,
                out eventAggregator,
                out _,
                additionalTrackedDownloads,
                downloadClientProvider,
                remotePathMappingService);
        }

        private static RetryFailedImportService CreateSubject(
            TrackedDownload trackedDownload,
            TrackedDownloadState stateAfterImport,
            out StubCompletedDownloadService completedDownloadService,
            out RecordingConversionTrackingService conversionTrackingService,
            out RecordingCommandResultReporter commandResultReporter,
            out RecordingEventAggregator eventAggregator,
            out RecordingDownloadClientFileSnapshotService snapshotService,
            TrackedDownload[] additionalTrackedDownloads = null,
            IProvideDownloadClient downloadClientProvider = null,
            IRemotePathMappingService remotePathMappingService = null)
        {
            var trackedDownloads = new List<TrackedDownload>();
            if (trackedDownload != null)
            {
                trackedDownloads.Add(trackedDownload);
            }

            if (additionalTrackedDownloads != null)
            {
                trackedDownloads.AddRange(additionalTrackedDownloads.Where(download => download != null));
            }

            var trackedDownloadService = new StubTrackedDownloadService(trackedDownloads.ToArray());

            completedDownloadService = new StubCompletedDownloadService(stateAfterImport);
            conversionTrackingService = new RecordingConversionTrackingService();
            commandResultReporter = new RecordingCommandResultReporter();
            eventAggregator = new RecordingEventAggregator();
            snapshotService = new RecordingDownloadClientFileSnapshotService();

            return new RetryFailedImportService(
                trackedDownloadService,
                downloadClientProvider ?? new StubDownloadClientProvider(),
                remotePathMappingService ?? new StubRemotePathMappingService(),
                completedDownloadService,
                snapshotService,
                conversionTrackingService,
                commandResultReporter,
                eventAggregator,
                LogManager.GetCurrentClassLogger());
        }

        private static TrackedDownload CreateTrackedDownload(string downloadId, TrackedDownloadState state, DownloadItemStatus status, string outputPath = null, List<string> filePaths = null)
        {
            return new TrackedDownload
            {
                DownloadClient = 1,
                DownloadItem = CreateClientItem(downloadId, status, outputPath, filePaths),
                State = state,
                IsTrackable = true
            };
        }

        private static StubDownloadClient CreateDownloadClient(params DownloadClientItem[] items)
        {
            return new StubDownloadClient
            {
                Definition = new DownloadClientDefinition
                {
                    Id = 1,
                    Name = "SABnzbd",
                    Protocol = DownloadProtocol.Usenet
                },
                Items = items
            };
        }

        private static DownloadClientItem CreateClientItem(string downloadId, DownloadItemStatus status, string outputPath = null, List<string> filePaths = null)
        {
            return new DownloadClientItem
            {
                DownloadId = downloadId,
                Title = $"Tracked {downloadId}",
                Status = status,
                OutputPath = outputPath == null ? default : new OsPath(outputPath),
                FilePaths = filePaths,
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Id = 1,
                    Name = "SABnzbd",
                    Protocol = DownloadProtocol.Usenet
                }
            };
        }
    }
}
