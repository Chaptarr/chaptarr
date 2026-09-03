using System.Collections.Generic;
using System.Threading.Tasks;
using Chaptarr.Api.V1.Books;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Datastore.Events;
using NzbDrone.Core.Validation;
using NzbDrone.SignalR;

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
            public TestableBookController(IBroadcastSignalRMessage broadcaster)
                : base(
                    authorService: null,
                    bookService: null,
                    addBookService: null,
                    editionService: null,
                    editionSelector: null,
                    seriesBookLinkService: null,
                    authorStatisticsService: null,
                    mediaFileService: null,
                    coverMapper: null,
                    upgradableSpecification: null,
                    signalRBroadcaster: broadcaster,
                    commandQueueManager: null,
                    eventAggregator: null,
                    metadataProfileService: null,
                    qualityProfileService: null,
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
