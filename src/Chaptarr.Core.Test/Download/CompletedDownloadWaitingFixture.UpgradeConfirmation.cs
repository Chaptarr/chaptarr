using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Download
{
    public partial class CompletedDownloadWaitingFixture
    {
        [TestCase(false, BookMediaType.Audiobook)]
        [TestCase(true, BookMediaType.Audiobook)]
        [TestCase(false, BookMediaType.Ebook)]
        [TestCase(true, BookMediaType.Ebook)]
        public void upgrade_confirmation_wait_survives_restart_and_never_completes_or_removes(bool postgres, BookMediaType mediaType)
        {
            Run(postgres, 1, 0, (subject, tracked, counters) =>
            {
                var path = tracked.DownloadItem.FilePaths.Single();
                File.WriteAllText(path, "downloaded recording");
                var completed = 0;
                counters.OnEvent = message => { if (message is DownloadCompletedEvent) completed++; };
                counters.ImportResults = new List<ImportResult>
                {
                    new(new ImportDecision<LocalBook>(new LocalBook { Path = path,
                        Quality = new QualityModel(mediaType == BookMediaType.Audiobook ? Quality.MP3 : Quality.EPUB) }),
                        ImportResultType.WaitingForConfirmation, "Waiting for you to confirm replacing 1 file(s).")
                };
                subject.Import(tracked);
                Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
                Assert.That(tracked.AwaitingImportConfirmation, Is.True);
                Assert.That(subject.CanRemoveDownload(tracked), Is.False);
                Assert.That(completed, Is.Zero);
                Assert.That(counters.FailedDownloads, Is.Zero);
                var history = counters.DownloadHistory.GetLatestDownloadHistoryItem("waiting");
                Assert.That(history.EventType, Is.EqualTo(DownloadHistoryEventType.DownloadImportIncomplete));
                Assert.That(history.Data["awaitingImportConfirmation"], Is.EqualTo("True"));
                var tracker = counters.NewTracker();
                var restored = tracker.TrackDownload(new DownloadClientDefinition { Id = 1 }, tracked.DownloadItem.Clone());
                Assert.That(restored.AwaitingImportConfirmation, Is.True);
                counters.Queue.Handle(new TrackedDownloadRefreshedEvent(new List<TrackedDownload> { restored }));
                var row = counters.Queue.GetQueue(false).Single();
                Assert.That(row.AwaitingImportConfirmation, Is.True);
                Assert.That(row.CanRetryImport, Is.False);
                Assert.That(row.ErrorMessage, Does.Contain("Waiting for you"));
                Assert.That(subject.CanRemoveDownload(restored), Is.False);
                Assert.That(File.ReadAllText(path), Is.EqualTo("downloaded recording"));
            }, mediaType: mediaType);
        }
    }
}
