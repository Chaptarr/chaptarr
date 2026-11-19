using System;
using System.Linq;
using Dapper;
using Chaptarr.Core.Test.Common;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Books
{
    public partial class AuthorSyncOptionsFixture
    {
        [Test]
        public void existing_files_on_opposite_works_settle_both_formats_before_initial_search([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database) { CatalogWorkCount = 2 };
                var request = h.Request();
                request.AudiobookMonitorExistingMode = request.EbookMonitorExistingMode = MonitorTypes.Existing;
                request.SearchForMissingBooks = true;
                h.Import(request);
                var books = h.Books.GetBooksByAuthor(h.Author.Id);
                foreach (var book in books)
                {
                    book.Title = book.HardcoverBookId == "hc:456789" ? "Dune" : "Dune Messiah";
                    h.BookRows.Update(book);
                    if (book.Title == "Dune" && book.MediaType == BookMediaType.Audiobook ||
                        book.Title == "Dune Messiah" && book.MediaType == BookMediaType.Ebook)
                    {
                        h.Files.Insert(new BookFile { Path = "/fixture/" + book.Id, EditionId = book.Editions.Single().Id,
                            MediaType = book.MediaType.ToString().ToLowerInvariant(), DateAdded = DateTime.UtcNow,
                            Modified = DateTime.UtcNow, Size = 100, Quality = new QualityModel(
                                book.MediaType == BookMediaType.Audiobook ? Quality.M4B : Quality.EPUB), MediaInfo = new MediaInfoModel() });
                    }
                }

                Assert.That(h.SearchTargets, Is.Empty);
                h.Scan();

                Assert.That(h.BookRows.GetBooksByAuthorId(h.Author.Id).All(b => b.IsMonitored()), Is.True);
                Assert.That(h.SearchTargets.Single(), Is.EquivalentTo(books.Where(b =>
                    b.Title == "Dune" && b.MediaType == BookMediaType.Ebook ||
                    b.Title == "Dune Messiah" && b.MediaType == BookMediaType.Audiobook).Select(b => b.Id)),
                    "The queue reads the real missing-book targets at admission, before the handler returns.");
            });
        }

        [Test]
        public void failed_reconcile_retries_without_reapplying_consumed_initial_defaults([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database) { CatalogWorkCount = 2 };
                var request = h.Request();
                request.SearchForMissingBooks = true;
                h.Import(request);
                var subject = new AuthorScannedHandler(new BookMonitoredService(h.Authors, h.Books, h.Logger),
                    h.Authors, h.Queue, Proxy<IBookAddedService>((_, _) => null), h.Events, h.Logger,
                    Proxy<IBookService>((_, _) => throw new InvalidOperationException("Reconcile database unavailable")));
                subject.Handle(new AuthorScannedEvent(h.Author));

                var persisted = new AuthorRepository(database, h.Events, h.Logger).Get(h.Author.Id);
                Assert.That(persisted.AddOptions, Is.Not.Null);
                Assert.That(persisted.AddOptions.Monitor, Is.Null);
                Assert.That(persisted.AddOptions.AudiobookMonitor, Is.Null);
                Assert.That(persisted.AddOptions.EbookMonitor, Is.Null);
                Assert.That(persisted.AddOptions.SearchForMissingBooks, Is.True);
                Assert.That(h.SearchTargets, Is.Empty);

                var books = h.BookRows.GetBooksByAuthorId(h.Author.Id);
                var unwanted = books.Where(b => b.HardcoverBookId == "hc:456789").ToList();
                foreach (var book in unwanted) book.SetMonitored(false);
                h.BookRows.SetMonitoring(unwanted);
                var wantedTwin = books.Single(b => b.HardcoverBookId != "hc:456789" && b.MediaType == BookMediaType.Ebook);
                wantedTwin.SetMonitored(false);
                h.BookRows.SetMonitoring(new[] { wantedTwin });

                Assert.That(h.Commands.OfType<BulkSyncFormatMonitoringCommand>(), Is.Empty, "Retries must not run back-to-back");
                Assert.That(persisted.AddOptions.InitialFormatSyncFailureCount, Is.EqualTo(1));
                Assert.That(persisted.AddOptions.InitialFormatSyncNotBefore,
                    Is.InRange(DateTime.UtcNow.AddSeconds(45), DateTime.UtcNow.AddSeconds(75)));
                FormatRetryTick(h, database);
                Assert.That(h.Commands.OfType<BulkSyncFormatMonitoringCommand>(), Is.Empty);
                persisted.AddOptions.InitialFormatSyncNotBefore = DateTime.UtcNow.AddSeconds(-1);
                h.Authors.SetAddOptions(persisted);
                FormatRetryTick(h, database);
                var retry = h.Commands.OfType<BulkSyncFormatMonitoringCommand>().Single();
                Assert.That(retry.InitialSetupAttempt, Is.EqualTo(1));
                Assert.That(retry.AuthorIds, Is.EqualTo(new[] { h.Author.Id }));
                Assert.That(retry.Trigger, Is.EqualTo(CommandTrigger.Scheduled));
                new AuthorScannedHandler(new BookMonitoredService(h.Authors, h.Books, h.Logger), h.Authors, h.Queue,
                    Proxy<IBookAddedService>((_, _) => null), h.Events, h.Logger, h.Books).Execute(retry);

                Assert.That(unwanted.Select(b => h.BookRows.Get(b.Id).IsMonitored()), Is.All.False);
                Assert.That(h.BookRows.Get(wantedTwin.Id).IsMonitored(), Is.True);
                Assert.That(h.SearchTargets.Single(), Is.EquivalentTo(books.Where(b => b.HardcoverBookId != "hc:456789").Select(b => b.Id)));
                Assert.That(new AuthorRepository(database, h.Events, h.Logger).Get(h.Author.Id).AddOptions, Is.Null);
            });
        }
        [Test]
        public void initial_sync_retry_spacing_survives_restart_and_keeps_retrying_at_the_cap([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var request = h.Request();
                request.SearchForMissingBooks = true;
                h.Import(request);
                var syncCalls = 0;
                AuthorScannedHandler Handler() => new(new BookMonitoredService(h.Authors, h.Books, h.Logger),
                    h.Authors, h.Queue, Proxy<IBookAddedService>((_, _) => null), h.Events, h.Logger,
                    Proxy<IBookService>((method, _) =>
                    {
                        Assert.That(method.Name, Is.EqualTo(nameof(IBookService.SyncMonitoredAcrossFormats)));
                        syncCalls++;
                        throw new InvalidOperationException("Reconcile unavailable");
                    }));
                Handler().Handle(new AuthorScannedEvent(h.Author));
                var metadataReads = h.MetadataFetches;
                var originalCommands = h.Commands.Count;
                for (var attempt = 1; attempt <= 6; attempt++)
                {
                    // Reload persisted state and reconstruct the tick and handler on each attempt.
                    var persisted = new AuthorRepository(database, h.Events, h.Logger).Get(h.Author.Id);
                    Assert.That(persisted.AddOptions.InitialFormatSyncFailureCount, Is.EqualTo(attempt));
                    var delay = (persisted.AddOptions.InitialFormatSyncNotBefore.Value - DateTime.UtcNow).TotalSeconds;
                    Assert.That(delay, Is.InRange(attempt <= 3 ? 45 : 235, attempt <= 3 ? 75 : 365));
                    Assert.That(persisted.AddOptions.SearchForMissingBooks, Is.True);
                    FormatRetryTick(h, database);
                    Assert.That(h.Commands.Count, Is.EqualTo(originalCommands + attempt - 1));
                    Handler().Execute(new BulkSyncFormatMonitoringCommand(new() { h.Author.Id }) { InitialSetupAttempt = 1 });
                    Assert.That(syncCalls, Is.EqualTo(attempt), "An early or duplicate queued attempt must respect the saved due time");
                    persisted.AddOptions.InitialFormatSyncNotBefore = DateTime.UtcNow.AddSeconds(-1);
                    h.Authors.SetAddOptions(persisted);
                    FormatRetryTick(h, database);
                    var command = h.Commands.Last();
                    Assert.That(command, Is.TypeOf<BulkSyncFormatMonitoringCommand>());
                    Handler().Execute((BulkSyncFormatMonitoringCommand)command);
                    Assert.That(syncCalls, Is.EqualTo(attempt + 1));
                }
                Assert.That(h.MetadataFetches, Is.EqualTo(metadataReads));
                Assert.That(h.SearchTargets, Is.Empty);
                Assert.That(h.Commands.Skip(originalCommands), Has.All.TypeOf<BulkSyncFormatMonitoringCommand>(), "Retries must not enqueue disk scans");
            });
        }

        [Test]
        public void pending_setup_index_keeps_due_selection_exact([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.Import(h.Request());
                var repository = new AuthorRepository(database, h.Events, h.Logger);
                var now = DateTime.UtcNow;
                var author = repository.Get(h.Author.Id);
                using var connection = database.OpenConnection();
                var definition = connection.QuerySingle<string>(postgres
                    ? "SELECT indexdef FROM pg_indexes WHERE indexname = 'IX_Authors_PendingSetup'"
                    : "SELECT sql FROM sqlite_master WHERE name = 'IX_Authors_PendingSetup'");
                Assert.That(definition, Does.Contain("AddOptions").And.Contain("IS NOT NULL"));
                foreach (var state in new[] { "none", "not-scheduled", "future", "waiting-metadata", "unavailable", "due" })
                {
                    author.AddOptions = state == "none" ? null : new AddAuthorOptions
                    {
                        InitialFormatSyncNotBefore = state == "not-scheduled" ? null : now.AddMinutes(state == "future" ? 5 : -1),
                        AwaitingMetadata = state == "waiting-metadata",
                        MetadataUnavailable = state == "unavailable"
                    };
                    repository.Update(author);
                    Assert.That(repository.GetDueInitialFormatSync(now), Is.EqualTo(state == "due" ? new[] { author.Id } : Array.Empty<int>()), state);
                }
            });
        }

        [Test]
        public void setup_retry_query_failure_does_not_skip_metadata_retry()
        {
            var messages = new NLog.Targets.MemoryTarget { Layout = "${level}|${message}" };
            var logging = new NLog.Config.LoggingConfiguration();
            logging.AddRule(NLog.LogLevel.Debug, NLog.LogLevel.Fatal, messages);
            using var logs = new NLog.LogFactory { Configuration = logging };
            var failed = true;
            var metadataGateCalls = 0;
            var metadataGate = Proxy<NzbDrone.Core.MetadataSource.IMetadataServerHealthGate>((method, args) =>
            {
                if (method.Name == "get_SourceName") return "fixture";
                Assert.That(method.Name, Is.EqualTo("CanBeginRequest"));
                metadataGateCalls++;
                args[0] = TimeSpan.FromMinutes(5);
                return false;
            });
            var repository = Proxy<IAuthorRepository>((method, _) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(IAuthorRepository.GetDueInitialFormatSync)));
                if (failed) throw new InvalidOperationException("Setup state unavailable");
                return new System.Collections.Generic.List<int>();
            });
            var subject = new RefreshAuthorService(metadataGate, authorInfo: null, authorService: null, bookService: null,
                editionService: null, metadataProfileService: null, refreshBookService: null, refreshSeriesService: null,
                eventAggregator: null, commandQueueManager: null, mediaFileService: null, historyRepository: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoHistoryRepositoryCalls(),
                rootFolderService: null, checkIfAuthorShouldBeRefreshed: null, monitorNewBookService: null,
                configService: null, importListExclusionService: null, syncMetadataService: null, syncQueueService: null,
                rootFolderSettingsResolver: null, logger: logs.GetLogger("SetupRetry"), bookRepository: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoBookRepositoryCalls(), authorRepository: repository);
            for (var attempt = 0; attempt < 11; attempt++)
                Assert.DoesNotThrow(() => subject.Execute(new RetryAuthorRefreshCommand()));
            Assert.That(metadataGateCalls, Is.EqualTo(11));
            Assert.That(messages.Logs.Count(line => line.StartsWith("Warn|")), Is.EqualTo(3));
            Assert.That(messages.Logs.Count(line => line.StartsWith("Debug|Initial format-sync")), Is.EqualTo(8));
            failed = false;
            subject.Execute(new RetryAuthorRefreshCommand());
            Assert.That(messages.Logs.Count(line => line.Contains("recovered after 11 failed attempts")), Is.EqualTo(1));
            failed = true;
            subject.Execute(new RetryAuthorRefreshCommand());
            Assert.That(metadataGateCalls, Is.EqualTo(13));
            Assert.That(messages.Logs.Last(line => line.StartsWith("Warn|")), Does.Contain("attempt 1)"));
        }

        [Test]
        public void setup_retry_cancellation_propagates_without_backoff_or_consuming_the_request([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.Import(h.Request());
                var before = h.Author.AddOptions.InitialFormatSyncFailureCount;
                var calls = 0;
                var handler = new AuthorScannedHandler(Proxy<IBookMonitoredService>((_, _) =>
                {
                    calls++;
                    throw new OperationCanceledException("setup cancelled");
                }), h.Authors, h.Queue, Proxy<IBookAddedService>((_, _) => null), h.Events, h.Logger, h.Books);
                Assert.Throws<OperationCanceledException>(() => handler.Execute(
                    new BulkSyncFormatMonitoringCommand(new() { h.Author.Id }) { InitialSetupAttempt = 1 }));
                Assert.That(calls, Is.EqualTo(1));
                var stored = h.AuthorRows.Get(h.Author.Id).AddOptions;
                Assert.That(stored, Is.Not.Null);
                Assert.That(stored.InitialFormatSyncFailureCount, Is.EqualTo(before));
                Assert.That(stored.InitialFormatSyncNotBefore, Is.Null);
                Assert.That(h.SearchTargets, Is.Empty);
            });
        }

        private static void FormatRetryTick(Harness h, NzbDrone.Core.Datastore.IMainDatabase database)
        {
            var unavailable = Proxy<NzbDrone.Core.MetadataSource.IMetadataServerHealthGate>((method, args) =>
            {
                if (method.Name == "get_SourceName") return "fixture";
                Assert.That(method.Name, Is.EqualTo("CanBeginRequest"));
                args[0] = TimeSpan.FromMinutes(5);
                return false;
            });
            new RefreshAuthorService(unavailable, authorInfo: null, authorService: h.Authors, bookService: null,
                editionService: null, metadataProfileService: null, refreshBookService: null, refreshSeriesService: null,
                eventAggregator: h.Events, commandQueueManager: h.Queue, mediaFileService: null, historyRepository: new global::NzbDrone.Core.History.HistoryRepository(database, h.Events),
                rootFolderService: null, checkIfAuthorShouldBeRefreshed: null, monitorNewBookService: null,
                configService: null, importListExclusionService: null, syncMetadataService: null, syncQueueService: null,
                rootFolderSettingsResolver: null, logger: h.Logger, mainDatabase: database, bookRepository: new BookRepository(database, h.Events), authorRepository: new AuthorRepository(database, h.Events, h.Logger))
                .Execute(new RetryAuthorRefreshCommand());
        }
    }
}
