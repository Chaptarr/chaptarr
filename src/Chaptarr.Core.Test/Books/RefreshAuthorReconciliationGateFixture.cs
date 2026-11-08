using Chaptarr.Core.Test.Common;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    [NonParallelizable]
    public class RefreshAuthorReconciliationGateFixture
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        public class ServiceProxy : DispatchProxy
        {
            public Func<MethodInfo, object[], object> Handler { get; set; }
            protected override object Invoke(MethodInfo method, object[] args) => Handler(method, args);
        }

        private sealed class CallbackTarget : Target
        {
            public Action<string> OnMessage { get; set; }
            protected override void Write(LogEventInfo logEvent) => OnMessage(logEvent.FormattedMessage);
        }

        private sealed class Refresh : RefreshAuthorService
        {
            public Refresh(Author commandAuthor, Action fetch, Action<int> load, Logger logger, Func<int, Author> read = null)
                : base(MetadataHealthGateStub.Ready(),
                    authorInfo: Proxy<IProvideAuthorInfo>((method, args) =>
                    {
                        if (method.Name != nameof(IProvideAuthorInfo.RefreshAuthorInfo))
                        {
                            throw new NotImplementedException(method.Name);
                        }

                        fetch?.Invoke();
                        return RefreshResult.Updated(commandAuthor);
                    }),
                    authorService: Proxy<IAuthorService>((method, args) => method.Name switch
                    {
                        nameof(IAuthorService.GetAuthors) or nameof(IAuthorService.GetExistingAuthors) or nameof(IAuthorService.GetAllAuthors) => new List<Author> { commandAuthor },
                        nameof(IAuthorService.GetAuthor) => read?.Invoke((int)args[0]) ?? NewAuthor((int)args[0]),
                        nameof(IAuthorService.SetAddOptions) => null,
                        _ => throw new NotImplementedException(method.Name)
                    }),
                    bookService: Proxy<IBookService>((method, args) =>
                    {
                        if (method.Name == nameof(IBookService.GetBooksForRefresh))
                        {
                            load?.Invoke((int)args[0]);
                        }
                        else if (method.Name != nameof(IBookService.GetBooksByAuthor))
                        {
                            throw new NotImplementedException(method.Name);
                        }

                        return new List<Book>();
                    }),
                    editionService: null, metadataProfileService: null, refreshBookService: null,
                    refreshSeriesService: null, eventAggregator: Proxy<IEventAggregator>((_, _) => null),
                    commandQueueManager: null, mediaFileService: null, historyRepository: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoHistoryRepositoryCalls(),
                    rootFolderService: null, checkIfAuthorShouldBeRefreshed: null,
                    monitorNewBookService: null, configService: null, importListExclusionService: null,
                    syncMetadataService: Proxy<IAuthorSyncMetadataService>((method, args) => method.Name switch
                    {
                        nameof(IAuthorSyncMetadataService.GetSyncMetadata) => new AuthorSyncMetadata
                        {
                            AuthorId = commandAuthor.Id, ExternalAuthorId = commandAuthor.HardcoverAuthorId
                        },
                        nameof(IAuthorSyncMetadataService.CreateOrUpdateSyncMetadata) => new AuthorSyncMetadata(),
                        nameof(IAuthorSyncMetadataService.GetSyncMetadataByExternalId) => null,
                        nameof(IAuthorSyncMetadataService.UpdateSyncResult) => null,
                        _ => throw new NotImplementedException(method.Name)
                    }),
                    syncQueueService: null, rootFolderSettingsResolver: null, logger: logger, bookRepository: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoBookRepositoryCalls(), authorRepository: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoPendingAuthorSetup())
            {
            }

            // Keep the real command, reconciliation, sorting and gate paths; no database or filesystem writes.
            protected override UpdateResult UpdateEntity(Author local, Author remote) => UpdateResult.None;
            protected override void SaveEntity(Author local) { }
        }

        private static T Proxy<T>(Func<MethodInfo, object[], object> handler)
            where T : class
        {
            var proxy = DispatchProxy.Create<T, ServiceProxy>();
            ((ServiceProxy)(object)proxy).Handler = handler;
            return proxy;
        }

        private static Author NewAuthor(int id) => new Author
        {
            Id = id, Name = $"Fixture author {id}", HardcoverAuthorId = $"hc:{id}", Books = new List<Book>()
        };

        private static Task Start(Action action) => Task.Factory.StartNew(action,
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        [Test]
        public void metadata_fetch_holds_no_author_lock([Values] bool sameAuthor)
        {
            using var fetching = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var author = NewAuthor(100);
            var directAuthor = sameAuthor ? author : NewAuthor(200);
            using var logs = new LogFactory();
            var service = new Refresh(author, () =>
            {
                fetching.Set();
                Assert.That(release.Wait(Timeout), Is.True);
            }, null, logs.GetLogger(nameof(RefreshAuthorReconciliationGateFixture)));
            var command = Start(() => service.Execute(new RefreshAuthorCommand(author.Id,
                refreshMetadata: true, rescanFolders: false, forceRefresh: true)));
            Task direct = null;
            try
            {
                Assert.That(fetching.Wait(Timeout), Is.True);
                direct = Start(() => service.ReconcileAuthorBlob(directAuthor, directAuthor));
                Assert.That(direct.Wait(Timeout), Is.True, "Network fetch must not lock either author.");
            }
            finally
            {
                release.Set();
                Assert.That(Task.WaitAll(direct == null ? new[] { command } : new[] { command, direct }, Timeout), Is.True);
            }
        }

        [Test]
        public void different_author_proceeds_while_same_author_waits_and_reloads_settled_state()
        {
            using var reconciling = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var waiting = new ManualResetEventSlim();
            var author = NewAuthor(100);
            var other = NewAuthor(200);
            var loads = 0;
            var settled = false;
            var reloads = new System.Collections.Concurrent.ConcurrentQueue<bool>();
            var messages = new System.Collections.Concurrent.ConcurrentQueue<string>();
            using var logs = new LogFactory();
            var target = new CallbackTarget { OnMessage = message =>
            {
                messages.Enqueue(message);
                if (message.Contains("Waiting for author")) waiting.Set();
            } };
            var configuration = new LoggingConfiguration(logs);
            configuration.AddRule(LogLevel.Info, LogLevel.Fatal, target);
            logs.Configuration = configuration;
            var service = new Refresh(author, null, id =>
            {
                if (id == author.Id && Interlocked.Increment(ref loads) == 1)
                {
                    reconciling.Set();
                    Assert.That(release.Wait(Timeout), Is.True);
                    settled = true;
                }
            }, logs.GetLogger(nameof(RefreshAuthorReconciliationGateFixture)), id =>
            {
                if (id == author.Id) reloads.Enqueue(settled);
                return NewAuthor(id);
            });
            var first = Start(() => service.ReconcileAuthorBlob(author, author));
            Task same = null;
            Task different = null;
            try
            {
                Assert.That(reconciling.Wait(Timeout), Is.True);
                same = Start(() => service.ReconcileAuthorBlob(author, author));
                Assert.That(waiting.Wait(Timeout), Is.True);
                different = Start(() => service.ReconcileAuthorBlob(other, other));
                Assert.That(different.Wait(Timeout), Is.True, "Another author must not wait for this reconciliation.");
                Assert.That(loads, Is.EqualTo(1));
            }
            finally
            {
                release.Set();
                Assert.That(Task.WaitAll(new[] { first, same ?? Task.CompletedTask, different ?? Task.CompletedTask }, Timeout), Is.True);
            }

            Assert.That(reloads, Is.EqualTo(new[] { false, true }), "Waiting callers reload under the lock after the writer settles.");
            Assert.That(messages.ToArray(), Has.Exactly(1).Contains("Waiting for author"));
            Assert.That(messages.ToArray(), Has.Exactly(1).Contains("after"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void same_author_wait_can_be_cancelled_without_loading_or_holding_the_lock(bool command)
        {
            using var reconciling = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var waiting = new ManualResetEventSlim();
            using var cancellation = new CancellationTokenSource();
            using var logs = new LogFactory();
            var target = new CallbackTarget { OnMessage = message =>
            {
                if (message.Contains("Waiting for author")) waiting.Set();
            } };
            var configuration = new LoggingConfiguration(logs);
            configuration.AddRule(LogLevel.Info, LogLevel.Fatal, target);
            logs.Configuration = configuration;
            var author = NewAuthor(100);
            var loads = 0;
            var service = new Refresh(author, null, _ =>
            {
                if (Interlocked.Increment(ref loads) == 1)
                {
                    reconciling.Set();
                    Assert.That(release.Wait(Timeout), Is.True);
                }
            }, logs.GetLogger(nameof(RefreshAuthorReconciliationGateFixture)));
            var first = Start(() => service.ReconcileAuthorBlob(author, author));
            Task cancelled = null;
            try
            {
                Assert.That(reconciling.Wait(Timeout), Is.True);
                cancelled = Start(() => Assert.Throws<OperationCanceledException>(() =>
                {
                    if (command)
                        service.Execute(new RefreshAuthorCommand(author.Id, refreshMetadata: true, rescanFolders: false, forceRefresh: true), cancellation.Token);
                    else
                        service.ReconcileAuthorBlob(author, author, cancellationToken: cancellation.Token);
                }));
                Assert.That(waiting.Wait(Timeout), Is.True);
                cancellation.Cancel();
                Assert.That(cancelled.Wait(Timeout), Is.True);
                Assert.That(loads, Is.EqualTo(1));
            }
            finally
            {
                release.Set();
                Assert.That(Task.WaitAll(new[] { first, cancelled ?? Task.CompletedTask }, Timeout), Is.True);
            }

            Assert.That(Start(() => service.ReconcileAuthorBlob(author, author)).Wait(Timeout), Is.True);
            Assert.That(loads, Is.EqualTo(2));
        }

        [Test]
        public void reconciliation_is_reentrant_on_the_author_lock()
        {
            var author = NewAuthor(100);
            using var logs = new LogFactory();
            var service = new Refresh(author, null, null, logs.GetLogger(nameof(RefreshAuthorReconciliationGateFixture)));
            var call = Start(() =>
            {
                lock (BookService.GetWantedEditionLock(author.Id))
                {
                    service.ReconcileAuthorBlob(author, author);
                }
            });
            Assert.That(call.Wait(Timeout), Is.True);
        }

        [TestCase(DatabaseType.SQLite)]
        [TestCase(DatabaseType.PostgreSQL)]
        public void existing_author_add_wait_does_not_hold_other_authors_behind_the_sqlite_gate(DatabaseType databaseType)
        {
            var authors = new Dictionary<int, Author> { [100] = NewAuthor(100), [200] = NewAuthor(200) };
            foreach (var author in authors.Values) author.AddOptions = new AddAuthorOptions { Monitor = null, AwaitingMetadata = true };
            using var locked = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var waiting = new ManualResetEventSlim();
            using var logs = new LogFactory();
            var target = new CallbackTarget { OnMessage = message =>
            {
                if (message.Contains("Waiting for author")) waiting.Set();
            } };
            var configuration = new LoggingConfiguration(logs);
            configuration.AddRule(LogLevel.Info, LogLevel.Fatal, target);
            logs.Configuration = configuration;
            var logger = logs.GetLogger(nameof(RefreshAuthorReconciliationGateFixture));
            var authorService = Proxy<IAuthorService>((method, args) => method.Name switch
            {
                nameof(IAuthorService.FindByProviderId) => authors[int.Parse((string)args[1])],
                nameof(IAuthorService.GetAuthor) => authors[(int)args[0]],
                nameof(IAuthorService.UpdateAuthor) => args[0],
                nameof(IAuthorService.SetAddOptions) => null,
                _ => throw new NotImplementedException(method.Name)
            });
            var bookService = Proxy<IBookService>((method, _) => method.Name == nameof(IBookService.GetBooksByAuthor)
                ? new List<Book>() : throw new NotImplementedException(method.Name));
            var refresh = new Refresh(authors[100], null, null, logger, id => authors[id]);
            var database = Proxy<IMainDatabase>((method, _) => method.Name == "get_DatabaseType"
                ? databaseType : throw new NotImplementedException(method.Name));
            var library = new AuthorLibraryService(authorService,
                Proxy<IProvideAuthorInfo>((_, args) => NewAuthor(int.Parse(((string)args[0]).Split(':')[1]))),
                bookService, null, null, null, null, null, null, null, null,
                Proxy<IEventAggregator>((_, _) => null), null, database, null, null, null, logger, refreshAuthorService: refresh);
            var writer = Start(() =>
            {
                lock (BookService.GetWantedEditionLock(100))
                {
                    locked.Set();
                    Assert.That(release.Wait(Timeout), Is.True);
                }
            });
            Task same = null;
            Task different = null;
            try
            {
                Assert.That(locked.Wait(Timeout), Is.True);
                same = Start(() => library.AddAuthorAsync("hc:100").GetAwaiter().GetResult());
                Assert.That(waiting.Wait(Timeout), Is.True);
                different = Start(() => library.AddAuthorAsync("hc:200").GetAwaiter().GetResult());
                Assert.That(different.Wait(Timeout), Is.True);
                Assert.That(authors[100].AddOptions.AwaitingMetadata, Is.True);
                Assert.That(authors[200].AddOptions.AwaitingMetadata, Is.False);
            }
            finally
            {
                release.Set();
                Assert.That(Task.WaitAll(new[] { writer, same ?? Task.CompletedTask, different ?? Task.CompletedTask }, Timeout), Is.True);
            }
        }

        [Test]
        public void direct_reconciliation_releases_gate_after_exception()
        {
            var author = NewAuthor(100);
            var loads = 0;
            using var logs = new LogFactory();
            var service = new Refresh(author, null, _ =>
            {
                if (Interlocked.Increment(ref loads) == 1)
                {
                    throw new InvalidOperationException("Fixture local load failure");
                }
            }, logs.GetLogger(nameof(RefreshAuthorReconciliationGateFixture)));

            Assert.Throws<InvalidOperationException>(() => service.ReconcileAuthorBlob(author, author));
            var following = Start(() => service.ReconcileAuthorBlob(author, author));
            Assert.That(following.Wait(Timeout), Is.True);
            Assert.That(loads, Is.EqualTo(2));
        }

        [Test]
        public void direct_reconciliation_proceeds_when_gate_is_free()
        {
            var author = NewAuthor(100);
            var loads = 0;
            using var logs = new LogFactory();
            var service = new Refresh(author, null, _ => Interlocked.Increment(ref loads),
                logs.GetLogger(nameof(RefreshAuthorReconciliationGateFixture)));

            var direct = Start(() => service.ReconcileAuthorBlob(author, author));
            Assert.That(direct.Wait(Timeout), Is.True);
            Assert.That(loads, Is.EqualTo(1));
        }
    }
}
