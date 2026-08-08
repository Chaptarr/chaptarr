using System;
using System.Reflection;
using Chaptarr.Api.V1.Author;
using NUnit.Framework;
using NzbDrone.Core.AuthorStats;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.AuthorStats
{
    [TestFixture]
    public class ImportTerminalEventOrderingFixture
    {
        [TestCase(typeof(ImportStageProgressEvent))]
        [TestCase(typeof(CommandExecutedEvent))]
        public void terminal_handlers_should_close_lifecycle_then_invalidate_statistics_then_broadcast_sync(Type eventType)
        {
            Assert.Multiple(() =>
            {
                Assert.That(OrderFor(typeof(ImportSessionProgressCleanupHandler), eventType), Is.EqualTo(EventHandleOrder.First));
                Assert.That(OrderFor(typeof(AuthorStatisticsService), eventType), Is.EqualTo(EventHandleOrder.Any));
                Assert.That(OrderFor(typeof(AuthorController), eventType), Is.EqualTo(EventHandleOrder.Last));
            });
        }

        private static EventHandleOrder OrderFor(Type handlerType, Type eventType)
        {
            var method = handlerType.GetMethod("Handle", new[] { eventType });
            Assert.That(method, Is.Not.Null, $"{handlerType.Name} must handle {eventType.Name}");

            return method.GetCustomAttribute<EventHandleOrderAttribute>()?.EventHandleOrder
                   ?? EventHandleOrder.Any;
        }
    }
}
