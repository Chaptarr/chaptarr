using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Notifications;

namespace Chaptarr.Core.Test.Notifications
{
    // A book delete published as part of a larger author delete already gets a single
    // OnAuthorDelete notification for the whole author. Sending OnBookDelete on top of that for
    // every one of its books is the "one notification per episode when the whole series was
    // deleted" noise Sonarr/Radarr deliberately don't send.
    [TestFixture]
    public class NotificationServiceBookDeletedFixture
    {
        private class ThrowingProxy<T> : DispatchProxy where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private class EmptyOnBookDeleteNotificationFactoryProxy : DispatchProxy
        {
            private int _calls;
            public int OnBookDeleteEnabledCalls => Volatile.Read(ref _calls);

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (string.Equals(targetMethod?.Name, nameof(INotificationFactory.OnBookDeleteEnabled), StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref _calls);
                    return new List<INotification>();
                }

                throw new NotImplementedException($"Test proxy does not implement INotificationFactory.{targetMethod?.Name}");
            }
        }

        [Test]
        public void should_not_look_up_notifications_at_all_when_part_of_an_author_delete()
        {
            // A throwing INotificationFactory proves OnBookDeleteEnabled is never even called -
            // not just that no notification happens to fire.
            var service = new NotificationService(
                DispatchProxy.Create<INotificationFactory, ThrowingProxy<INotificationFactory>>(),
                DispatchProxy.Create<INotificationStatusService, ThrowingProxy<INotificationStatusService>>(),
                DispatchProxy.Create<IEditionService, ThrowingProxy<IEditionService>>(),
                null,
                LogManager.GetCurrentClassLogger());

            var book = new Book { Id = 1, Author = new Author { Id = 1, Name = "Jim Butcher" } };

            Assert.DoesNotThrow(() =>
                service.HandleAsync(new BookDeletedEvent(book, deleteFiles: true, addImportListExclusion: false, partOfAuthorDelete: true)));
        }

        [Test]
        public void should_still_look_up_notifications_for_a_standalone_book_delete()
        {
            var factory = DispatchProxy.Create<INotificationFactory, EmptyOnBookDeleteNotificationFactoryProxy>();
            var factoryRecorder = (EmptyOnBookDeleteNotificationFactoryProxy)(object)factory;

            var service = new NotificationService(
                factory,
                DispatchProxy.Create<INotificationStatusService, ThrowingProxy<INotificationStatusService>>(),
                DispatchProxy.Create<IEditionService, ThrowingProxy<IEditionService>>(),
                null,
                LogManager.GetCurrentClassLogger());

            var book = new Book { Id = 1, Author = new Author { Id = 1, Name = "Jim Butcher" } };

            service.HandleAsync(new BookDeletedEvent(book, deleteFiles: true, addImportListExclusion: false, partOfAuthorDelete: false));

            Assert.That(SpinWait.SpinUntil(() => factoryRecorder.OnBookDeleteEnabledCalls == 1, TimeSpan.FromSeconds(5)), Is.True);
        }
    }
}
