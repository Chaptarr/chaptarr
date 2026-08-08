using System;
using System.Collections.Generic;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class TrackedDownloadAlreadyImportedFixture
    {
        [Test]
        public void should_not_treat_empty_remote_book_list_as_already_imported()
        {
            var subject = new TrackedDownloadAlreadyImported(LogManager.GetCurrentClassLogger());

            var trackedDownload = new TrackedDownload
            {
                DownloadItem = new DownloadClientItem
                {
                    DownloadId = "download-1",
                    Title = "Missing.Context",
                    DownloadClientInfo = new DownloadClientItemClientInfo
                    {
                        Name = "qBittorrent",
                        Protocol = DownloadProtocol.Torrent
                    }
                },
                RemoteBook = new RemoteBook
                {
                    Books = new List<Book>()
                }
            };

            var historyItems = new List<EntityHistory>
            {
                new()
                {
                    EventType = EntityHistoryEventType.BookFileImported,
                    BookId = 123,
                    AuthorId = 7,
                    Date = DateTime.UtcNow
                }
            };

            Assert.That(subject.IsImported(trackedDownload, historyItems), Is.False);
        }

        [Test]
        public void should_not_treat_missing_remote_book_context_as_already_imported()
        {
            var subject = new TrackedDownloadAlreadyImported(LogManager.GetCurrentClassLogger());

            var trackedDownload = new TrackedDownload
            {
                DownloadItem = new DownloadClientItem
                {
                    DownloadId = "download-2",
                    Title = "Missing.Context",
                    DownloadClientInfo = new DownloadClientItemClientInfo
                    {
                        Name = "qBittorrent",
                        Protocol = DownloadProtocol.Torrent
                    }
                },
                RemoteBook = null
            };

            var historyItems = new List<EntityHistory>
            {
                new()
                {
                    EventType = EntityHistoryEventType.BookFileImported,
                    BookId = 123,
                    AuthorId = 7,
                    Date = DateTime.UtcNow
                }
            };

            Assert.That(subject.IsImported(trackedDownload, historyItems), Is.False);
        }

        [Test]
        public void should_only_require_import_history_for_books_matching_release_media_type()
        {
            var subject = new TrackedDownloadAlreadyImported(LogManager.GetCurrentClassLogger());

            var trackedDownload = new TrackedDownload
            {
                DownloadItem = new DownloadClientItem
                {
                    DownloadId = "download-3",
                    Title = "Mixed.Context.Audiobook",
                    DownloadClientInfo = new DownloadClientItemClientInfo
                    {
                        Name = "qBittorrent",
                        Protocol = DownloadProtocol.Torrent
                    }
                },
                RemoteBook = new RemoteBook
                {
                    ParsedBookInfo = new ParsedBookInfo
                    {
                        Quality = new QualityModel(Quality.MP3)
                    },
                    Books = new List<Book>
                    {
                        new() { Id = 123, AuthorId = 7, Title = "Expected Audiobook", MediaType = BookMediaType.Audiobook },
                        new() { Id = 456, AuthorId = 7, Title = "Expected Ebook", MediaType = BookMediaType.Ebook }
                    }
                }
            };

            var historyItems = new List<EntityHistory>
            {
                new()
                {
                    EventType = EntityHistoryEventType.BookFileImported,
                    BookId = 123,
                    AuthorId = 7,
                    Date = DateTime.UtcNow
                }
            };

            Assert.That(subject.IsImported(trackedDownload, historyItems), Is.True);
        }
    }
}
