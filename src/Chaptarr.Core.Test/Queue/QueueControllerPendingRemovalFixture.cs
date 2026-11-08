using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.Queue;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Queue;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using CoreQueue = NzbDrone.Core.Queue.Queue;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Queue
{
    [TestFixture]
    public class QueueControllerPendingRemovalFixture
    {
        [TestCase(false, false, false, false)]
        [TestCase(false, false, false, true)]
        [TestCase(false, false, true, false)]
        [TestCase(false, false, true, true)]
        [TestCase(false, true, false, false)]
        [TestCase(false, true, false, true)]
        [TestCase(false, true, true, false)]
        [TestCase(false, true, true, true)]
        [TestCase(true, false, false, false)]
        [TestCase(true, false, false, true)]
        [TestCase(true, false, true, false)]
        [TestCase(true, false, true, true)]
        [TestCase(true, true, false, false)]
        [TestCase(true, true, false, true)]
        [TestCase(true, true, true, false)]
        [TestCase(true, true, true, true)]
        public void pending_removal_should_honor_blocklist_choice_only_for_a_proven_book(bool postgres, bool bulk, bool blocklist, bool resolved)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var events = Proxy<IEventAggregator>((method, args) => null);
                var pendingRepository = new PendingReleaseRepository(database, events);
                var blocklistRepository = new BlocklistRepository(database, events);
                var author = new Author { Id = 38, Name = "Test Writer", HardcoverAuthorId = "hc:38" };
                var book = new Book { Id = 501, AuthorId = author.Id, Title = "Paper Lanterns", HardcoverBookId = "hc:501", MediaType = BookMediaType.Audiobook, Editions = new List<Edition>() };
                var remote = new RemoteBook
                {
                    Author = author, Books = resolved ? new List<Book> { book } : new List<Book>(),
                    ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.M4B) },
                    Release = new ReleaseInfo { Title = "Test Writer - Paper Lanterns [M4B]", Indexer = "Test Indexer", PublishDate = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc) }
                };
                var pending = pendingRepository.Insert(new PendingRelease
                {
                    AuthorId = author.Id, Title = remote.Release.Title, Added = DateTime.UtcNow,
                    Release = remote.Release, ParsedBookInfo = remote.ParsedBookInfo, Reason = PendingReleaseReason.Delay,
                    AdditionalInfo = new PendingReleaseAdditionalInfo { BookIds = resolved ? new List<int> { book.Id } : new List<int>() }
                });
                var queue = new CoreQueue { Id = 97, Author = author, Book = resolved ? book : null, RemoteBook = remote, Quality = remote.ParsedBookInfo.Quality, Status = resolved ? "delay" : "warning" };
                var removes = 0;
                var blocks = 0;
                var pendingService = Proxy<IPendingReleaseService>((method, args) =>
                {
                    if (method.Name == nameof(IPendingReleaseService.GetPendingQueue)) return new List<CoreQueue> { queue };
                    if (method.Name == nameof(IPendingReleaseService.FindPendingQueueItem)) return (int)args[0] == queue.Id ? queue : null;
                    if (method.Name == nameof(IPendingReleaseService.RemovePendingQueueItems))
                    {
                        removes++;
                        pendingRepository.Delete(pending.Id);
                        return null;
                    }

                    throw new NotImplementedException(method.Name);
                });
                var nativeBlocklist = new BlocklistService(blocklistRepository, null, null, LogManager.GetCurrentClassLogger());
                var blocklistService = Proxy<IBlocklistService>((method, args) => { blocks++; return method.Invoke(nativeBlocklist, args); });
                var profileRepository = Proxy<IProfileRepository>((method, args) => method.Name == nameof(IProfileRepository.All)
                    ? new List<QualityProfile> { new() { Id = 1, Items = new List<QualityProfileQualityItem> { new() { Quality = Quality.M4B, Allowed = true } } } }
                    : throw new NotImplementedException(method.Name));
                var profiles = new QualityProfileService(profileRepository, null, null, null, null, null, LogManager.GetCurrentClassLogger());
                var trackedService = Proxy<ITrackedDownloadService>((method, args) => method.Name == nameof(ITrackedDownloadService.StopTracking) ? null : throw new NotImplementedException(method.Name));
                var controller = new QueueController(global::Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<global::NzbDrone.Core.Books.ISeriesBookLinkService>((_, _) => throw new global::System.InvalidOperationException("Series batch loading was not expected in this fixture")), Proxy<IDownloadImportModeResolver>((method, args) => false), null, null, pendingService, profiles, trackedService, null, null, null, blocklistService, null);

                if (bulk) controller.RemoveMany(new QueueBulkResource { Ids = new List<int> { queue.Id, queue.Id } }, blocklist: blocklist);
                else controller.RemoveAction(queue.Id, blocklist: blocklist);

                var saved = blocklistRepository.All().ToList();
                var expectedBlocks = blocklist && resolved ? 1 : 0;
                Assert.Multiple(() =>
                {
                    Assert.That(pendingRepository.All(), Is.Empty);
                    Assert.That(removes, Is.EqualTo(1));
                    Assert.That(blocks, Is.EqualTo(expectedBlocks));
                    Assert.That(saved.Count, Is.EqualTo(expectedBlocks));
                    if (expectedBlocks > 0) Assert.That(saved.First().BookProviderIds, Does.Contain(book.HardcoverBookId));
                });
                TestContext.Progress.WriteLine($"PENDING_REMOVAL database={database.DatabaseType} bulk={bulk} blocklist={blocklist} resolved={resolved} removes={removes} blockWrites={saved.Count}");
            });
        }

        [TestCase("ebook", null, "ebook-imported")]
        [TestCase("unknown", null, null)]
        [TestCase("audio", BookMediaType.Ebook, "ebook-imported")]
        [TestCase("ebook", BookMediaType.Audiobook, "audio-imported")]
        [TestCase("unknown", BookMediaType.Ebook, "ebook-imported")]
        public void change_category_should_use_resolved_book_or_known_quality_and_leave_unknown_media_unassigned(string quality, BookMediaType? bookMedia, string expectedCategory)
        {
            var downloadItem = new DownloadClientItem { DownloadId = "category-selection", Category = "unrecognized" };
            var remote = new RemoteBook
            {
                Books = bookMedia.HasValue ? new() { new Book { Id = 501, MediaType = bookMedia.Value } } : new(),
                ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(quality == "ebook" ? Quality.EPUB : quality == "audio" ? Quality.M4B : Quality.Unknown) }
            };
            var tracked = new TrackedDownload { DownloadClient = 7, DownloadItem = downloadItem, RemoteBook = remote };
            var actualCategories = new List<string>();
            var client = Proxy<IDownloadClient>((method, args) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(IDownloadClient.MarkItemAsImported)));
                var resolved = PostImportCategoryResolver.Resolve((DownloadClientItem)args[0], "audio", "ebook", "audio-imported", "ebook-imported");
                if (resolved != null) actualCategories.Add(resolved);
                return null;
            });
            var profiles = new QualityProfileService(Proxy<IProfileRepository>((method, args) => new List<QualityProfile>
                { new() { Id = 1, Items = new() { new() { Quality = Quality.M4B, Allowed = true } } } }),
                null, null, null, null, null, LogManager.GetCurrentClassLogger());
            var queue = Proxy<IQueueService>((method, args) => new CoreQueue { Id = 17, DownloadId = downloadItem.DownloadId });
            var pending = Proxy<IPendingReleaseService>((method, args) => null);
            var trackedService = Proxy<ITrackedDownloadService>((method, args) => method.Name == nameof(ITrackedDownloadService.Find) ? tracked : null);
            var provider = Proxy<IProvideDownloadClient>((method, args) => client);
            var controller = new QueueController(global::Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<global::NzbDrone.Core.Books.ISeriesBookLinkService>((_, _) => throw new global::System.InvalidOperationException("Series batch loading was not expected in this fixture")), Proxy<IDownloadImportModeResolver>((method, args) => false), null, queue, pending, profiles, trackedService, null, null, provider, null, null);
            controller.RemoveAction(17, removeFromClient: false, changeCategory: true);
            Assert.That(actualCategories, Is.EqualTo(expectedCategory == null ? Array.Empty<string>() : new[] { expectedCategory }));
            Assert.That(downloadItem.MediaType, Is.Null, "The tracked/client snapshot must remain unchanged.");
        }
    }
}
