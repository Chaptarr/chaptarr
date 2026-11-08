using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Chaptarr.Core.Test.MediaFiles;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.Subsonic;

namespace Chaptarr.Core.Test.Notifications
{
    [TestFixture]
    public class SubsonicQueueFixture
    {
        [Test]
        public void batches_scans_across_provider_instances_and_keeps_definitions_separate()
        {
            var cache = new CacheManager();
            var updates = 0;
            var service = AuthorDeletionSafetyFixture.Proxy<ISubsonicService>((method, _) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(ISubsonicService.Update)));
                updates++;
                return null;
            });
            var book = new Book { Title = "Book" };
            Create(service, cache, 1).OnBookDelete(new BookDeleteMessage(book, true));
            Create(service, cache, 1).OnBookDelete(new BookDeleteMessage(book, true));
            Create(service, cache, 1).OnAuthorDelete(new AuthorDeleteMessage(new Author(), true));
            Assert.That(updates, Is.Zero, "The physical deletion batch must finish before scanning");
            Create(service, cache, 2).ProcessQueue();
            Assert.That(updates, Is.Zero);
            Parallel.Invoke(Create(service, cache, 1).ProcessQueue, Create(service, cache, 1).ProcessQueue);
            Assert.That(updates, Is.EqualTo(1));
            Assert.That(Create(service, cache, 1).HasPendingQueue, Is.False);
        }

        [Test]
        public void failed_scan_is_visible_and_can_be_drained_again()
        {
            var cache = new CacheManager();
            var failed = true;
            var updates = 0;
            var service = AuthorDeletionSafetyFixture.Proxy<ISubsonicService>((_, _) =>
            {
                updates++;
                if (failed) throw new IOException("Synthetic scan failure");
                return null;
            });
            var notification = Create(service, cache, 1);
            notification.OnRename(new Author(), new());
            Assert.Throws<IOException>(notification.ProcessQueue);
            Assert.That(notification.HasPendingQueue, Is.True);
            failed = false;
            Create(service, cache, 1).ProcessQueue();
            Assert.That(updates, Is.EqualTo(2));
            Assert.That(notification.HasPendingQueue, Is.False);
        }

        [TestCase(false, true)]
        [TestCase(true, false)]
        public void does_not_scan_when_files_are_kept_or_library_updates_are_disabled(bool deletedFiles, bool updateLibrary)
        {
            var service = AuthorDeletionSafetyFixture.Proxy<ISubsonicService>((_, _) => throw new AssertionException("Unexpected scan"));
            var notification = Create(service, new CacheManager(), 1);
            ((SubsonicSettings)notification.Definition.Settings).UpdateLibrary = updateLibrary;
            notification.OnBookDelete(new BookDeleteMessage(new Book(), deletedFiles));
            notification.ProcessQueue();
            Assert.That(notification.HasPendingQueue, Is.False);
        }

        private static Subsonic Create(ISubsonicService service, ICacheManager cache, int id) => new Subsonic(service, cache, Chaptarr.Core.Test.Common.MemoryPendingNotifications.For(cache))
        {
            Definition = new NotificationDefinition { Id = id, Settings = new SubsonicSettings { Notify = false, UpdateLibrary = true } }
        };
    }
}
