using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Chaptarr.Http;
using Chaptarr.Api.V1.Ignored;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Messaging.Commands;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class IgnoredControllerFixture
    {
        [Test]
        public void delete_should_evict_tracked_download_case_insensitively_and_refresh_queue()
        {
            var historyService = new StubDownloadHistoryService(new List<string> { "ABCDEF" });
            var trackedDownloadService = new StubTrackedDownloadService(new TrackedDownload
            {
                DownloadItem = new DownloadClientItem
                {
                    DownloadId = "abcdef",
                    Title = "Ignored Book"
                },
                IsTrackable = true
            });
            var commandQueue = new StubCommandQueue();
            var sut = new IgnoredController(historyService, trackedDownloadService, commandQueue);

            sut.DeleteIgnored(42);

            Assert.Multiple(() =>
            {
                Assert.That(historyService.RemovedIds, Is.EqualTo(new[] { 42 }));
                Assert.That(trackedDownloadService.StoppedDownloadIds, Is.EqualTo(new[] { "abcdef" }));
                Assert.That(trackedDownloadService.StopTrackingCalls, Is.EqualTo(1));
                Assert.That(commandQueue.RefreshCount, Is.EqualTo(1));
            });
        }

        [Test]
        public void delete_should_not_stop_tracking_when_download_is_not_cached()
        {
            var historyService = new StubDownloadHistoryService(new List<string> { "ABCDEF" });
            var trackedDownloadService = new StubTrackedDownloadService();
            var commandQueue = new StubCommandQueue();
            var sut = new IgnoredController(historyService, trackedDownloadService, commandQueue);

            sut.DeleteIgnored(42);

            Assert.Multiple(() =>
            {
                Assert.That(trackedDownloadService.StoppedDownloadIds, Is.Empty);
                Assert.That(trackedDownloadService.StopTrackingCalls, Is.EqualTo(0));
                Assert.That(commandQueue.RefreshCount, Is.EqualTo(1));
            });
        }

        [Test]
        public void get_should_only_report_trackable_cached_downloads_as_in_client()
        {
            var historyService = new StubDownloadHistoryService(
                new List<string>(),
                new List<DownloadHistory>
                {
                    IgnoredHistory(1, "TRACKED"),
                    IgnoredHistory(2, "UNTRACKABLE")
                });

            var trackedDownloadService = new StubTrackedDownloadService(
                new TrackedDownload
                {
                    DownloadItem = new DownloadClientItem
                    {
                        DownloadId = "tracked",
                        Title = "Tracked Book"
                    },
                    IsTrackable = true
                },
                new TrackedDownload
                {
                    DownloadItem = new DownloadClientItem
                    {
                        DownloadId = "untrackable",
                        Title = "Deleted Book"
                    },
                    IsTrackable = false
                });

            var sut = new IgnoredController(historyService, trackedDownloadService, new StubCommandQueue());

            var resource = sut.GetIgnored(new PagingRequestResource
            {
                Page = 1,
                PageSize = 20
            });

            Assert.Multiple(() =>
            {
                Assert.That(resource.Records.Single(r => r.DownloadId == "TRACKED").IsInClient, Is.True);
                Assert.That(resource.Records.Single(r => r.DownloadId == "UNTRACKABLE").IsInClient, Is.False);
            });
        }

        private static DownloadHistory IgnoredHistory(int id, string downloadId)
        {
            return new DownloadHistory
            {
                Id = id,
                EventType = DownloadHistoryEventType.DownloadIgnored,
                DownloadId = downloadId,
                SourceTitle = downloadId,
                Date = DateTime.UtcNow
            };
        }

        private sealed class StubDownloadHistoryService : IDownloadHistoryService
        {
            private readonly List<string> _downloadIds;
            private readonly List<DownloadHistory> _currentlyIgnored;

            public StubDownloadHistoryService(List<string> downloadIds, List<DownloadHistory> currentlyIgnored = null)
            {
                _downloadIds = downloadIds;
                _currentlyIgnored = currentlyIgnored ?? new List<DownloadHistory>();
                RemovedIds = new List<int>();
            }

            public List<int> RemovedIds { get; }

            public bool DownloadAlreadyImported(string downloadId) => throw new NotImplementedException();
            public DownloadHistory GetLatestDownloadHistoryItem(string downloadId) => throw new NotImplementedException();
            public DownloadHistory GetLatestGrab(string downloadId) => throw new NotImplementedException();

            public PagingSpec<DownloadHistory> CurrentlyIgnored(PagingSpec<DownloadHistory> pagingSpec)
            {
                pagingSpec.TotalRecords = _currentlyIgnored.Count;
                pagingSpec.Records = _currentlyIgnored;
                return pagingSpec;
            }

            public List<string> RemoveIgnored(int id)
            {
                RemovedIds.Add(id);
                return _downloadIds;
            }

            public List<string> RemoveIgnored(List<int> ids)
            {
                RemovedIds.AddRange(ids);
                return _downloadIds;
            }
        }

        private sealed class StubTrackedDownloadService : ITrackedDownloadService
        {
            private readonly List<TrackedDownload> _trackedDownloads;

            public StubTrackedDownloadService(params TrackedDownload[] trackedDownloads)
            {
                _trackedDownloads = new List<TrackedDownload>(trackedDownloads);
                StoppedDownloadIds = new List<string>();
            }

            public List<string> StoppedDownloadIds { get; }
            public int StopTrackingCalls { get; private set; }

            public TrackedDownload Find(string downloadId) => _trackedDownloads.Find(t => t.DownloadItem.DownloadId == downloadId);
            public void StopTracking(string downloadId)
            {
                StopTrackingCalls++;
                StoppedDownloadIds.Add(downloadId);
            }

            public void StopTracking(List<string> downloadIds)
            {
                StopTrackingCalls++;
                StoppedDownloadIds.AddRange(downloadIds);
            }

            public TrackedDownload TrackDownload(DownloadClientDefinition downloadClient, DownloadClientItem downloadItem) => throw new NotImplementedException();
            public List<TrackedDownload> GetTrackedDownloads() => _trackedDownloads;
            public void UpdateTrackable(List<TrackedDownload> trackedDownloads) => throw new NotImplementedException();
        }

        private sealed class StubCommandQueue : IManageCommandQueue
        {
            public int RefreshCount { get; private set; }

            public List<CommandModel> PushMany<TCommand>(List<TCommand> commands)
                where TCommand : Command => throw new NotImplementedException();

            public CommandModel Push<TCommand>(TCommand command, CommandPriority priority = CommandPriority.Normal, CommandTrigger trigger = CommandTrigger.Unspecified)
                where TCommand : Command
            {
                if (command is RefreshMonitoredDownloadsCommand)
                {
                    RefreshCount++;
                }

                return null;
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
    }
}
