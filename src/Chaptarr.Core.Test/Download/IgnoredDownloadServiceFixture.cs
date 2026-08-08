using System.Collections.Generic;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class IgnoredDownloadServiceFixture
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

        [Test]
        public void should_allow_ignoring_unknown_downloads_by_download_id()
        {
            var eventAggregator = new RecordingEventAggregator();
            var service = new IgnoredDownloadService(eventAggregator, LogManager.GetCurrentClassLogger());

            var trackedDownload = new TrackedDownload
            {
                DownloadItem = new DownloadClientItem
                {
                    DownloadId = "download-1",
                    Title = "Some.Unknown.Release",
                    DownloadClientInfo = new DownloadClientItemClientInfo
                    {
                        Id = 1,
                        Name = "TestClient",
                        Type = "TestClient",
                        Protocol = DownloadProtocol.Torrent
                    }
                },
                RemoteBook = null
            };

            var result = service.IgnoreDownload(trackedDownload);

            Assert.That(result, Is.True);
            Assert.That(eventAggregator.Events, Has.Exactly(1).InstanceOf<DownloadIgnoredEvent>());

            var ignoredEvent = (DownloadIgnoredEvent)eventAggregator.Events[0];
            Assert.That(ignoredEvent.DownloadId, Is.EqualTo("download-1"));
            Assert.That(ignoredEvent.AuthorId, Is.EqualTo(0));
            Assert.That(ignoredEvent.BookIds, Is.Not.Null);
            Assert.That(ignoredEvent.BookIds, Is.Empty);
            Assert.That(ignoredEvent.Message, Is.EqualTo("Manually ignored (unknown download)"));
        }
    }
}

