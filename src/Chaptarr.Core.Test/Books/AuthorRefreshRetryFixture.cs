using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class AuthorRefreshRetryFixture
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private sealed class Refresh : RefreshAuthorService
        {
            public bool FailChildren;
            public Refresh(IProvideAuthorInfo info, IAuthorService authors, IAuthorSyncMetadataService sync,
                IMetadataServerHealthGate gate = null, Action onCatalogRead = null)
                : base(gate ?? MetadataHealthGateStub.Ready(), info, authors,
                    InterfaceProxy.Proxy<IBookService>((method, _) =>
                    {
                        if (method.Name != nameof(IBookService.GetBooksByAuthor)) throw new AssertionException(method.Name);
                        onCatalogRead?.Invoke();
                        return new List<Book>();
                    }),
                    null, null, null, null, InterfaceProxy.Proxy<IEventAggregator>((_, _) => null),
                    null, null, global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoHistoryRepositoryCalls(), null, null, null, null, null, sync, null, null, Logger, bookRepository: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoBookRepositoryCalls(), authorRepository: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoPendingAuthorSetup()) { }
            protected override List<Book> GetRemoteChildren(Author local, Author remote, RefreshContext context) => new();
            protected override List<Book> GetLocalChildren(Author local, List<Book> remote, RefreshContext context) => new();
            protected override void PrepareChildrenForRefresh(Author author, SortedChildren children, List<Book> remote, RefreshContext context) { }
            protected override bool RefreshChildren(SortedChildren children, List<Book> remote, Author author,
                bool force, bool tags, DateTime? last, RefreshContext context)
            {
                if (FailChildren) throw new InvalidOperationException("planted local repair failure");
                return false;
            }
        }

        private static Author InsertAuthor(IMainDatabase database, int key, AddAuthorOptions options = null) =>
            new AuthorRepository(database, null, Logger).Insert(new Author
            {
                Name = "Retry Author " + key, CleanName = "retryauthor" + key, SortName = "retryauthor" + key,
                TitleSlug = "retry-author-" + key, Overview = "", Path = TestContext.CurrentContext.TestDirectory,
                HardcoverAuthorId = "hc:" + key, RemoteProviderIds = new() { "hc:" + key },
                Added = DateTime.UtcNow, AddOptions = options
            });

        private static IAuthorService Authors(AuthorRepository rows) => InterfaceProxy.Proxy<IAuthorService>((method, args) => method.Name switch
        {
            nameof(IAuthorService.GetAuthors) or nameof(IAuthorService.GetExistingAuthors) => rows.All().Where(a => ((IEnumerable<int>)args[0]).Contains(a.Id)).ToList(),
            nameof(IAuthorService.GetAllAuthors) => rows.All().ToList(),
            nameof(IAuthorService.GetAuthor) => rows.Get((int)args[0]),
            nameof(IAuthorService.UpdateAuthor) => rows.Update((Author)args[0]),
            _ => throw new AssertionException(method.Name)
        });

        [Test]
        public void failed_local_refresh_retries_after_restart_and_clears_on_success([Values(false, true)] bool postgres,
            [Values(false, true)] bool mappingCollision)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var author = InsertAuthor(database, 101);
                var authors = Authors(new AuthorRepository(database, null, Logger));
                var syncRows = new AuthorSyncMetadataRepository(database, null);
                var sync = new AuthorSyncMetadataService(syncRows, Logger);
                sync.CreateOrUpdateSyncMetadata(author.Id, mappingCollision ? "hc:199" : "hc:101", "old");
                if (mappingCollision)
                {
                    var other = InsertAuthor(database, 102);
                    sync.CreateOrUpdateSyncMetadata(other.Id, "hc:101");
                }
                var fetches = 0;
                var info = InterfaceProxy.Proxy<IProvideAuthorInfo>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(IProvideAuthorInfo.RefreshAuthorInfo)));
                    Assert.That(args[2], Is.True, "retry must fetch published data instead of accepting an old ETag");
                    fetches++;
                    return RefreshResult.Updated(new Author
                    {
                        Name = author.Name, CleanName = author.CleanName, SortName = author.SortName, Overview = "new metadata", TitleSlug = author.TitleSlug,
                        HardcoverAuthorId = "hc:101", RemoteProviderIds = new() { "hc:101" }, Books = new(), Series = new()
                    }, "new");
                });
                var failed = new Refresh(info, authors, sync) { FailChildren = !mappingCollision };
                failed.Execute(new RefreshAuthorCommand(author.Id, true, false, forceRefresh: true));
                var pending = syncRows.FindByAuthorId(author.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(pending.LastSyncStatus, Is.EqualTo(SyncStatus.Failed));
                    Assert.That(pending.SyncFailureCount, Is.EqualTo(1));
                    Assert.That(pending.NextSyncNotBefore, Is.InRange(DateTime.UtcNow.AddMinutes(59), DateTime.UtcNow.AddHours(1)));
                    Assert.That(pending.ETag, Is.Null, "failed local reconciliation must not acknowledge the fetched snapshot");
                    Assert.That(pending.LastError, Does.Contain(mappingCollision ? "Reconcile author identity first" : "planted local repair failure"));
                });
                if (mappingCollision) syncRows.Delete(syncRows.FindByExternalAuthorId("hc:101"));
                // Advance only the persisted fixture clock; reconstruct services to model restart.
                pending.NextSyncNotBefore = DateTime.UtcNow.AddSeconds(-1);
                syncRows.Update(pending);
                var recovered = new Refresh(info, authors, new AuthorSyncMetadataService(new AuthorSyncMetadataRepository(database, null), Logger));
                recovered.Execute(new RetryAuthorRefreshCommand());
                var saved = syncRows.FindByAuthorId(author.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(fetches, Is.EqualTo(2));
                    Assert.That(saved.LastSyncStatus, Is.EqualTo(SyncStatus.Success), saved.LastError);
                    Assert.That(saved.NextSyncNotBefore, Is.Null);
                    Assert.That(saved.SyncFailureCount, Is.Zero);
                    Assert.That(saved.ETag, Is.EqualTo("new"));
                });
                recovered.Execute(new RetryAuthorRefreshCommand());
                Assert.That(fetches, Is.EqualTo(2), "a successful refresh leaves the retry queue");
            });
        }

        [Test]
        public void post_fetch_processing_errors_count_towards_the_retry_backoff([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var author = InsertAuthor(database, 1);
                var rows = new AuthorSyncMetadataRepository(database, null);
                var sync = new AuthorSyncMetadataService(rows, Logger);
                sync.CreateOrUpdateSyncMetadata(author.Id, "hc:1", "old");
                var storedAuthors = Authors(new AuthorRepository(database, null, Logger));
                var fetched = false;
                var authors = InterfaceProxy.Proxy<IAuthorService>((method, args) =>
                {
                    if (fetched && method.Name == nameof(IAuthorService.GetAllAuthors))
                        throw new InvalidOperationException("planted local author lookup failure");
                    return method.Invoke(storedAuthors, args);
                });
                var info = InterfaceProxy.Proxy<IProvideAuthorInfo>((method, args) =>
                {
                    fetched = true;
                    return RefreshResult.Updated(new Author { HardcoverAuthorId = "hc:1", RemoteProviderIds = new() { "hc:1" } }, "new");
                });
                var refresh = new Refresh(info, authors, sync);
                for (var attempt = 1; attempt <= 4; attempt++)
                {
                    refresh.Execute(new RefreshAuthorCommand(author.Id, true, false, forceRefresh: true));
                    var saved = rows.FindByAuthorId(author.Id);
                    Assert.That(saved.SyncFailureCount, Is.EqualTo(attempt));
                    Assert.That(saved.LastHttpStatus, Is.EqualTo(200));
                    Assert.That(saved.LastError, Does.Contain("planted local author lookup failure"));
                    Assert.That(saved.ETag, Is.EqualTo("old"));
                    Assert.That((saved.NextSyncNotBefore.Value - DateTime.UtcNow).TotalSeconds,
                        Is.InRange(attempt <= 3 ? 45 : 235, attempt <= 3 ? 75 : 365));
                }
            });
        }

        [Test]
        public void due_query_excludes_healthy_future_and_deleted_authors_and_is_bounded([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var authors = new AuthorRepository(database, null, Logger);
                var sync = new AuthorSyncMetadataRepository(database, null);
                var eligible = new List<int>();
                for (var i = 1; i <= 6; i++)
                {
                    var author = InsertAuthor(database, i);
                    sync.Insert(new AuthorSyncMetadata { AuthorId = author.Id, ExternalAuthorId = "hc:" + i,
                        LastSyncStatus = i == 1 ? SyncStatus.Success : SyncStatus.Failed,
                        NextSyncNotBefore = i == 2 ? DateTime.UtcNow.AddMinutes(1) : i == 3 ? null : DateTime.UtcNow.AddMinutes(-10 + i) });
                    if (i == 4) authors.Delete(author.Id);
                    if (i > 4) eligible.Add(author.Id);
                }
                Assert.That(sync.GetDueForSync().Select(item => item.AuthorId), Is.EqualTo(eligible));
                Assert.That(sync.GetDueForSync(1).Select(item => item.AuthorId), Is.EqualTo(eligible.Take(1)));
            });
        }

        [Test]
        public void held_initial_setup_is_not_reopened_by_refresh_retry([Values(false, true)] bool postgres,
            [Values(false, true)] bool refused)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var author = InsertAuthor(database, 1, new AddAuthorOptions { AwaitingMetadata = !refused, MetadataUnavailable = refused });
                var rows = new AuthorSyncMetadataRepository(database, null);
                rows.Insert(new AuthorSyncMetadata { AuthorId = author.Id, ExternalAuthorId = "hc:1", LastSyncStatus = SyncStatus.Failed,
                    NextSyncNotBefore = DateTime.UtcNow.AddMinutes(-1) });
                var info = InterfaceProxy.Proxy<IProvideAuthorInfo>((_, _) => throw new AssertionException("Pending/refused setup must stay with Wanted Authors"));
                new Refresh(info, Authors(new AuthorRepository(database, null, Logger)), new AuthorSyncMetadataService(rows, Logger))
                    .Execute(new RetryAuthorRefreshCommand());
                Assert.That(rows.FindByAuthorId(author.Id).NextSyncNotBefore, Is.Null);
                Assert.That(new AuthorRepository(database, null, Logger).Get(author.Id).AddOptions.MetadataUnavailable, Is.EqualTo(refused));
            });
        }

        [Test]
        public void retry_cancellation_does_not_become_a_failure()
        {
            var noReads = InterfaceProxy.Proxy<IAuthorSyncMetadataService>((_, _) => throw new AssertionException("Cancelled task must not read or write retry state"));
            Assert.That(() => new Refresh(null, null, noReads).Execute(new RetryAuthorRefreshCommand(), new CancellationToken(true)),
                Throws.InstanceOf<OperationCanceledException>());
        }

        [Test]
        public void outage_skips_author_work_then_allows_a_real_recovery_probe([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var rows = new AuthorSyncMetadataRepository(database, null);
                var sync = new AuthorSyncMetadataService(rows, Logger);
                for (var i = 1; i <= 3; i++)
                {
                    var author = InsertAuthor(database, i);
                    sync.CreateOrUpdateSyncMetadata(author.Id, "hc:" + i);
                    sync.UpdateSyncResult(author.Id, false, error: "initial outage");
                }
                void MakeDue()
                {
                    foreach (var row in rows.All())
                    {
                        row.NextSyncNotBefore = DateTime.UtcNow.AddMinutes(-1);
                        rows.Update(row);
                    }
                }
                MakeDue();
                var health = new MetadataServerHealthService(Logger);
                var config = InterfaceProxy.Proxy<NzbDrone.Core.Configuration.IConfigService>((_, _) => "https://metadata.test");
                var gate = new MetadataServerHealthGate(config, health, Logger);
                health.ReportFailure(gate.SourceName, new WebException("connection unavailable"));
                var state = health.GetStatus(gate.SourceName);
                var fetches = 0;
                var catalogReads = 0;
                var failProbe = true;
                var info = InterfaceProxy.Proxy<IProvideAuthorInfo>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(IProvideAuthorInfo.RefreshAuthorInfo)));
                    Assert.That(gate.TryBeginRequest(out _), Is.True, "only the request claims the probe");
                    fetches++;
                    if (failProbe)
                    {
                        gate.ReportException(new WebException("probe failed", WebExceptionStatus.ConnectFailure));
                        return RefreshResult.Error("probe failed", HttpStatusCode.ServiceUnavailable);
                    }
                    health.ReportSuccess(gate.SourceName);
                    return RefreshResult.NoChanges("recovered");
                });
                var refresh = new Refresh(info, Authors(new AuthorRepository(database, null, Logger)), sync, gate, () => catalogReads++);
                var savedAttempts = rows.All().ToDictionary(row => row.Id, row => (row.LastSyncAttempt, row.NextSyncNotBefore));
                refresh.Execute(new RetryAuthorRefreshCommand());
                Assert.That(fetches, Is.Zero);
                Assert.That(catalogReads, Is.Zero);
                foreach (var row in rows.All())
                    Assert.That((row.LastSyncAttempt, row.NextSyncNotBefore), Is.EqualTo(savedAttempts[row.Id]), "an outage wait writes no author state");

                state.RateLimitedUntil = DateTime.UtcNow.AddSeconds(-1);
                refresh.Execute(new RetryAuthorRefreshCommand());
                Assert.That(fetches, Is.EqualTo(1), "a failed probe must stop the remaining authors in this batch");
                Assert.That(rows.All().Count(row => row.LastSyncAttempt != savedAttempts[row.Id].LastSyncAttempt), Is.EqualTo(1));
                var readsAfterProbe = catalogReads;
                refresh.Execute(new RetryAuthorRefreshCommand());
                Assert.That(catalogReads, Is.EqualTo(readsAfterProbe));
                Assert.That(rows.All().Select(row => row.SyncFailureCount), Is.All.Zero);

                MakeDue();
                failProbe = false;
                state.RateLimitedUntil = DateTime.UtcNow.AddSeconds(-1);
                refresh.Execute(new RetryAuthorRefreshCommand());
                Assert.That(fetches, Is.EqualTo(4), "successful recovery releases every due author");
                Assert.That(rows.All().Select(row => row.LastSyncStatus), Is.All.EqualTo(SyncStatus.Success));
                Assert.That(rows.All().Select(row => row.NextSyncNotBefore), Is.All.Null);
            });
        }

        [Test]
        public void escaped_retry_is_deferred_without_claiming_an_author_failure([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var author = InsertAuthor(database, 1);
                var rows = new AuthorSyncMetadataRepository(database, null);
                var sync = new AuthorSyncMetadataService(rows, Logger);
                sync.CreateOrUpdateSyncMetadata(author.Id, "hc:1", "old");
                sync.UpdateSyncResult(author.Id, false, error: "original failure");
                var pending = rows.FindByAuthorId(author.Id);
                pending.NextSyncNotBefore = DateTime.UtcNow.AddMinutes(-1);
                rows.Update(pending);
                var reads = 0;
                var refresh = new Refresh(null, Authors(new AuthorRepository(database, null, Logger)), sync,
                    onCatalogRead: () => { reads++; throw new InvalidOperationException("planted catalog read failure"); });
                refresh.Execute(new RetryAuthorRefreshCommand());
                var saved = rows.FindByAuthorId(author.Id);
                Assert.Multiple(() =>
                {
                    Assert.That((saved.NextSyncNotBefore.Value - DateTime.UtcNow).TotalSeconds, Is.InRange(235, 360));
                    Assert.That(saved.SyncFailureCount, Is.Zero);
                    Assert.That(saved.ETag, Is.EqualTo("old"));
                    Assert.That(saved.LastError, Is.EqualTo("original failure"));
                });
                refresh.Execute(new RetryAuthorRefreshCommand());
                Assert.That(reads, Is.EqualTo(1));
            });
        }

        [Test]
        public void provider_outages_do_not_penalize_authors_and_rate_limits_are_bounded([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var author = InsertAuthor(database, 1);
                var rows = new AuthorSyncMetadataRepository(database, null);
                using var logs = new LogFactory();
                var sink = new NLog.Targets.MemoryTarget { Layout = "${level}|${message}" };
                var config = new NLog.Config.LoggingConfiguration(); config.AddRuleForAllLevels(sink); logs.Configuration = config;
                var service = new AuthorSyncMetadataService(rows, logs.GetLogger("retry-test"));
                service.CreateOrUpdateSyncMetadata(author.Id, "hc:1");
                for (var attempt = 1; attempt <= 5; attempt++)
                {
                    service.UpdateSyncResult(author.Id, false, error: "connection unavailable");
                    var seconds = (rows.FindByAuthorId(author.Id).NextSyncNotBefore.Value - DateTime.UtcNow).TotalSeconds;
                    Assert.That(seconds, Is.InRange(45, 75));
                    Assert.That(rows.FindByAuthorId(author.Id).SyncFailureCount, Is.Zero);
                }
                Assert.That(sink.Logs.Count(line => line.StartsWith("Warn|")), Is.EqualTo(1));
                service.UpdateSyncResult(author.Id, false, error: "rate limited", httpStatus: 429);
                Assert.That((rows.FindByAuthorId(author.Id).NextSyncNotBefore.Value - DateTime.UtcNow).TotalMinutes, Is.InRange(59, 60));
                service.UpdateSyncResult(author.Id, false, error: "identity repair failed", httpStatus: 200, localFailure: true);
                var repair = rows.FindByAuthorId(author.Id);
                Assert.That(repair.SyncFailureCount, Is.EqualTo(1));
                Assert.That(repair.ETag, Is.Null);
                Assert.That((repair.NextSyncNotBefore.Value - DateTime.UtcNow).TotalMinutes, Is.InRange(59, 60));
                service.UpdateSyncResult(author.Id, false, error: "untyped missing author", httpStatus: 404);
                Assert.That((rows.FindByAuthorId(author.Id).NextSyncNotBefore.Value - DateTime.UtcNow).TotalMinutes, Is.InRange(59, 60));
                service.UpdateSyncResult(author.Id, false, error: "permanent provider refusal", httpStatus: 422, retryAllowed: false);
                Assert.That(rows.FindByAuthorId(author.Id).NextSyncNotBefore, Is.Null);
            });
        }
    }
}
