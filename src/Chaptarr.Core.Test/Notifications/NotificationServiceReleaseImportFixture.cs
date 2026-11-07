using System;
using System.Collections.Generic;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Notifications;

namespace Chaptarr.Core.Test.Notifications
{
    [TestFixture]
    public class NotificationServiceReleaseImportFixture
    {
        private class NotificationFactoryProxy : DispatchProxy
        {
            public List<INotification> Notifications { get; set; } = new List<INotification>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    nameof(INotificationFactory.OnReleaseImportEnabled) => Notifications,
                    "GetAvailableProviders" => Notifications,
                    _ => throw new NotImplementedException($"Test proxy does not implement INotificationFactory.{targetMethod?.Name}")
                };
            }
        }

        private class NotificationProxy : DispatchProxy
        {
            public NotificationDefinition Definition { get; set; }
            public BookDownloadMessage ReleaseImportMessage { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case "get_Definition":
                        return Definition;
                    case "set_Definition":
                        Definition = (NotificationDefinition)args[0];
                        return null;
                    case "get_NotifyOnLibraryImports":
                    case "get_HasPendingQueue":
                        return false;
                    case nameof(INotification.OnReleaseImport):
                        ReleaseImportMessage = (BookDownloadMessage)args[0];
                        return null;
                    case "ProcessQueue":
                        return null;
                    default:
                        throw new NotImplementedException($"Test proxy does not implement INotification.{targetMethod?.Name}");
                }
            }
        }

        private class NotificationStatusServiceProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name == "GetBlockedProviders"
                    ? new List<NotificationStatus>()
                    : null;
            }
        }

        private class ThrowingProxy<T> : DispatchProxy where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        [Test]
        public void should_dispatch_release_import_for_a_tracked_download()
        {
            var notification = DispatchProxy.Create<INotification, NotificationProxy>();
            var notificationRecorder = (NotificationProxy)(object)notification;
            notificationRecorder.Definition = new NotificationDefinition
            {
                Id = 17,
                Name = "Import webhook",
                OnReleaseImport = true
            };

            var factory = DispatchProxy.Create<INotificationFactory, NotificationFactoryProxy>();
            ((NotificationFactoryProxy)(object)factory).Notifications = new List<INotification> { notification };

            var service = new NotificationService(
                factory,
                DispatchProxy.Create<INotificationStatusService, NotificationStatusServiceProxy>(),
                DispatchProxy.Create<IEditionService, ThrowingProxy<IEditionService>>(),
                null,
                LogManager.GetCurrentClassLogger());

            var author = new Author { Id = 1, Name = "Novel Author" };
            var book = new Book { Id = 2, Title = "Imported Novel", Author = author };
            var importedFile = new BookFile { Id = 3, Path = "/books/imported-novel.epub" };
            var imported = new BookImportedEvent(
                author,
                book,
                new List<BookFile> { importedFile },
                new List<BookFile>(),
                true,
                new DownloadClientItem { DownloadId = "download-17" });

            service.Handle(imported);

            Assert.That(notificationRecorder.ReleaseImportMessage, Is.Not.Null);
            Assert.That(notificationRecorder.ReleaseImportMessage.Book, Is.SameAs(book));
            Assert.That(notificationRecorder.ReleaseImportMessage.DownloadId, Is.EqualTo("download-17"));
        }
    }
}
