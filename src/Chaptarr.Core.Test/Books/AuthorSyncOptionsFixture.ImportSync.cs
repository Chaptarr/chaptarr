using System;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.MetadataSource.BookInfo;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Books
{
    public partial class AuthorSyncOptionsFixture
    {
        private static void PrepareIndependentBookMonitoring(Harness h, bool sync = false)
        {
            var request = h.Request(sync: false);
            request.EbookMonitorExistingMode = MonitorTypes.None;
            h.Import(request);
            h.Scan();
            var author = h.Author;
            author.SyncMonitoredAcrossFormats = sync;
            h.Authors.UpdateAuthor(author);
            h.Commands.Clear();
            h.MetadataFetches = 0;
            Assert.That(h.Author.AddOptions, Is.Null);
            Assert.That(h.BookRows.GetBooksByAuthorId(author.Id).Single(book => book.MediaType == BookMediaType.Audiobook).IsMonitored(), Is.True);
            Assert.That(h.BookRows.GetBooksByAuthorId(author.Id).Single(book => book.MediaType == BookMediaType.Ebook).IsMonitored(), Is.False);
        }

        [Test]
        public void import_sync_explicit_enable_uses_existing_command_for_settled_author(
            [Values(false, true)] bool postgres,
            [Values("audiobook", "ebook", "both")] string media,
            [Values(false, true)] bool alreadyEnabled)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                PrepareIndependentBookMonitoring(h, alreadyEnabled);
                var request = MinimalImport(media);
                request.SyncMonitoredAcrossFormats = true;
                h.Import(request);
                Assert.Multiple(() =>
                {
                    Assert.That(h.Author.SyncMonitoredAcrossFormats, Is.True);
                    Assert.That(h.Author.AddOptions, Is.Null);
                    Assert.That(h.MetadataFetches, Is.Zero);
                    Assert.That(h.SearchTargets, Is.Empty);
                    Assert.That(h.Commands, Has.Count.EqualTo(1));
                    Assert.That(h.Commands.Single(), Is.TypeOf<BulkSyncFormatMonitoringCommand>());
                });
                var command = (BulkSyncFormatMonitoringCommand)h.Commands.Single();
                Assert.That(command.AuthorIds, Is.EqualTo(new[] { h.Author.Id }));
                new AuthorScannedHandler(null, h.Authors, h.Queue, null, h.Events, h.Logger, h.Books).Execute(command);
                Assert.That(h.BookRows.GetBooksByAuthorId(h.Author.Id).Select(book => book.IsMonitored()), Is.EqualTo(new[] { true, true }));
            });
        }

        [Test]
        public void import_sync_omitted_or_disabled_does_not_reconcile_existing_books(
            [Values(false, true)] bool postgres,
            [Values(false, true)] bool alreadyEnabled,
            [Values(null, false)] bool? requestedSync)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                PrepareIndependentBookMonitoring(h, alreadyEnabled);
                var request = MinimalImport();
                request.SyncMonitoredAcrossFormats = requestedSync;
                h.Import(request);
                Assert.Multiple(() =>
                {
                    Assert.That(h.Author.SyncMonitoredAcrossFormats, Is.EqualTo(requestedSync ?? alreadyEnabled));
                    Assert.That(h.Commands, Is.Empty);
                    Assert.That(h.MetadataFetches, Is.Zero);
                    Assert.That(h.BookRows.GetBooksByAuthorId(h.Author.Id).Single(book => book.MediaType == BookMediaType.Ebook).IsMonitored(), Is.False);
                });
            });
        }

        [Test]
        public void search_only_for_settled_author_must_preserve_independent_book_monitoring(
            [Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                PrepareIndependentBookMonitoring(h);
                var before = h.BookRows.GetBooksByAuthorId(h.Author.Id)
                    .ToDictionary(book => book.Id, book => book.IsMonitored());
                var request = MinimalImport();
                request.SyncMonitoredAcrossFormats = false;
                request.SearchForMissingBooks = true;
                h.Import(request);
                Assert.That(h.Author.AddOptions.Monitor, Is.Null);
                h.Scan();
                Assert.That(h.BookRows.GetBooksByAuthorId(h.Author.Id)
                    .ToDictionary(book => book.Id, book => book.IsMonitored()), Is.EqualTo(before));
                Assert.That(h.SearchTargets.Single(), Is.EquivalentTo(before.Where(pair => pair.Value).Select(pair => pair.Key)));
            });
        }

        [Test]
        public void import_sync_initial_setup_reconciles_only_after_scan_before_search(
            [Values(false, true)] bool postgres,
            [Values("new", "missing-format", "search")] string setup)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                if (setup == "missing-format")
                {
                    h.Import(h.Request("audiobook", false));
                    h.Scan();
                    h.Commands.Clear();
                }
                else if (setup == "search")
                {
                    PrepareIndependentBookMonitoring(h);
                }

                var request = setup == "search" ? MinimalImport() : h.Request(setup == "missing-format" ? "ebook" : "both");
                request.SyncMonitoredAcrossFormats = true;
                request.SearchForMissingBooks = true;
                h.Import(request);
                Assert.Multiple(() =>
                {
                    Assert.That(h.Commands.OfType<RefreshAuthorCommand>().Count(command => command.RescanFolders), Is.EqualTo(1));
                    Assert.That(h.Commands.OfType<BulkSyncFormatMonitoringCommand>(), Is.Empty);
                    Assert.That(h.Author.AddOptions.SearchForMissingBooks, Is.True);
                    Assert.That(h.SearchTargets, Is.Empty);
                });
                h.Scan();
                h.Scan();
                Assert.That(h.Author.AddOptions, Is.Null);
                Assert.That(h.SearchTargets.Single(), Is.EquivalentTo(h.BookRows.GetBooksByAuthorId(h.Author.Id)
                    .Where(book => setup != "missing-format" || book.MediaType == BookMediaType.Ebook).Select(book => book.Id)));
                Assert.That(h.Commands.OfType<BulkSyncFormatMonitoringCommand>(), Is.Empty);
            });
        }

        [Test]
        public void import_sync_failed_hydration_waits_for_retry_and_scan([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.Import(h.Request("audiobook", false));
                h.Scan();
                h.Commands.Clear();
                h.MetadataError = new BookInfoException("Metadata is temporarily unavailable");
                var request = h.Request("ebook");
                request.SearchForMissingBooks = true;
                h.Import(request);
                h.Scan();
                Assert.That(h.Author.AddOptions.AwaitingMetadata, Is.True);
                Assert.That(h.Commands, Is.Empty);
                Assert.That(h.SearchTargets, Is.Empty);
                h.MetadataError = null;
                h.Import(request);
                Assert.That(h.Commands.Single(), Is.TypeOf<RefreshAuthorCommand>());
                h.Scan();
                Assert.That(h.SearchTargets.Single(), Is.EqualTo(h.BookRows.GetBooksByAuthorId(h.Author.Id)
                    .Where(book => book.MediaType == BookMediaType.Ebook).Select(book => book.Id).ToArray()));
                Assert.That(h.Commands.OfType<BulkSyncFormatMonitoringCommand>(), Is.Empty);
            });
        }

        [Test]
        public void import_sync_repeated_enable_recovers_rejected_command_handoff([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                PrepareIndependentBookMonitoring(h);
                var request = MinimalImport();
                request.SyncMonitoredAcrossFormats = true;
                Set(h.Controller, "_commandQueueManager", Proxy<IManageCommandQueue>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo("Push"));
                    Assert.That(args[0], Is.TypeOf<BulkSyncFormatMonitoringCommand>());
                    throw new InvalidOperationException("Command queue unavailable");
                }));
                Assert.Throws<InvalidOperationException>(() => h.Import(request));
                Assert.That(h.Author.SyncMonitoredAcrossFormats, Is.True);
                Set(h.Controller, "_commandQueueManager", h.Queue);
                h.Import(request);
                new AuthorScannedHandler(null, h.Authors, h.Queue, null, h.Events, h.Logger, h.Books)
                    .Execute(h.Commands.OfType<BulkSyncFormatMonitoringCommand>().Single());
                Assert.That(h.BookRows.GetBooksByAuthorId(h.Author.Id).Select(book => book.IsMonitored()), Is.EqualTo(new[] { true, true }));
                Assert.That(h.MetadataFetches, Is.Zero);
            });
        }

        [Test]
        public void import_sync_command_honors_disable_before_execution([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                PrepareIndependentBookMonitoring(h);
                var request = MinimalImport();
                request.SyncMonitoredAcrossFormats = true;
                h.Import(request);
                request.SyncMonitoredAcrossFormats = false;
                h.Import(request);
                new AuthorScannedHandler(null, h.Authors, h.Queue, null, h.Events, h.Logger, h.Books)
                    .Execute(h.Commands.OfType<BulkSyncFormatMonitoringCommand>().Single());
                Assert.That(h.Author.SyncMonitoredAcrossFormats, Is.False);
                Assert.That(h.Commands, Has.Count.EqualTo(1));
                Assert.That(h.BookRows.GetBooksByAuthorId(h.Author.Id).Single(book => book.MediaType == BookMediaType.Ebook).IsMonitored(), Is.False);
            });
        }
    }
}
