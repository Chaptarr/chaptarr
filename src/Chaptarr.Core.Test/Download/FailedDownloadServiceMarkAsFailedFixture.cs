using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Download
{
    /// <summary>
    /// The overload completed-download handling uses to fail a download for a reason Chaptarr
    /// determined itself. What matters is that it emits the same <see cref="DownloadFailedEvent"/>
    /// every other failure path emits — that event is what drives blocklisting (BlocklistService),
    /// the replacement search (RedownloadFailedDownloadService) and client cleanup (DownloadEventHub).
    /// </summary>
    [TestFixture]
    public class FailedDownloadServiceMarkAsFailedFixture
    {
        private RecordingEventAggregator _eventAggregator;
        private HistoryServiceProxy _historyProxy;
        private FailedDownloadService _subject;

        [SetUp]
        public void SetUp()
        {
            _eventAggregator = new RecordingEventAggregator();

            var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
            _historyProxy = (HistoryServiceProxy)(object)historyService;

            _subject = new FailedDownloadService(
                historyService,
                DispatchProxy.Create<ITrackedDownloadService, ThrowingTrackedDownloadService>(),
                _eventAggregator);
        }

        [Test]
        public void should_publish_download_failed_event_with_the_grabbed_books_and_reason()
        {
            _historyProxy.GrabbedItems = new List<EntityHistory>
            {
                BuildGrab(bookId: 901),
                BuildGrab(bookId: 902)
            };

            var trackedDownload = BuildTrackedDownload();

            _subject.MarkAsFailed(trackedDownload, "No file in this download is in a format allowed by the quality profile (MOBI)");

            var failed = _eventAggregator.Events.OfType<DownloadFailedEvent>().SingleOrDefault();

            Assert.That(failed, Is.Not.Null, "blocklisting and the replacement search both hang off this event");
            Assert.That(failed.DownloadId, Is.EqualTo("learn-my-lesson"));
            Assert.That(failed.BookIds, Is.EquivalentTo(new[] { 901, 902 }));
            Assert.That(failed.AuthorId, Is.EqualTo(7));
            Assert.That(failed.SourceTitle, Is.EqualTo("Learn My Lesson [mobi]"));
            Assert.That(failed.Message, Does.Contain("MOBI"));
            Assert.That(failed.SkipRedownload, Is.False, "a replacement search should be allowed to run");
            Assert.That(trackedDownload.State, Is.EqualTo(TrackedDownloadState.DownloadFailed));
        }

        [Test]
        public void should_honour_skip_redownload()
        {
            _historyProxy.GrabbedItems = new List<EntityHistory> { BuildGrab(bookId: 901) };

            _subject.MarkAsFailed(BuildTrackedDownload(), "reason", skipRedownload: true);

            var failed = _eventAggregator.Events.OfType<DownloadFailedEvent>().Single();

            Assert.That(failed.SkipRedownload, Is.True);
        }

        [Test]
        public void should_not_fail_a_download_chaptarr_never_grabbed()
        {
            // No grab record means no release to blocklist and no target to search again.
            _historyProxy.GrabbedItems = new List<EntityHistory>();

            var trackedDownload = BuildTrackedDownload();

            _subject.MarkAsFailed(trackedDownload, "reason");

            Assert.That(_eventAggregator.Events.OfType<DownloadFailedEvent>(), Is.Empty);
            Assert.That(trackedDownload.State, Is.Not.EqualTo(TrackedDownloadState.DownloadFailed));
            Assert.That(trackedDownload.StatusMessages, Is.Not.Empty);
        }

        private static EntityHistory BuildGrab(int bookId)
        {
            return new EntityHistory
            {
                EventType = EntityHistoryEventType.Grabbed,
                DownloadId = "learn-my-lesson",
                AuthorId = 7,
                BookId = bookId,
                SourceTitle = "Learn My Lesson [mobi]",
                Date = DateTime.UtcNow.AddMinutes(-10),
                Quality = new QualityModel(Quality.MOBI),
                Data = new Dictionary<string, string>()
            };
        }

        private static TrackedDownload BuildTrackedDownload()
        {
            return new TrackedDownload
            {
                DownloadClient = 1,
                State = TrackedDownloadState.ImportPending,
                DownloadItem = new DownloadClientItem
                {
                    DownloadId = "learn-my-lesson",
                    Title = "Learn My Lesson [mobi]"
                }
            };
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

        private class HistoryServiceProxy : DispatchProxy
        {
            public List<EntityHistory> GrabbedItems { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IHistoryService.Find))
                {
                    return GrabbedItems;
                }

                throw new NotImplementedException($"Test proxy does not implement IHistoryService.{targetMethod?.Name}");
            }
        }

        private class ThrowingTrackedDownloadService : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"MarkAsFailed(TrackedDownload, ...) must not need ITrackedDownloadService.{targetMethod?.Name}");
            }
        }
    }
}
