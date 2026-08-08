using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using NzbDrone.Common.Crypto;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Queue;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Queue
{
    [TestFixture]
    public class QueueServiceGrabHistoryFallbackFixture
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
            public List<(string DownloadId, EntityHistoryEventType EventType)> FindCalls { get; } = new();
            public List<(List<string> DownloadIds, EntityHistoryEventType EventType)> FindByDownloadIdsCalls { get; } = new();
            public List<EntityHistory> FindResult { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IHistoryService.Find) && args.Length == 2)
                {
                    FindCalls.Add(((string)args[0], (EntityHistoryEventType)args[1]));
                    return FindResult;
                }

                if (targetMethod.Name == nameof(IHistoryService.FindByDownloadIds) && args.Length == 2)
                {
                    FindByDownloadIdsCalls.Add((((IEnumerable<string>)args[0]).ToList(), (EntityHistoryEventType)args[1]));
                    return FindResult;
                }

                throw new NotImplementedException($"Test proxy does not implement {targetMethod.DeclaringType?.Name}.{targetMethod.Name}");
            }
        }

        private class DiskProviderProxy : DispatchProxy
        {
            public List<string> Files { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    nameof(IDiskProvider.FolderExists) => true,
                    nameof(IDiskProvider.GetFiles) => Files,
                    _ => throw new NotImplementedException($"Test proxy does not implement IDiskProvider.{targetMethod?.Name}")
                };
            }
        }

        [Test]
        public void should_use_grab_history_book_when_remote_book_has_no_books()
        {
            var eventAggregator = new RecordingEventAggregator();
            var grabbedAt = new DateTime(2026, 05, 04, 12, 00, 00, DateTimeKind.Utc);

            var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
            var historyProxy = (HistoryServiceProxy)(object)historyService;

            var grabbedAuthor = new Author
            {
                Id = 100,
                Name = "A.F. Kay"
            };

            var grabbedBook = new Book
            {
                Id = 200,
                Title = "Test Book"
            };

            historyProxy.FindResult = new List<EntityHistory>
            {
                new()
                {
                    EventType = EntityHistoryEventType.Grabbed,
                    DownloadId = "download-1",
                    Date = grabbedAt,
                    Author = grabbedAuthor,
                    Book = grabbedBook,
                    Data = new Dictionary<string, string>
                    {
                        ["Indexer"] = "MyAnonamouse",
                        ["DownloadForced"] = bool.FalseString,
                        ["ReleaseSource"] = ReleaseSourceType.InteractiveSearch.ToString()
                    }
                }
            };

            var service = new QueueService(eventAggregator, historyService);

            var trackedDownload = new TrackedDownload
            {
                DownloadClient = 7,
                Protocol = DownloadProtocol.Torrent,
                IsTrackable = true,
                DownloadItem = new DownloadClientItem
                {
                    DownloadClientInfo = new DownloadClientItemClientInfo
                    {
                        Name = "Test Client",
                        HasPostImportCategory = false
                    },
                    DownloadId = "download-1",
                    Title = "A.F. Kay - Test Book",
                    TotalSize = 123,
                    RemainingSize = 100,
                    RemainingTime = TimeSpan.FromMinutes(10),
                    OutputPath = new OsPath("/downloads/test")
                },
                RemoteBook = new RemoteBook
                {
                    Author = grabbedAuthor
                }
            };

            service.Handle(new TrackedDownloadRefreshedEvent(new List<TrackedDownload> { trackedDownload }));

            var queue = service.GetQueue();

            Assert.That(queue, Has.Count.EqualTo(1));
            Assert.That(queue[0].Book, Is.Not.Null);
            Assert.That(queue[0].Book.Id, Is.EqualTo(200));
            Assert.That(queue[0].Added, Is.EqualTo(grabbedAt));
            Assert.That(queue[0].Indexer, Is.EqualTo("MyAnonamouse"));
            Assert.That(queue[0].TargetBookIds, Is.EqualTo(new List<int> { 200 }));
            Assert.That(queue[0].DownloadForced, Is.True);

            var expectedId = HashConverter.GetHashInt31($"trackedDownload-7-download-1-book200");
            Assert.That(queue[0].Id, Is.EqualTo(expectedId));
            Assert.That(eventAggregator.Events, Has.Some.InstanceOf<QueueUpdatedEvent>());
        }

        [Test]
        public void should_use_grab_history_quality_when_remote_quality_is_unknown_audio()
        {
            var eventAggregator = new RecordingEventAggregator();
            var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
            var historyProxy = (HistoryServiceProxy)(object)historyService;

            var author = new Author
            {
                Id = 85,
                Name = "Brandon Sanderson"
            };

            var book = new Book
            {
                Id = 5651,
                Title = "The Hero of Ages",
                MediaType = BookMediaType.Audiobook
            };

            historyProxy.FindResult = new List<EntityHistory>
            {
                new()
                {
                    EventType = EntityHistoryEventType.Grabbed,
                    DownloadId = "download-m4b",
                    Date = DateTime.UtcNow,
                    Author = author,
                    Book = book,
                    Quality = new QualityModel(Quality.M4B),
                    Data = new Dictionary<string, string>()
                }
            };

            var service = new QueueService(eventAggregator, historyService);

            var trackedDownload = new TrackedDownload
            {
                DownloadClient = 7,
                Protocol = DownloadProtocol.Torrent,
                IsTrackable = true,
                DownloadItem = new DownloadClientItem
                {
                    DownloadClientInfo = new DownloadClientItemClientInfo
                    {
                        Name = "Test Client",
                        HasPostImportCategory = false
                    },
                    DownloadId = "download-m4b",
                    Title = "Brandon Sanderson - Mistborn 03 - The Hero of Ages (GraphicAudio)",
                    TotalSize = 123,
                    RemainingSize = 0,
                    OutputPath = new OsPath("/downloads/test")
                },
                RemoteBook = new RemoteBook
                {
                    Author = author,
                    Books = new List<Book> { book },
                    ParsedBookInfo = new ParsedBookInfo
                    {
                        Quality = new QualityModel(Quality.UnknownAudio)
                    }
                }
            };

            service.Handle(new TrackedDownloadRefreshedEvent(new List<TrackedDownload> { trackedDownload }));

            var queue = service.GetQueue();

            Assert.That(queue, Has.Count.EqualTo(1));
            Assert.That(queue[0].Quality.Quality, Is.EqualTo(Quality.M4B));
        }

        [Test]
        public void should_lookup_grab_history_once_per_download_when_release_maps_to_multiple_books()
        {
            var eventAggregator = new RecordingEventAggregator();
            var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
            var historyProxy = (HistoryServiceProxy)(object)historyService;

            var author = new Author
            {
                Id = 85,
                Name = "Brandon Sanderson"
            };

            var firstBook = new Book
            {
                Id = 5651,
                Title = "The Hero of Ages",
                MediaType = BookMediaType.Audiobook
            };

            var secondBook = new Book
            {
                Id = 5652,
                Title = "The Hero of Ages: Commentary",
                MediaType = BookMediaType.Audiobook
            };

            historyProxy.FindResult = new List<EntityHistory>
            {
                new()
                {
                    EventType = EntityHistoryEventType.Grabbed,
                    DownloadId = "download-multi-book",
                    Date = DateTime.UtcNow,
                    Author = author,
                    BookId = firstBook.Id,
                    Book = firstBook,
                    Quality = new QualityModel(Quality.MP3),
                    Data = new Dictionary<string, string>()
                }
            };

            var service = new QueueService(eventAggregator, historyService);

            var trackedDownload = new TrackedDownload
            {
                DownloadClient = 7,
                Protocol = DownloadProtocol.Torrent,
                IsTrackable = true,
                DownloadItem = new DownloadClientItem
                {
                    DownloadClientInfo = new DownloadClientItemClientInfo
                    {
                        Name = "Test Client",
                        HasPostImportCategory = false
                    },
                    DownloadId = "download-multi-book",
                    Title = "Brandon Sanderson - Multi Book Release",
                    TotalSize = 123,
                    RemainingSize = 0,
                    OutputPath = new OsPath("/downloads/test")
                },
                RemoteBook = new RemoteBook
                {
                    Author = author,
                    Books = new List<Book> { firstBook, secondBook },
                    ParsedBookInfo = new ParsedBookInfo
                    {
                        Quality = new QualityModel(Quality.MP3)
                    }
                }
            };

            service.Handle(new TrackedDownloadRefreshedEvent(new List<TrackedDownload> { trackedDownload }));

            var queue = service.GetQueue();

            Assert.That(queue, Has.Count.EqualTo(2));
            Assert.That(historyProxy.FindCalls, Is.Empty);
            Assert.That(historyProxy.FindByDownloadIdsCalls, Has.Count.EqualTo(1));
            Assert.That(historyProxy.FindByDownloadIdsCalls[0].DownloadIds, Is.EqualTo(new[] { "download-multi-book" }));
            Assert.That(historyProxy.FindByDownloadIdsCalls[0].EventType, Is.EqualTo(EntityHistoryEventType.Grabbed));
        }

        [Test]
        public void should_infer_completed_folder_quality_from_download_client_file_paths()
        {
            var eventAggregator = new RecordingEventAggregator();
            var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
            var historyProxy = (HistoryServiceProxy)(object)historyService;
            historyProxy.FindResult = new List<EntityHistory>();

            var service = new QueueService(eventAggregator, historyService);
            var trackedDownload = CreateFailedCompletedDownload("download-file-paths-mp3", Quality.UnknownAudio);
            trackedDownload.DownloadItem.FilePaths = new List<string>
            {
                "/downloads/Harry Potter and the Chamber of Secrets - CD 01.mp3",
                "/downloads/Harry Potter and the Chamber of Secrets - CD 02.mp3",
                "/downloads/readme.txt"
            };

            service.Handle(new TrackedDownloadRefreshedEvent(new List<TrackedDownload> { trackedDownload }));

            var queue = service.GetQueue();

            Assert.That(queue, Has.Count.EqualTo(1));
            Assert.That(queue[0].Quality.Quality, Is.EqualTo(Quality.MP3));
        }

        [Test]
        public void should_infer_completed_folder_quality_by_scanning_output_folder_once()
        {
            var eventAggregator = new RecordingEventAggregator();
            var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
            var historyProxy = (HistoryServiceProxy)(object)historyService;
            historyProxy.FindResult = new List<EntityHistory>();

            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var diskProxy = (DiskProviderProxy)(object)diskProvider;
            diskProxy.Files = new List<string>
            {
                "/downloads/hp/Harry Potter and the Chamber of Secrets - CD 01.mp3",
                "/downloads/hp/Harry Potter and the Chamber of Secrets - CD 02.mp3",
                "/downloads/hp/Harry Potter and the Chamber of Secrets.sample.mp3",
                "/downloads/hp/Harry Potter and the Chamber of Secrets.part"
            };

            var service = new QueueService(eventAggregator, historyService, diskProvider: diskProvider);
            var trackedDownload = CreateFailedCompletedDownload("download-scan-mp3", Quality.UnknownAudio);
            trackedDownload.DownloadItem.OutputPath = new OsPath("/downloads/hp");

            service.Handle(new TrackedDownloadRefreshedEvent(new List<TrackedDownload> { trackedDownload }));

            var queue = service.GetQueue();

            Assert.That(queue, Has.Count.EqualTo(1));
            Assert.That(queue[0].Quality.Quality, Is.EqualTo(Quality.MP3));
        }

        [TestCase(TrackedDownloadState.ImportBlocked)]
        public void should_show_import_status_message_before_download_client_message(TrackedDownloadState state)
        {
            var eventAggregator = new RecordingEventAggregator();

            var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
            var historyProxy = (HistoryServiceProxy)(object)historyService;
            historyProxy.FindResult = new List<EntityHistory>();

            var service = new QueueService(eventAggregator, historyService);

            var trackedDownload = new TrackedDownload
            {
                DownloadClient = 7,
                Protocol = DownloadProtocol.Torrent,
                IsTrackable = true,
                State = state,
                DownloadItem = new DownloadClientItem
                {
                    DownloadClientInfo = new DownloadClientItemClientInfo
                    {
                        Name = "Test Client",
                        HasPostImportCategory = false
                    },
                    DownloadId = "download-2",
                    Title = "A.F. Kay - Test Book",
                    TotalSize = 123,
                    RemainingSize = 0,
                    OutputPath = new OsPath("/downloads/test"),
                    Message = "No files eligible for import"
                }
            };

            trackedDownload.Warn(
                new TrackedDownloadStatusMessage("A.F. Kay - Test Book", "NO_ELIGIBLE_FILES"),
                new TrackedDownloadStatusMessage("Test Book.m4b", "Additional physical copy cannot be imported because the managed destination is already occupied."));

            service.Handle(new TrackedDownloadRefreshedEvent(new List<TrackedDownload> { trackedDownload }));

            var queue = service.GetQueue();

            Assert.That(queue, Has.Count.EqualTo(1));
            Assert.That(queue[0].ErrorMessage, Is.EqualTo("Additional physical copy cannot be imported because the managed destination is already occupied."));
        }

        [Test]
        public void should_mark_failed_mp3_to_m4b_conversion_download_retryable()
        {
            var eventAggregator = new RecordingEventAggregator();
            var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
            var historyProxy = (HistoryServiceProxy)(object)historyService;
            historyProxy.FindResult = new List<EntityHistory>();

            var service = new QueueService(eventAggregator, historyService);
            var trackedDownload = CreateFailedCompletedDownload("download-retry-mp3", Quality.MP3);

            service.Handle(new TrackedDownloadRefreshedEvent(new List<TrackedDownload> { trackedDownload }));

            var queue = service.GetQueue();

            Assert.That(queue, Has.Count.EqualTo(1));
            Assert.That(queue[0].CanRetryImport, Is.True);
        }

        [Test]
        public void should_mark_blocked_import_download_retryable()
        {
            var eventAggregator = new RecordingEventAggregator();
            var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
            var historyProxy = (HistoryServiceProxy)(object)historyService;
            historyProxy.FindResult = new List<EntityHistory>();

            var service = new QueueService(eventAggregator, historyService);
            var trackedDownload = CreateFailedCompletedDownload("download-retry-blocked", Quality.MP3);
            trackedDownload.State = TrackedDownloadState.ImportBlocked;

            service.Handle(new TrackedDownloadRefreshedEvent(new List<TrackedDownload> { trackedDownload }));

            var queue = service.GetQueue();

            Assert.That(queue, Has.Count.EqualTo(1));
            Assert.That(queue[0].CanRetryImport, Is.True);
        }

        [TestCase(12)]
        [TestCase(2)]
        [TestCase(3)]
        public void should_mark_completed_failed_download_retryable_for_any_source_quality(int qualityId)
        {
            var eventAggregator = new RecordingEventAggregator();
            var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
            var historyProxy = (HistoryServiceProxy)(object)historyService;
            historyProxy.FindResult = new List<EntityHistory>();

            var service = new QueueService(eventAggregator, historyService);
            var trackedDownload = CreateFailedCompletedDownload($"download-no-retry-{qualityId}", Quality.FindById(qualityId));

            service.Handle(new TrackedDownloadRefreshedEvent(new List<TrackedDownload> { trackedDownload }));

            var queue = service.GetQueue();

            Assert.That(queue, Has.Count.EqualTo(1));
            Assert.That(queue[0].CanRetryImport, Is.True);
        }

        [Test]
        public void should_not_mark_failed_download_without_download_id_retryable()
        {
            var eventAggregator = new RecordingEventAggregator();
            var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
            var historyProxy = (HistoryServiceProxy)(object)historyService;
            historyProxy.FindResult = new List<EntityHistory>();

            var service = new QueueService(eventAggregator, historyService);
            var trackedDownload = CreateFailedCompletedDownload("download-no-id", Quality.MP3);
            trackedDownload.DownloadItem.DownloadId = null;

            service.Handle(new TrackedDownloadRefreshedEvent(new List<TrackedDownload> { trackedDownload }));

            var queue = service.GetQueue();

            Assert.That(queue, Has.Count.EqualTo(1));
            Assert.That(queue[0].CanRetryImport, Is.False);
        }

        [Test]
        public void should_mark_active_conversion_cancellable_until_cancel_requested()
        {
            var eventAggregator = new RecordingEventAggregator();
            var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
            var historyProxy = (HistoryServiceProxy)(object)historyService;
            historyProxy.FindResult = new List<EntityHistory>();

            var conversionTrackingService = new ConversionTrackingService(eventAggregator);
            using var cancellation = new CancellationTokenSource();
            conversionTrackingService.Start("download-convert-cancel", Quality.M4B.Id, Quality.M4B.Name, "Converting to M4B");
            conversionTrackingService.RegisterCancellation("download-convert-cancel", cancellation);

            var service = new QueueService(eventAggregator, historyService, conversionTrackingService);
            var trackedDownload = CreateFailedCompletedDownload("download-convert-cancel", Quality.MP3);
            trackedDownload.State = TrackedDownloadState.Importing;

            service.Handle(new TrackedDownloadRefreshedEvent(new List<TrackedDownload> { trackedDownload }));

            var queue = service.GetQueue();

            Assert.That(queue, Has.Count.EqualTo(1));
            Assert.That(queue[0].ConversionStatus, Is.EqualTo("converting"));
            Assert.That(queue[0].CanCancelConversion, Is.True);

            conversionTrackingService.Cancel("download-convert-cancel");
            queue = service.GetQueue();

            Assert.That(queue[0].ConversionStatus, Is.EqualTo("cancelling"));
            Assert.That(queue[0].CanCancelConversion, Is.False);

            conversionTrackingService.Complete("download-convert-cancel");
        }

        [Test]
        public void get_queue_should_return_a_copy_without_mutating_cached_rows()
        {
            var eventAggregator = new RecordingEventAggregator();
            var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
            var historyProxy = (HistoryServiceProxy)(object)historyService;
            historyProxy.FindResult = new List<EntityHistory>();

            var conversionTrackingService = new ConversionTrackingService(eventAggregator);
            conversionTrackingService.Start("download-copy", Quality.M4B.Id, Quality.M4B.Name, "Converting to M4B");

            var service = new QueueService(eventAggregator, historyService, conversionTrackingService);
            var trackedDownload = CreateFailedCompletedDownload("download-copy", Quality.MP3);
            trackedDownload.State = TrackedDownloadState.Importing;

            service.Handle(new TrackedDownloadRefreshedEvent(new List<TrackedDownload> { trackedDownload }));

            var firstRead = service.GetQueue();
            Assert.That(firstRead, Has.Count.EqualTo(1));
            Assert.That(firstRead[0].ConversionStatus, Is.EqualTo("converting"));

            firstRead[0].Title = "mutated by caller";
            firstRead[0].TargetBookIds.Add(999);

            var secondRead = service.GetQueue();

            Assert.That(secondRead[0].Title, Is.Not.EqualTo("mutated by caller"));
            Assert.That(secondRead[0].TargetBookIds, Does.Not.Contain(999));

            conversionTrackingService.Complete("download-copy");
        }

        [Test]
        public void should_insert_updated_importing_download_into_queue_with_conversion_status()
        {
            var eventAggregator = new RecordingEventAggregator();
            var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
            var historyProxy = (HistoryServiceProxy)(object)historyService;
            historyProxy.FindResult = new List<EntityHistory>();

            var conversionTrackingService = new ConversionTrackingService(eventAggregator);
            conversionTrackingService.Start("download-upsert-convert", Quality.M4B.Id, Quality.M4B.Name, "Converting to M4B");

            var service = new QueueService(eventAggregator, historyService, conversionTrackingService);
            service.Handle(new TrackedDownloadRefreshedEvent(new List<TrackedDownload>()));

            var trackedDownload = CreateFailedCompletedDownload("download-upsert-convert", Quality.MP3);
            trackedDownload.State = TrackedDownloadState.Importing;

            service.Handle(new TrackedDownloadUpdatedEvent(trackedDownload));

            var queue = service.GetQueue();

            Assert.That(queue, Has.Count.EqualTo(1));
            Assert.That(queue[0].DownloadId, Is.EqualTo("download-upsert-convert"));
            Assert.That(queue[0].TrackedDownloadState, Is.EqualTo(TrackedDownloadState.Importing));
            Assert.That(queue[0].ConversionStatus, Is.EqualTo("converting"));
            Assert.That(queue[0].ConversionMessage, Is.EqualTo("Converting to M4B"));
            Assert.That(eventAggregator.Events, Has.Some.InstanceOf<QueueUpdatedEvent>());

            conversionTrackingService.Complete("download-upsert-convert");
        }

        [Test]
        public void should_remove_updated_imported_download_from_queue()
        {
            var eventAggregator = new RecordingEventAggregator();
            var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
            var historyProxy = (HistoryServiceProxy)(object)historyService;
            historyProxy.FindResult = new List<EntityHistory>();

            var service = new QueueService(eventAggregator, historyService);
            var trackedDownload = CreateFailedCompletedDownload("download-terminal-imported", Quality.MP3);
            trackedDownload.State = TrackedDownloadState.Importing;

            service.Handle(new TrackedDownloadRefreshedEvent(new List<TrackedDownload> { trackedDownload }));
            Assert.That(service.GetQueue(), Has.Count.EqualTo(1));

            trackedDownload.State = TrackedDownloadState.Imported;
            service.Handle(new TrackedDownloadUpdatedEvent(trackedDownload));

            Assert.That(service.GetQueue(), Is.Empty);
        }

        [TestCase(TrackedDownloadState.ImportBlocked)]
        public void should_keep_updated_incomplete_import_download_visible_and_retryable(TrackedDownloadState terminalState)
        {
            var eventAggregator = new RecordingEventAggregator();
            var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
            var historyProxy = (HistoryServiceProxy)(object)historyService;
            historyProxy.FindResult = new List<EntityHistory>();

            var service = new QueueService(eventAggregator, historyService);
            service.Handle(new TrackedDownloadRefreshedEvent(new List<TrackedDownload>()));

            var trackedDownload = CreateFailedCompletedDownload("download-terminal-failed", Quality.MP3);
            trackedDownload.State = TrackedDownloadState.Importing;

            service.Handle(new TrackedDownloadUpdatedEvent(trackedDownload));
            Assert.That(service.GetQueue()[0].TrackedDownloadState, Is.EqualTo(TrackedDownloadState.Importing));

            trackedDownload.State = terminalState;
            service.Handle(new TrackedDownloadUpdatedEvent(trackedDownload));

            var queue = service.GetQueue();

            Assert.That(queue, Has.Count.EqualTo(1));
            Assert.That(queue[0].DownloadId, Is.EqualTo("download-terminal-failed"));
            Assert.That(queue[0].TrackedDownloadState, Is.EqualTo(terminalState));
            Assert.That(queue[0].CanRetryImport, Is.True);
        }

        private static TrackedDownload CreateFailedCompletedDownload(string downloadId, Quality sourceQuality)
        {
            var author = CreateAudiobookAuthorWithM4bConversionProfile();

            return new TrackedDownload
            {
                DownloadClient = 7,
                Protocol = DownloadProtocol.Torrent,
                IsTrackable = true,
                State = TrackedDownloadState.ImportBlocked,
                DownloadItem = new DownloadClientItem
                {
                    DownloadClientInfo = new DownloadClientItemClientInfo
                    {
                        Name = "Test Client",
                        HasPostImportCategory = false
                    },
                    DownloadId = downloadId,
                    Title = $"Test {sourceQuality.Name}",
                    Status = DownloadItemStatus.Completed,
                    TotalSize = 123,
                    RemainingSize = 0,
                    OutputPath = new OsPath($"/downloads/{downloadId}")
                },
                RemoteBook = new RemoteBook
                {
                    Author = author,
                    Books = new List<Book>
                    {
                        new()
                        {
                            Id = 300,
                            Title = "Test Book",
                            MediaType = BookMediaType.Audiobook
                        }
                    },
                    ParsedBookInfo = new ParsedBookInfo
                    {
                        Quality = new QualityModel(sourceQuality)
                    }
                }
            };
        }

        private static Author CreateAudiobookAuthorWithM4bConversionProfile()
        {
            var profile = new QualityProfile
            {
                Id = 10,
                Name = "Audiobook",
                ProfileType = ProfileType.Audiobook,
                ConvertMp3ToM4b = true,
                ConvertToQualityId = Quality.M4B.Id,
                Items = new List<QualityProfileQualityItem>
                {
                    new() { Quality = Quality.MP3, Allowed = true },
                    new() { Quality = Quality.FLAC, Allowed = true },
                    new() { Quality = Quality.M4B, Allowed = true }
                }
            };

            return new Author
            {
                Id = 100,
                Name = "Test Author",
                AudiobookQualityProfileId = profile.Id,
                AudiobookQualityProfile = profile
            };
        }
    }
}
