using Chaptarr.Api.V1.Queue;
using NUnit.Framework;
using CoreTrackedDownloadState = NzbDrone.Core.Download.TrackedDownloads.TrackedDownloadState;
using QueueModel = NzbDrone.Core.Queue.Queue;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class QueueResourceBookshelfCompatibilityFixture
    {
        [TestCase(CoreTrackedDownloadState.Downloading, TrackedDownloadState.Downloading)]
        [TestCase(CoreTrackedDownloadState.DownloadFailed, TrackedDownloadState.DownloadFailed)]
        [TestCase(CoreTrackedDownloadState.DownloadFailedPending, TrackedDownloadState.DownloadFailedPending)]
        [TestCase(CoreTrackedDownloadState.ImportPending, TrackedDownloadState.ImportPending)]
        [TestCase(CoreTrackedDownloadState.Importing, TrackedDownloadState.Importing)]
        [TestCase(CoreTrackedDownloadState.ImportBlocked, TrackedDownloadState.ImportFailed)]
        [TestCase(CoreTrackedDownloadState.Imported, TrackedDownloadState.Imported)]
        [TestCase(CoreTrackedDownloadState.Ignored, TrackedDownloadState.Ignored)]
        public void should_map_internal_queue_states_to_the_bookshelf_v1_enum(
            CoreTrackedDownloadState internalState,
            TrackedDownloadState bookshelfState)
        {
            var resource = new QueueModel
            {
                TrackedDownloadState = internalState
            }.ToResource(includeAuthor: false, includeBook: false);

            Assert.That(resource.TrackedDownloadState, Is.EqualTo(bookshelfState));
        }
    }
}
