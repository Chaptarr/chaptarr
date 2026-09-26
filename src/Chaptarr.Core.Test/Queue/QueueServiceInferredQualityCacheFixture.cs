using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Queue;

namespace Chaptarr.Core.Test.Queue
{
    [TestFixture]
    public class QueueServiceInferredQualityCacheFixture
    {
        private sealed class NullEventAggregator : IEventAggregator
        {
            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
            }
        }

        private class HistoryServiceProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IHistoryService.Find) && args.Length == 2)
                {
                    return new List<EntityHistory>();
                }

                if (targetMethod.Name == nameof(IHistoryService.FindByDownloadIds) && args.Length == 2)
                {
                    return new List<EntityHistory>();
                }

                throw new NotImplementedException($"Test proxy does not implement {targetMethod.DeclaringType?.Name}.{targetMethod.Name}");
            }
        }

        private static QueueService CreateService()
        {
            return new QueueService(new NullEventAggregator(), DispatchProxy.Create<IHistoryService, HistoryServiceProxy>());
        }

        private static TrackedDownload CreateCompletedDownload(string downloadId, IEnumerable<string> filePaths, params Book[] books)
        {
            return new TrackedDownload
            {
                DownloadClient = 7,
                Protocol = DownloadProtocol.Torrent,
                IsTrackable = true,
                State = TrackedDownloadState.ImportBlocked,
                DownloadItem = new DownloadClientItem
                {
                    DownloadClientInfo = new DownloadClientItemClientInfo { Name = "Test Client", HasPostImportCategory = false },
                    DownloadId = downloadId,
                    Title = $"Test {downloadId}",
                    Status = DownloadItemStatus.Completed,
                    TotalSize = 123,
                    RemainingSize = 0,
                    OutputPath = new OsPath($"/downloads/{downloadId}"),
                    FilePaths = filePaths.ToList()
                },
                RemoteBook = new RemoteBook
                {
                    Author = new Author { Id = 100, Name = "Test Author" },
                    Books = books.ToList(),
                    ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.Unknown) }
                }
            };
        }

        [Test]
        public void should_infer_quality_per_media_type_when_one_download_maps_to_audiobook_and_ebook()
        {
            var service = CreateService();
            var audiobook = new Book { Id = 1, Title = "Same Work", MediaType = BookMediaType.Audiobook };
            var ebook = new Book { Id = 2, Title = "Same Work", MediaType = BookMediaType.Ebook };
            var download = CreateCompletedDownload(
                "download-mixed",
                new[] { "/downloads/Same Work - CD 01.mp3", "/downloads/Same Work - CD 02.mp3", "/downloads/Same Work.epub" },
                audiobook,
                ebook);

            service.Handle(new TrackedDownloadRefreshedEvent(new List<TrackedDownload> { download }));

            var queue = service.GetQueue();
            var audiobookItem = queue.Single(item => item.Book.Id == audiobook.Id);
            var ebookItem = queue.Single(item => item.Book.Id == ebook.Id);

            Assert.That(audiobookItem.Quality.Quality, Is.EqualTo(Quality.MP3));
            Assert.That(ebookItem.Quality.Quality, Is.EqualTo(Quality.EPUB));
        }

        [Test]
        public void should_reinfer_quality_after_the_download_leaves_and_returns_to_the_tracker()
        {
            var service = CreateService();
            var audiobook = new Book { Id = 1, Title = "Work", MediaType = BookMediaType.Audiobook };

            service.Handle(new TrackedDownloadRefreshedEvent(new List<TrackedDownload>
            {
                CreateCompletedDownload("download-reused", new[] { "/downloads/Work - CD 01.mp3" }, audiobook)
            }));
            Assert.That(service.GetQueue().Single().Quality.Quality, Is.EqualTo(Quality.MP3));

            // The download leaves the tracker, which prunes its cached quality.
            service.Handle(new TrackedDownloadRefreshedEvent(new List<TrackedDownload>()));

            service.Handle(new TrackedDownloadRefreshedEvent(new List<TrackedDownload>
            {
                CreateCompletedDownload("download-reused", new[] { "/downloads/Work.flac" }, audiobook)
            }));
            Assert.That(service.GetQueue().Single().Quality.Quality, Is.EqualTo(Quality.FLAC));
        }

        [Test]
        public void should_not_throw_when_queue_refreshes_run_concurrently()
        {
            var service = CreateService();
            var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();

            Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, worker =>
            {
                try
                {
                    for (var i = 0; i < 300; i++)
                    {
                        // Each refresh tracks a different rotating window of downloads, so refreshes both add
                        // entries and prune entries that other workers are adding at the same time.
                        var downloads = Enumerable.Range(0, 12)
                            .Select(n => CreateCompletedDownload(
                                $"download-{(worker * 7 + i + n) % 40}",
                                new[] { "/downloads/a - CD 01.mp3", "/downloads/a - CD 02.mp3" },
                                new Book { Id = n + 1, Title = "Book", MediaType = BookMediaType.Audiobook }))
                            .ToList();

                        service.Handle(new TrackedDownloadRefreshedEvent(downloads));
                        service.Handle(new TrackedDownloadUpdatedEvent(downloads[0]));
                    }
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex);
                }
            });

            Assert.That(errors, Is.Empty, () => errors.FirstOrDefault()?.ToString());
        }
    }
}
