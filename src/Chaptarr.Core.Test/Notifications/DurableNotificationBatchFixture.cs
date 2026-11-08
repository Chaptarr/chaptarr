using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NzbDrone.Core.Datastore;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.Kavita;
using NzbDrone.Core.Notifications.Subsonic;
using NzbDrone.Core.Notifications.Synology;
using NzbDrone.Core.ThingiProvider.Events;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Notifications
{
    [TestFixture]
    public class DurableNotificationBatchFixture
    {
        [TestCase(false)]
        [TestCase(true)]
        public void a_book_batch_persists_all_paths_in_one_statement_and_deduplicates_repeats(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var writes = 0;
                var observed = Proxy<IMainDatabase>((method, args) =>
                    method.Name == nameof(IMainDatabase.OpenConnection)
                        ? CommandObserver.Wrap(database.OpenConnection(), command =>
                          { if (command.CommandText.StartsWith("INSERT INTO")) writes++; }, includeWrites: true)
                        : method.Invoke(database, args));
                var queue = new PendingNotificationRepository(observed, Proxy<IEventAggregator>((_, _) => null), LogManager.GetCurrentClassLogger());
                var paths = Enumerable.Range(0, 100).Select(index => $"/books/Dune/part-{index}.mp3").ToArray();
                queue.Enqueue(1, paths.Concat(paths));
                Assert.That(writes, Is.EqualTo(1));
                Assert.That(queue.GetPending(1).Select(item => item.Path), Is.EquivalentTo(paths));
                queue.Enqueue(1, paths);
                Assert.That(queue.GetPending(1).ToList(), Has.Count.EqualTo(100));
                queue.Enqueue(2, paths);
                queue.Complete(queue.GetPending(1));
                Assert.That(queue.HasPending(1), Is.False);
                Assert.That(queue.GetPending(2).ToList(), Has.Count.EqualTo(100));
            });
        }

        [Test]
        public void keyset_drain_keeps_all_work_when_completed_rows_disappear([Values] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var reads = new List<string>();
                var observed = Proxy<IMainDatabase>((method, args) => method.Name == nameof(IMainDatabase.OpenConnection)
                    ? CommandObserver.Wrap(database.OpenConnection(), command =>
                    {
                        if (command.CommandText.StartsWith("SELECT * FROM \"PendingNotificationItems\"")) reads.Add(command.CommandText);
                    }) : method.Invoke(database, args));
                var queue = new PendingNotificationRepository(observed, Proxy<IEventAggregator>((_, _) => null), LogManager.GetCurrentClassLogger());
                var paths = Enumerable.Range(0, 1101).Select(i => $"/books/Book-{i}/metadata.json").ToArray();
                queue.Enqueue(1, paths);
                queue.Enqueue(2, paths);
                var processed = new List<string>();
                foreach (var batch in queue.GetPending(1).Chunk(137))
                {
                    processed.AddRange(batch.Select(item => item.Path));
                    queue.Complete(batch);
                }
                Assert.That(processed, Is.EquivalentTo(paths));
                Assert.That(reads, Has.Count.EqualTo(3), "Three bounded reads, with no offset skipping after deletes");
                Assert.That(queue.HasPending(1), Is.False);
                Assert.That(queue.GetPending(2).Count(), Is.EqualTo(paths.Length));
            });
        }

        [Test]
        public void long_paths_keep_their_bytes_without_an_oversized_index_key([Values] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var queue = new PendingNotificationRepository(database, Proxy<IEventAggregator>((_, _) => null), LogManager.GetCurrentClassLogger());
                var path = "/books/" + string.Join("/", Enumerable.Range(0, 100).Select(_ => Guid.NewGuid().ToString("N"))) + "/Book.epub";
                queue.Enqueue(1, path);
                queue.Enqueue(1, path);
                queue.Enqueue(1, path + ".copy");
                var saved = queue.GetPending(1).ToList();
                Assert.That(saved.Select(item => item.Path), Is.EquivalentTo(new[] { path, path + ".copy" }));
                Assert.That(saved.All(item => item.PathKey.Length == 64), Is.True);
                Assert.That(saved[0].PathKey, Is.Not.EqualTo(saved[1].PathKey));
            });
        }

        [Test]
        public void synology_keeps_folder_coalescing_across_pages_and_retries([Values] bool postgres, [Values] bool fail)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var queue = new PendingNotificationRepository(database, Proxy<IEventAggregator>((_, _) => null), LogManager.GetCurrentClassLogger());
                var folder = @"C:\books\Author".AsOsAgnostic();
                queue.Enqueue(7, Enumerable.Range(0, 1101).Select(i => Path.Combine(folder, "Book-" + i + ".epub")));
                queue.Enqueue(7, folder, true);
                var unrelated = @"C:\books\Other\Book.epub".AsOsAgnostic();
                queue.Enqueue(7, unrelated);
                var deletedFolders = new List<string>();
                var deletedFiles = new List<string>();
                var throwOnce = fail;
                var provider = new SynologyIndexer(Proxy<ISynologyIndexerProxy>((method, args) =>
                {
                    if (method.Name == nameof(ISynologyIndexerProxy.DeleteFolder)) deletedFolders.Add((string)args[0]);
                    else if (method.Name == nameof(ISynologyIndexerProxy.DeleteFile))
                    {
                        if (throwOnce) { throwOnce = false; throw new IOException("Synthetic indexer failure"); }
                        deletedFiles.Add((string)args[0]);
                    }
                    else throw new AssertionException(method.Name);
                    return null;
                }), Proxy<IDiskProvider>((_, _) => false), new CacheManager(), queue)
                { Definition = new NotificationDefinition { Id = 7, Settings = new SynologyIndexerSettings { UpdateLibrary = true } } };
                if (fail)
                {
                    Assert.Throws<IOException>(() => provider.ProcessQueue());
                    Assert.That(queue.GetPending(7).Select(item => item.Path), Is.EquivalentTo(new[] { folder, unrelated }));
                }
                provider.ProcessQueue();
                Assert.That(deletedFolders, Is.EqualTo(Enumerable.Repeat(folder, fail ? 2 : 1)));
                Assert.That(deletedFiles, Is.EqualTo(new[] { unrelated }));
                Assert.That(queue.HasPending(7), Is.False);
            });
        }

        [Test]
        public void restart_drains_saved_batches_and_retains_failed_work(
            [Values(false, true)] bool postgres, [Values("kavita", "subsonic", "synology")] string kind)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var events = Proxy<IEventAggregator>((_, _) => null);
                var queue = new PendingNotificationRepository(database, events, LogManager.GetCurrentClassLogger());
                var calls = 0;
                var fail = false;
                object Send()
                {
                    calls++;
                    if (fail) throw new IOException("Synthetic connection failure");
                    return null;
                }
                INotification Create()
                {
                    // New cache and repository emulate a restart; no old batch memory is reused.
                    var pending = new PendingNotificationRepository(database, events, LogManager.GetCurrentClassLogger());
                    var cache = new CacheManager();
                    INotification provider = kind switch
                    {
                        "kavita" => new Kavita(Proxy<IKavitaService>((_, _) => Send()), cache, pending)
                        { Definition = new NotificationDefinition { Id = 7, Settings = new KavitaSettings { Notify = true } } },
                        "subsonic" => new Subsonic(Proxy<ISubsonicService>((_, _) => Send()), cache, pending)
                        { Definition = new NotificationDefinition { Id = 7, Settings = new SubsonicSettings { UpdateLibrary = true } } },
                        _ => new SynologyIndexer(Proxy<ISynologyIndexerProxy>((_, _) => Send()), Proxy<IDiskProvider>((_, _) => false), cache, pending)
                        { Definition = new NotificationDefinition { Id = 7, Settings = new SynologyIndexerSettings { UpdateLibrary = true } } }
                    };
                    return provider;
                }
                void Enqueue(INotification provider)
                {
                    if (kind == "subsonic") provider.OnRename(new Author(), new());
                    else provider.OnBookFileDelete(new BookFileDeleteMessage { BookFile = new BookFile { Path = (@"C:\library\Dune\" + new string('x', 120) + @"\" + new string('y', 120) + @"\Dune.epub").AsOsAgnostic() } });
                }
                var original = Create();
                Enqueue(original);
                Enqueue(original);
                Assert.That(queue.GetPending(7).ToList(), Has.Count.EqualTo(1));
                Assert.That(calls, Is.Zero);
                var failures = 0;
                NotificationService Service(INotification provider) => new(
                    Proxy<INotificationFactory>((_, _) => new List<INotification> { provider }),
                    Proxy<INotificationStatusService>((method, _) =>
                    {
                        if (method.Name == nameof(INotificationStatusService.GetBlockedProviders)) return new List<NotificationStatus>();
                        if (method.Name == nameof(INotificationStatusService.RecordFailure)) failures++;
                        return null;
                    }), null, LogManager.GetCurrentClassLogger());
                fail = true;
                Service(Create()).HandleAsync(new ApplicationStartedEvent());
                Assert.That(queue.HasPending(7), Is.True);
                Assert.That(failures, Is.EqualTo(1));
                fail = false;
                Service(Create()).HandleAsync(new ApplicationStartedEvent());
                Assert.That(calls, Is.EqualTo(2));
                Assert.That(queue.HasPending(7), Is.False);
                Service(Create()).HandleAsync(new ApplicationStartedEvent());
                Assert.That(calls, Is.EqualTo(2), "Successful work is not replayed on the next restart");
                Enqueue(Create());
                queue.Handle(new ProviderDeletedEvent<INotification>(7));
                Assert.That(queue.HasPending(7), Is.False, "Removing a connection removes its queued work");
            });
        }
    }
}
