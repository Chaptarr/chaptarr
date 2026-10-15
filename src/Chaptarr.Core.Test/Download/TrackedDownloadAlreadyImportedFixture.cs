using System;
using System.Collections.Generic;
using NzbDrone.Common.Serializer;
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
        [TestCase(BookMediaType.Audiobook)] [TestCase(BookMediaType.Ebook)]
        public void alternative_history_completion_should_require_current_grab_and_complete_source_paths(BookMediaType media)
        {
            var grabbed = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
            var extension = media == BookMediaType.Audiobook ? ".mp3" : ".epub";
            var first = "/downloads/one" + extension;
            var second = "/downloads/two" + (media == BookMediaType.Audiobook ? ".mp3" : ".mobi");
            var companion = "/downloads/one" + (media == BookMediaType.Audiobook ? ".epub" : ".m4b");
            var tracked = new TrackedDownload { DownloadItem = new DownloadClientItem { DownloadId = "alt", Title = "Captured alternatives",
                FilePaths = new List<string> { first, second, companion, "/downloads/cover.jpg" } },
                RemoteBook = new RemoteBook { AlternativeCopies = true, AlternativeCopyGrabDate = grabbed, TargetEditionIds = new() { [10] = 100, [11] = 110 },
                    Books = new List<Book> { new() { Id = 10, AuthorId = 7, MediaType = media }, new() { Id = 11, AuthorId = 7, MediaType = media } } } };
            var imported = new EntityHistory { BookId = 10, EditionId = 100, AuthorId = 7, EventType = EntityHistoryEventType.BookFileImported, Date = grabbed.AddMinutes(1),
                Data = new Dictionary<string,string> { ["droppedPath"] = first } };
            var history = new List<EntityHistory> { imported };
            var subject = new TrackedDownloadAlreadyImported(LogManager.GetCurrentClassLogger());
            Assert.That(subject.IsImported(tracked, history), Is.False);
            imported.Data["sourcePaths"] = new[] { first, second }.ToJson();
            Assert.That(subject.IsImported(tracked, history), Is.True);
            imported.Data["sourcePaths"] = "{broken";
            Assert.That(subject.IsImported(tracked, history), Is.False);
            imported.Data["sourcePaths"] = new[] { first, second }.ToJson();
            imported.EditionId = 101;
            Assert.That(subject.IsImported(tracked, history), Is.False);
            imported.EditionId = 100;
            imported.Date = grabbed.AddMinutes(-1);
            Assert.That(subject.IsImported(tracked, history), Is.False);
            imported.Date = grabbed.AddMinutes(1); imported.BookId = 99;
            Assert.That(subject.IsImported(tracked, history), Is.False);
            imported.BookId = 10; tracked.RemoteBook.AlternativeCopies = false;
            Assert.That(subject.IsImported(tracked, history), Is.False);
        }

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
