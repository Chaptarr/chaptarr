using System.Collections.Generic;
using System.IO;
using Chaptarr.Core.Test.MediaFiles;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Disk;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.Synology;

namespace Chaptarr.Core.Test.Notifications
{
    [TestFixture]
    public class SynologyDeletionFixture
    {
        [Test]
        public void deletion_only_removes_index_entries_when_files_were_deleted(
            [Values(false, true)] bool authorDeletion,
            [Values(false, true)] bool deleteFiles,
            [Values(false, true)] bool updateLibrary)
        {
            var calls = new List<(string Method, string Path)>();
            var statusCalls = new List<string>();
            var author = new Author { Name = "Author", Path = Path.GetFullPath("library/Author") };
            var file = new BookFile { Path = Path.Combine(author.Path, "Book.m4b") };
            var notification = new SynologyIndexer(AuthorDeletionSafetyFixture.Proxy<ISynologyIndexerProxy>((method, args) =>
            {
                calls.Add((method.Name, (string)args[0]));
                return null;
            }), AuthorDeletionSafetyFixture.Proxy<IDiskProvider>((_, _) => false), new CacheManager(), new Chaptarr.Core.Test.Common.MemoryPendingNotifications())
            {
                Definition = new NotificationDefinition { Settings = new SynologyIndexerSettings { UpdateLibrary = updateLibrary } }
            };
            var service = new NotificationService(
                AuthorDeletionSafetyFixture.Proxy<INotificationFactory>((_, _) => new List<INotification> { notification }),
                AuthorDeletionSafetyFixture.Proxy<INotificationStatusService>((method, _) =>
                {
                    if (method.Name == nameof(INotificationStatusService.GetBlockedProviders)) return new List<NotificationStatus>();
                    statusCalls.Add(method.Name);
                    return null;
                }), null, LogManager.GetCurrentClassLogger());

            if (authorDeletion)
            {
                service.Handle(new AuthorDeletedEvent(author, deleteFiles, false));
            }
            else
            {
                var book = new Book { Author = author, Title = "Book", BookFiles = new List<BookFile> { file } };
                service.Handle(new BookDeletedEvent(book, deleteFiles, false));
            }

            Assert.That(calls, Is.Empty);
            service.HandleAsync(new DeleteCompletedEvent());

            if (deleteFiles && updateLibrary)
            {
                Assert.That(calls, Is.EqualTo(new[]
                {
                    authorDeletion
                        ? (nameof(ISynologyIndexerProxy.DeleteFolder), author.Path)
                        : (nameof(ISynologyIndexerProxy.DeleteFile), file.Path)
                }));
            }
            else
            {
                Assert.That(calls, Is.Empty);
            }

            Assert.That(statusCalls, Is.EqualTo(new[] { nameof(INotificationStatusService.RecordSuccess) }));
        }
    }
}
