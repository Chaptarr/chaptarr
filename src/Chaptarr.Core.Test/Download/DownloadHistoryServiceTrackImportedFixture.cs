using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class DownloadHistoryServiceTrackImportedFixture
    {
        private class ThrowingProxy<T> : DispatchProxy where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (typeof(T) == typeof(IHistoryService) && targetMethod?.Name == nameof(IHistoryService.FindByDownloadId))
                {
                    return new List<EntityHistory>();
                }

                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private class DownloadHistoryRepositoryProxy : DispatchProxy
        {
            public List<DownloadHistory> Inserted { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IDownloadHistoryRepository.Insert))
                {
                    var history = (DownloadHistory)args[0];
                    Inserted.Add(history);
                    return history;
                }

                if (targetMethod.Name == nameof(IDownloadHistoryRepository.FindByDownloadId))
                {
                    return new List<DownloadHistory>();
                }

                throw new NotImplementedException($"Test proxy does not implement {targetMethod.DeclaringType?.Name}.{targetMethod.Name}");
            }
        }

        [Test]
        public void should_record_import_history_when_imported_book_author_navigation_is_missing()
        {
            var repository = DispatchProxy.Create<IDownloadHistoryRepository, DownloadHistoryRepositoryProxy>();
            var repositoryProxy = (DownloadHistoryRepositoryProxy)(object)repository;
            var historyService = DispatchProxy.Create<IHistoryService, ThrowingProxy<IHistoryService>>();
            var service = new DownloadHistoryService(repository, historyService);

            var author = new Author { Id = 57, Name = "Riley Sager" };
            var book = new Book { Id = 3076, AuthorId = author.Id, Author = author, Title = "The House Across the Lake" };
            var edition = new Edition { Id = 12387, BookId = book.Id, Title = book.Title };

            var localBook = new LocalBook
            {
                Path = "/downloads/complete/Riley Sager - The House Across the Lake.m4b",
                Author = author,
                Book = book,
                Edition = edition,
                Quality = new QualityModel(Quality.M4B)
            };

            var importedBook = new BookFile
            {
                Id = 1001,
                Path = "/audiobooks/Riley Sager/The House Across the Lake.m4b",
                EditionId = edition.Id,
                Edition = edition,
                Quality = new QualityModel(Quality.M4B),
                Author = null
            };

            var downloadClientItem = new DownloadClientItem
            {
                DownloadId = "abc123",
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Id = 12,
                    Name = "qBittorrent - NAS",
                    Type = "qBittorrent",
                    Protocol = NzbDrone.Core.Indexers.DownloadProtocol.Torrent
                }
            };

            service.Handle(new TrackImportedEvent(localBook, importedBook, new List<BookFile>(), true, downloadClientItem));

            Assert.That(repositoryProxy.Inserted, Has.Count.EqualTo(1));
            Assert.That(repositoryProxy.Inserted[0].AuthorId, Is.EqualTo(author.Id));
            Assert.That(repositoryProxy.Inserted[0].BookId, Is.EqualTo(book.Id));
            Assert.That(repositoryProxy.Inserted[0].DownloadId, Is.EqualTo("ABC123"));
            Assert.That(repositoryProxy.Inserted[0].DownloadClientId, Is.EqualTo(12));
            Assert.That(repositoryProxy.Inserted[0].Protocol, Is.EqualTo(NzbDrone.Core.Indexers.DownloadProtocol.Torrent));
        }

        [Test]
        public void should_record_import_history_when_download_client_info_is_missing()
        {
            var repository = DispatchProxy.Create<IDownloadHistoryRepository, DownloadHistoryRepositoryProxy>();
            var repositoryProxy = (DownloadHistoryRepositoryProxy)(object)repository;
            var historyService = DispatchProxy.Create<IHistoryService, ThrowingProxy<IHistoryService>>();
            var service = new DownloadHistoryService(repository, historyService);

            var author = new Author { Id = 38, Name = "SenLinYu" };
            var book = new Book { Id = 3077, AuthorId = author.Id, Author = author, Title = "Alchemised" };
            var edition = new Edition { Id = 12392, BookId = book.Id, Title = book.Title };

            var localBook = new LocalBook
            {
                Path = "/downloads/complete/Alchemised.m4b",
                Author = author,
                Book = book,
                Edition = edition,
                Quality = new QualityModel(Quality.M4B)
            };

            var importedBook = new BookFile
            {
                Id = 1002,
                Path = "/audiobooks/SenLinYu/Alchemised.m4b",
                EditionId = edition.Id,
                Edition = edition,
                Quality = new QualityModel(Quality.M4B),
                Author = author
            };

            var downloadClientItem = new DownloadClientItem
            {
                DownloadId = "xyz987",
                DownloadClientInfo = null
            };

            service.Handle(new TrackImportedEvent(localBook, importedBook, new List<BookFile>(), true, downloadClientItem));

            Assert.That(repositoryProxy.Inserted, Has.Count.EqualTo(1));
            Assert.That(repositoryProxy.Inserted[0].AuthorId, Is.EqualTo(author.Id));
            Assert.That(repositoryProxy.Inserted[0].BookId, Is.EqualTo(book.Id));
            Assert.That(repositoryProxy.Inserted[0].DownloadId, Is.EqualTo("XYZ987"));
            Assert.That(repositoryProxy.Inserted[0].DownloadClientId, Is.EqualTo(0));
            Assert.That(repositoryProxy.Inserted[0].Protocol, Is.EqualTo(NzbDrone.Core.Indexers.DownloadProtocol.Unknown));
            Assert.That(repositoryProxy.Inserted[0].Data["DownloadClient"], Is.Null);
            Assert.That(repositoryProxy.Inserted[0].Data["DownloadClientName"], Is.Null);
        }

        [Test]
        public void should_record_completed_download_history_when_download_client_info_is_missing()
        {
            var repository = DispatchProxy.Create<IDownloadHistoryRepository, DownloadHistoryRepositoryProxy>();
            var repositoryProxy = (DownloadHistoryRepositoryProxy)(object)repository;
            var historyService = DispatchProxy.Create<IHistoryService, ThrowingProxy<IHistoryService>>();
            var service = new DownloadHistoryService(repository, historyService);

            var trackedDownload = new TrackedDownload
            {
                DownloadClient = 9,
                DownloadItem = new DownloadClientItem
                {
                    DownloadId = "done123",
                    Title = "Imported Title",
                    DownloadClientInfo = null
                }
            };

            service.Handle(new DownloadCompletedEvent(trackedDownload, 57));

            Assert.That(repositoryProxy.Inserted, Has.Count.EqualTo(1));
            Assert.That(repositoryProxy.Inserted[0].EventType, Is.EqualTo(DownloadHistoryEventType.DownloadImported));
            Assert.That(repositoryProxy.Inserted[0].DownloadId, Is.EqualTo("DONE123"));
            Assert.That(repositoryProxy.Inserted[0].DownloadClientId, Is.EqualTo(9));
            Assert.That(repositoryProxy.Inserted[0].Data["DownloadClient"], Is.Null);
            Assert.That(repositoryProxy.Inserted[0].Data["DownloadClientName"], Is.Null);
        }

        [Test]
        public void should_record_incomplete_download_history_when_download_client_info_is_missing()
        {
            var repository = DispatchProxy.Create<IDownloadHistoryRepository, DownloadHistoryRepositoryProxy>();
            var repositoryProxy = (DownloadHistoryRepositoryProxy)(object)repository;
            var historyService = DispatchProxy.Create<IHistoryService, ThrowingProxy<IHistoryService>>();
            var service = new DownloadHistoryService(repository, historyService);

            var trackedDownload = new TrackedDownload
            {
                DownloadClient = 4,
                Protocol = NzbDrone.Core.Indexers.DownloadProtocol.Torrent,
                DownloadItem = new DownloadClientItem
                {
                    DownloadId = "oops123",
                    OutputPath = new OsPath("/downloads/Some Book"),
                    DownloadClientInfo = null
                },
                RemoteBook = new RemoteBook
                {
                    Author = new Author { Id = 38, Name = "SenLinYu" },
                    Books = new List<Book> { new Book { Id = 3077, AuthorId = 38, Title = "Alchemised" } }
                }
            };
            trackedDownload.Warn(new TrackedDownloadStatusMessage("Some Book", "IMPORT_EXCEPTION"));

            service.Handle(new BookImportIncompleteEvent(trackedDownload));

            Assert.That(repositoryProxy.Inserted, Has.Count.EqualTo(1));
            Assert.That(repositoryProxy.Inserted[0].EventType, Is.EqualTo(DownloadHistoryEventType.DownloadImportIncomplete));
            Assert.That(repositoryProxy.Inserted[0].DownloadId, Is.EqualTo("OOPS123"));
            Assert.That(repositoryProxy.Inserted[0].DownloadClientId, Is.EqualTo(4));
            Assert.That(repositoryProxy.Inserted[0].Data["DownloadClient"], Is.Null);
            Assert.That(repositoryProxy.Inserted[0].Data["DownloadClientName"], Is.Null);
        }

        [Test]
        public void should_record_incomplete_download_history_for_matching_release_media_type()
        {
            var repository = DispatchProxy.Create<IDownloadHistoryRepository, DownloadHistoryRepositoryProxy>();
            var repositoryProxy = (DownloadHistoryRepositoryProxy)(object)repository;
            var historyService = DispatchProxy.Create<IHistoryService, ThrowingProxy<IHistoryService>>();
            var service = new DownloadHistoryService(repository, historyService);

            var trackedDownload = new TrackedDownload
            {
                DownloadClient = 4,
                Protocol = NzbDrone.Core.Indexers.DownloadProtocol.Torrent,
                DownloadItem = new DownloadClientItem
                {
                    DownloadId = "audio-sibling",
                    OutputPath = new OsPath("/downloads/Same Title"),
                    DownloadClientInfo = null
                },
                RemoteBook = new RemoteBook
                {
                    Author = new Author { Id = 38, Name = "SenLinYu" },
                    ParsedBookInfo = new ParsedBookInfo
                    {
                        Quality = new QualityModel(Quality.MP3)
                    },
                    Books = new List<Book>
                    {
                        new Book { Id = 3077, AuthorId = 38, Title = "Same Title", MediaType = BookMediaType.Audiobook },
                        new Book { Id = 3078, AuthorId = 38, Title = "Same Title", MediaType = BookMediaType.Ebook }
                    }
                }
            };
            trackedDownload.Warn(new TrackedDownloadStatusMessage("Same Title", "IMPORT_EXCEPTION"));

            service.Handle(new BookImportIncompleteEvent(trackedDownload));

            Assert.That(repositoryProxy.Inserted, Has.Count.EqualTo(1));
            Assert.That(repositoryProxy.Inserted[0].BookId, Is.EqualTo(3077));
        }
    }
}
