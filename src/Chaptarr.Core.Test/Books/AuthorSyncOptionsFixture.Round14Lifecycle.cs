using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Books
{
    public partial class AuthorSyncOptionsFixture
    {
        [Test]
        public void failed_monitoring_checkpoint_preserves_the_unacknowledged_choice_for_retry(
            [Values(false, true)] bool postgres, [Values("audiobook", "ebook", "generic")] string phase)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.Import(h.Request("both", false));
                var author = h.Author;
                if (phase == "ebook") author.AddOptions.AudiobookMonitor = null;
                if (phase == "generic")
                {
                    author.AddOptions.AudiobookMonitor = null;
                    author.AddOptions.EbookMonitor = null;
                    author.AddOptions.Monitor = MonitorTypes.All;
                }
                h.Authors.SetAddOptions(author);
                var writes = 0;
                var authorService = Proxy<IAuthorService>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(IAuthorService.SetAddOptions)));
                    if (++writes == 1) throw new InvalidOperationException("Checkpoint write unavailable");
                    h.Authors.SetAddOptions((Author)args[0]);
                    return null;
                });
                new AuthorScannedHandler(new BookMonitoredService(h.Authors, h.Books, h.Logger),
                    authorService, h.Queue, Proxy<IBookAddedService>((_, _) => null), h.Events, h.Logger, h.Books)
                    .Handle(new AuthorScannedEvent(author));

                var stored = h.AuthorRows.Get(author.Id).AddOptions;
                var pending = phase == "audiobook" ? stored.AudiobookMonitor : phase == "ebook" ? stored.EbookMonitor : stored.Monitor;
                Assert.That(writes, Is.EqualTo(2), "The failed checkpoint is followed by one durable failure-state write");
                Assert.That(pending, Is.EqualTo(MonitorTypes.All), "The failure-state write must retain the unacknowledged choice");
                Assert.That(stored.InitialFormatSyncFailureCount, Is.EqualTo(1));
                Assert.That(stored.InitialFormatSyncNotBefore, Is.GreaterThan(DateTime.UtcNow));
                Assert.That(h.BookRows.GetBooksByAuthorId(author.Id).Any(book => book.IsMonitored()), Is.True,
                    "The monitoring operation succeeded before its consumption checkpoint failed");
            });
        }

        [Test]
        public void first_scan_monitoring_failure_is_persisted_and_selected_by_the_minute_retry(
            [Values(false, true)] bool postgres, [Values(false, true)] bool skipped)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.Import(h.Request("both", false));
                var subject = new AuthorScannedHandler(Proxy<IBookMonitoredService>((_, _) =>
                    throw new InvalidOperationException("First monitoring write unavailable")),
                    h.Authors, h.Queue, Proxy<IBookAddedService>((_, _) => null), h.Events, h.Logger, h.Books);
                if (skipped) subject.Handle(new AuthorScanSkippedEvent(h.Author, AuthorScanSkippedReason.NoFolderEvidence));
                else subject.Handle(new AuthorScannedEvent(h.Author));

                var saved = h.AuthorRows.Get(h.Author.Id);
                Assert.That(saved.AddOptions.InitialFormatSyncFailureCount, Is.EqualTo(1));
                Assert.That(saved.AddOptions.InitialFormatSyncNotBefore, Is.GreaterThan(DateTime.UtcNow));
                Assert.That(saved.AddOptions.AudiobookMonitor, Is.EqualTo(MonitorTypes.All));
                Assert.That(h.Commands.OfType<BulkSyncFormatMonitoringCommand>(), Is.Empty);
                saved.AddOptions.InitialFormatSyncNotBefore = DateTime.UtcNow.AddSeconds(-1);
                h.Authors.SetAddOptions(saved);
                FormatRetryTick(h, database);

                var retry = h.Commands.OfType<BulkSyncFormatMonitoringCommand>().Single();
                Assert.That(retry.AuthorIds, Is.EqualTo(new[] { saved.Id }));
                new AuthorScannedHandler(new BookMonitoredService(h.Authors, h.Books, h.Logger),
                    h.Authors, h.Queue, Proxy<IBookAddedService>((_, _) => null), h.Events, h.Logger, h.Books).Execute(retry);
                Assert.That(h.AuthorRows.Get(saved.Id).AddOptions, Is.Null);
                Assert.That(h.BookRows.GetBooksByAuthorId(saved.Id).All(book => book.IsMonitored()), Is.True);
            });
        }

        [Test]
        public void second_media_monitoring_failure_does_not_reapply_the_checkpointed_first_media(
            [Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var request = h.Request("both", false);
                request.SearchForMissingBooks = true;
                h.Import(request);
                var realMonitoring = new BookMonitoredService(h.Authors, h.Books, h.Logger);
                var subject = new AuthorScannedHandler(Proxy<IBookMonitoredService>((_, args) =>
                {
                    var options = (MonitoringOptions)args[1];
                    if (options.MediaType == BookMediaType.Ebook) throw new InvalidOperationException("Second media unavailable");
                    realMonitoring.SetBookMonitoredStatus((Author)args[0], options, initialSetup: true);
                    return null;
                }), h.Authors, h.Queue, Proxy<IBookAddedService>((_, _) => null), h.Events, h.Logger, h.Books);
                subject.Handle(new AuthorScannedEvent(h.Author));

                var saved = h.AuthorRows.Get(h.Author.Id);
                Assert.That(saved.AddOptions.AudiobookMonitor, Is.Null);
                Assert.That(saved.AddOptions.EbookMonitor, Is.EqualTo(MonitorTypes.All));
                Assert.That(saved.AddOptions.InitialFormatSyncFailureCount, Is.EqualTo(1));
                Assert.That(h.SearchTargets, Is.Empty);
                var audio = h.BookRows.GetBooksByAuthorId(saved.Id).Single(book => book.MediaType == BookMediaType.Audiobook);
                audio.SetMonitored(false);
                h.BookRows.SetMonitoring(new[] { audio });
                saved.AddOptions.InitialFormatSyncNotBefore = DateTime.UtcNow.AddSeconds(-1);
                h.Authors.SetAddOptions(saved);
                FormatRetryTick(h, database);
                var retry = h.Commands.OfType<BulkSyncFormatMonitoringCommand>().Single();
                new AuthorScannedHandler(realMonitoring, h.Authors, h.Queue,
                    Proxy<IBookAddedService>((_, _) => null), h.Events, h.Logger, h.Books).Execute(retry);

                var ebook = h.BookRows.GetBooksByAuthorId(saved.Id).Single(book => book.MediaType == BookMediaType.Ebook);
                Assert.That(h.BookRows.Get(audio.Id).IsMonitored(), Is.False,
                    "A user's per-book edit after a durable first-media checkpoint must survive the second-media retry");
                Assert.That(ebook.IsMonitored(), Is.True);
                Assert.That(h.SearchTargets.Single(), Is.EqualTo(new[] { ebook.Id }));
                Assert.That(h.AuthorRows.Get(saved.Id).AddOptions, Is.Null);
            });
        }

        [Test]
        public void setup_retry_selection_is_ordered_bounded_and_leaves_the_remainder_for_the_next_tick(
            [Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.Import(h.Request());
                var now = DateTime.UtcNow;
                var seed = h.Author;
                var authors = new List<Author>();
                for (var index = 0; index < 102; index++)
                {
                    var author = seed.ShallowClone();
                    author.Id = 0;
                    author.HardcoverAuthorId = "hc:" + (800000 + index);
                    author.Name = "Bounded setup " + index;
                    author.TitleSlug = "bounded-setup-" + index;
                    author.AddOptions = new AddAuthorOptions { InitialFormatSyncNotBefore = now.AddMinutes(-index - 1) };
                    authors.Add(author);
                }
                h.AuthorRows.InsertMany(authors);
                var expected = authors.OrderBy(author => author.AddOptions.InitialFormatSyncNotBefore).ThenBy(author => author.Id).ToList();
                var first = h.AuthorRows.GetDueInitialFormatSync(now);
                Assert.That(first, Is.EqualTo(expected.Take(100).Select(author => author.Id)));
                foreach (var author in expected.Take(100)) author.AddOptions = null;
                h.AuthorRows.UpdateMany(expected.Take(100).ToList());
                Assert.That(h.AuthorRows.GetDueInitialFormatSync(now), Is.EqualTo(expected.Skip(100).Select(author => author.Id)));
            });
        }
    }
}
