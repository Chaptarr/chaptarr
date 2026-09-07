using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Queue;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.Queue
{
    [TestFixture]
    public class DirectQueueProtocolFixture
    {
        private sealed class RecordingEventAggregator : IEventAggregator
        {
            public List<IEvent> Events { get; } = new();

            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
                Events.Add(@event);
            }
        }

        private class HistoryServiceProxy : DispatchProxy
        {
            public List<EntityHistory> FindResult { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IHistoryService.Find) && args?.Length == 2)
                {
                    return FindResult;
                }

                if (targetMethod?.Name == nameof(IHistoryService.FindByDownloadIds) && args?.Length == 2)
                {
                    return FindResult;
                }

                throw new NotImplementedException($"Test proxy does not implement {targetMethod?.DeclaringType?.Name}.{targetMethod?.Name}");
            }
        }

        [Test]
        public void should_surface_direct_protocol_and_grab_history_book_in_queue()
        {
            var eventAggregator = new RecordingEventAggregator();
            var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
            ((HistoryServiceProxy)(object)historyService).FindResult = new List<EntityHistory>
            {
                new()
                {
                    EventType = EntityHistoryEventType.Grabbed,
                    DownloadId = "direct-queue-1",
                    Date = new DateTime(2026, 08, 10, 12, 00, 00, DateTimeKind.Utc),
                    Author = new Author { Id = 77, Name = "Lois McMaster Bujold" },
                    Book = new Book { Id = 501, Title = "A Civil Campaign", MediaType = BookMediaType.Ebook },
                    Data = new Dictionary<string, string>
                    {
                        [EntityHistory.INDEXER] = "Direct Download",
                        ["DownloadForced"] = bool.TrueString
                    }
                }
            };

            var service = new QueueService(eventAggregator, historyService);
            service.Handle(new TrackedDownloadRefreshedEvent(new List<TrackedDownload>
            {
                new()
                {
                    DownloadClient = 7,
                    Protocol = DownloadProtocol.Direct,
                    IsTrackable = true,
                    DownloadItem = new DownloadClientItem
                    {
                        DownloadId = "direct-queue-1",
                        Title = "Lois McMaster Bujold - A Civil Campaign [epub]",
                        Status = DownloadItemStatus.Completed,
                        OutputPath = new OsPath("/downloads/direct/A Civil Campaign.epub"),
                        DownloadClientInfo = new DownloadClientItemClientInfo
                        {
                            Name = "Direct Download",
                            Protocol = DownloadProtocol.Direct,
                            HasPostImportCategory = false
                        }
                    },
                    RemoteBook = new RemoteBook
                    {
                        Author = new Author { Id = 77, Name = "Lois McMaster Bujold" }
                    }
                }
            }));

            var queue = service.GetQueue();

            Assert.That(queue, Has.Count.EqualTo(1));
            Assert.That(queue[0].Protocol, Is.EqualTo(DownloadProtocol.Direct));
            Assert.That(queue[0].Book?.Id, Is.EqualTo(501));
            Assert.That(queue[0].Indexer, Is.EqualTo("Direct Download"));
            Assert.That(queue[0].DownloadForced, Is.True);
            Assert.That(eventAggregator.Events, Has.Some.InstanceOf<QueueUpdatedEvent>());
        }
    }
}
