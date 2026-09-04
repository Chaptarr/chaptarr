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
using NzbDrone.Core.CustomFormats;
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

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class DirectTrackedDownloadHydrationFixture
    {
        private sealed class RecordingEventAggregator : IEventAggregator
        {
            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
            }
        }

        private sealed class DirectParsingService : IParsingService
        {
            public Author GetAuthor(string title) => throw new NotImplementedException();

            public RemoteBook Map(ParsedBookInfo parsedBookInfo, SearchCriteriaBase searchCriteria = null)
            {
                return new RemoteBook
                {
                    ParsedBookInfo = parsedBookInfo ?? new ParsedBookInfo()
                };
            }

            public RemoteBook Map(ParsedBookInfo parsedBookInfo, int authorId, IEnumerable<int> bookIds)
            {
                return new RemoteBook
                {
                    ParsedBookInfo = new ParsedBookInfo(),
                    Author = new Author { Id = authorId },
                    Books = (bookIds ?? Array.Empty<int>())
                        .Select(id => new Book
                        {
                            Id = id,
                            AuthorId = authorId,
                            MediaType = BookMediaType.Ebook
                        })
                        .ToList()
                };
            }

            public List<Book> GetBooks(ParsedBookInfo parsedBookInfo, Author author, SearchCriteriaBase searchCriteria = null) => throw new NotImplementedException();
            public ParsedBookInfo ParseBookTitleFuzzy(string title) => throw new NotImplementedException();
            public Book GetLocalBook(string filename, Author author) => throw new NotImplementedException();
        }

        private class DownloadHistoryServiceProxy : DispatchProxy
        {
            public DownloadHistory Latest { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IDownloadHistoryService.GetLatestDownloadHistoryItem) ||
                    targetMethod?.Name == nameof(IDownloadHistoryService.GetLatestGrab))
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
                    return HistoryItems.OrderByDescending(item => item.Date).FirstOrDefault();
                }

                throw new NotImplementedException($"Test proxy does not implement IHistoryService.{targetMethod?.Name}");
            }
        }

        private sealed class StubCustomFormatCalculationService : ICustomFormatCalculationService
        {
            public List<CustomFormat> ParseCustomFormat(RemoteBook remoteBook, long size) => new();
            public List<CustomFormat> ParseCustomFormat(BookFile bookFile, Author artist) => throw new NotImplementedException();
            public List<CustomFormat> ParseCustomFormat(BookFile bookFile) => throw new NotImplementedException();
            public List<CustomFormat> ParseCustomFormat(Blocklist blocklist, Author artist) => throw new NotImplementedException();
            public List<CustomFormat> ParseCustomFormat(EntityHistory history, Author artist) => throw new NotImplementedException();
            public List<CustomFormat> ParseCustomFormat(LocalBook localBook) => throw new NotImplementedException();
        }

        [Test]
        public void should_restore_direct_release_metadata_from_grab_history_for_restart_hydration()
        {
            var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
            ((HistoryServiceProxy)(object)historyService).HistoryItems = new List<EntityHistory>
            {
                new()
                {
                    EventType = EntityHistoryEventType.Grabbed,
                    DownloadId = "direct-history-1",
                    AuthorId = 77,
                    BookId = 501,
                    SourceTitle = "Fallback Download Title",
                    Date = DateTime.UtcNow,
                    Data = new Dictionary<string, string>
                    {
                        ["ReleaseSource"] = ReleaseSourceType.Search.ToString()
                    }
                }
            };

            var downloadHistoryService = DispatchProxy.Create<IDownloadHistoryService, DownloadHistoryServiceProxy>();
            ((DownloadHistoryServiceProxy)(object)downloadHistoryService).Latest = new DownloadHistory
            {
                DownloadId = "direct-history-1",
                Protocol = DownloadProtocol.Direct,
                IndexerId = 33,
                Release = new ReleaseInfo
                {
                    Guid = "DIRECT-HISTORY-1",
                    Title = "Lois McMaster Bujold - A Civil Campaign [epub]",
                    Author = "Lois McMaster Bujold",
                    Book = "A Civil Campaign",
                    Isbn = "9780671578856",
                    DownloadUrl = "https://downloads.example/civil-campaign.epub",
                    InfoUrl = "https://info.example/civil-campaign",
                    CommentUrl = "https://comments.example/civil-campaign",
                    Container = "epub",
                    Narrator = "Metadata Narrator",
                    DownloadProtocol = DownloadProtocol.Direct,
                    Indexer = "Direct Download",
                    Size = 12345
                }
            };

            var subject = new TrackedDownloadService(
                new DirectParsingService(),
                new CacheManager(),
                historyService,
                new RecordingEventAggregator(),
                downloadHistoryService,
                new StubCustomFormatCalculationService(),
                LogManager.GetCurrentClassLogger(),
                NoopDownloadClientFileSnapshotService.Instance);

            var trackedDownload = subject.TrackDownload(
                new DownloadClientDefinition
                {
                    Id = 9,
                    Name = "Direct Download",
                    Protocol = DownloadProtocol.Direct
                },
                new DownloadClientItem
                {
                    DownloadId = "direct-history-1",
                    Title = "Unparseable Direct Payload",
                    DownloadClientInfo = new DownloadClientItemClientInfo
                    {
                        Id = 9,
                        Name = "Direct Download",
                        Protocol = DownloadProtocol.Direct
                    }
                });

            Assert.That(trackedDownload, Is.Not.Null);
            Assert.That(trackedDownload.Protocol, Is.EqualTo(DownloadProtocol.Direct));
            Assert.That(trackedDownload.RemoteBook?.Release?.DownloadProtocol, Is.EqualTo(DownloadProtocol.Direct));
            Assert.That(trackedDownload.RemoteBook?.Release?.Author, Is.EqualTo("Lois McMaster Bujold"));
            Assert.That(trackedDownload.RemoteBook?.Release?.Book, Is.EqualTo("A Civil Campaign"));
            Assert.That(trackedDownload.RemoteBook?.Release?.Isbn, Is.EqualTo("9780671578856"));
            Assert.That(trackedDownload.RemoteBook?.Release?.Container, Is.EqualTo("epub"));
            Assert.That(trackedDownload.RemoteBook?.Release?.Narrator, Is.EqualTo("Metadata Narrator"));
            Assert.That(trackedDownload.RemoteBook?.ParsedBookInfo?.AuthorName, Is.EqualTo("Lois McMaster Bujold"));
            Assert.That(trackedDownload.RemoteBook?.ParsedBookInfo?.BookTitle, Is.EqualTo("A Civil Campaign"));
            Assert.That(trackedDownload.RemoteBook?.ParsedBookInfo?.ReleaseTitle, Is.EqualTo("Lois McMaster Bujold - A Civil Campaign [epub]"));
            Assert.That(trackedDownload.RemoteBook?.ParsedBookInfo?.Narrator, Is.EqualTo("Metadata Narrator"));
            Assert.That(trackedDownload.RemoteBook?.ParsedBookInfo?.ExtraInfo["Isbn"], Is.EqualTo("9780671578856"));
            Assert.That(trackedDownload.RemoteBook?.ParsedBookInfo?.ExtraInfo["Container"], Is.EqualTo("epub"));
        }
    }
}
