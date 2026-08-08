using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class DownloadProcessingServiceFixture
    {
        private class ConfigProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == "get_EnableCompletedDownloadHandling")
                {
                    return true;
                }

                throw new NotImplementedException($"Unexpected config call: {targetMethod?.Name}");
            }
        }

        private sealed class RecordingCompletedDownloadService : ICompletedDownloadService
        {
            private readonly ManualResetEventSlim _conversionFinished;

            public RecordingCompletedDownloadService(ManualResetEventSlim conversionFinished)
            {
                _conversionFinished = conversionFinished;
            }

            public List<string> ImportedDownloadIds { get; } = new();
            public bool NativeProcessedWhileConversionActive { get; private set; }

            public void Check(TrackedDownload trackedDownload)
            {
            }

            public void Import(TrackedDownload trackedDownload)
            {
                ImportedDownloadIds.Add(trackedDownload.DownloadItem.DownloadId);
                if (trackedDownload.DownloadItem.DownloadId == "native-ready")
                {
                    NativeProcessedWhileConversionActive = !_conversionFinished.IsSet;
                }

                // The conversion-backed item represents ImportApprovedBooks returning Pending
                // immediately after enqueueing its durable job. The detached converter remains
                // active behind this gate while the serialized sweep advances to the native M4B.
            }

            public bool VerifyImport(TrackedDownload trackedDownload, List<NzbDrone.Core.MediaFiles.BookImport.ImportResult> importResults)
            {
                return true;
            }
        }

        private sealed class NoOpFailedDownloadService : IFailedDownloadService
        {
            public void MarkAsFailed(int historyId, bool skipRedownload = false)
            {
            }

            public void MarkAsFailed(string downloadId, bool skipRedownload = false)
            {
            }

            public void MarkAsFailed(TrackedDownload trackedDownload, string reason, bool skipRedownload = false)
            {
            }

            public void Check(TrackedDownload trackedDownload)
            {
            }

            public void ProcessFailed(TrackedDownload trackedDownload)
            {
            }
        }

        private sealed class StaticTrackedDownloadService : ITrackedDownloadService
        {
            public List<TrackedDownload> Downloads { get; init; } = new();

            public TrackedDownload Find(string downloadId) => Downloads.Find(item => item.DownloadItem.DownloadId == downloadId);
            public void StopTracking(string downloadId) { }
            public void StopTracking(List<string> downloadIds) { }
            public TrackedDownload TrackDownload(DownloadClientDefinition downloadClient, DownloadClientItem downloadItem) => throw new NotImplementedException();
            public List<TrackedDownload> GetTrackedDownloads() => Downloads;
            public void UpdateTrackable(List<TrackedDownload> trackedDownloads) { }
        }

        private sealed class NoOpEventAggregator : IEventAggregator
        {
            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
            }
        }

        [Test]
        public void native_import_should_proceed_while_earlier_conversion_job_is_still_running()
        {
            using var conversionFinished = new ManualResetEventSlim(false);
            var completed = new RecordingCompletedDownloadService(conversionFinished);
            var tracked = new StaticTrackedDownloadService
            {
                Downloads = new List<TrackedDownload>
                {
                    CreatePending("conversion-pending"),
                    CreatePending("native-ready")
                }
            };
            var config = DispatchProxy.Create<IConfigService, ConfigProxy>();
            var service = new DownloadProcessingService(
                config,
                completed,
                new NoOpFailedDownloadService(),
                tracked,
                new NoOpEventAggregator(),
                LogManager.GetCurrentClassLogger());

            service.Execute(new ProcessMonitoredDownloadsCommand());

            Assert.That(completed.ImportedDownloadIds, Is.EqualTo(new[] { "conversion-pending", "native-ready" }));
            Assert.That(completed.NativeProcessedWhileConversionActive, Is.True);
        }

        private static TrackedDownload CreatePending(string downloadId)
        {
            return new TrackedDownload
            {
                DownloadItem = new DownloadClientItem { DownloadId = downloadId, Title = downloadId },
                State = TrackedDownloadState.ImportPending,
                IsTrackable = true
            };
        }
    }
}
