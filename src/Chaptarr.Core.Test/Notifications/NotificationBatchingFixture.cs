using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.Kavita;
using NzbDrone.Core.Notifications.Synology;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Notifications
{
    [TestFixture]
    public class NotificationBatchingFixture
    {
        private CacheManager _cache;
        private string _root;
        private List<(int Id, string Action, string Path)> _calls;
        private string _failPath;

        [SetUp]
        public void SetUp()
        {
            _cache = new CacheManager();
            _root = Path.Combine(Path.GetTempPath(), "chaptarr-notification-batch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _calls = new();
            _failPath = null;
        }

        [TearDown]
        public void TearDown() => Directory.Delete(_root, true);

        [TestCase(false)]
        [TestCase(true)]
        public void bulk_delete_defers_external_work_and_coalesces_redundant_requests(bool synology)
        {
            var folder = Path.Combine(_root, "Author");
            var author = new Author { Name = "Author", Path = folder };
            var service = CreateService(synology);
            for (var index = 0; index < 40; index++)
            {
                var file = new BookFile { Path = Path.Combine(folder, $"Book {index}.epub") };
                service.Handle(new BookDeletedEvent(new Book { Author = author, Title = $"Book {index}", BookFiles = new() { file } }, false, false, filesAlreadyDeleted: true));
            }

            if (synology) service.Handle(new AuthorDeletedEvent(author, true, false));
            Assert.That(_calls, Is.Empty, "External updates wait for deletion completion");
            service.HandleAsync(new DeleteCompletedEvent());
            Assert.That(_calls, Is.EqualTo(new[] { (1, synology ? "DeleteFolder" : "Notify", folder) }));
            Assert.That(Create(synology).HasPendingQueue, Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void distinct_paths_and_definitions_are_preserved_and_parallel_flushes_do_not_duplicate(bool synology)
        {
            var paths = new[] { "Author/Book One/part.epub", "Author/Book Two/part.epub", "Author Two/part.epub" }
                .Select(path => Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar))).ToArray();
            foreach (var path in paths)
            {
                DeleteBook(Create(synology), path);
                DeleteBook(Create(synology), path);
            }

            DeleteBook(Create(synology, 2), paths[0]);
            Parallel.Invoke(Create(synology).ProcessQueue, Create(synology).ProcessQueue);
            Assert.That(_calls, Is.EquivalentTo(paths.Select(path => (1, synology ? "DeleteFile" : "Notify", synology ? path : Path.GetDirectoryName(path)))));
            Assert.That(Create(synology, 2).HasPendingQueue, Is.True);
            Create(synology, 2).ProcessQueue();
            Assert.That(_calls.Last().Id, Is.EqualTo(2));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void failed_work_remains_queued_without_repeating_completed_work(bool synology)
        {
            var first = Path.Combine(_root, "First", "book.epub");
            var second = Path.Combine(_root, "Second", "book.epub");
            DeleteBook(Create(synology), first);
            DeleteBook(Create(synology), second);
            _failPath = synology ? second : Path.GetDirectoryName(second);
            Assert.Throws<IOException>(Create(synology).ProcessQueue);
            Assert.That(Create(synology).HasPendingQueue, Is.True);
            _failPath = null;
            Create(synology).ProcessQueue();
            Assert.That(_calls.Count(call => call.Path == (synology ? first : Path.GetDirectoryName(first))), Is.EqualTo(1));
            Assert.That(_calls.Count, Is.EqualTo(3));
            Assert.That(Create(synology).HasPendingQueue, Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void disabled_or_keep_files_events_do_not_schedule_updates(bool synology)
        {
            var file = Path.Combine(_root, "book.epub");
            DeleteBook(Create(synology), file, false);
            DeleteBook(Create(synology, enabled: false), file);
            Create(synology).ProcessQueue();
            Assert.That(_calls, Is.Empty);
            DeleteBook(Create(synology), file);
            Create(synology, enabled: false).ProcessQueue();
            Assert.That(_calls, Is.Empty);
            Assert.That(Create(synology).HasPendingQueue, Is.False);
        }

        [Test]
        public void synology_keeps_retained_folder_and_only_removes_selected_file_entries()
        {
            var folder = Path.Combine(_root, "Author");
            Directory.CreateDirectory(folder);
            var retained = Path.Combine(folder, "Book.epub");
            File.WriteAllText(retained, "Retained ebook");
            var deleted = Path.Combine(folder, "Book.m4b");
            DeleteBook(Create(true), deleted);
            Create(true).OnAuthorDelete(new AuthorDeleteMessage(new Author { Path = folder }, true));
            Create(true).ProcessQueue();
            Assert.That(_calls, Is.EqualTo(new[] { (1, "DeleteFile", deleted) }));
            Assert.That(File.ReadAllText(retained), Is.EqualTo("Retained ebook"));
        }

        [Test]
        public void synology_failed_parent_removal_keeps_child_work_and_does_not_cover_a_similarly_named_folder()
        {
            var folder = Path.Combine(_root, "Author");
            var outside = Path.Combine(_root, "Author Two", "Book.m4b");
            DeleteBook(Create(true), Path.Combine(folder, "Book", "Part 01.m4b"));
            DeleteBook(Create(true), outside);
            Create(true).OnAuthorDelete(new AuthorDeleteMessage(new Author { Path = folder }, true));
            _failPath = folder;
            Assert.Throws<IOException>(Create(true).ProcessQueue);
            Assert.That(Create(true).HasPendingQueue, Is.True);
            _failPath = null;
            Create(true).ProcessQueue();
            Assert.That(_calls.Count(call => call.Action == "DeleteFolder"), Is.EqualTo(2));
            Assert.That(_calls.Where(call => call.Action == "DeleteFile").Select(call => call.Path), Is.EqualTo(new[] { outside }));
        }

        [Test]
        public void synology_retry_does_not_unindex_a_new_file_at_the_same_path()
        {
            var path = Path.Combine(_root, "Book.m4b");
            DeleteBook(Create(true), path);
            _failPath = path;
            Assert.Throws<IOException>(Create(true).ProcessQueue);
            File.WriteAllText(path, "Newly imported recording");
            _failPath = null;
            Create(true).ProcessQueue();
            Assert.That(_calls, Has.Count.EqualTo(1));
            Assert.That(Create(true).HasPendingQueue, Is.False);
            Assert.That(File.ReadAllText(path), Is.EqualTo("Newly imported recording"));
        }

        private INotification Create(bool synology, int id = 1, bool enabled = true)
        {
            if (synology)
            {
                return new SynologyIndexer(Proxy<ISynologyIndexerProxy>((method, args) => Record(id, method.Name, (string)args[0])),
                    Proxy<IDiskProvider>((method, args) => method.Name switch
                    {
                        nameof(IDiskProvider.FolderExists) => Directory.Exists((string)args[0]),
                        nameof(IDiskProvider.FileExists) => File.Exists((string)args[0]),
                        _ => throw new NotImplementedException(method.Name)
                    }), _cache, Chaptarr.Core.Test.Common.MemoryPendingNotifications.For(_cache))
                {
                    Definition = new NotificationDefinition { Id = id, Settings = new SynologyIndexerSettings { UpdateLibrary = enabled } }
                };
            }

            return new Kavita(Proxy<IKavitaService>((method, args) => Record(id, method.Name, (string)args[1])), _cache, Chaptarr.Core.Test.Common.MemoryPendingNotifications.For(_cache))
            {
                Definition = new NotificationDefinition { Id = id, Settings = new KavitaSettings { Notify = enabled } }
            };
        }

        private object Record(int id, string action, string path)
        {
            _calls.Add((id, action, path));
            if (path == _failPath) throw new IOException("Synthetic notification failure");
            return null;
        }

        private NotificationService CreateService(bool synology) => new(
            Proxy<INotificationFactory>((_, _) => new List<INotification> { Create(synology) }),
            Proxy<INotificationStatusService>((method, _) => method.Name == nameof(INotificationStatusService.GetBlockedProviders)
                ? new List<NotificationStatus>() : null), null, LogManager.GetCurrentClassLogger());

        private static void DeleteBook(INotification notification, string path, bool deleteFiles = true) =>
            notification.OnBookDelete(new BookDeleteMessage(new Book { BookFiles = new() { new BookFile { Path = path } } }, deleteFiles));
    }
}
