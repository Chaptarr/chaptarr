using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Common;
using Chaptarr.Core.Test.MetadataSource.BookInfo;
using Dapper;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo;
using NzbDrone.Core.Messaging.Commands;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Books
{
    public partial class AuthorSyncOptionsFixture
    {
        [Test]
        public void adding_the_missing_format_searches_that_format_only_after_its_catalog_and_monitoring_are_saved(
            [Values(false, true)] bool postgres, [Values("audiobook", "ebook")] string requestedSide)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.Import(h.Request(requestedSide == "ebook" ? "audiobook" : "ebook", false));
                h.Scan();
                var request = h.Request(requestedSide, false);
                request.SearchForMissingBooks = true;
                h.Import(request);
                Assert.That(h.SearchTargets, Is.Empty);
                h.Scan();
                var targets = h.SearchTargets.Single();
                Assert.That(targets, Is.Not.Empty);
                Assert.That(h.Books.GetBooks(targets).All(book => book.MediaType.ToString().Equals(requestedSide, StringComparison.OrdinalIgnoreCase)), Is.True);
                Assert.That(h.Author.AddOptions, Is.Null);
            });
        }

        [Test]
        public void concurrent_cancel_cannot_be_overwritten_by_stale_metadata_readiness_save([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database) { Available = false };
                var request = h.Request("audiobook", false);
                request.SearchForMissingBooks = true;
                h.Import(request);
                var pending = h.Pending.GetByProviderId("hc:123456");
                var rows = new PendingAuthorImportRepository(database, h.Events);
                using var rowCancelled = new ManualResetEventSlim();
                Set(h.Pending, "_repository", Proxy<IPendingAuthorImportRepository>((method, args) =>
                {
                    var result = method.Invoke(rows, args);
                    if (method.Name == nameof(IPendingAuthorImportRepository.TryUpdateRequest) && result is true &&
                        ((PendingAuthorImport)args[0]).AudiobookStatus == PendingImportStatus.NotRequested)
                        rowCancelled.Set();
                    return result;
                }));
                Task cancel = null;
                var reachedReadiness = false;
                var observedCancelledRow = false;
                var cancelledInsideReadiness = false;
                Set(h.Library, "_authorService", Proxy<IAuthorService>((method, args) =>
                {
                    if (!reachedReadiness && method.Name == nameof(IAuthorService.SetAddOptions) &&
                        ((Author)args[0]).AddOptions is { AwaitingMetadata: false })
                    {
                        reachedReadiness = true;
                        cancel = Task.Run(() => h.Pending.Cancel(pending.Id));
                        observedCancelledRow = rowCancelled.Wait(TimeSpan.FromSeconds(5));
                        cancelledInsideReadiness = cancel.Wait(TimeSpan.FromMilliseconds(100));
                    }
                    return method.Invoke(h.Authors, args);
                }));
                h.Available = true;
                try
                {
                    h.ProcessPending();
                }
                finally
                {
                    Assert.That(cancel?.Wait(TimeSpan.FromSeconds(5)), Is.True);
                }
                Assert.That(reachedReadiness, Is.True);
                Assert.That(observedCancelledRow, Is.True);
                Assert.That(cancelledInsideReadiness, Is.False, "The existing author lock serializes acknowledgment with readiness persistence.");
                Assert.That(h.Author.AddOptions, Is.Null);
                h.Scan();
                Assert.That(h.SearchTargets, Is.Empty);
            });
        }

        [Test]
        public void cancelling_before_first_author_insert_cannot_leave_setup_for_a_later_scan(
            [Values(false, true)] bool postgres, [Values(false, true)] bool delete)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database) { Available = false };
                var request = h.Request("audiobook", false);
                request.SearchForMissingBooks = true;
                h.Import(request);
                var pending = h.Pending.GetByProviderId("hc:123456");
                Assert.That(h.Author, Is.Null);
                h.Available = true;
                var intercepted = false;
                Set(h.Library, "_authorService", Proxy<IAuthorService>((method, args) =>
                {
                    if (method.Name == nameof(IAuthorService.AddAuthor))
                    {
                        intercepted = true;
                        h.Pending.Cancel(pending.Id);
                        if (delete) h.Pending.Delete(pending.Id);
                    }
                    return method.Invoke(h.Authors, args);
                }));
                h.ProcessPending();
                Assert.That(intercepted, Is.True, "Cancellation must run after the actual pre-persistence version check.");
                Assert.That(h.Author, Is.Not.Null, "Cancellation does not roll back already admitted metadata writes.");
                Assert.That(h.Author.AddOptions, Is.Null);
                Assert.That(h.Commands.OfType<RefreshAuthorCommand>(), Is.Empty);
                h.Scan();
                Assert.That(h.SearchTargets, Is.Empty);
            });
        }

        [Test]
        public void cancelling_owned_search_does_not_leak_to_other_side_or_remove_independent_search(
            [Values(false, true)] bool postgres, [Values(false, true)] bool independentSearch)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var audio = h.Request("audiobook", false);
                audio.SearchForMissingBooks = independentSearch;
                h.Import(audio);
                h.MetadataError = new BookInfoException("Initial metadata outage");
                var ebook = h.Request("ebook", false);
                ebook.SearchForMissingBooks = true;
                h.Import(ebook);
                if (independentSearch)
                {
                    var author = h.Author;
                    author.AddOptions.RecordInitialSearch(true, false, false);
                    h.Authors.SetAddOptions(author);
                }
                h.Pending.Handle(new AuthorSetupMetadataRefusedEvent(h.Author, "hc:123456",
                    new AuthorTerminalException("author_no_primary_works", "hc:123456", null, "Declared author stop", true)));
                var pending = h.Pending.GetByProviderId("hc:123456");
                h.Pending.Delete(pending.Id);
                Assert.That(h.Author.AddOptions.AudiobookMonitor, Is.EqualTo(MonitorTypes.All));
                Assert.That(h.Author.AddOptions.EbookMonitor, Is.Null);
                Assert.That(h.Author.AddOptions.SearchForMissingBooks, Is.EqualTo(independentSearch));
                h.Scan();
                Assert.That(h.SearchTargets, Has.Count.EqualTo(independentSearch ? 1 : 0));
            });
        }

        [Test]
        public void removing_last_owned_setup_preserves_an_independent_search_only_request([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.Import(h.Request("audiobook", false));
                h.Scan();
                h.MetadataError = new BookInfoException("Initial metadata outage");
                var request = h.Request("ebook", false);
                request.SearchForMissingBooks = true;
                h.Import(request);
                var author = h.Author;
                author.AddOptions.RecordInitialSearch(true, false, false);
                h.Authors.SetAddOptions(author);
                h.Pending.Handle(new AuthorSetupMetadataRefusedEvent(h.Author, "hc:123456",
                    new AuthorTerminalException("author_no_primary_works", "hc:123456", null, "Declared author stop", true)));
                h.Pending.Delete(h.Pending.GetByProviderId("hc:123456").Id);
                Assert.That(h.Author.AddOptions, Is.Not.Null);
                Assert.That(h.Author.AddOptions.EbookMonitor, Is.Null);
                Assert.That(h.Author.AddOptions.SearchForMissingBooks, Is.True);
                Assert.That(h.Author.AddOptions.AwaitingMetadata, Is.False);
                h.Scan();
                Assert.That(h.SearchTargets, Has.Count.EqualTo(1));
            });
        }

        [Test]
        public void ownership_ids_do_not_imply_refusal_and_retry_keeps_another_failed_owner_held(
            [Values(false, true)] bool postgres, [Values(false, true)] bool otherOwnerFailed,
            [Values(false, true)] bool retry)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.Import(h.Request("both", false));
                var rows = new PendingAuthorImportRepository(database, h.Events);
                var audio = rows.Insert(new PendingAuthorImport
                {
                    ProviderId = "hc:123456", ProviderPrefix = "hc", CreatedAt = DateTime.UtcNow, NextAttemptAt = DateTime.UtcNow,
                    AudiobookStatus = otherOwnerFailed ? PendingImportStatus.Failed : PendingImportStatus.Pending,
                    OverallStatus = otherOwnerFailed ? PendingImportStatus.Failed : PendingImportStatus.Pending
                });
                var ebook = rows.Insert(new PendingAuthorImport
                {
                    ProviderId = "hc:123456", ProviderPrefix = "hc", CreatedAt = DateTime.UtcNow, NextAttemptAt = DateTime.UtcNow,
                    EbookStatus = PendingImportStatus.Failed, OverallStatus = PendingImportStatus.Failed
                });
                var author = h.Author;
                author.AddOptions.AudiobookSetupPendingImportId = audio.Id;
                author.AddOptions.EbookSetupPendingImportId = ebook.Id;
                author.AddOptions.AwaitingMetadata = true;
                author.AddOptions.AudiobookAwaitingMetadata = author.AddOptions.EbookAwaitingMetadata = true;
                author.AddOptions.MetadataUnavailable = true;
                h.Authors.SetAddOptions(author);
                if (retry)
                {
                    if (!otherOwnerFailed) rows.Delete(audio.Id);
                    h.Pending.RetryNow(ebook.Id);
                }
                else h.Pending.Delete(ebook.Id);
                Assert.That(h.Author.AddOptions.MetadataUnavailable, Is.EqualTo(otherOwnerFailed));
            });
        }

        [Test]
        public void cancelling_owned_missing_side_setup_preserves_the_other_sides_unfinished_initial_monitoring(
            [Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.Import(h.Request("audiobook", false));
                h.MetadataError = new BookInfoException("Initial metadata outage");
                h.Import(h.Request("ebook", false));
                h.Pending.Handle(new AuthorSetupMetadataRefusedEvent(h.Author, "hc:123456",
                    new AuthorTerminalException("author_no_primary_works", "hc:123456", null, "Declared author stop", true)));
                var pending = h.Pending.GetByProviderId("hc:123456");
                Assert.That(pending.HasAudiobook(), Is.False, "A hydrated side still awaiting its scan is independent of missing-side metadata.");
                h.Pending.RetryNow(pending.Id);
                h.Pending.Cancel(pending.Id);
                Assert.That(h.Author.AddOptions.AudiobookMonitor, Is.EqualTo(MonitorTypes.All));
                Assert.That(h.Author.AddOptions.EbookMonitor, Is.Null);
                Assert.That(h.Author.AddOptions.AwaitingMetadata, Is.False);
                Assert.That(h.Author.AddOptions.MetadataUnavailable, Is.False);
            });
        }

        [Test]
        public void partial_initial_edition_insertion_keeps_checkpoint_and_reconciles_every_book_on_retry(
            [Values(false, true)] bool postgres, [Values(false, true)] bool newAuthor,
            [Values("audiobook", "ebook")] string missingSide)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database) { CatalogWorkCount = 3 };
                if (!newAuthor)
                {
                    h.Import(h.Request(missingSide == "ebook" ? "audiobook" : "ebook", false));
                    h.Scan();
                }

                h.Commands.Clear();
                var request = h.Request(missingSide, false);
                request.SearchForMissingBooks = true;
                using var connection = database.OpenConnection();
                connection.Execute(postgres
                    ? "CREATE FUNCTION reject_initial_edition() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'fixture initial edition failure'; END; $$; CREATE TRIGGER reject_initial_edition BEFORE INSERT ON \"Editions\" FOR EACH ROW EXECUTE FUNCTION reject_initial_edition();"
                    : "CREATE TRIGGER reject_initial_edition BEFORE INSERT ON Editions BEGIN SELECT RAISE(ABORT, 'fixture initial edition failure'); END;");
                try
                {
                    Assert.That(() => h.Import(request), Throws.Exception.With.Message.Contains("fixture initial edition failure"));
                    Assert.That(h.Author.AddOptions?.AwaitingMetadata, Is.True);
                    Assert.That(h.Author.AddOptions?.SearchForMissingBooks, Is.True);
                    var side = missingSide == "ebook" ? BookMediaType.Ebook : BookMediaType.Audiobook;
                    var partial = h.Books.GetBooksByAuthor(h.Author.Id).Where(book => book.MediaType == side).ToList();
                    Assert.That(partial, Has.Count.EqualTo(3), "The actual separate Book batch committed before Edition insertion failed.");
                    Assert.That(h.Editions.GetEditionsByBook(partial.Select(book => book.Id)), Is.Empty);
                    h.Scan();
                    Assert.That(h.Author.AddOptions?.AwaitingMetadata, Is.True);
                    Assert.That(h.SearchTargets, Is.Empty);
                }
                finally
                {
                    connection.Execute(postgres
                        ? "DROP TRIGGER reject_initial_edition ON \"Editions\"; DROP FUNCTION reject_initial_edition();"
                        : "DROP TRIGGER reject_initial_edition;");
                }

                h.Import(request);
                var books = h.Books.GetBooksByAuthor(h.Author.Id);
                Assert.That(books, Has.Count.EqualTo(newAuthor ? 3 : 6));
                Assert.That(books.All(book => h.Editions.GetEditionsByBook(book.Id).Count == 1), Is.True);
                Assert.That(h.Author.AddOptions?.AwaitingMetadata, Is.False);
                Assert.That(h.SearchTargets, Is.Empty);
                h.Scan();
                Assert.That(h.Author.AddOptions, Is.Null);
                Assert.That(h.SearchTargets, Has.Count.EqualTo(1));
            });
        }

        [Test]
        public void automatic_metadata_recovery_forces_one_scoped_initial_scan_despite_normal_rescan_policy(
            [Values(false, true)] bool postgres,
            [Values(RescanAfterRefreshType.Never, RescanAfterRefreshType.AfterManual)] RescanAfterRefreshType policy,
            [Values(false, true)] bool emptyCatalog, [Values(false, true)] bool rescanFolders)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.Import(h.Request("audiobook", false));
                h.Scan();
                h.MetadataError = new BookInfoException("Initial metadata outage");
                var request = h.Request("ebook", false);
                request.SearchForMissingBooks = true;
                h.Import(request);
                h.MetadataError = null;
                if (emptyCatalog) h.CatalogWorkCount = 0;
                h.Commands.Clear();
                var info = Proxy<IProvideAuthorInfo>((_, _) => RefreshResult.Updated(h.Info.GetAuthorInfo("hc:123456"), "ready"));
                HydrationRefresh(h, database, info, policy).Execute(new RefreshAuthorCommand(h.Author.Id, refreshMetadata: true, rescanFolders: rescanFolders)
                {
                    Trigger = CommandTrigger.Scheduled
                });
                var scan = h.Commands.OfType<RescanFoldersCommand>().Single();
                Assert.That(scan.AuthorIds, Is.EqualTo(new[] { h.Author.Id }));
                Assert.That(scan.Folders, Does.Not.Contain(h.AudioRoot.Path));
                Assert.That(scan.Folders, Does.Not.Contain(h.EbookRoot.Path));
                Assert.That(h.Author.AddOptions?.AwaitingMetadata, Is.False);
                Assert.That(h.SearchTargets, Is.Empty);
                h.Scan();
                Assert.That(h.Author.AddOptions, Is.Null);
                Assert.That(h.SearchTargets, Has.Count.EqualTo(1));
            });
        }

        [Test]
        public void typed_missing_side_refusal_reuses_retained_wanted_request_and_retry_delete_lifecycle(
            [Values(false, true)] bool postgres, [Values(false, true)] bool retry)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var messages = new NLog.Targets.MemoryTarget { Layout = "${message}" };
                var logConfig = new NLog.Config.LoggingConfiguration();
                logConfig.AddRule(NLog.LogLevel.Info, NLog.LogLevel.Fatal, messages);
                using var logs = new NLog.LogFactory { Configuration = logConfig };
                var h = new Harness(database, logger: logs.GetLogger("SetupRefusal"));
                h.Import(h.Request("audiobook", false));
                h.Scan();
                var originalBooks = h.Books.GetBooksByAuthor(h.Author.Id).Select(book => book.Id).ToArray();
                h.MetadataError = new BookInfoException("Initial metadata outage");
                var request = h.Request("ebook", false);
                request.SearchForMissingBooks = true;
                h.Import(request);
                h.Commands.Clear();
                var calls = 0;
                var http = Proxy<IHttpClient>((_, args) =>
                {
                    calls++;
                    return new HttpResponse((HttpRequest)args[0], new HttpHeader { ContentType = "application/json" },
                        "{\"code\":\"author_no_primary_works\",\"providerId\":\"hc:123456\",\"message\":\"Declared author stop\",\"retryable\":false,\"reopenable\":true}", HttpStatusCode.NotFound);
                });
                var metadata = BookInfoProxyCanonicalLookupFixture.CreateProxy(http);
                var refresh = HydrationRefresh(h, database, metadata);
                refresh.Execute(new RefreshAuthorCommand(h.Author.Id, refreshMetadata: true, rescanFolders: true, forceRefresh: true));
                var pending = h.Pending.GetByProviderId("hc:123456");
                Assert.That(pending, Is.Not.Null);
                Assert.That(pending.OverallStatus, Is.EqualTo(PendingImportStatus.Failed));
                Assert.That(pending.LastError, Does.Contain("Declared author stop"));
                Assert.That(pending.HasAudiobook(), Is.False);
                Assert.That(pending.HasEbook(), Is.True);
                Assert.That(h.Author.AddOptions.EbookSetupPendingImportId, Is.EqualTo(pending.Id));
                Assert.That(h.Author.AddOptions.MetadataUnavailable, Is.True);
                Assert.That(h.Books.GetBooksByAuthor(h.Author.Id).Select(book => book.Id), Is.EqualTo(originalBooks));
                h.Scan();
                Assert.That(h.SearchTargets, Is.Empty);
                refresh.Execute(new RefreshAuthorCommand(h.Author.Id, refreshMetadata: true, rescanFolders: true, forceRefresh: true)
                {
                    Trigger = CommandTrigger.Manual
                });
                Assert.That(calls, Is.EqualTo(1), "A retained provider refusal must not cause nightly full retries.");
                Assert.That(messages.Logs.Any(message => message.Contains("Wanted > Authors > Retry")), Is.True,
                    "Explicit refresh must explain why the refused request is deferred.");

                if (retry)
                {
                    h.Pending.RetryNow(pending.Id);
                    Assert.That(h.Author.AddOptions.MetadataUnavailable, Is.False);
                    h.MetadataError = null;
                    h.ProcessPending();
                    Assert.That(h.Pending.Get(pending.Id), Is.Null);
                    Assert.That(h.Author.AddOptions?.AwaitingMetadata, Is.False);
                    Assert.That(h.SearchTargets, Is.Empty);
                    h.Scan();
                    Assert.That(h.SearchTargets, Has.Count.EqualTo(1));
                }
                else
                {
                    h.Pending.Delete(pending.Id);
                    Assert.That(h.Author.AddOptions, Is.Null);
                    h.Scan();
                    Assert.That(h.SearchTargets, Is.Empty);
                }
            });
        }
    }
}
