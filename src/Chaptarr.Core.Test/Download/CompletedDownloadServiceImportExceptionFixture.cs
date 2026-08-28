using System;
using System.Collections.Generic;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class CompletedDownloadServiceImportExceptionFixture
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

        private sealed class StubProvideImportItemService : IProvideImportItemService
        {
            private readonly OsPath _outputPath;

            public StubProvideImportItemService(string outputPath)
            {
                _outputPath = new OsPath(outputPath);
            }

            public DownloadClientItem ProvideImportItem(DownloadClientItem item, DownloadClientItem previousImportAttempt)
            {
                var clone = item.Clone();
                clone.OutputPath = _outputPath;
                return clone;
            }
        }

        private sealed class ThrowingDownloadedBooksImportService : IDownloadedBooksImportService
        {
            private readonly Exception _exception;

            public ThrowingDownloadedBooksImportService(Exception exception)
            {
                _exception = exception;
            }

            public List<ImportResult> ProcessPath(string path, ImportMode importMode = ImportMode.Auto, NzbDrone.Core.Books.Author author = null, DownloadClientItem downloadClientItem = null, NzbDrone.Core.Parser.Model.RemoteBook remoteBook = null, bool requireDefaultRootFolderForMissingAuthors = false)
                => throw _exception;

            public List<ImportResult> ProcessFolder(string path, ImportMode importMode = ImportMode.Auto, NzbDrone.Core.Books.Author author = null, DownloadClientItem downloadClientItem = null, NzbDrone.Core.Parser.Model.RemoteBook remoteBook = null, bool requireDefaultRootFolderForMissingAuthors = false)
                => throw new NotImplementedException();

            public List<ImportResult> ProcessFile(string path, ImportMode importMode = ImportMode.Auto, NzbDrone.Core.Books.Author author = null, DownloadClientItem downloadClientItem = null, NzbDrone.Core.Parser.Model.RemoteBook remoteBook = null, bool requireDefaultRootFolderForMissingAuthors = false)
                => throw new NotImplementedException();
        }

        private sealed class PendingDownloadedBooksImportService : IDownloadedBooksImportService
        {
            public List<ImportResult> ProcessPath(string path, ImportMode importMode = ImportMode.Auto, NzbDrone.Core.Books.Author author = null, DownloadClientItem downloadClientItem = null, NzbDrone.Core.Parser.Model.RemoteBook remoteBook = null, bool requireDefaultRootFolderForMissingAuthors = false)
            {
                var decision = new ImportDecision<NzbDrone.Core.Parser.Model.LocalBook>(new NzbDrone.Core.Parser.Model.LocalBook
                {
                    Path = path
                });
                return new List<ImportResult> { new(decision, ImportResultType.Pending) };
            }

            public List<ImportResult> ProcessFolder(string path, ImportMode importMode = ImportMode.Auto, NzbDrone.Core.Books.Author author = null, DownloadClientItem downloadClientItem = null, NzbDrone.Core.Parser.Model.RemoteBook remoteBook = null, bool requireDefaultRootFolderForMissingAuthors = false)
                => throw new NotImplementedException();

            public List<ImportResult> ProcessFile(string path, ImportMode importMode = ImportMode.Auto, NzbDrone.Core.Books.Author author = null, DownloadClientItem downloadClientItem = null, NzbDrone.Core.Parser.Model.RemoteBook remoteBook = null, bool requireDefaultRootFolderForMissingAuthors = false)
                => throw new NotImplementedException();
        }

        private sealed class PassthroughDownloadImportModeResolver : IDownloadImportModeResolver
        {
            public ImportMode Resolve(ImportMode requestedMode, DownloadClientItem downloadClientItem) => requestedMode;
            public DownloadImportPolicy ResolvePolicy(ImportMode requestedMode, DownloadClientItem downloadClientItem) => new(requestedMode, false);
            public bool ShouldPreserveDownloadClientItem(DownloadClientItem downloadClientItem) => false;
        }

        private class ThrowingProxy<T> : DispatchProxy where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (typeof(T) == typeof(IHistoryService) && targetMethod?.Name == nameof(IHistoryService.FindByDownloadId))
                {
                    return new List<EntityHistory>();
                }

                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        [Test, Platform(Exclude = "Win", Reason = "Test uses Unix paths")]
        public void should_block_import_and_publish_incomplete_event_when_import_throws()
        {
            var eventAggregator = new RecordingEventAggregator();
            var exception = new InvalidOperationException("boom");

            var downloadItem = new DownloadClientItem
            {
                DownloadId = "download-1",
                Title = "Test.Download",
                Status = DownloadItemStatus.Completed,
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Id = 1,
                    Name = "Deluge",
                    Protocol = DownloadProtocol.Torrent
                }
            };

            var trackedDownload = new TrackedDownload
            {
                DownloadClient = 1,
                DownloadItem = downloadItem,
                State = TrackedDownloadState.ImportPending
            };

            var subject = new CompletedDownloadService(
                eventAggregator,
                DispatchProxy.Create<IHistoryService, ThrowingProxy<IHistoryService>>(),
                new StubProvideImportItemService("/tmp/chaptarr-import-exception"),
                new ThrowingDownloadedBooksImportService(exception),
                new PassthroughDownloadImportModeResolver(),
                DispatchProxy.Create<ITrackedDownloadAlreadyImported, ThrowingProxy<ITrackedDownloadAlreadyImported>>(),
                NoopFailedDownloadService.Instance,
                LogManager.GetCurrentClassLogger(),
                NoopDownloadClientFileSnapshotService.Instance);

            Assert.DoesNotThrow(() => subject.Import(trackedDownload));

            Assert.That(trackedDownload.State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
            Assert.That(trackedDownload.Status, Is.EqualTo(TrackedDownloadStatus.Warning));
            Assert.That(trackedDownload.StatusMessages, Is.Not.Empty);
            Assert.That(trackedDownload.StatusMessages[0].Messages, Does.Contain("IMPORT_EXCEPTION"));
            Assert.That(trackedDownload.StatusMessages[1].Messages, Does.Contain("InvalidOperationException: boom"));
            Assert.That(eventAggregator.Events, Has.Some.InstanceOf<TrackedDownloadUpdatedEvent>());
            Assert.That(eventAggregator.Events, Has.Some.InstanceOf<BookImportIncompleteEvent>());
        }

        [Test, Platform(Exclude = "Win", Reason = "Test uses Unix paths")]
        public void should_leave_import_pending_without_incomplete_event_when_conversion_is_detached()
        {
            var eventAggregator = new RecordingEventAggregator();
            var downloadItem = new DownloadClientItem
            {
                DownloadId = "download-pending-conversion",
                Title = "Test.Pending.Conversion",
                Status = DownloadItemStatus.Completed,
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Id = 1,
                    Name = "SABnzbd",
                    Protocol = DownloadProtocol.Usenet
                }
            };

            var trackedDownload = new TrackedDownload
            {
                DownloadClient = 1,
                DownloadItem = downloadItem,
                State = TrackedDownloadState.ImportPending
            };

            var subject = new CompletedDownloadService(
                eventAggregator,
                DispatchProxy.Create<IHistoryService, ThrowingProxy<IHistoryService>>(),
                new StubProvideImportItemService("/tmp/chaptarr-pending-conversion"),
                new PendingDownloadedBooksImportService(),
                new PassthroughDownloadImportModeResolver(),
                DispatchProxy.Create<ITrackedDownloadAlreadyImported, ThrowingProxy<ITrackedDownloadAlreadyImported>>(),
                NoopFailedDownloadService.Instance,
                LogManager.GetCurrentClassLogger(),
                NoopDownloadClientFileSnapshotService.Instance);

            subject.Import(trackedDownload);

            Assert.That(trackedDownload.State, Is.EqualTo(TrackedDownloadState.ImportPending));
            Assert.That(trackedDownload.StatusMessages, Is.Empty);
            Assert.That(eventAggregator.Events, Has.Some.InstanceOf<TrackedDownloadUpdatedEvent>());
            Assert.That(eventAggregator.Events, Has.None.InstanceOf<BookImportIncompleteEvent>());
        }
    }
}
