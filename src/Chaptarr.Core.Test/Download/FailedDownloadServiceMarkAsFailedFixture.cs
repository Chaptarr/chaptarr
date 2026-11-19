using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Messaging.Events;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class FailedDownloadServiceMarkAsFailedFixture
    {
        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void manual_failure_keeps_client_data_and_honours_search_choice(bool fromHistory, bool skipSearch)
        {
            var grabbed = new List<EntityHistory> { Grab(901), Grab(902) };
            var events = new List<DownloadFailedEvent>();
            var tracked = new TrackedDownload();
            var subject = Subject(grabbed, events, tracked);
            if (fromHistory) subject.MarkAsFailed(1, skipSearch);
            else subject.MarkAsFailed("download-id", skipSearch);
            var failed = events.Single();
            Assert.That(failed.DownloadId, Is.EqualTo("download-id"));
            Assert.That(failed.AuthorId, Is.EqualTo(7));
            Assert.That(failed.BookIds, Is.EquivalentTo(new[] { 901, 902 }));
            Assert.That(failed.SourceTitle, Is.EqualTo("Dune"));
            Assert.That(failed.SkipRedownload, Is.EqualTo(skipSearch));
            Assert.That(failed.SkipDownloadClientRemoval, Is.True);
            Assert.That(failed.TrackedDownload, Is.SameAs(fromHistory ? null : tracked));
        }

        [Test]
        public void history_without_download_id_still_keeps_data()
        {
            var grabbed = new List<EntityHistory> { Grab(901) };
            grabbed[0].DownloadId = null;
            var events = new List<DownloadFailedEvent>();
            Subject(grabbed, events, null).MarkAsFailed(1);
            Assert.That(events.Single().SkipDownloadClientRemoval, Is.True);
            Assert.That(events.Single().BookIds, Is.EqualTo(new[] { 901 }));
        }

        [Test]
        public void unknown_download_does_not_publish_a_failure()
        {
            var events = new List<DownloadFailedEvent>();
            Subject(new(), events, null).MarkAsFailed("not-grabbed");
            Assert.That(events, Is.Empty);
        }

        private static EntityHistory Grab(int book) => new() {
            AuthorId = 7, BookId = book, DownloadId = "download-id", SourceTitle = "Dune",
            EventType = EntityHistoryEventType.Grabbed, Data = new Dictionary<string, string>()
        };

        private static FailedDownloadService Subject(List<EntityHistory> grabs, List<DownloadFailedEvent> events, TrackedDownload tracked) => new(
            Proxy<IHistoryService>((method, _) => method.Name switch {
                nameof(IHistoryService.Get) => grabs[0],
                nameof(IHistoryService.Find) => grabs,
                _ => throw new NotSupportedException(method.Name)
            }),
            Proxy<ITrackedDownloadService>((method, _) => method.Name == nameof(ITrackedDownloadService.Find)
                ? tracked : throw new NotSupportedException(method.Name)),
            Proxy<IEventAggregator>((_, args) => { events.Add((DownloadFailedEvent)args[0]); return null; }));
    }
}
