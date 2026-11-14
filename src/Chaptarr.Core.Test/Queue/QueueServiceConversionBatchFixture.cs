using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Chaptarr.Core.Test.Common;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Queue;

namespace Chaptarr.Core.Test.Queue
{
    [TestFixture]
    public class QueueServiceConversionBatchFixture
    {
        [Test]
        public void queue_conversion_status_uses_one_batch_and_preserves_other_books_in_a_pack()
        {
            var reads = 0;
            var selectedIds = new List<string>();
            var jobs = InterfaceProxy.Proxy<IConversionJobService>((method, args) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(IConversionJobService.Get)));
                Assert.That(args[0], Is.InstanceOf<IEnumerable<string>>(), "Per-row conversion reads must not return");
                reads++;
                selectedIds = ((IEnumerable<string>)args[0]).Distinct().ToList();
                return new List<ConversionJob>
                {
                    new() { DownloadId = "pack", Status = ConversionJobStatus.Failed, Message = "Book A failed", RequestJson = JsonSerializer.Serialize(new ConversionJobRequest { BookId = 11 }) }
                };
            });
            var tracking = new ConversionTrackingService(InterfaceProxy.Proxy<IEventAggregator>((method, args) => null));
            tracking.Fail("pack", "Book A failed");
            var service = new QueueService(null, null, null, tracking, conversionJobService: jobs);
            var queue = Enumerable.Range(0, 500).Select(index => new NzbDrone.Core.Queue.Queue
            {
                DownloadId = "download-" + index, Book = new Book { Id = index + 100 }
            }).ToList();
            queue.Add(new NzbDrone.Core.Queue.Queue { DownloadId = "pack", Book = new Book { Id = 11 } });
            queue.Add(new NzbDrone.Core.Queue.Queue { DownloadId = "pack", Book = new Book { Id = 12 }, ConversionMessage = "Waiting for Book B" });
            typeof(QueueService).GetMethod("ApplyConversionStatuses", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(service, new object[] { queue });
            Assert.That(reads, Is.EqualTo(1));
            Assert.That(selectedIds.Count, Is.EqualTo(501));
            Assert.That(queue[^2].ConversionStatus, Is.EqualTo("failed"));
            Assert.That(queue[^1].ConversionStatus, Is.Null);
            Assert.That(queue[^1].ConversionMessage, Is.EqualTo("Waiting for Book B"));
            TestContext.Progress.WriteLine("Conversion queue: 502 book rows, 501 downloads, 1 bulk service read, no per-row job reads.");
        }
    }
}
