using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class TrackedDownloadServiceFixture
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

        private sealed class TestParsingService : IParsingService
        {
            public int MapCalls { get; private set; }
            public int MapWithIdsCalls { get; private set; }

            public Author GetAuthor(string title) => throw new NotImplementedException();

            public RemoteBook Map(ParsedBookInfo parsedBookInfo, SearchCriteriaBase searchCriteria = null)
            {
                MapCalls++;
                return new RemoteBook
                {
                    ParsedBookInfo = parsedBookInfo
                };
            }

            public RemoteBook Map(ParsedBookInfo parsedBookInfo, int authorId, IEnumerable<int> bookIds)
            {
                MapWithIdsCalls++;
                throw new InvalidOperationException("TrackedDownloadService should not map downloads using authorId/bookIds during cache refresh.");
            }

            public List<Book> GetBooks(ParsedBookInfo parsedBookInfo, Author author, SearchCriteriaBase searchCriteria = null) => throw new NotImplementedException();
            public ParsedBookInfo ParseBookTitleFuzzy(string title) => throw new NotImplementedException();
            public Book GetLocalBook(string filename, Author author) => throw new NotImplementedException();
        }

        private sealed class HistoryRecoveryParsingService : IParsingService
        {
            public int MapCalls { get; private set; }
            public int MapWithIdsCalls { get; private set; }
            public int LastAuthorId { get; private set; }
            public List<int> LastBookIds { get; private set; }

            public Author GetAuthor(string title) => throw new NotImplementedException();

            public RemoteBook Map(ParsedBookInfo parsedBookInfo, SearchCriteriaBase searchCriteria = null)
            {
                MapCalls++;
                return new RemoteBook
                {
                    ParsedBookInfo = parsedBookInfo
                };
            }

            public RemoteBook Map(ParsedBookInfo parsedBookInfo, int authorId, IEnumerable<int> bookIds)
            {
                MapWithIdsCalls++;
                LastAuthorId = authorId;
                LastBookIds = bookIds?.ToList() ?? new List<int>();

                return new RemoteBook
                {
                    ParsedBookInfo = parsedBookInfo,
                    Author = authorId > 0 ? new Author { Id = authorId, Name = "Cory Doctorow" } : null,
                    Books = LastBookIds.Select(id => new Book
                    {
                        Id = id,
                        AuthorId = authorId,
                        Title = "Enshittification: Why Everything Suddenly Got Worse and What to Do About It",
                        MediaType = BookMediaType.Audiobook
                    }).ToList()
                };
            }

            public List<Book> GetBooks(ParsedBookInfo parsedBookInfo, Author author, SearchCriteriaBase searchCriteria = null) => throw new NotImplementedException();
            public ParsedBookInfo ParseBookTitleFuzzy(string title) => throw new NotImplementedException();
            public Book GetLocalBook(string filename, Author author) => throw new NotImplementedException();
        }

        private sealed class DirectMatchParsingService : IParsingService
        {
            public Author Author { get; set; } = new Author { Id = 85, Name = "Brandon Sanderson" };

            public Author GetAuthor(string title) => throw new NotImplementedException();

            public RemoteBook Map(ParsedBookInfo parsedBookInfo, SearchCriteriaBase searchCriteria = null)
            {
                return new RemoteBook
                {
                    ParsedBookInfo = parsedBookInfo,
                    Author = Author,
                    Books = new List<Book>
                    {
                        new()
                        {
                            Id = 5651,
                            AuthorId = 85,
                            Title = "The Hero of Ages",
                            MediaType = BookMediaType.Audiobook
                        }
                    }
                };
            }

            // A Chaptarr-owned grab is targeted by the ids recorded against its DownloadId, so this
            // overload is now the normal path for downloads with grab history (2026-07-25).
            public RemoteBook Map(ParsedBookInfo parsedBookInfo, int authorId, IEnumerable<int> bookIds)
            {
                LastBookIds = bookIds?.ToList() ?? new List<int>();

                return new RemoteBook
                {
                    ParsedBookInfo = parsedBookInfo,
                    Author = Author,
                    Books = LastBookIds
                        .Select(id => new Book
                        {
                            Id = id,
                            AuthorId = Author.Id,
                            Title = "The Hero of Ages",
                            MediaType = BookMediaType.Audiobook
                        })
                        .ToList()
                };
            }

            public List<int> LastBookIds { get; private set; } = new List<int>();

            public List<Book> GetBooks(ParsedBookInfo parsedBookInfo, Author author, SearchCriteriaBase searchCriteria = null) => throw new NotImplementedException();
            public ParsedBookInfo ParseBookTitleFuzzy(string title) => throw new NotImplementedException();
            public Book GetLocalBook(string filename, Author author) => throw new NotImplementedException();
        }

        private class HistoryServiceProxy : DispatchProxy
        {
            public List<EntityHistory> HistoryItems { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IHistoryService.FindByDownloadId))
                {
                    return HistoryItems;
                }

                if (targetMethod?.Name == nameof(IHistoryService.MostRecentForDownloadId))
                {
                    return HistoryItems.Count > 0 ? HistoryItems[0] : null;
                }

                throw new NotImplementedException($"Test proxy does not implement IHistoryService.{targetMethod?.Name}");
            }
        }

        private class DownloadHistoryServiceProxy : DispatchProxy
        {
            public DownloadHistory Latest { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IDownloadHistoryService.GetLatestDownloadHistoryItem))
                {
                    return Latest;
                }

                if (targetMethod?.Name == nameof(IDownloadHistoryService.GetLatestGrab))
                {
                    return Latest;
                }

                if (targetMethod?.Name == nameof(IDownloadHistoryService.DownloadAlreadyImported))
                {
                    return false;
                }

                throw new NotImplementedException($"Test proxy does not implement IDownloadHistoryService.{targetMethod?.Name}");
            }
        }

        private sealed class StubCustomFormatCalculationService : ICustomFormatCalculationService
        {
            private readonly List<CustomFormat> _formats;

            public StubCustomFormatCalculationService(params CustomFormat[] formats)
            {
                _formats = formats.ToList();
            }

            public List<CustomFormat> ParseCustomFormat(RemoteBook remoteBook, long size) => _formats;
            public List<CustomFormat> ParseCustomFormat(BookFile bookFile, Author artist) => throw new NotImplementedException();
            public List<CustomFormat> ParseCustomFormat(BookFile bookFile) => throw new NotImplementedException();
            public List<CustomFormat> ParseCustomFormat(Blocklist blocklist, Author artist) => throw new NotImplementedException();
            public List<CustomFormat> ParseCustomFormat(EntityHistory history, Author artist) => throw new NotImplementedException();
            public List<CustomFormat> ParseCustomFormat(LocalBook localBook) => throw new NotImplementedException();
        }

        private class ThrowingProxy<T> : DispatchProxy where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private static TrackedDownloadService CreateUpdateTrackableSubject(CacheManager cacheManager)
        {
            return new TrackedDownloadService(
                DispatchProxy.Create<IParsingService, ThrowingProxy<IParsingService>>(),
                cacheManager,
                DispatchProxy.Create<IHistoryService, ThrowingProxy<IHistoryService>>(),
                new RecordingEventAggregator(),
                DispatchProxy.Create<IDownloadHistoryService, ThrowingProxy<IDownloadHistoryService>>(),
                DispatchProxy.Create<ICustomFormatCalculationService, ThrowingProxy<ICustomFormatCalculationService>>(),
                LogManager.GetCurrentClassLogger(),
                NoopDownloadClientFileSnapshotService.Instance);
        }

        private static TrackedDownload CreateTrackedDownload(string downloadId, TrackedDownloadState state)
        {
            return new TrackedDownload
            {
                State = state,
                IsTrackable = true,
                DownloadItem = new NzbDrone.Core.Download.DownloadClientItem
                {
                    DownloadId = downloadId,
                    Title = downloadId,
                    Status = DownloadItemStatus.Completed,
                    DownloadClientInfo = new NzbDrone.Core.Download.DownloadClientItemClientInfo
                    {
                        Name = "sabnzb",
                        Protocol = DownloadProtocol.Usenet
                    }
                }
            };
        }

        [Test]
        public void should_not_throw_when_author_is_deleted_and_tracked_download_cache_is_refreshed()
        {
            var parsingService = new TestParsingService();
            var cacheManager = new CacheManager();
            var eventAggregator = new RecordingEventAggregator();

            var service = new TrackedDownloadService(
                parsingService,
                cacheManager,
                DispatchProxy.Create<IHistoryService, ThrowingProxy<IHistoryService>>(),
                eventAggregator,
                DispatchProxy.Create<IDownloadHistoryService, ThrowingProxy<IDownloadHistoryService>>(),
                DispatchProxy.Create<ICustomFormatCalculationService, ThrowingProxy<ICustomFormatCalculationService>>(),
                LogManager.GetCurrentClassLogger(),
                NoopDownloadClientFileSnapshotService.Instance);

            var author = new Author
            {
                Id = 42,
                Name = "Travis Beacham"
            };

            var trackedDownload = new TrackedDownload
            {
                DownloadItem = new NzbDrone.Core.Download.DownloadClientItem
                {
                    DownloadId = "download-1",
                    Title = "Impact Winter by Travis Beacham"
                },
                RemoteBook = new RemoteBook
                {
                    Author = author
                }
            };

            cacheManager.GetCache<TrackedDownload>(service.GetType()).Set(trackedDownload.DownloadItem.DownloadId, trackedDownload);

            Assert.DoesNotThrow(() => service.Handle(new AuthorDeletedEvent(author, deleteFiles: false, addImportListExclusion: false)));
            Assert.That(parsingService.MapCalls, Is.EqualTo(1));
            Assert.That(parsingService.MapWithIdsCalls, Is.EqualTo(0));
            Assert.That(eventAggregator.Events, Has.Some.InstanceOf<TrackedDownloadRefreshedEvent>());
        }

        [Test]
        public void should_rehydrate_remote_book_from_history_ids_when_titles_cannot_be_parsed()
        {
            var parsingService = new HistoryRecoveryParsingService();
            var cacheManager = new CacheManager();
            var eventAggregator = new RecordingEventAggregator();

            var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
            ((HistoryServiceProxy)(object)historyService).HistoryItems = new List<EntityHistory>
            {
                new()
                {
                    EventType = EntityHistoryEventType.Grabbed,
                    DownloadId = "D266F429499B6978E951D60C6F9F9159F67744CA",
                    AuthorId = 76,
                    BookId = 4634,
                    SourceTitle = "Enshittification: Why Everything Suddenly Got Worse and What to Do about It",
                    Date = DateTime.UtcNow
                }
            };

            var downloadHistoryService = DispatchProxy.Create<IDownloadHistoryService, DownloadHistoryServiceProxy>();

            var service = new TrackedDownloadService(
                parsingService,
                cacheManager,
                historyService,
                eventAggregator,
                downloadHistoryService,
                new StubCustomFormatCalculationService(),
                LogManager.GetCurrentClassLogger(),
                NoopDownloadClientFileSnapshotService.Instance);

            var trackedDownload = service.TrackDownload(
                new DownloadClientDefinition
                {
                    Id = 5,
                    Name = "binhex",
                    Protocol = DownloadProtocol.Torrent
                },
                new NzbDrone.Core.Download.DownloadClientItem
                {
                    DownloadId = "D266F429499B6978E951D60C6F9F9159F67744CA",
                    Title = "Cory Doctorow - Enshittification",
                    DownloadClientInfo = new NzbDrone.Core.Download.DownloadClientItemClientInfo
                    {
                        Name = "binhex",
                        Protocol = DownloadProtocol.Torrent
                    }
            });

            Assert.That(trackedDownload, Is.Not.Null);
            Assert.That(parsingService.MapWithIdsCalls, Is.EqualTo(1));
            Assert.That(parsingService.LastAuthorId, Is.EqualTo(76));
            Assert.That(parsingService.LastBookIds, Is.EqualTo(new List<int> { 4634 }));
            Assert.That(trackedDownload.RemoteBook, Is.Not.Null);
            Assert.That(trackedDownload.RemoteBook.Author?.Id, Is.EqualTo(76));
            Assert.That(trackedDownload.RemoteBook.Books.Select(book => book.Id).ToList(), Is.EqualTo(new List<int> { 4634 }));
        }

        [Test]
        public void should_restore_queue_grab_metadata_from_history_after_restart()
        {
            var parsingService = new HistoryRecoveryParsingService();
            var cacheManager = new CacheManager();
            var eventAggregator = new RecordingEventAggregator();
            var grabbedAt = new DateTime(2026, 05, 04, 12, 30, 00, DateTimeKind.Utc);

            var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
            ((HistoryServiceProxy)(object)historyService).HistoryItems = new List<EntityHistory>
            {
                new()
                {
                    EventType = EntityHistoryEventType.Grabbed,
                    DownloadId = "DOWNLOAD-QUEUE-METADATA",
                    AuthorId = 76,
                    BookId = 4634,
                    SourceTitle = "Cory Doctorow - Enshittification [MP3]",
                    Date = grabbedAt,
                    Quality = new NzbDrone.Core.Qualities.QualityModel(NzbDrone.Core.Qualities.Quality.MP3),
                    Data = new Dictionary<string, string>
                    {
                        ["Indexer"] = "MyAnonamouse",
                        ["IndexerFlags"] = "Freeleech",
                        ["Size"] = "123456"
                    }
                }
            };

            var downloadHistoryService = DispatchProxy.Create<IDownloadHistoryService, DownloadHistoryServiceProxy>();
            ((DownloadHistoryServiceProxy)(object)downloadHistoryService).Latest = new DownloadHistory
            {
                EventType = DownloadHistoryEventType.DownloadGrabbed,
                DownloadId = "DOWNLOAD-QUEUE-METADATA",
                Date = grabbedAt,
                IndexerId = 99,
                Protocol = DownloadProtocol.Torrent,
                Release = new ReleaseInfo
                {
                    Indexer = "MyAnonamouse",
                    IndexerId = 99,
                    Title = "Cory Doctorow - Enshittification [MP3]",
                    Size = 123456,
                    Guid = "mam:123",
                    DownloadProtocol = DownloadProtocol.Torrent
                }
            };

            var service = new TrackedDownloadService(
                parsingService,
                cacheManager,
                historyService,
                eventAggregator,
                downloadHistoryService,
                new StubCustomFormatCalculationService(),
                LogManager.GetCurrentClassLogger(),
                NoopDownloadClientFileSnapshotService.Instance);

            var trackedDownload = service.TrackDownload(
                new DownloadClientDefinition
                {
                    Id = 5,
                    Name = "binhex",
                    Protocol = DownloadProtocol.Torrent
                },
                new NzbDrone.Core.Download.DownloadClientItem
                {
                    DownloadId = "download-queue-metadata",
                    Title = "Cory Doctorow - Enshittification",
                    TotalSize = 123456,
                    DownloadClientInfo = new NzbDrone.Core.Download.DownloadClientItemClientInfo
                    {
                        Name = "binhex",
                        Protocol = DownloadProtocol.Torrent
                    }
                });

            Assert.That(trackedDownload, Is.Not.Null);
            Assert.That(trackedDownload.Indexer, Is.EqualTo("MyAnonamouse"));
            Assert.That(trackedDownload.Added, Is.EqualTo(grabbedAt));
            Assert.That(trackedDownload.RemoteBook?.Release, Is.Not.Null);
            Assert.That(trackedDownload.RemoteBook.Release.Indexer, Is.EqualTo("MyAnonamouse"));
            Assert.That(trackedDownload.RemoteBook.Release.IndexerId, Is.EqualTo(99));
            Assert.That(trackedDownload.RemoteBook.Release.Title, Is.EqualTo("Cory Doctorow - Enshittification [MP3]"));
            Assert.That(trackedDownload.RemoteBook.Release.Size, Is.EqualTo(123456));
            Assert.That(trackedDownload.RemoteBook.Release.IndexerFlags, Is.EqualTo(IndexerFlags.Freeleech));
            Assert.That(trackedDownload.RemoteBook.Release.DownloadProtocol, Is.EqualTo(DownloadProtocol.Torrent));
        }

        [Test]
        public void should_restore_exact_quality_from_grab_history_when_download_title_only_parses_unknown_audio()
        {
            var parsingService = new DirectMatchParsingService();
            var format = new CustomFormat
            {
                Id = 10,
                Name = "Preferred Narrators",
                AppliesTo = CustomFormatMediaType.Audiobook
            };
            var profile = new QualityProfile
            {
                Id = 20,
                ProfileType = ProfileType.Audiobook,
                FormatItems = new List<ProfileFormatItem>
                {
                    new() { Format = format, Score = 50 }
                }
            };
            parsingService.Author.AudiobookQualityProfileId = profile.Id;
            parsingService.Author.AudiobookQualityProfile = new LazyLoaded<QualityProfile>(profile);
            var cacheManager = new CacheManager();
            var eventAggregator = new RecordingEventAggregator();

            var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
            ((HistoryServiceProxy)(object)historyService).HistoryItems = new List<EntityHistory>
            {
                new()
                {
                    EventType = EntityHistoryEventType.Grabbed,
                    DownloadId = "HERO-OF-AGES",
                    AuthorId = 85,
                    BookId = 5651,
                    SourceTitle = "The Hero of Ages",
                    Date = DateTime.UtcNow,
                    Quality = new QualityModel(Quality.M4B),
                    Data = new Dictionary<string, string>
                    {
                        ["DownloadForced"] = bool.FalseString,
                        ["ReleaseSource"] = ReleaseSourceType.InteractiveSearch.ToString()
                    }
                }
            };

            var downloadHistoryService = DispatchProxy.Create<IDownloadHistoryService, DownloadHistoryServiceProxy>();

            var service = new TrackedDownloadService(
                parsingService,
                cacheManager,
                historyService,
                eventAggregator,
                downloadHistoryService,
                new StubCustomFormatCalculationService(format),
                LogManager.GetCurrentClassLogger(),
                NoopDownloadClientFileSnapshotService.Instance);

            var trackedDownload = service.TrackDownload(
                new DownloadClientDefinition
                {
                    Id = 5,
                    Name = "binhex",
                    Protocol = DownloadProtocol.Torrent
                },
                new NzbDrone.Core.Download.DownloadClientItem
                {
                    DownloadId = "hero-of-ages",
                    Title = "Brandon Sanderson - Mistborn 03 - The Hero of Ages (GraphicAudio)",
                    DownloadClientInfo = new NzbDrone.Core.Download.DownloadClientItemClientInfo
                    {
                        Name = "binhex",
                        Protocol = DownloadProtocol.Torrent
                    }
                });

            Assert.That(trackedDownload, Is.Not.Null);
            Assert.That(trackedDownload.RemoteBook?.ParsedBookInfo?.Quality?.Quality, Is.EqualTo(Quality.M4B));
            Assert.That(trackedDownload.RemoteBook?.CustomFormatScore, Is.EqualTo(50));
            Assert.That(trackedDownload.DownloadItem.DownloadForced, Is.True);

            // The books recorded against this DownloadId are the target; the client title only
            // supplied metadata.
            Assert.That(parsingService.LastBookIds, Is.EqualTo(new[] { 5651 }));
            Assert.That(trackedDownload.RemoteBook?.Books?.Select(b => b.Id), Is.EqualTo(new[] { 5651 }));
        }

        [Test]
        public void should_keep_the_grabbed_target_when_the_client_title_would_parse_to_other_books()
        {
            var parsingService = new DirectMatchParsingService();
            var cacheManager = new CacheManager();
            var eventAggregator = new RecordingEventAggregator();

            var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
            ((HistoryServiceProxy)(object)historyService).HistoryItems = new List<EntityHistory>
            {
                new()
                {
                    EventType = EntityHistoryEventType.Grabbed,
                    DownloadId = "captains-fury",
                    AuthorId = 85,
                    BookId = 4242,
                    SourceTitle = "Captain's Fury (Codex Alera 2), epub, please...thanks - Codex Alera 04 - Captain's Fury.mobi",
                    Date = DateTime.UtcNow,
                    Quality = new QualityModel(Quality.MOBI),
                    Data = new Dictionary<string, string>()
                }
            };

            var service = new TrackedDownloadService(
                parsingService,
                cacheManager,
                historyService,
                eventAggregator,
                DispatchProxy.Create<IDownloadHistoryService, DownloadHistoryServiceProxy>(),
                new StubCustomFormatCalculationService(),
                LogManager.GetCurrentClassLogger(),
                NoopDownloadClientFileSnapshotService.Instance);

            var trackedDownload = service.TrackDownload(
                new DownloadClientDefinition { Id = 5, Name = "sab", Protocol = DownloadProtocol.Usenet },
                new NzbDrone.Core.Download.DownloadClientItem
                {
                    DownloadId = "captains-fury",
                    Title = "Captain's Fury (Codex Alera 2), epub, please...thanks - Codex Alera 04 - Captain's Fury.mobi",
                    DownloadClientInfo = new NzbDrone.Core.Download.DownloadClientItemClientInfo
                    {
                        Name = "sab",
                        Protocol = DownloadProtocol.Usenet
                    }
                });

            // A title naming two series positions must not turn one grab into two expected books.
            Assert.That(parsingService.LastBookIds, Is.EqualTo(new[] { 4242 }));
            Assert.That(trackedDownload.RemoteBook?.Books?.Select(b => b.Id), Is.EqualTo(new[] { 4242 }));
        }

        [Test]
        public void should_fall_back_to_title_matching_when_the_grabbed_book_row_is_gone()
        {
            // The 2026-05-23 stale-target contract: a deleted or merged local book row must not
            // strand the download. GetExistingBooks drops the id, and title matching takes over
            // rather than the import being blocked on a target that no longer exists.
            var parsingService = new StaleTargetParsingService();
            var cacheManager = new CacheManager();
            var eventAggregator = new RecordingEventAggregator();

            var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
            ((HistoryServiceProxy)(object)historyService).HistoryItems = new List<EntityHistory>
            {
                new()
                {
                    EventType = EntityHistoryEventType.Grabbed,
                    DownloadId = "hero-of-ages",
                    AuthorId = 85,
                    BookId = 999999, // row since deleted
                    SourceTitle = "Brandon Sanderson - The Hero of Ages",
                    Date = DateTime.UtcNow,
                    Quality = new QualityModel(Quality.M4B),
                    Data = new Dictionary<string, string>()
                }
            };

            var service = new TrackedDownloadService(
                parsingService,
                cacheManager,
                historyService,
                eventAggregator,
                DispatchProxy.Create<IDownloadHistoryService, DownloadHistoryServiceProxy>(),
                new StubCustomFormatCalculationService(),
                LogManager.GetCurrentClassLogger(),
                NoopDownloadClientFileSnapshotService.Instance);

            var trackedDownload = service.TrackDownload(
                new DownloadClientDefinition { Id = 5, Name = "binhex", Protocol = DownloadProtocol.Torrent },
                new NzbDrone.Core.Download.DownloadClientItem
                {
                    DownloadId = "hero-of-ages",
                    Title = "Brandon Sanderson - Mistborn 03 - The Hero of Ages",
                    DownloadClientInfo = new NzbDrone.Core.Download.DownloadClientItemClientInfo
                    {
                        Name = "binhex",
                        Protocol = DownloadProtocol.Torrent
                    }
                });

            Assert.That(parsingService.MapWithIdsCalls, Is.GreaterThanOrEqualTo(1), "the grabbed target should be tried first");
            Assert.That(trackedDownload, Is.Not.Null, "a dead grabbed id must not strand the download");

            var trackedBookIds = trackedDownload.RemoteBook?.Books?.Select(b => b.Id).ToList() ?? new List<int>();
            Assert.That(trackedBookIds, Does.Not.Contain(999999), "the deleted row must not survive as a phantom target");
        }

        /// <summary>
        /// Grabbed ids resolve to nothing (rows deleted), exactly as GetExistingBooks behaves for
        /// stale ids; title matching still finds a book.
        /// </summary>
        private sealed class StaleTargetParsingService : IParsingService
        {
            public Author Author { get; set; } = new Author { Id = 85, Name = "Brandon Sanderson" };
            public int MapWithIdsCalls { get; private set; }

            public Author GetAuthor(string title) => throw new NotImplementedException();

            public RemoteBook Map(ParsedBookInfo parsedBookInfo, SearchCriteriaBase searchCriteria = null)
            {
                return new RemoteBook
                {
                    ParsedBookInfo = parsedBookInfo,
                    Author = Author,
                    Books = new List<Book>
                    {
                        new() { Id = 5651, AuthorId = 85, Title = "The Hero of Ages", MediaType = BookMediaType.Audiobook }
                    }
                };
            }

            public RemoteBook Map(ParsedBookInfo parsedBookInfo, int authorId, IEnumerable<int> bookIds)
            {
                MapWithIdsCalls++;

                return new RemoteBook
                {
                    ParsedBookInfo = parsedBookInfo,
                    Author = Author,
                    Books = new List<Book>()
                };
            }

            public List<Book> GetBooks(ParsedBookInfo parsedBookInfo, Author author, SearchCriteriaBase searchCriteria = null) => throw new NotImplementedException();
            public ParsedBookInfo ParseBookTitleFuzzy(string title) => throw new NotImplementedException();
            public Book GetLocalBook(string filename, Author author) => throw new NotImplementedException();
        }

        [Test]
        public void should_retrack_same_hash_when_a_new_grab_was_recorded_after_terminal_state()
        {
            var parsingService = new HistoryRecoveryParsingService();
            var cacheManager = new CacheManager();
            var eventAggregator = new RecordingEventAggregator();
            var grabbedAt = new DateTime(2026, 05, 04, 16, 22, 46, DateTimeKind.Utc);

            var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
            ((HistoryServiceProxy)(object)historyService).HistoryItems = new List<EntityHistory>
            {
                new()
                {
                    EventType = EntityHistoryEventType.Grabbed,
                    DownloadId = "SAME-HASH",
                    AuthorId = 44,
                    BookId = 4990,
                    SourceTitle = "Harry Potter And The Goblet Of Fire",
                    Date = grabbedAt
                }
            };

            var downloadHistoryService = DispatchProxy.Create<IDownloadHistoryService, DownloadHistoryServiceProxy>();
            ((DownloadHistoryServiceProxy)(object)downloadHistoryService).Latest = new DownloadHistory
            {
                EventType = DownloadHistoryEventType.DownloadGrabbed,
                DownloadId = "SAME-HASH",
                Date = grabbedAt,
                Protocol = DownloadProtocol.Torrent,
                Release = new ReleaseInfo
                {
                    Indexer = "MyAnonaMouse",
                    Title = "Harry Potter And The Goblet Of Fire",
                    DownloadProtocol = DownloadProtocol.Torrent
                }
            };

            var service = new TrackedDownloadService(
                parsingService,
                cacheManager,
                historyService,
                eventAggregator,
                downloadHistoryService,
                new StubCustomFormatCalculationService(),
                LogManager.GetCurrentClassLogger(),
                NoopDownloadClientFileSnapshotService.Instance);

            cacheManager.GetCache<TrackedDownload>(service.GetType()).Set("SAME-HASH", new TrackedDownload
            {
                State = TrackedDownloadState.Imported,
                IsTrackable = true,
                DownloadItem = new NzbDrone.Core.Download.DownloadClientItem
                {
                    DownloadId = "SAME-HASH",
                    Title = "Old imported queue item",
                    Status = DownloadItemStatus.Completed,
                    DownloadClientInfo = new NzbDrone.Core.Download.DownloadClientItemClientInfo
                    {
                        Name = "binhex",
                        Protocol = DownloadProtocol.Torrent
                    }
                },
                RemoteBook = new RemoteBook
                {
                    Books = new List<Book>
                    {
                        new()
                        {
                            Id = 2017,
                            AuthorId = 44,
                            Title = "Harry Potter and the Goblet of Fire",
                            MediaType = BookMediaType.Audiobook
                        }
                    }
                }
            });

            var trackedDownload = service.TrackDownload(
                new DownloadClientDefinition
                {
                    Id = 5,
                    Name = "binhex",
                    Protocol = DownloadProtocol.Torrent
                },
                new NzbDrone.Core.Download.DownloadClientItem
                {
                    DownloadId = "SAME-HASH",
                    Title = "Harry Potter And The Goblet Of Fire",
                    Status = DownloadItemStatus.Downloading,
                    DownloadClientInfo = new NzbDrone.Core.Download.DownloadClientItemClientInfo
                    {
                        Name = "binhex",
                        Protocol = DownloadProtocol.Torrent
                    }
                });

            Assert.That(trackedDownload, Is.Not.Null);
            Assert.That(trackedDownload.State, Is.EqualTo(TrackedDownloadState.Downloading));
            Assert.That(parsingService.MapWithIdsCalls, Is.EqualTo(1));
            Assert.That(parsingService.LastAuthorId, Is.EqualTo(44));
            Assert.That(parsingService.LastBookIds, Is.EqualTo(new List<int> { 4990 }));
        }

        [TestCase(TrackedDownloadState.Importing)]
        [TestCase(TrackedDownloadState.ImportBlocked)]
        public void should_not_rebuild_non_downloading_import_state_when_latest_history_is_still_grabbed(TrackedDownloadState existingState)
        {
            var repairDelayedGrabHistory = existingState == TrackedDownloadState.ImportBlocked;
            var parsingService = new HistoryRecoveryParsingService();
            var cacheManager = new CacheManager();
            var eventAggregator = new RecordingEventAggregator();

            var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
            ((HistoryServiceProxy)(object)historyService).HistoryItems = new List<EntityHistory>
            {
                new()
                {
                    EventType = EntityHistoryEventType.Grabbed,
                    DownloadId = "LONG-CONVERSION",
                    AuthorId = 95,
                    BookId = 6465,
                    SourceTitle = "The 5 Second Rule",
                    Date = DateTime.UtcNow,
                    Data = new Dictionary<string, string>
                    {
                        ["DownloadForced"] = bool.FalseString,
                        ["ReleaseSource"] = ReleaseSourceType.InteractiveSearch.ToString()
                    }
                }
            };

            var downloadHistoryService = DispatchProxy.Create<IDownloadHistoryService, DownloadHistoryServiceProxy>();
            ((DownloadHistoryServiceProxy)(object)downloadHistoryService).Latest = new DownloadHistory
            {
                EventType = DownloadHistoryEventType.DownloadGrabbed,
                DownloadId = "LONG-CONVERSION",
                Date = DateTime.UtcNow,
                Protocol = DownloadProtocol.Torrent
            };

            var service = new TrackedDownloadService(
                parsingService,
                cacheManager,
                historyService,
                eventAggregator,
                downloadHistoryService,
                new StubCustomFormatCalculationService(),
                LogManager.GetCurrentClassLogger(),
                NoopDownloadClientFileSnapshotService.Instance);

            var cached = new TrackedDownload
            {
                State = existingState,
                IsTrackable = true,
                DownloadItem = new NzbDrone.Core.Download.DownloadClientItem
                {
                    DownloadId = "LONG-CONVERSION",
                    Title = "The 5 Second Rule by Mel Robbins (Audio)",
                    Status = DownloadItemStatus.Completed,
                    DownloadForced = !repairDelayedGrabHistory,
                    DownloadClientInfo = new NzbDrone.Core.Download.DownloadClientItemClientInfo
                    {
                        Name = "binhex",
                        Protocol = DownloadProtocol.Torrent
                    }
                },
                RemoteBook = new RemoteBook
                {
                    Books = new List<Book>
                    {
                        new()
                        {
                            Id = 6465,
                            AuthorId = 95,
                            Title = "The 5 Second Rule",
                            MediaType = BookMediaType.Audiobook
                        }
                    }
                }
            };

            cacheManager.GetCache<TrackedDownload>(service.GetType()).Set("LONG-CONVERSION", cached);

            var trackedDownload = service.TrackDownload(
                new DownloadClientDefinition
                {
                    Id = 5,
                    Name = "binhex",
                    Protocol = DownloadProtocol.Torrent
                },
                new NzbDrone.Core.Download.DownloadClientItem
                {
                    DownloadId = "LONG-CONVERSION",
                    Title = "The 5 Second Rule by Mel Robbins (Audio)",
                    Status = DownloadItemStatus.Completed,
                    DownloadClientInfo = new NzbDrone.Core.Download.DownloadClientItemClientInfo
                    {
                        Name = "binhex",
                        Protocol = DownloadProtocol.Torrent
                    }
                });

            Assert.That(trackedDownload, Is.SameAs(cached));
            Assert.That(trackedDownload.State, Is.EqualTo(existingState));
            Assert.That(trackedDownload.DownloadItem.DownloadForced, Is.True);
            Assert.That(trackedDownload.Added.HasValue, Is.EqualTo(repairDelayedGrabHistory));
            Assert.That(parsingService.MapWithIdsCalls, Is.EqualTo(0));
        }

        [TestCase(TrackedDownloadState.ImportPending)]
        [TestCase(TrackedDownloadState.Importing)]
        public void should_keep_missing_import_pipeline_download_trackable(TrackedDownloadState state)
        {
            var cacheManager = new CacheManager();
            var service = CreateUpdateTrackableSubject(cacheManager);
            var cached = CreateTrackedDownload($"missing-{state}", state);

            cacheManager.GetCache<TrackedDownload>(service.GetType()).Set(cached.DownloadItem.DownloadId, cached);

            var refreshed = new List<TrackedDownload>();

            service.UpdateTrackable(refreshed);

            Assert.That(cached.IsTrackable, Is.True);
            Assert.That(refreshed, Has.Count.EqualTo(1));
            Assert.That(refreshed[0], Is.SameAs(cached));
        }

        [TestCase(TrackedDownloadState.Downloading)]
        [TestCase(TrackedDownloadState.ImportBlocked)]
        [TestCase(TrackedDownloadState.Imported)]
        public void should_drop_missing_non_importing_download_from_trackable_refresh(TrackedDownloadState state)
        {
            var cacheManager = new CacheManager();
            var service = CreateUpdateTrackableSubject(cacheManager);
            var cached = CreateTrackedDownload($"missing-{state}", state);

            cacheManager.GetCache<TrackedDownload>(service.GetType()).Set(cached.DownloadItem.DownloadId, cached);

            var refreshed = new List<TrackedDownload>();

            service.UpdateTrackable(refreshed);

            Assert.That(cached.IsTrackable, Is.False);
            Assert.That(refreshed, Is.Empty);
        }

        [Test]
        public void should_not_capture_snapshot_for_imported_download_after_cache_loss()
        {
            var snapshotService = new RecordingDownloadClientFileSnapshotService();
            var downloadHistoryService = DispatchProxy.Create<IDownloadHistoryService, DownloadHistoryServiceProxy>();
            ((DownloadHistoryServiceProxy)(object)downloadHistoryService).Latest = new DownloadHistory
            {
                EventType = DownloadHistoryEventType.DownloadImported,
                DownloadId = "restarted-imported-download"
            };

            var service = new TrackedDownloadService(
                new TestParsingService(),
                new CacheManager(),
                DispatchProxy.Create<IHistoryService, HistoryServiceProxy>(),
                new RecordingEventAggregator(),
                downloadHistoryService,
                new StubCustomFormatCalculationService(),
                LogManager.GetCurrentClassLogger(),
                snapshotService);

            var tracked = service.TrackDownload(
                new DownloadClientDefinition { Id = 1, Name = "sabnzb", Protocol = DownloadProtocol.Usenet },
                CreateTrackedDownload("restarted-imported-download", TrackedDownloadState.Imported).DownloadItem);

            Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.Imported));
            Assert.That(snapshotService.CaptureClientListCalls, Is.EqualTo(0), "an imported download must not re-create its deleted snapshot after a restart");
        }

        [Test]
        public void should_capture_snapshot_for_new_download_without_history()
        {
            var snapshotService = new RecordingDownloadClientFileSnapshotService();
            var downloadHistoryService = DispatchProxy.Create<IDownloadHistoryService, DownloadHistoryServiceProxy>();

            var service = new TrackedDownloadService(
                new TestParsingService(),
                new CacheManager(),
                DispatchProxy.Create<IHistoryService, HistoryServiceProxy>(),
                new RecordingEventAggregator(),
                downloadHistoryService,
                new StubCustomFormatCalculationService(),
                LogManager.GetCurrentClassLogger(),
                snapshotService);

            service.TrackDownload(
                new DownloadClientDefinition { Id = 1, Name = "sabnzb", Protocol = DownloadProtocol.Usenet },
                CreateTrackedDownload("fresh-download", TrackedDownloadState.Downloading).DownloadItem);

            Assert.That(snapshotService.CaptureClientListCalls, Is.EqualTo(1));
        }

        private sealed class RecordingDownloadClientFileSnapshotService : IDownloadClientFileSnapshotService
        {
            public int CaptureClientListCalls { get; private set; }

            public void CaptureClientList(DownloadClientItem item)
            {
                CaptureClientListCalls++;
            }

            public void CaptureCompletedOutput(DownloadClientItem item)
            {
            }

            public void ApplySnapshot(DownloadClientItem item)
            {
            }

            public void Delete(DownloadClientItem item)
            {
            }
        }
    }
}
