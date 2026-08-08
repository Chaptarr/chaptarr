using System;
using System.Collections.Generic;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.History
{
    [TestFixture]
    public class HistoryServiceTrackImportedFixture
    {
        private class HistoryRepositoryProxy : DispatchProxy
        {
            public List<EntityHistory> Inserted { get; } = new();
            public List<EntityHistory> DownloadHistory { get; set; } = new();
            public List<EntityHistory> DownloadIdHistory { get; set; } = new();
            public int FindDownloadHistoryCalls { get; private set; }
            public int InsertManyCalls { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IHistoryRepository.Insert):
                        var history = (EntityHistory)args[0];
                        Inserted.Add(history);
                        return history;

                    case nameof(IHistoryRepository.InsertMany):
                        InsertManyCalls++;
                        Inserted.AddRange((IEnumerable<EntityHistory>)args[0]);
                        return null;

                    case nameof(IHistoryRepository.FindDownloadHistory):
                        FindDownloadHistoryCalls++;
                        return DownloadHistory;

                    case nameof(IHistoryRepository.FindByDownloadId):
                        return DownloadIdHistory;

                    default:
                        throw new NotImplementedException($"Test proxy does not implement IHistoryRepository.{targetMethod?.Name}");
                }
            }
        }

        [Test]
        public void handle_should_not_throw_when_track_imported_event_missing_bookinfo_navigation()
        {
            var repository = DispatchProxy.Create<IHistoryRepository, HistoryRepositoryProxy>();
            var repositoryProxy = (HistoryRepositoryProxy)(object)repository;
            var service = new HistoryService(repository, LogManager.GetCurrentClassLogger());

            var message = new TrackImportedEvent(
                new LocalBook
                {
                    Path = "/downloads/Test Book.m4b"
                },
                new BookFile
                {
                    Id = 42,
                    Path = "/library/Test Book.m4b",
                    EditionId = 3,
                    Edition = new Edition
                    {
                        Id = 3,
                        BookId = 10,
                        Book = new Book
                        {
                            Id = 10,
                            AuthorId = 7
                        }
                    }
                },
                new List<BookFile>(),
                true,
                null);

            Assert.DoesNotThrow(() => service.Handle(message));

            Assert.That(repositoryProxy.Inserted, Has.Count.EqualTo(1));

            var inserted = repositoryProxy.Inserted[0];
            Assert.That(inserted.AuthorId, Is.EqualTo(7));
            Assert.That(inserted.BookId, Is.EqualTo(10));
            Assert.That(inserted.EditionId, Is.EqualTo(3));
            Assert.That(inserted.Quality, Is.Not.Null);
            Assert.That(inserted.SourceTitle, Is.EqualTo("Test Book"));
            Assert.That(inserted.DownloadId, Is.Null);
            Assert.That(inserted.Data["FileId"], Is.EqualTo("42"));
            Assert.That(inserted.Data["DroppedPath"], Is.EqualTo("/downloads/Test Book.m4b"));
            Assert.That(inserted.Data["ImportedPath"], Is.EqualTo("/library/Test Book.m4b"));
        }

        [Test]
        public void find_download_id_should_return_null_when_track_imported_event_lacks_required_history_context()
        {
            var repository = DispatchProxy.Create<IHistoryRepository, HistoryRepositoryProxy>();
            var repositoryProxy = (HistoryRepositoryProxy)(object)repository;
            var service = new HistoryService(repository, LogManager.GetCurrentClassLogger());

            var message = new TrackImportedEvent(
                new LocalBook
                {
                    Path = "/downloads/Test Book.m4b"
                },
                new BookFile
                {
                    Path = "/library/Test Book.m4b"
                },
                new List<BookFile>(),
                true,
                null);

            var result = service.FindDownloadId(message);

            Assert.That(result, Is.Null);
            Assert.That(repositoryProxy.FindDownloadHistoryCalls, Is.EqualTo(0));
        }

        [Test]
        public void imported_event_should_copy_custom_format_score_from_grabbed_history()
        {
            var repository = DispatchProxy.Create<IHistoryRepository, HistoryRepositoryProxy>();
            var repositoryProxy = (HistoryRepositoryProxy)(object)repository;
            repositoryProxy.DownloadIdHistory = new List<EntityHistory>
            {
                new EntityHistory
                {
                    EventType = EntityHistoryEventType.Grabbed,
                    DownloadId = "CUSTOM-FORMAT",
                    AuthorId = 7,
                    BookId = 10,
                    Date = DateTime.UtcNow.AddMinutes(-5),
                    Quality = new QualityModel(Quality.MP3),
                    Data = new Dictionary<string, string>
                    {
                        { "CustomFormatScore", "75" }
                    }
                }
            };

            var service = new HistoryService(repository, LogManager.GetCurrentClassLogger());
            var author = new Author { Id = 7 };
            var book = new Book { Id = 10, AuthorId = 7 };
            var edition = new Edition { Id = 3, BookId = 10, Book = book };
            var downloadClientItem = new DownloadClientItem
            {
                DownloadId = "custom-format",
                DownloadClientInfo = new DownloadClientItemClientInfo()
            };

            service.Handle(new TrackImportedEvent(
                new LocalBook
                {
                    Path = "/downloads/Test Book.m4b",
                    Author = author,
                    Book = book,
                    Quality = new QualityModel(Quality.MP3)
                },
                new BookFile
                {
                    Id = 42,
                    Path = "/library/Test Book.m4b",
                    EditionId = 3,
                    Edition = edition,
                    Author = author,
                    Quality = new QualityModel(Quality.MP3)
                },
                new List<BookFile>(),
                true,
                downloadClientItem));

            Assert.That(repositoryProxy.Inserted, Has.Count.EqualTo(1));
            Assert.That(repositoryProxy.Inserted[0].Data["CustomFormatScore"], Is.EqualTo("75"));
        }

        [Test]
        public void import_incomplete_should_fall_back_to_grabbed_quality_when_tracked_download_quality_is_unknown()
        {
            var repository = DispatchProxy.Create<IHistoryRepository, HistoryRepositoryProxy>();
            var repositoryProxy = (HistoryRepositoryProxy)(object)repository;
            repositoryProxy.DownloadIdHistory = new List<EntityHistory>
            {
                new EntityHistory
                {
                    EventType = EntityHistoryEventType.Grabbed,
                    DownloadId = "FANTASTIC-BEAST",
                    AuthorId = 41,
                    BookId = 1931,
                    Date = DateTime.UtcNow.AddMinutes(-5),
                    Quality = new QualityModel(Quality.MP3)
                }
            };

            var service = new HistoryService(repository, LogManager.GetCurrentClassLogger());
            var trackedDownload = new TrackedDownload
            {
                DownloadItem = new DownloadClientItem
                {
                    DownloadId = "fantastic-beast",
                    Title = "Fantastic Beast and Where to Find Them"
                },
                RemoteBook = new RemoteBook
                {
                    ParsedBookInfo = new ParsedBookInfo
                    {
                        Quality = new QualityModel(Quality.Unknown)
                    },
                    Books = new List<Book>
                    {
                        new Book
                        {
                            Id = 1931,
                            AuthorId = 41,
                            Editions = new List<Edition>
                            {
                                new Edition { Id = 4782, Monitored = true }
                            }
                        }
                    }
                }
            };

            service.Handle(new BookImportIncompleteEvent(trackedDownload));

            Assert.That(repositoryProxy.Inserted, Has.Count.EqualTo(1));
            Assert.That(repositoryProxy.Inserted[0].Quality.Quality, Is.EqualTo(Quality.MP3));
            Assert.That(repositoryProxy.Inserted[0].DownloadId, Is.EqualTo("FANTASTIC-BEAST"));
        }

        [Test]
        public void import_incomplete_should_fall_back_to_grabbed_quality_when_tracked_download_quality_is_unknown_audio()
        {
            var repository = DispatchProxy.Create<IHistoryRepository, HistoryRepositoryProxy>();
            var repositoryProxy = (HistoryRepositoryProxy)(object)repository;
            repositoryProxy.DownloadIdHistory = new List<EntityHistory>
            {
                new EntityHistory
                {
                    EventType = EntityHistoryEventType.Grabbed,
                    DownloadId = "HERO-OF-AGES",
                    AuthorId = 85,
                    BookId = 5651,
                    Date = DateTime.UtcNow.AddMinutes(-5),
                    Quality = new QualityModel(Quality.M4B)
                }
            };

            var service = new HistoryService(repository, LogManager.GetCurrentClassLogger());
            var trackedDownload = new TrackedDownload
            {
                DownloadItem = new DownloadClientItem
                {
                    DownloadId = "hero-of-ages",
                    Title = "Brandon Sanderson - Mistborn 03 - The Hero of Ages (GraphicAudio)"
                },
                RemoteBook = new RemoteBook
                {
                    ParsedBookInfo = new ParsedBookInfo
                    {
                        Quality = new QualityModel(Quality.UnknownAudio)
                    },
                    Books = new List<Book>
                    {
                        new Book
                        {
                            Id = 5651,
                            AuthorId = 85,
                            MediaType = BookMediaType.Audiobook,
                            Editions = new List<Edition>
                            {
                                new Edition { Id = 15130, Monitored = true }
                            }
                        }
                    }
                }
            };

            service.Handle(new BookImportIncompleteEvent(trackedDownload));

            Assert.That(repositoryProxy.Inserted, Has.Count.EqualTo(1));
            Assert.That(repositoryProxy.Inserted[0].Quality.Quality, Is.EqualTo(Quality.M4B));
        }

        [Test]
        public void import_incomplete_should_record_unknown_audio_for_audiobook_when_exact_quality_is_unknown()
        {
            var repository = DispatchProxy.Create<IHistoryRepository, HistoryRepositoryProxy>();
            var repositoryProxy = (HistoryRepositoryProxy)(object)repository;
            var service = new HistoryService(repository, LogManager.GetCurrentClassLogger());
            var trackedDownload = new TrackedDownload
            {
                DownloadItem = new DownloadClientItem
                {
                    DownloadId = "goblet",
                    Title = "J.K. ROWLING - Harry Potter And The Goblet Of Fire [Book 4] - Mine (B)"
                },
                RemoteBook = new RemoteBook
                {
                    ParsedBookInfo = new ParsedBookInfo
                    {
                        Quality = new QualityModel(Quality.Unknown)
                    },
                    Books = new List<Book>
                    {
                        new Book
                        {
                            Id = 2017,
                            AuthorId = 44,
                            Title = "Harry Potter and the Goblet of Fire",
                            MediaType = BookMediaType.Audiobook
                        }
                    }
                }
            };

            service.Handle(new BookImportIncompleteEvent(trackedDownload));

            Assert.That(repositoryProxy.Inserted, Has.Count.EqualTo(1));
            Assert.That(repositoryProxy.Inserted[0].Quality.Quality, Is.EqualTo(Quality.UnknownAudio));
        }

        [Test]
        public void grabbed_event_should_record_unknown_audio_for_audiobook_when_exact_quality_is_unknown()
        {
            var repository = DispatchProxy.Create<IHistoryRepository, HistoryRepositoryProxy>();
            var repositoryProxy = (HistoryRepositoryProxy)(object)repository;
            var service = new HistoryService(repository, LogManager.GetCurrentClassLogger());
            var remoteBook = new RemoteBook
            {
                Author = new Author { Id = 44, Name = "J.K. Rowling" },
                ParsedBookInfo = new ParsedBookInfo
                {
                    Quality = new QualityModel(Quality.Unknown)
                },
                Release = new ReleaseInfo
                {
                    Title = "J.K. ROWLING - Harry Potter And The Goblet Of Fire [Book 4] - Mine (B)"
                },
                Books = new List<Book>
                {
                    new Book
                    {
                        Id = 2017,
                        AuthorId = 44,
                        Title = "Harry Potter and the Goblet of Fire",
                        MediaType = BookMediaType.Audiobook
                    }
                }
            };

            service.Handle(new BookGrabbedEvent(remoteBook));

            Assert.That(repositoryProxy.Inserted, Has.Count.EqualTo(1));
            Assert.That(repositoryProxy.Inserted[0].Quality.Quality, Is.EqualTo(Quality.UnknownAudio));
        }

        [Test]
        public void grabbed_event_should_record_file_type_quality_when_indexer_supplies_it()
        {
            var repository = DispatchProxy.Create<IHistoryRepository, HistoryRepositoryProxy>();
            var repositoryProxy = (HistoryRepositoryProxy)(object)repository;
            var service = new HistoryService(repository, LogManager.GetCurrentClassLogger());
            var remoteBook = new RemoteBook
            {
                Author = new Author { Id = 85, Name = "Brandon Sanderson" },
                ParsedBookInfo = new ParsedBookInfo
                {
                    Quality = new QualityModel(Quality.UnknownAudio)
                },
                Release = new TorrentInfo
                {
                    Title = "Brandon Sanderson - Mistborn 03 - The Hero of Ages (GraphicAudio)",
                    Indexer = "MyAnonaMouse",
                    FileType = "m4b"
                },
                Books = new List<Book>
                {
                    new Book
                    {
                        Id = 5651,
                        AuthorId = 85,
                        Title = "The Hero of Ages",
                        MediaType = BookMediaType.Audiobook
                    }
                }
            };

            service.Handle(new BookGrabbedEvent(remoteBook));

            Assert.That(repositoryProxy.Inserted, Has.Count.EqualTo(1));
            Assert.That(repositoryProxy.Inserted[0].Quality.Quality, Is.EqualTo(Quality.M4B));
        }

        [TestCase(ReleaseSourceType.InteractiveSearch, true, true)]
        [TestCase(ReleaseSourceType.Search, false, true)]
        [TestCase(ReleaseSourceType.Search, true, false)]
        public void grabbed_event_should_persist_manual_override_for_every_interactive_selection(
            ReleaseSourceType releaseSource,
            bool downloadAllowed,
            bool expectedForced)
        {
            var repository = DispatchProxy.Create<IHistoryRepository, HistoryRepositoryProxy>();
            var repositoryProxy = (HistoryRepositoryProxy)(object)repository;
            var service = new HistoryService(repository, LogManager.GetCurrentClassLogger());
            var remoteBook = new RemoteBook
            {
                DownloadAllowed = downloadAllowed,
                ReleaseSource = releaseSource,
                ParsedBookInfo = new ParsedBookInfo
                {
                    Quality = new QualityModel(Quality.M4B)
                },
                Release = new ReleaseInfo
                {
                    Title = "Jim Butcher - Storm Front"
                },
                Books = new List<Book>
                {
                    new Book
                    {
                        Id = 42,
                        AuthorId = 7,
                        Title = "Storm Front",
                        MediaType = BookMediaType.Audiobook
                    }
                }
            };

            service.Handle(new BookGrabbedEvent(remoteBook));

            Assert.That(repositoryProxy.Inserted, Has.Count.EqualTo(1));
            Assert.That(repositoryProxy.Inserted[0].Data["DownloadForced"], Is.EqualTo(expectedForced.ToString()));
        }

        [Test]
        public void import_incomplete_should_only_write_history_for_matching_release_media_type()
        {
            var repository = DispatchProxy.Create<IHistoryRepository, HistoryRepositoryProxy>();
            var repositoryProxy = (HistoryRepositoryProxy)(object)repository;
            var service = new HistoryService(repository, LogManager.GetCurrentClassLogger());
            var trackedDownload = new TrackedDownload
            {
                DownloadItem = new DownloadClientItem
                {
                    DownloadId = "audio-sibling",
                    Title = "Same Title Audiobook"
                },
                RemoteBook = new RemoteBook
                {
                    ParsedBookInfo = new ParsedBookInfo
                    {
                        Quality = new QualityModel(Quality.MP3)
                    },
                    Books = new List<Book>
                    {
                        new Book { Id = 10, AuthorId = 41, Title = "Same Title", MediaType = BookMediaType.Audiobook },
                        new Book { Id = 11, AuthorId = 41, Title = "Same Title", MediaType = BookMediaType.Ebook }
                    }
                }
            };

            service.Handle(new BookImportIncompleteEvent(trackedDownload));

            Assert.That(repositoryProxy.Inserted, Has.Count.EqualTo(1));
            Assert.That(repositoryProxy.Inserted[0].BookId, Is.EqualTo(10));
        }

        [Test]
        public void converted_event_should_record_final_book_and_import_paths()
        {
            var repository = DispatchProxy.Create<IHistoryRepository, HistoryRepositoryProxy>();
            var repositoryProxy = (HistoryRepositoryProxy)(object)repository;
            var service = new HistoryService(repository, LogManager.GetCurrentClassLogger());
            var author = new Author { Id = 7 };
            var finalBook = new Book { Id = 11, AuthorId = 7 };
            var edition = new Edition { Id = 13, BookId = 11, Book = finalBook };
            var downloadClientItem = new DownloadClientItem
            {
                DownloadId = "convert-me",
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Type = "qBittorrent",
                    Name = "Main qBit"
                }
            };

            var localBook = new LocalBook
            {
                Path = "/library/Book/.chaptarr-conversions/convert-me/work/Book.m4b",
                SceneName = "Downloaded Book",
                Author = author,
                Book = finalBook,
                Edition = edition,
                Quality = new QualityModel(Quality.M4B),
                IsGeneratedConversion = true,
                GeneratedConversionSourceQuality = new QualityModel(Quality.MP3),
                GeneratedConversionSourcePaths = new List<string> { "/downloads/Book/001.mp3", "/downloads/Book/002.mp3" },
                GeneratedConversionOutputPath = "/library/Book/.chaptarr-conversions/convert-me/work/Book.m4b",
                GeneratedConversionOutputSize = 123
            };

            var importedBook = new BookFile
            {
                Id = 42,
                Path = "/library/Book/Book.m4b",
                Size = 456,
                Quality = new QualityModel(Quality.M4B),
                EditionId = 13,
                Edition = edition,
                Author = author
            };

            service.Handle(new BookFileConvertedEvent(localBook, importedBook, author, finalBook, downloadClientItem));

            Assert.That(repositoryProxy.Inserted, Has.Count.EqualTo(1));
            var inserted = repositoryProxy.Inserted[0];
            Assert.That(inserted.EventType, Is.EqualTo(EntityHistoryEventType.BookFileConverted));
            Assert.That(inserted.AuthorId, Is.EqualTo(7));
            Assert.That(inserted.BookId, Is.EqualTo(11));
            Assert.That(inserted.EditionId, Is.EqualTo(13));
            Assert.That(inserted.DownloadId, Is.EqualTo("CONVERT-ME"));
            Assert.That(inserted.Quality.Quality, Is.EqualTo(Quality.M4B));
            Assert.That(inserted.Data["SourcePath"], Is.EqualTo("/downloads/Book/001.mp3"));
            Assert.That(inserted.Data["SourceFileCount"], Is.EqualTo("2"));
            Assert.That(inserted.Data["SourceQuality"], Is.EqualTo("MP3"));
            Assert.That(inserted.Data["TargetQuality"], Is.EqualTo("M4B"));
            Assert.That(inserted.Data["ConvertedPath"], Is.EqualTo("/library/Book/.chaptarr-conversions/convert-me/work/Book.m4b"));
            Assert.That(inserted.Data["ImportedPath"], Is.EqualTo("/library/Book/Book.m4b"));
            Assert.That(inserted.Data["OutputSize"], Is.EqualTo("456"));
            Assert.That(inserted.Data["DownloadClientName"], Is.EqualTo("Main qBit"));
        }

        [Test]
        public void conversion_failed_event_should_record_error_against_matched_book()
        {
            var repository = DispatchProxy.Create<IHistoryRepository, HistoryRepositoryProxy>();
            var repositoryProxy = (HistoryRepositoryProxy)(object)repository;
            var service = new HistoryService(repository, LogManager.GetCurrentClassLogger());
            var author = new Author { Id = 7 };
            var book = new Book { Id = 10, AuthorId = 7 };
            var localBook = new LocalBook
            {
                Path = "/downloads/Book/001.mp3",
                Author = author,
                Book = book,
                Edition = new Edition { Id = 12, BookId = 10, Book = book },
                Quality = new QualityModel(Quality.MP3)
            };

            service.Handle(new BookFileConversionFailedEvent(
                localBook,
                new[] { "/downloads/Book/001.mp3" },
                book,
                author,
                new QualityModel(Quality.M4B),
                "/library/Book/.chaptarr-conversions/work/Book.m4b",
                "m4b-tool exploded",
                null));

            Assert.That(repositoryProxy.Inserted, Has.Count.EqualTo(1));
            var inserted = repositoryProxy.Inserted[0];
            Assert.That(inserted.EventType, Is.EqualTo(EntityHistoryEventType.BookFileConversionFailed));
            Assert.That(inserted.AuthorId, Is.EqualTo(7));
            Assert.That(inserted.BookId, Is.EqualTo(10));
            Assert.That(inserted.EditionId, Is.EqualTo(12));
            Assert.That(inserted.Data["SourcePath"], Is.EqualTo("/downloads/Book/001.mp3"));
            Assert.That(inserted.Data["ConvertedPath"], Is.EqualTo("/library/Book/.chaptarr-conversions/work/Book.m4b"));
            Assert.That(inserted.Data["Message"], Is.EqualTo("m4b-tool exploded"));
        }
    }
}
