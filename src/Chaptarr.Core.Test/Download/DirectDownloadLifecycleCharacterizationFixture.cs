using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentValidation.Results;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.ThingiProvider;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class DirectDownloadLifecycleCharacterizationFixture
    {
        private sealed class StubDownloadImportModeResolver : IDownloadImportModeResolver
        {
            public bool Preserve { get; set; }
            public ImportMode Resolve(ImportMode requestedMode, DownloadClientItem downloadClientItem) => requestedMode;
            public DownloadImportPolicy ResolvePolicy(ImportMode requestedMode, DownloadClientItem downloadClientItem) => new(requestedMode, Preserve);
            public bool ShouldPreserveDownloadClientItem(DownloadClientItem downloadClientItem) => Preserve;
        }

        private sealed class StubDownloadClientProvider : IProvideDownloadClient
        {
            public StubDownloadClientProvider(IDownloadClient downloadClient) { DownloadClient = downloadClient; }
            private IDownloadClient DownloadClient { get; }
            public IDownloadClient GetDownloadClient(DownloadProtocol downloadProtocol, BookMediaType mediaType, int indexerId = 0, bool filterBlockedClients = false, HashSet<int> tags = null) => throw new NotImplementedException();
            public IEnumerable<IDownloadClient> GetDownloadClients(bool filterBlockedClients = false) => throw new NotImplementedException();
            public IDownloadClient Get(int id) => DownloadClient;
        }

        private sealed class StubDownloadClient : IDownloadClient
        {
            public int RemoveCalls { get; private set; }
            public bool? LastDeleteData { get; private set; }
            public int MarkImportedCalls { get; private set; }
            public string Name => "Direct Download";
            public Type ConfigContract => typeof(object);
            public ProviderMessage Message => null;
            public IEnumerable<ProviderDefinition> DefaultDefinitions => Array.Empty<ProviderDefinition>();
            public ProviderDefinition Definition { get; set; }
            public DownloadProtocol Protocol => DownloadProtocol.Direct;
            public ValidationResult Test() => new();
            public object RequestAction(string action, IDictionary<string, string> query) => null;
            public Task<string> Download(RemoteBook remoteBook, IIndexer indexer) => throw new NotImplementedException();
            public IEnumerable<DownloadClientItem> GetItems() => throw new NotImplementedException();
            public DownloadClientItem GetImportItem(DownloadClientItem item, DownloadClientItem previousImportAttempt) => throw new NotImplementedException();
            public void RemoveItem(DownloadClientItem item, bool deleteData) { RemoveCalls++; LastDeleteData = deleteData; }
            public DownloadClientInfo GetStatus() => throw new NotImplementedException();
            public void MarkItemAsImported(DownloadClientItem downloadClientItem) { MarkImportedCalls++; }
        }

        [Test]
        public void should_remove_completed_direct_download_with_delete_data_true()
        {
            var downloadClient = new StubDownloadClient
            {
                Definition = new DownloadClientDefinition { Id = 7, Name = "Direct Download", Protocol = DownloadProtocol.Direct, RemoveCompletedDownloads = true, RemoveFailedDownloads = true }
            };
            var subject = BuildSubject(downloadClient, preserve: false);

            subject.Handle(new DownloadCompletedEvent(TrackedDownload(), 1));

            Assert.That(downloadClient.MarkImportedCalls, Is.EqualTo(1));
            Assert.That(downloadClient.RemoveCalls, Is.EqualTo(1));
            Assert.That(downloadClient.LastDeleteData, Is.True);
        }

        [Test]
        public void should_preserve_completed_direct_download_when_definition_disables_cleanup()
        {
            var downloadClient = new StubDownloadClient
            {
                Definition = new DownloadClientDefinition { Id = 7, Name = "Direct Download", Protocol = DownloadProtocol.Direct, RemoveCompletedDownloads = false, RemoveFailedDownloads = true }
            };
            var subject = BuildSubject(downloadClient, preserve: false);

            subject.Handle(new DownloadCompletedEvent(TrackedDownload(), 1));

            Assert.That(downloadClient.MarkImportedCalls, Is.EqualTo(1));
            Assert.That(downloadClient.RemoveCalls, Is.EqualTo(0));
        }

        private static DownloadEventHub BuildSubject(StubDownloadClient downloadClient, bool preserve)
        {
            return new DownloadEventHub(null, new StubDownloadClientProvider(downloadClient), new StubDownloadImportModeResolver { Preserve = preserve }, LogManager.GetLogger("DirectDownloadLifecycleCharacterizationFixture"));
        }

        private static TrackedDownload TrackedDownload()
        {
            return new TrackedDownload
            {
                DownloadClient = 7,
                State = TrackedDownloadState.Imported,
                Protocol = DownloadProtocol.Direct,
                DownloadItem = new DownloadClientItem
                {
                    Title = "Frank Herbert - Dune [epub]",
                    DownloadId = "DIRECT-Catalog-123",
                    Status = DownloadItemStatus.Completed,
                    CanBeRemoved = true,
                    DownloadClientInfo = new DownloadClientItemClientInfo { Id = 7, Name = "Direct Download", Protocol = DownloadProtocol.Direct }
                }
            };
        }
    }
}
