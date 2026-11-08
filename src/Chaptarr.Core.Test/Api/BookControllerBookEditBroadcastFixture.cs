using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Chaptarr.Api.V1.Books;
using Chaptarr.Http;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.AuthorStats;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Datastore.Events;
using NzbDrone.Core.Download;
using NzbDrone.Core.MediaCover;
using CoverImage = NzbDrone.Core.MediaCover.MediaCover;
using NzbDrone.Core.Validation;
using NzbDrone.SignalR;

using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class BookControllerBookEditBroadcastFixture
    {
        private sealed class RecordingSignalRBroadcaster : IBroadcastSignalRMessage
        {
            public bool IsConnected => true;
            public List<SignalRMessage> Messages { get; } = new List<SignalRMessage>();

            public Task BroadcastMessage(SignalRMessage message)
            {
                Messages.Add(message);
                return Task.CompletedTask;
            }
        }

        private sealed class TestableBookController : BookController
        {
            public TestableBookController(IBroadcastSignalRMessage broadcaster,
                                          ISeriesBookLinkService seriesBookLinkService = null,
                                          IAuthorStatisticsService authorStatisticsService = null,
                                          IMapCoversToLocal coverMapper = null, IBookService bookService = null)
                : base(
                    authorService: null,
                    bookService: bookService,
                    addBookService: null,
                    editionService: null,
                    editionSelector: null,
                    seriesBookLinkService: seriesBookLinkService,
                    authorStatisticsService: authorStatisticsService,
                    mediaFileService: null,
                    coverMapper: coverMapper,
                    upgradableSpecification: null,
                    signalRBroadcaster: broadcaster,
                    commandQueueManager: null,
                    eventAggregator: null,
                    rootFolderService: null,
                    qualityProfileExistsValidator: new QualityProfileExistsValidator(new TestQualityProfileService()),
                    metadataProfileExistsValidator: new MetadataProfileExistsValidator(new TestMetadataProfileService()),
                    logger: LogManager.GetCurrentClassLogger())
            {
            }

            public List<int> LoadedBookIds { get; } = new List<int>();

            protected override BookResource GetResourceByIdForBroadcast(int id)
            {
                LoadedBookIds.Add(id);
                return new BookResource { Id = id };
            }

            public void FlushBookEdits()
            {
                FlushPendingBookEdits();
            }
        }

        [Test]
        public void identity_merge_removes_absorbed_rows_without_loading_deleted_books()
        {
            var broadcaster = new RecordingSignalRBroadcaster();
            var loads = 0;
            var books = Proxy<IBookService>((method, args) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(IBookService.GetBooks)));
                Assert.That((IEnumerable<int>)args[0], Is.EquivalentTo(new[] { 10, 12 }));
                loads++;
                return new List<Book> { new() { Id = 10, AuthorId = 1, Title = "Dune" }, new() { Id = 12, AuthorId = 1, Title = "Dune Messiah" } };
            });
            var stats = Proxy<IAuthorStatisticsService>((_, _) => new AuthorStatistics { BookStatistics = new() });
            var links = Proxy<ISeriesBookLinkService>((_, _) => new List<SeriesBookLink>());
            var covers = Proxy<IMapCoversToLocal>((_, _) => null);
            var controller = new TestableBookController(broadcaster, links, stats, covers, books);
            controller.Handle(new LibraryIdentityChangedEvent(new[] { 1 }, new[] { 10, 11, 12 }, new[] { 11 }));
            Assert.That(broadcaster.Messages, Has.Count.EqualTo(3));
            Assert.That(((ResourceChangeMessage<BookResource>)broadcaster.Messages.Single(message => message.Action == ModelAction.Deleted).Body).Resource.Id, Is.EqualTo(11));
            Assert.That(broadcaster.Messages.Where(message => message.Action == ModelAction.Updated)
                .Select(message => ((ResourceChangeMessage<BookResource>)message.Body).Resource.Id), Is.EquivalentTo(new[] { 10, 12 }));
            Assert.That(loads, Is.EqualTo(1));
            Assert.That(controller.LoadedBookIds, Is.Empty, "survivors load in one batch and deleted rows are never loaded");
        }

        [Test]
        public void grabbed_book_broadcast_maps_ineligible_covers_without_changing_cached_book_state()
        {
            const int bookId = 101;
            const string cachedUrl = "/MediaCover/Book/101/Cover.jpg";
            const string remoteUrl = "https://covers.example/book.jpg";
            var book = new Book
            {
                Id = bookId,
                AuthorId = 202,
                Title = "Fixture Book",
                MediaType = BookMediaType.Audiobook,
                AudiobookMonitored = false,
                EbookMonitored = false,
                Images = new List<CoverImage>
                {
                    new(MediaCoverTypes.Cover, cachedUrl)
                    {
                        RemoteUrl = remoteUrl,
                        Hash = "saved-cover-hash"
                    }
                }
            };
            var mappedBookIds = new List<int>();
            var authorStatistics = Proxy<IAuthorStatisticsService>((method, args) =>
            {
                if (method.Name == nameof(IAuthorStatisticsService.AuthorStatistics) && args.Length == 1)
                {
                    return new AuthorStatistics { BookStatistics = new List<BookStatistics>() };
                }

                throw new NotImplementedException(method.Name);
            });
            var seriesLinks = Proxy<ISeriesBookLinkService>((method, args) =>
            {
                if (method.Name == nameof(ISeriesBookLinkService.GetLinksByBook))
                {
                    return new List<SeriesBookLink>();
                }

                throw new NotImplementedException(method.Name);
            });
            var coverMapper = Proxy<IMapCoversToLocal>((method, args) =>
            {
                if (method.Name == nameof(IMapCoversToLocal.ConvertBookCoversToLocalUrls))
                {
                    var covers = ((IEnumerable<KeyValuePair<int, List<CoverImage>>>)args[0]).ToList();
                    mappedBookIds.AddRange(covers.Select(entry => entry.Key));
                    foreach (var cover in covers.SelectMany(entry => entry.Value))
                    {
                        cover.RemoteUrl ??= cover.Url;
                        cover.Url = null;
                        cover.TemporaryUrl = "/MediaCoverProxy/temporary-book-cover";
                    }

                    return null;
                }

                if (method.Name == nameof(IMapCoversToLocal.ConvertToLocalUrls))
                {
                    throw new AssertionException("Book resources must use the set-based cover mapper");
                }

                throw new NotImplementedException(method.Name);
            });
            var broadcaster = new RecordingSignalRBroadcaster();
            var controller = new TestableBookController(broadcaster, seriesLinks, authorStatistics, coverMapper);

            controller.Handle(new BookGrabbedEvent(new NzbDrone.Core.Parser.Model.RemoteBook
            {
                Books = new List<Book> { book }
            }));

            var message = (ResourceChangeMessage<BookResource>)broadcaster.Messages.Single().Body;
            var broadcastBook = message.Resource;
            Assert.Multiple(() =>
            {
                Assert.That(message.Action, Is.EqualTo(ModelAction.Updated));
                Assert.That(broadcastBook.Id, Is.EqualTo(bookId));
                Assert.That(broadcastBook.Grabbed, Is.True);
                Assert.That(broadcastBook.Images.Single().Url, Is.Null);
                Assert.That(broadcastBook.Images.Single().TemporaryUrl, Is.EqualTo("/MediaCoverProxy/temporary-book-cover"));
                Assert.That(broadcastBook.Images.Single().RemoteUrl, Is.EqualTo(remoteUrl));
                Assert.That(mappedBookIds, Is.EqualTo(new[] { bookId }));
                Assert.That(book.Images.Single().Url, Is.EqualTo(cachedUrl));
                Assert.That(book.Images.Single().Hash, Is.EqualTo("saved-cover-hash"));
                Assert.That(book.Images.Single().TemporaryUrl, Is.Null);
                Assert.That(book.AudiobookMonitored, Is.False);
                Assert.That(book.EbookMonitored, Is.False);
            });
        }

        [Test]
        public void single_book_edit_keeps_its_immediate_row_update()
        {
            var broadcaster = new RecordingSignalRBroadcaster();
            var controller = new TestableBookController(broadcaster);

            controller.Handle(new BookEditedEvent(new Book { Id = 101 }, new Book { Id = 101 }));
            controller.FlushBookEdits();

            Assert.Multiple(() =>
            {
                Assert.That(controller.LoadedBookIds, Is.EqualTo(new[] { 101 }));
                Assert.That(broadcaster.Messages, Has.Count.EqualTo(1));
                Assert.That(broadcaster.Messages[0].Action, Is.EqualTo(ModelAction.Updated));
            });
        }

        [Test]
        public void book_edit_burst_loads_one_row_then_sends_one_collection_sync()
        {
            var broadcaster = new RecordingSignalRBroadcaster();
            var controller = new TestableBookController(broadcaster);

            controller.Handle(new BookEditedEvent(new Book { Id = 101 }, new Book { Id = 101 }));
            controller.Handle(new BookEditedEvent(new Book { Id = 102 }, new Book { Id = 102 }));
            controller.Handle(new BookEditedEvent(new Book { Id = 103 }, new Book { Id = 103 }));
            controller.FlushBookEdits();

            Assert.Multiple(() =>
            {
                Assert.That(controller.LoadedBookIds, Is.EqualTo(new[] { 101 }));
                Assert.That(broadcaster.Messages, Has.Count.EqualTo(2));
                Assert.That(broadcaster.Messages[0].Action, Is.EqualTo(ModelAction.Updated));
                Assert.That(broadcaster.Messages[1].Action, Is.EqualTo(ModelAction.Sync));
            });
        }
    }
}
