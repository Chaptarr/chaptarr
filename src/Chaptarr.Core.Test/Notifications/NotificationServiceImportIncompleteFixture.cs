using System;
using System.Collections.Generic;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Notifications;

namespace Chaptarr.Core.Test.Notifications
{
    [TestFixture]
    public class NotificationServiceImportIncompleteFixture
    {
        private class ThrowingProxy<T> : DispatchProxy where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private class EmptyImportFailureNotificationFactoryProxy : DispatchProxy
        {
            public List<INotification> Notifications { get; set; } = new List<INotification>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (string.Equals(targetMethod?.Name, nameof(INotificationFactory.OnImportFailureEnabled), StringComparison.Ordinal))
                {
                    return Notifications;
                }

                throw new NotImplementedException($"Test proxy does not implement INotificationFactory.{targetMethod?.Name}");
            }
        }

        private class RecordingImportFailureNotificationProxy : DispatchProxy
        {
            public NotificationDefinition Definition { get; set; }
            public BookDownloadMessage ImportFailureMessage { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (string.Equals(targetMethod?.Name, "get_Definition", StringComparison.Ordinal))
                {
                    return Definition;
                }

                if (string.Equals(targetMethod?.Name, "set_Definition", StringComparison.Ordinal))
                {
                    Definition = (NotificationDefinition)args[0];
                    return null;
                }

                if (string.Equals(targetMethod?.Name, nameof(INotification.OnImportFailure), StringComparison.Ordinal))
                {
                    ImportFailureMessage = (BookDownloadMessage)args[0];
                    return null;
                }

                throw new NotImplementedException($"Test proxy does not implement INotification.{targetMethod?.Name}");
            }
        }

        private class NoOpNotificationStatusServiceProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args) => null;
        }

        private static NotificationService CreateSut(List<INotification> notifications)
        {
            var factory = DispatchProxy.Create<INotificationFactory, EmptyImportFailureNotificationFactoryProxy>();
            ((EmptyImportFailureNotificationFactoryProxy)(object)factory).Notifications = notifications;

            return new NotificationService(
                factory,
                DispatchProxy.Create<INotificationStatusService, NoOpNotificationStatusServiceProxy>(),
                DispatchProxy.Create<IEditionService, ThrowingProxy<IEditionService>>(),
                null,
                LogManager.GetCurrentClassLogger());
        }

        [Test]
        public void should_send_generic_notification_when_download_title_and_author_are_missing()
        {
            var notification = DispatchProxy.Create<INotification, RecordingImportFailureNotificationProxy>();
            var recorder = (RecordingImportFailureNotificationProxy)(object)notification;
            recorder.Definition = new NotificationDefinition { Id = 1, Name = "Import failures" };
            var service = CreateSut(new List<INotification> { notification });

            var trackedDownload = new TrackedDownload
            {
                DownloadItem = null,
                RemoteBook = null
            };

            Assert.DoesNotThrow(() => service.Handle(new BookImportIncompleteEvent(trackedDownload)));
            Assert.That(recorder.ImportFailureMessage.Message, Is.EqualTo("Chaptarr failed to import all files for an unknown download"));
        }

        [Test]
        public void should_skip_author_tagged_notification_when_import_author_is_missing()
        {
            var notification = DispatchProxy.Create<INotification, RecordingImportFailureNotificationProxy>();
            var recorder = (RecordingImportFailureNotificationProxy)(object)notification;
            recorder.Definition = new NotificationDefinition { Id = 1, Name = "Tagged import failures", Tags = new HashSet<int> { 17 } };
            var service = CreateSut(new List<INotification> { notification });

            var trackedDownload = new TrackedDownload
            {
                DownloadItem = null,
                RemoteBook = null
            };

            Assert.DoesNotThrow(() => service.Handle(new BookImportIncompleteEvent(trackedDownload)));
            Assert.That(recorder.ImportFailureMessage, Is.Null);
        }
    }
}
