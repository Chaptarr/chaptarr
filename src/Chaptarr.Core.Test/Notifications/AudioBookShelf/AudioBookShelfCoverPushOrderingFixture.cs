using System;
using System.Reflection;
using NUnit.Framework;
using NzbDrone.Core.Extras;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.Messaging;
using NzbDrone.Core.Notifications.AudioBookShelf;

namespace Chaptarr.Core.Test.Notifications.AudioBookShelf
{
    [TestFixture]
    public class AudioBookShelfCoverPushOrderingFixture
    {
        [Test]
        public void cover_push_should_run_after_the_folder_cover_is_written()
        {
            Assert.Multiple(() =>
            {
                Assert.That(OrderFor(typeof(ExtraService)), Is.EqualTo(EventHandleOrder.Any));
                Assert.That(OrderFor(typeof(AudioBookShelfLibraryEditService)), Is.EqualTo(EventHandleOrder.Last));
            });
        }

        private static EventHandleOrder OrderFor(Type handlerType)
        {
            var method = handlerType.GetMethod("Handle", new[] { typeof(MediaCoversUpdatedEvent) });
            Assert.That(method, Is.Not.Null, $"{handlerType.Name} must handle {nameof(MediaCoversUpdatedEvent)}");

            return method.GetCustomAttribute<EventHandleOrderAttribute>()?.EventHandleOrder
                   ?? EventHandleOrder.Any;
        }
    }
}
