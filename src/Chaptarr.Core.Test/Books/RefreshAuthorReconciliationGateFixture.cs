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
            public Refresh(Author commandAuthor, Action fetch, Action<int> load, Logger logger)
                : base(
                    authorInfo: Proxy<IProvideAuthorInfo>((method, args) =>
                    {
                        if (method.Name != nameof(IProvideAuthorInfo.RefreshAuthorInfo))
                        {
                            throw new NotImplementedException(method.Name);
                        }

                        fetch?.Invoke();
                        return RefreshResult.Updated(commandAuthor);
                    }),
                    authorService: Proxy<IAuthorService>((method, args) => method.Name is nameof(IAuthorService.GetAuthors) or nameof(IAuthorService.GetExistingAuthors)
                        ? new List<Author> { commandAuthor } : throw new NotImplementedException(method.Name)),
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
                    commandQueueManager: null, mediaFileService: null, historyService: null,
                    rootFolderService: null, checkIfAuthorShouldBeRefreshed: null,
                    monitorNewBookService: null, configService: null, importListExclusionService: null,
                    syncMetadataService: Proxy<IAuthorSyncMetadataService>((method, args) => method.Name switch
                    {
                        nameof(IAuthorSyncMetadataService.GetSyncMetadata) => new AuthorSyncMetadata
                        {
                            AuthorId = commandAuthor.Id, ExternalAuthorId = commandAuthor.HardcoverAuthorId
                        },
                        nameof(IAuthorSyncMetadataService.CreateOrUpdateSyncMetadata) => new AuthorSyncMetadata(),
                        nameof(IAuthorSyncMetadataService.UpdateSyncResult) => null,
                        _ => throw new NotImplementedException(method.Name)
                    }),
                    syncQueueService: null, rootFolderSettingsResolver: null, logger: logger)
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
        public void direct_reconciliation_waits_for_command_metadata_phase([Values] bool sameAuthor)
        {
            using var fetching = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var waiting = new ManualResetEventSlim();
            using var loaded = new ManualResetEventSlim();
            var commandAuthor = NewAuthor(100);
            var directAuthor = sameAuthor ? commandAuthor : NewAuthor(200);
            var directThreadId = 0;
            var loadedBeforeRelease = false;
            var acquiredVisible = false;
            var target = new CallbackTarget
            {
                OnMessage = message =>
                {
                    if (message.Contains("Waiting for active author metadata refresh") &&
                        message.Contains("direct reconciliation") && message.Contains(directAuthor.Name))
                    {
                        waiting.Set();
                    }

                    if (message.Contains("Acquired author metadata refresh gate") &&
                        message.Contains("direct reconciliation") && message.Contains(directAuthor.Name))
                    {
                        acquiredVisible = true;
                    }
                }
            };
            using var logs = new LogFactory();
            var configuration = new LoggingConfiguration(logs);
            configuration.AddRule(LogLevel.Info, LogLevel.Fatal, target);
            logs.Configuration = configuration;
            var service = new Refresh(commandAuthor, () =>
            {
                fetching.Set();
                if (!release.Wait(Timeout))
                {
                    throw new TimeoutException("Test did not release the metadata response.");
                }
            }, _ =>
            {
                if (Environment.CurrentManagedThreadId == directThreadId)
                {
                    loadedBeforeRelease = !release.IsSet;
                    loaded.Set();
                }
            }, logs.GetLogger(nameof(RefreshAuthorReconciliationGateFixture)));

            var command = Start(() => service.Execute(new RefreshAuthorCommand(commandAuthor.Id,
                refreshMetadata: true, rescanFolders: false, forceRefresh: true)));
            Task direct = null;
            try
            {
                Assert.That(fetching.Wait(Timeout), Is.True);
                direct = Start(() =>
                {
                    directThreadId = Environment.CurrentManagedThreadId;
                    service.ReconcileAuthorBlob(directAuthor, directAuthor);
                });

                Assert.That(WaitHandle.WaitAny(new[] { waiting.WaitHandle, loaded.WaitHandle }, Timeout), Is.Zero,
                    "Direct reconciliation must log its gate wait before loading local books.");
                Assert.That(loaded.IsSet, Is.False);
            }
            finally
            {
                release.Set();
                Assert.That(Task.WaitAll(direct == null ? new[] { command } : new[] { command, direct }, Timeout), Is.True);
            }

            Assert.That(loaded.IsSet, Is.True);
            Assert.That(loadedBeforeRelease, Is.False);
            Assert.That(acquiredVisible, Is.True, "An ordinary log must explain when the waiting request resumes.");
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
