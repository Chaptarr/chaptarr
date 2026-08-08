using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class CompletedDownloadServiceStrictVerificationFixture
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

        private sealed class StubProvideImportItemService : IProvideImportItemService
        {
            private readonly OsPath _outputPath;

            public StubProvideImportItemService(string outputPath)
            {
                _outputPath = new OsPath(outputPath);
            }

            public DownloadClientItem ProvideImportItem(DownloadClientItem item, DownloadClientItem previousImportAttempt)
            {
                var clone = item.Clone();
                clone.OutputPath = _outputPath;
                return clone;
            }
        }

        private sealed class RecordingDownloadedBooksImportService : IDownloadedBooksImportService
        {
            public int CallCount { get; private set; }
            public List<ImportResult> Results { get; set; } = new();

            public List<ImportResult> ProcessPath(string path, ImportMode importMode = ImportMode.Auto, Author author = null, DownloadClientItem downloadClientItem = null, RemoteBook remoteBook = null, bool requireDefaultRootFolderForMissingAuthors = false)
            {
                CallCount++;
                return Results;
            }

            public List<ImportResult> ProcessFolder(string path, ImportMode importMode = ImportMode.Auto, Author author = null, DownloadClientItem downloadClientItem = null, RemoteBook remoteBook = null, bool requireDefaultRootFolderForMissingAuthors = false)
                => throw new NotImplementedException();

            public List<ImportResult> ProcessFile(string path, ImportMode importMode = ImportMode.Auto, Author author = null, DownloadClientItem downloadClientItem = null, RemoteBook remoteBook = null, bool requireDefaultRootFolderForMissingAuthors = false)
                => throw new NotImplementedException();
        }

        private sealed class PassthroughDownloadImportModeResolver : IDownloadImportModeResolver
        {
            public ImportMode Resolve(ImportMode requestedMode, DownloadClientItem downloadClientItem) => requestedMode;
            public DownloadImportPolicy ResolvePolicy(ImportMode requestedMode, DownloadClientItem downloadClientItem) => new(requestedMode, false);
            public bool ShouldPreserveDownloadClientItem(DownloadClientItem downloadClientItem) => false;
        }

        private sealed class StubTrackedDownloadAlreadyImported : ITrackedDownloadAlreadyImported
        {
            public bool Result { get; set; }

            public bool IsImported(TrackedDownload trackedDownload, List<EntityHistory> historyItems) => Result;
        }

        private class HistoryServiceProxy : DispatchProxy
        {
            public List<EntityHistory> HistoryItems { get; set; } = new();
            public List<string> FindByDownloadIdCalls { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IHistoryService.FindByDownloadId))
                {
                    var downloadId = (string)args[0];
                    FindByDownloadIdCalls.Add(downloadId);
                    return HistoryItems.Where(h => h.DownloadId == downloadId).ToList();
                }

                if (targetMethod?.Name == nameof(IHistoryService.MostRecentForDownloadId))
                {
                    return HistoryItems.OrderByDescending(h => h.Date).FirstOrDefault();
                }

                throw new NotImplementedException($"Test proxy does not implement IHistoryService.{targetMethod?.Name}");
            }
        }

        [Test, Platform(Exclude = "Win", Reason = "Test uses Unix paths")]
        public void should_mark_completed_download_as_imported_when_grab_history_exists_but_target_books_are_missing()
        {
            var eventAggregator = new RecordingEventAggregator();
            var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
            var historyProxy = (HistoryServiceProxy)(object)historyService;
            historyProxy.HistoryItems = new List<EntityHistory>
            {
                new EntityHistory
                {
                    EventType = EntityHistoryEventType.Grabbed,
                    DownloadId = "download-1",
                    Date = DateTime.UtcNow
                }
            };

            var importService = new RecordingDownloadedBooksImportService
            {
                Results = new List<ImportResult>
                {
                    CreateImportedResult(101, 7, "Recovered Match")
                }
            };

            var trackedDownload = new TrackedDownload
            {
                DownloadClient = 1,
                DownloadItem = new DownloadClientItem
                {
                    DownloadId = "download-1",
                    Title = "Missing.Context",
                    Status = DownloadItemStatus.Completed,
                    DownloadClientInfo = new DownloadClientItemClientInfo
                    {
                        Id = 1,
                        Name = "qBittorrent",
                        Protocol = DownloadProtocol.Torrent
                    }
                },
                RemoteBook = new RemoteBook
                {
                    Author = new Author { Id = 7, Name = "Test Author" },
                    Books = new List<Book>()
                },
                State = TrackedDownloadState.ImportPending
            };

            var subject = new CompletedDownloadService(
                eventAggregator,
                historyService,
                new StubProvideImportItemService("/tmp/chaptarr-missing-context"),
                importService,
                new PassthroughDownloadImportModeResolver(),
                new StubTrackedDownloadAlreadyImported(),
                NoopFailedDownloadService.Instance,
                LogManager.GetCurrentClassLogger(),
                NoopDownloadClientFileSnapshotService.Instance);

            subject.Import(trackedDownload);

            Assert.That(importService.CallCount, Is.EqualTo(1));
            Assert.That(trackedDownload.State, Is.EqualTo(TrackedDownloadState.Imported));
            Assert.That(trackedDownload.StatusMessages, Is.Empty);
            Assert.That(historyProxy.FindByDownloadIdCalls, Has.All.EqualTo("download-1"));
            Assert.That(eventAggregator.Events, Has.One.InstanceOf<DownloadCompletedEvent>());
            Assert.That(eventAggregator.Events, Has.None.InstanceOf<BookImportIncompleteEvent>());
        }

        [Test]
        public void should_skip_completed_download_when_import_path_is_missing()
        {
            var eventAggregator = new RecordingEventAggregator();
            var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
            ((HistoryServiceProxy)(object)historyService).HistoryItems = new List<EntityHistory>
            {
                new EntityHistory
                {
                    EventType = EntityHistoryEventType.Grabbed,
                    DownloadId = "download-no-path",
                    Date = DateTime.UtcNow
                }
            };

            var importService = new RecordingDownloadedBooksImportService();
            var trackedDownload = new TrackedDownload
            {
                DownloadClient = 1,
                DownloadItem = new DownloadClientItem
                {
                    DownloadId = "download-no-path",
                    Title = "Missing.Path",
                    Status = DownloadItemStatus.Completed,
                    DownloadClientInfo = new DownloadClientItemClientInfo
                    {
                        Id = 1,
                        Name = "qBittorrent",
                        Protocol = DownloadProtocol.Torrent
                    }
                },
                State = TrackedDownloadState.Downloading
            };

            var subject = new CompletedDownloadService(
                eventAggregator,
                historyService,
                new StubProvideImportItemService(string.Empty),
                importService,
                new PassthroughDownloadImportModeResolver(),
                new StubTrackedDownloadAlreadyImported(),
                NoopFailedDownloadService.Instance,
                LogManager.GetCurrentClassLogger(),
                NoopDownloadClientFileSnapshotService.Instance);

            subject.Check(trackedDownload);

            Assert.That(importService.CallCount, Is.EqualTo(0));
            Assert.That(trackedDownload.State, Is.EqualTo(TrackedDownloadState.Downloading));
            Assert.That(trackedDownload.StatusMessages.SelectMany(m => m.Messages), Has.Some.Contains("Download doesn't contain intermediate path"));
            Assert.That(eventAggregator.Events, Has.None.InstanceOf<BookImportIncompleteEvent>());
            Assert.That(eventAggregator.Events, Has.None.InstanceOf<DownloadCompletedEvent>());
        }

        [Test]
        public void should_skip_completed_download_when_not_grabbed_and_not_in_category()
        {
            var eventAggregator = new RecordingEventAggregator();
            var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
            var importService = new RecordingDownloadedBooksImportService();
            var trackedDownload = new TrackedDownload
            {
                DownloadClient = 1,
                DownloadItem = new DownloadClientItem
                {
                    DownloadId = "download-no-context",
                    Title = "Missing.Context",
                    Status = DownloadItemStatus.Completed,
                    DownloadClientInfo = new DownloadClientItemClientInfo
                    {
                        Id = 1,
                        Name = "qBittorrent",
                        Protocol = DownloadProtocol.Torrent
                    }
                },
                State = TrackedDownloadState.Downloading
            };

            var subject = new CompletedDownloadService(
                eventAggregator,
                historyService,
                new StubProvideImportItemService("/tmp/chaptarr-no-context"),
                importService,
                new PassthroughDownloadImportModeResolver(),
                new StubTrackedDownloadAlreadyImported(),
                NoopFailedDownloadService.Instance,
                LogManager.GetCurrentClassLogger(),
                NoopDownloadClientFileSnapshotService.Instance);

            subject.Check(trackedDownload);

            Assert.That(importService.CallCount, Is.EqualTo(0));
            Assert.That(trackedDownload.State, Is.EqualTo(TrackedDownloadState.Downloading));
            Assert.That(trackedDownload.StatusMessages.SelectMany(m => m.Messages), Has.Some.Contains("Download wasn't grabbed by Chaptarr and not in a category"));
            Assert.That(eventAggregator.Events, Has.None.InstanceOf<BookImportIncompleteEvent>());
            Assert.That(eventAggregator.Events, Has.None.InstanceOf<DownloadCompletedEvent>());
        }

        [Test, Platform(Exclude = "Win", Reason = "Test uses Unix paths")]
        public void should_publish_incomplete_event_when_best_effort_import_finds_no_eligible_files()
        {
            var eventAggregator = new RecordingEventAggregator();
            var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
            ((HistoryServiceProxy)(object)historyService).HistoryItems = new List<EntityHistory>
            {
                new EntityHistory
                {
                    EventType = EntityHistoryEventType.Grabbed,
                    DownloadId = "download-empty",
                    Date = DateTime.UtcNow
                }
            };

            var trackedDownload = new TrackedDownload
            {
                DownloadClient = 1,
                DownloadItem = new DownloadClientItem
                {
                    DownloadId = "download-empty",
                    Title = "Missing.Context.Empty",
                    Status = DownloadItemStatus.Completed,
                    DownloadClientInfo = new DownloadClientItemClientInfo
                    {
                        Id = 1,
                        Name = "qBittorrent",
                        Protocol = DownloadProtocol.Torrent
                    }
                },
                RemoteBook = new RemoteBook
                {
                    Books = new List<Book>()
                },
                State = TrackedDownloadState.ImportPending
            };

            var subject = new CompletedDownloadService(
                eventAggregator,
                historyService,
                new StubProvideImportItemService("/tmp/chaptarr-missing-context-empty"),
                new RecordingDownloadedBooksImportService(),
                new PassthroughDownloadImportModeResolver(),
                new StubTrackedDownloadAlreadyImported(),
                NoopFailedDownloadService.Instance,
                LogManager.GetCurrentClassLogger(),
                NoopDownloadClientFileSnapshotService.Instance);

            Assert.DoesNotThrow(() => subject.Import(trackedDownload));
            Assert.That(trackedDownload.State, Is.EqualTo(TrackedDownloadState.ImportPending));
            Assert.That(eventAggregator.Events, Has.None.InstanceOf<BookImportIncompleteEvent>());

            Assert.DoesNotThrow(() => subject.Import(trackedDownload));
            Assert.That(trackedDownload.State, Is.EqualTo(TrackedDownloadState.ImportPending));
            Assert.That(eventAggregator.Events, Has.None.InstanceOf<BookImportIncompleteEvent>());

            Assert.DoesNotThrow(() => subject.Import(trackedDownload));
            Assert.That(trackedDownload.State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
            Assert.That(trackedDownload.StatusMessages, Is.Not.Empty);
            Assert.That(trackedDownload.StatusMessages[0].Messages, Does.Contain("NO_ELIGIBLE_FILES"));
            Assert.That(eventAggregator.Events, Has.Some.InstanceOf<BookImportIncompleteEvent>());
            Assert.That(eventAggregator.Events, Has.None.InstanceOf<DownloadCompletedEvent>());
        }

        [Test, Platform(Exclude = "Win", Reason = "Test uses Unix paths")]
        public void should_stamp_actual_file_quality_from_import_results_before_incomplete_event()
        {
            var eventAggregator = new RecordingEventAggregator();
            var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
            ((HistoryServiceProxy)(object)historyService).HistoryItems = new List<EntityHistory>
            {
                new EntityHistory
                {
                    EventType = EntityHistoryEventType.Grabbed,
                    DownloadId = "download-file-quality",
                    BookId = 101,
                    AuthorId = 42,
                    Date = DateTime.UtcNow
                }
            };

            var importService = new RecordingDownloadedBooksImportService
            {
                Results = new List<ImportResult>
                {
                    CreateSkippedResult(101, 42, "The Hero of Ages", Quality.M4B)
                }
            };

            var trackedDownload = new TrackedDownload
            {
                DownloadClient = 1,
                DownloadItem = new DownloadClientItem
                {
                    DownloadId = "download-file-quality",
                    Title = "Brandon Sanderson - Mistborn 03 - The Hero of Ages (GraphicAudio)",
                    Status = DownloadItemStatus.Completed,
                    DownloadClientInfo = new DownloadClientItemClientInfo
                    {
                        Id = 1,
                        Name = "qBittorrent",
                        Protocol = DownloadProtocol.Torrent
                    }
                },
                RemoteBook = new RemoteBook
                {
                    Author = new Author { Id = 42, Name = "Brandon Sanderson" },
                    ParsedBookInfo = new ParsedBookInfo
                    {
                        Quality = new QualityModel(Quality.UnknownAudio)
                    },
                    Books = new List<Book>
                    {
                        new() { Id = 101, AuthorId = 42, Title = "The Hero of Ages", MediaType = BookMediaType.Audiobook }
                    }
                },
                State = TrackedDownloadState.ImportPending
            };

            var subject = new CompletedDownloadService(
                eventAggregator,
                historyService,
                new StubProvideImportItemService("/tmp/chaptarr-file-quality"),
                importService,
                new PassthroughDownloadImportModeResolver(),
                new StubTrackedDownloadAlreadyImported { Result = false },
                NoopFailedDownloadService.Instance,
                LogManager.GetCurrentClassLogger(),
                NoopDownloadClientFileSnapshotService.Instance);

            subject.Import(trackedDownload);

            Assert.That(trackedDownload.RemoteBook.ParsedBookInfo.Quality.Quality, Is.EqualTo(Quality.M4B));
            Assert.That(eventAggregator.Events, Has.Some.InstanceOf<BookImportIncompleteEvent>());
        }

        [Test, Platform(Exclude = "Win", Reason = "Test uses Unix paths")]
        public void should_complete_import_using_imported_author_when_remote_author_is_missing()
        {
            var eventAggregator = new RecordingEventAggregator();
            var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();

            var importService = new RecordingDownloadedBooksImportService
            {
                Results = new List<ImportResult>
                {
                    CreateImportedResult(101, 42, "Recovered Match")
                }
            };

            var trackedDownload = new TrackedDownload
            {
                DownloadClient = 1,
                DownloadItem = new DownloadClientItem
                {
                    DownloadId = "download-null-author",
                    Title = "Missing.Author",
                    Status = DownloadItemStatus.Completed,
                    DownloadClientInfo = new DownloadClientItemClientInfo
                    {
                        Id = 1,
                        Name = "qBittorrent",
                        Protocol = DownloadProtocol.Torrent
                    }
                },
                RemoteBook = new RemoteBook
                {
                    Books = new List<Book>()
                },
                State = TrackedDownloadState.ImportPending
            };

            var subject = new CompletedDownloadService(
                eventAggregator,
                historyService,
                new StubProvideImportItemService("/tmp/chaptarr-null-author"),
                importService,
                new PassthroughDownloadImportModeResolver(),
                new StubTrackedDownloadAlreadyImported(),
                NoopFailedDownloadService.Instance,
                LogManager.GetCurrentClassLogger(),
                NoopDownloadClientFileSnapshotService.Instance);

            Assert.DoesNotThrow(() => subject.Import(trackedDownload));

            var completedEvent = eventAggregator.Events.OfType<DownloadCompletedEvent>().Single();
            Assert.That(trackedDownload.State, Is.EqualTo(TrackedDownloadState.Imported));
            Assert.That(completedEvent.AuthorId, Is.EqualTo(42));
        }

        [Test]
        public void verify_import_should_require_exact_expected_book_ids_not_just_matching_count()
        {
            var eventAggregator = new RecordingEventAggregator();
            var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
            ((HistoryServiceProxy)(object)historyService).HistoryItems = new List<EntityHistory>();

            var trackedDownload = new TrackedDownload
            {
                DownloadClient = 1,
                DownloadItem = new DownloadClientItem
                {
                    DownloadId = "download-2",
                    Title = "Wrong.Match",
                    Status = DownloadItemStatus.Completed,
                    DownloadClientInfo = new DownloadClientItemClientInfo
                    {
                        Id = 1,
                        Name = "qBittorrent",
                        Protocol = DownloadProtocol.Torrent
                    }
                },
                RemoteBook = new RemoteBook
                {
                    Author = new Author { Id = 7, Name = "Test Author" },
                    Books = new List<Book>
                    {
                        new() { Id = 10, AuthorId = 7, Title = "Expected One" },
                        new() { Id = 11, AuthorId = 7, Title = "Expected Two" }
                    }
                }
            };

            var subject = new CompletedDownloadService(
                eventAggregator,
                historyService,
                new StubProvideImportItemService("/tmp/chaptarr-verify-import"),
                new RecordingDownloadedBooksImportService(),
                new PassthroughDownloadImportModeResolver(),
                new StubTrackedDownloadAlreadyImported { Result = false },
                NoopFailedDownloadService.Instance,
                LogManager.GetCurrentClassLogger(),
                NoopDownloadClientFileSnapshotService.Instance);

            var importResults = new List<ImportResult>
            {
                CreateImportedResult(11, 7, "Expected Two"),
                CreateImportedResult(999, 7, "Wrong Book")
            };

            var verified = subject.VerifyImport(trackedDownload, importResults);

            Assert.That(verified, Is.False);
            Assert.That(eventAggregator.Events, Is.Empty);
        }

        [Test]
        public void verify_import_should_only_expect_books_matching_release_media_type()
        {
            var eventAggregator = new RecordingEventAggregator();
            var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
            ((HistoryServiceProxy)(object)historyService).HistoryItems = new List<EntityHistory>();

            var trackedDownload = new TrackedDownload
            {
                DownloadClient = 1,
                DownloadItem = new DownloadClientItem
                {
                    DownloadId = "download-audio-sibling",
                    Title = "Same.Title.Audiobook",
                    Status = DownloadItemStatus.Completed,
                    DownloadClientInfo = new DownloadClientItemClientInfo
                    {
                        Id = 1,
                        Name = "qBittorrent",
                        Protocol = DownloadProtocol.Torrent
                    }
                },
                RemoteBook = new RemoteBook
                {
                    Author = new Author { Id = 7, Name = "Test Author" },
                    ParsedBookInfo = new ParsedBookInfo
                    {
                        Quality = new QualityModel(Quality.MP3)
                    },
                    Books = new List<Book>
                    {
                        new() { Id = 10, AuthorId = 7, Title = "Expected Audiobook", MediaType = BookMediaType.Audiobook },
                        new() { Id = 11, AuthorId = 7, Title = "Expected Ebook", MediaType = BookMediaType.Ebook }
                    }
                }
            };

            var subject = new CompletedDownloadService(
                eventAggregator,
                historyService,
                new StubProvideImportItemService("/tmp/chaptarr-verify-import-sibling"),
                new RecordingDownloadedBooksImportService(),
                new PassthroughDownloadImportModeResolver(),
                new StubTrackedDownloadAlreadyImported { Result = false },
                NoopFailedDownloadService.Instance,
                LogManager.GetCurrentClassLogger(),
                NoopDownloadClientFileSnapshotService.Instance);

            var verified = subject.VerifyImport(trackedDownload, new List<ImportResult>
            {
                CreateImportedResult(10, 7, "Expected Audiobook", BookMediaType.Audiobook)
            });

            Assert.That(verified, Is.True);
            Assert.That(trackedDownload.State, Is.EqualTo(TrackedDownloadState.Imported));
            Assert.That(eventAggregator.Events, Has.One.InstanceOf<DownloadCompletedEvent>());
        }

        [Test, Platform(Exclude = "Win", Reason = "Test uses Unix paths")]
        public void import_should_not_fail_strict_verification_for_paired_ebook_sibling()
        {
            var eventAggregator = new RecordingEventAggregator();
            var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
            ((HistoryServiceProxy)(object)historyService).HistoryItems = new List<EntityHistory>
            {
                new EntityHistory
                {
                    EventType = EntityHistoryEventType.Grabbed,
                    DownloadId = "download-audio-import-sibling",
                    BookId = 10,
                    AuthorId = 7,
                    Date = DateTime.UtcNow
                }
            };

            var importService = new RecordingDownloadedBooksImportService
            {
                Results = new List<ImportResult>
                {
                    CreateImportedResult(10, 7, "Expected Audiobook", BookMediaType.Audiobook)
                }
            };

            var trackedDownload = new TrackedDownload
            {
                DownloadClient = 1,
                DownloadItem = new DownloadClientItem
                {
                    DownloadId = "download-audio-import-sibling",
                    Title = "Same.Title.Audiobook",
                    Status = DownloadItemStatus.Completed,
                    DownloadClientInfo = new DownloadClientItemClientInfo
                    {
                        Id = 1,
                        Name = "qBittorrent",
                        Protocol = DownloadProtocol.Torrent
                    }
                },
                RemoteBook = new RemoteBook
                {
                    Author = new Author { Id = 7, Name = "Test Author" },
                    ParsedBookInfo = new ParsedBookInfo
                    {
                        Quality = new QualityModel(Quality.MP3)
                    },
                    Books = new List<Book>
                    {
                        new() { Id = 10, AuthorId = 7, Title = "Expected Audiobook", MediaType = BookMediaType.Audiobook },
                        new() { Id = 11, AuthorId = 7, Title = "Expected Ebook", MediaType = BookMediaType.Ebook }
                    }
                },
                State = TrackedDownloadState.ImportPending
            };

            var subject = new CompletedDownloadService(
                eventAggregator,
                historyService,
                new StubProvideImportItemService("/tmp/chaptarr-strict-audio-sibling"),
                importService,
                new PassthroughDownloadImportModeResolver(),
                new StubTrackedDownloadAlreadyImported { Result = false },
                NoopFailedDownloadService.Instance,
                LogManager.GetCurrentClassLogger(),
                NoopDownloadClientFileSnapshotService.Instance);

            subject.Import(trackedDownload);

            Assert.That(trackedDownload.State, Is.EqualTo(TrackedDownloadState.Imported));
            Assert.That(eventAggregator.Events, Has.One.InstanceOf<DownloadCompletedEvent>());
            Assert.That(eventAggregator.Events, Has.None.InstanceOf<BookImportIncompleteEvent>());
        }

        [Test, Platform(Exclude = "Win", Reason = "Test uses Unix paths")]
        public void should_allow_best_effort_import_when_grabbed_target_allows_any_edition()
        {
            var eventAggregator = new RecordingEventAggregator();
            var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
            ((HistoryServiceProxy)(object)historyService).HistoryItems = new List<EntityHistory>
            {
                new EntityHistory
                {
                    EventType = EntityHistoryEventType.Grabbed,
                    DownloadId = "download-any-edition",
                    BookId = 10,
                    AuthorId = 7,
                    Date = DateTime.UtcNow
                }
            };

            var importService = new RecordingDownloadedBooksImportService
            {
                Results = new List<ImportResult>
                {
                    CreateImportedResult(999, 7, "Matched Different Edition")
                }
            };

            var trackedDownload = new TrackedDownload
            {
                DownloadClient = 1,
                DownloadItem = new DownloadClientItem
                {
                    DownloadId = "download-any-edition",
                    Title = "Flexible.Target",
                    Status = DownloadItemStatus.Completed,
                    DownloadClientInfo = new DownloadClientItemClientInfo
                    {
                        Id = 1,
                        Name = "qBittorrent",
                        Protocol = DownloadProtocol.Torrent
                    }
                },
                RemoteBook = new RemoteBook
                {
                    Author = new Author { Id = 7, Name = "Test Author" },
                    Books = new List<Book>
                    {
                        new() { Id = 10, AuthorId = 7, Title = "Original Target", AnyEditionOk = true }
                    }
                },
                State = TrackedDownloadState.ImportPending
            };

            var subject = new CompletedDownloadService(
                eventAggregator,
                historyService,
                new StubProvideImportItemService("/tmp/chaptarr-any-edition"),
                importService,
                new PassthroughDownloadImportModeResolver(),
                new StubTrackedDownloadAlreadyImported { Result = false },
                NoopFailedDownloadService.Instance,
                LogManager.GetCurrentClassLogger(),
                NoopDownloadClientFileSnapshotService.Instance);

            subject.Import(trackedDownload);

            Assert.That(trackedDownload.State, Is.EqualTo(TrackedDownloadState.Imported));
            Assert.That(eventAggregator.Events, Has.One.InstanceOf<DownloadCompletedEvent>());
            Assert.That(eventAggregator.Events, Has.None.InstanceOf<BookImportIncompleteEvent>());
        }

        [Test, Platform(Exclude = "Win", Reason = "Test uses Unix paths")]
        public void should_reject_mismatch_when_grabbed_target_has_manual_pinned_edition()
        {
            var eventAggregator = new RecordingEventAggregator();
            var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
            ((HistoryServiceProxy)(object)historyService).HistoryItems = new List<EntityHistory>
            {
                new EntityHistory
                {
                    EventType = EntityHistoryEventType.Grabbed,
                    DownloadId = "download-manual-pin",
                    BookId = 10,
                    AuthorId = 7,
                    Date = DateTime.UtcNow
                }
            };

            var importService = new RecordingDownloadedBooksImportService
            {
                Results = new List<ImportResult>
                {
                    CreateImportedResult(999, 7, "Matched Different Edition")
                }
            };

            var trackedDownload = new TrackedDownload
            {
                DownloadClient = 1,
                DownloadItem = new DownloadClientItem
                {
                    DownloadId = "download-manual-pin",
                    Title = "Pinned.Target",
                    Status = DownloadItemStatus.Completed,
                    DownloadClientInfo = new DownloadClientItemClientInfo
                    {
                        Id = 1,
                        Name = "qBittorrent",
                        Protocol = DownloadProtocol.Torrent
                    }
                },
                RemoteBook = new RemoteBook
                {
                    Author = new Author { Id = 7, Name = "Test Author" },
                    Books = new List<Book>
                    {
                        new()
                        {
                            Id = 10,
                            AuthorId = 7,
                            Title = "Pinned Target",
                            AnyEditionOk = true,
                            Editions = new List<Edition>
                            {
                                new() { Id = 100, BookId = 10, Title = "User Pinned Edition", Monitored = true, ManualAdd = true }
                            }
                        }
                    }
                },
                State = TrackedDownloadState.ImportPending
            };

            var subject = new CompletedDownloadService(
                eventAggregator,
                historyService,
                new StubProvideImportItemService("/tmp/chaptarr-manual-pin"),
                importService,
                new PassthroughDownloadImportModeResolver(),
                new StubTrackedDownloadAlreadyImported { Result = false },
                NoopFailedDownloadService.Instance,
                LogManager.GetCurrentClassLogger(),
                NoopDownloadClientFileSnapshotService.Instance);

            subject.Import(trackedDownload);

            Assert.That(trackedDownload.State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
            Assert.That(trackedDownload.StatusMessages.SelectMany(m => m.Messages), Has.Some.Contains("Pinned Target"));
            Assert.That(trackedDownload.StatusMessages.SelectMany(m => m.Messages), Has.Some.Contains("Matched Different Edition"));
            Assert.That(eventAggregator.Events, Has.None.InstanceOf<DownloadCompletedEvent>());
            Assert.That(eventAggregator.Events, Has.Some.InstanceOf<BookImportIncompleteEvent>());
        }

        [Test, Platform(Exclude = "Win", Reason = "Test uses Unix paths")]
        public void should_publish_incomplete_event_when_imported_books_do_not_match_grabbed_targets()
        {
            var eventAggregator = new RecordingEventAggregator();
            var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
            ((HistoryServiceProxy)(object)historyService).HistoryItems = new List<EntityHistory>
            {
                new EntityHistory
                {
                    EventType = EntityHistoryEventType.Grabbed,
                    DownloadId = "download-mismatch",
                    BookId = 10,
                    AuthorId = 7,
                    Date = DateTime.UtcNow
                }
            };

            var importService = new RecordingDownloadedBooksImportService
            {
                Results = new List<ImportResult>
                {
                    CreateImportedResult(999, 7, "Wrong Book")
                }
            };

            var trackedDownload = new TrackedDownload
            {
                DownloadClient = 1,
                DownloadItem = new DownloadClientItem
                {
                    DownloadId = "download-mismatch",
                    Title = "Wrong.Match",
                    Status = DownloadItemStatus.Completed,
                    DownloadClientInfo = new DownloadClientItemClientInfo
                    {
                        Id = 1,
                        Name = "qBittorrent",
                        Protocol = DownloadProtocol.Torrent
                    }
                },
                RemoteBook = new RemoteBook
                {
                    Author = new Author { Id = 7, Name = "Test Author" },
                    Books = new List<Book>
                    {
                        new() { Id = 10, AuthorId = 7, Title = "Expected One", AnyEditionOk = false }
                    }
                },
                State = TrackedDownloadState.ImportPending
            };

            var subject = new CompletedDownloadService(
                eventAggregator,
                historyService,
                new StubProvideImportItemService("/tmp/chaptarr-strict-mismatch"),
                importService,
                new PassthroughDownloadImportModeResolver(),
                new StubTrackedDownloadAlreadyImported { Result = false },
                NoopFailedDownloadService.Instance,
                LogManager.GetCurrentClassLogger(),
                NoopDownloadClientFileSnapshotService.Instance);

            subject.Import(trackedDownload);

            Assert.That(trackedDownload.State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
            Assert.That(trackedDownload.StatusMessages.SelectMany(m => m.Messages), Has.Some.Contains("Expected One"));
            Assert.That(trackedDownload.StatusMessages.SelectMany(m => m.Messages), Has.Some.Contains("Wrong Book"));
            Assert.That(eventAggregator.Events, Has.Some.InstanceOf<BookImportIncompleteEvent>());
            Assert.That(eventAggregator.Events, Has.None.InstanceOf<DownloadCompletedEvent>());
        }

        private static ImportResult CreateImportedResult(int bookId, int authorId, string title, BookMediaType mediaType = BookMediaType.Audiobook)
        {
            var author = new Author { Id = authorId, Name = "Test Author" };
            var book = new Book { Id = bookId, AuthorId = authorId, Author = author, Title = title, MediaType = mediaType };
            var localBook = new LocalBook
            {
                Author = author,
                Book = book
            };

            return new ImportResult(new ImportDecision<LocalBook>(localBook));
        }

        private static ImportResult CreateSkippedResult(int bookId, int authorId, string title, Quality quality)
        {
            var author = new Author { Id = authorId, Name = "Test Author" };
            var book = new Book { Id = bookId, AuthorId = authorId, Author = author, Title = title, MediaType = BookMediaType.Audiobook };
            var localBook = new LocalBook
            {
                Author = author,
                Book = book,
                Path = $"/tmp/{title}.m4b",
                Quality = new QualityModel(quality)
            };

            return new ImportResult(new ImportDecision<LocalBook>(localBook), "NO_MATCH_HOLY_GRAIL");
        }
    }
}
