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
    public class DownloadEventHubPreservationFixture
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
            private readonly IDownloadClient _downloadClient;

            public StubDownloadClientProvider(IDownloadClient downloadClient)
            {
                _downloadClient = downloadClient;
            }

            public IDownloadClient GetDownloadClient(DownloadProtocol downloadProtocol, BookMediaType mediaType, int indexerId = 0, bool filterBlockedClients = false, HashSet<int> tags = null)
            {
                throw new NotImplementedException();
            }

            public IEnumerable<IDownloadClient> GetDownloadClients(bool filterBlockedClients = false)
            {
                throw new NotImplementedException();
            }

            public IDownloadClient Get(int id) => _downloadClient;
        }

        private sealed class StubDownloadClient : IDownloadClient, IPreserveDownloadClientItemAfterImport
        {
            public int RemoveCalls { get; private set; }
            public bool? LastDeleteData { get; private set; }
            public int MarkImportedCalls { get; private set; }
            public bool PreserveAfterImport { get; set; }
            public int PreserveAfterImportChecks { get; private set; }
            public BookMediaType? LastPreserveAfterImportMediaType { get; private set; }

            public string Name => "Test Client";
            public Type ConfigContract => typeof(object);
            public ProviderMessage Message => null;
            public IEnumerable<ProviderDefinition> DefaultDefinitions => Array.Empty<ProviderDefinition>();
            public ProviderDefinition Definition { get; set; } = new DownloadClientDefinition
            {
                Id = 7,
                Name = "Test Client",
                RemoveCompletedDownloads = true,
                RemoveFailedDownloads = true
            };

            public DownloadProtocol Protocol => DownloadProtocol.Torrent;

            public Task<string> Download(RemoteBook remoteBook, IIndexer indexer)
            {
                throw new NotImplementedException();
            }

            public IEnumerable<DownloadClientItem> GetItems()
            {
                throw new NotImplementedException();
            }

            public DownloadClientItem GetImportItem(DownloadClientItem item, DownloadClientItem previousImportAttempt)
            {
                throw new NotImplementedException();
            }

            public void RemoveItem(DownloadClientItem item, bool deleteData)
            {
                RemoveCalls++;
                LastDeleteData = deleteData;
            }

            public DownloadClientInfo GetStatus()
            {
                throw new NotImplementedException();
            }

            public void MarkItemAsImported(DownloadClientItem downloadClientItem)
            {
                MarkImportedCalls++;
            }

            public bool ShouldPreserveItemAfterImport(DownloadClientItem downloadClientItem)
            {
                PreserveAfterImportChecks++;
                LastPreserveAfterImportMediaType = downloadClientItem.MediaType;
                return PreserveAfterImport;
            }

            public ValidationResult Test() => new();

            public object RequestAction(string stage, IDictionary<string, string> query)
            {
                throw new NotImplementedException();
            }
        }

        private static TrackedDownload TrackedDownload(BookMediaType? mediaType = null)
        {
            var trackedDownload = new TrackedDownload
            {
                DownloadClient = 7,
                State = TrackedDownloadState.Imported,
                DownloadItem = new DownloadClientItem
                {
                    Title = "Test Download",
                    DownloadId = "ABC",
                    Status = DownloadItemStatus.Completed,
                    CanBeRemoved = true,
                    DownloadClientInfo = new DownloadClientItemClientInfo
                    {
                        Id = 7,
                        Name = "Test Client",
                        Protocol = DownloadProtocol.Torrent
                    }
                }
            };

            if (mediaType.HasValue)
            {
                trackedDownload.RemoteBook = new RemoteBook
                {
                    Books = new List<Book>
                    {
                        new Book { MediaType = mediaType.Value }
                    }
                };
            }

            return trackedDownload;
        }

        private static DownloadEventHub BuildSubject(StubDownloadClient downloadClient, StubDownloadImportModeResolver resolver)
        {
            return new DownloadEventHub(
                null,
                new StubDownloadClientProvider(downloadClient),
                resolver,
                LogManager.GetLogger("DownloadEventHubPreservationFixture"));
        }

        [Test]
        public void should_remove_normal_completed_download_when_client_allows_removal()
        {
            var downloadClient = new StubDownloadClient();
            var resolver = new StubDownloadImportModeResolver { Preserve = false };
            var subject = BuildSubject(downloadClient, resolver);

            subject.Handle(new DownloadCompletedEvent(TrackedDownload(), 1));

            Assert.That(downloadClient.MarkImportedCalls, Is.EqualTo(1));
            Assert.That(downloadClient.RemoveCalls, Is.EqualTo(1));
            Assert.That(downloadClient.LastDeleteData, Is.True);
        }

        [Test]
        public void should_not_remove_preserved_completed_download_when_client_allows_removal()
        {
            var downloadClient = new StubDownloadClient();
            var resolver = new StubDownloadImportModeResolver { Preserve = true };
            var subject = BuildSubject(downloadClient, resolver);

            subject.Handle(new DownloadCompletedEvent(TrackedDownload(), 1));

            Assert.That(downloadClient.MarkImportedCalls, Is.EqualTo(1));
            Assert.That(downloadClient.RemoveCalls, Is.EqualTo(0));
        }

        [Test]
        public void should_not_remove_post_import_preserved_completed_download_when_client_allows_removal()
        {
            var downloadClient = new StubDownloadClient { PreserveAfterImport = true };
            var resolver = new StubDownloadImportModeResolver { Preserve = false };
            var subject = BuildSubject(downloadClient, resolver);

            subject.Handle(new DownloadCompletedEvent(TrackedDownload(), 1));

            Assert.That(downloadClient.MarkImportedCalls, Is.EqualTo(1));
            Assert.That(downloadClient.PreserveAfterImportChecks, Is.EqualTo(1));
            Assert.That(downloadClient.RemoveCalls, Is.EqualTo(0));
        }

        [Test]
        public void should_pass_remote_book_media_type_to_post_import_preserve_check()
        {
            var downloadClient = new StubDownloadClient { PreserveAfterImport = true };
            var resolver = new StubDownloadImportModeResolver { Preserve = false };
            var subject = BuildSubject(downloadClient, resolver);

            subject.Handle(new DownloadCompletedEvent(TrackedDownload(BookMediaType.Ebook), 1));

            Assert.That(downloadClient.LastPreserveAfterImportMediaType, Is.EqualTo(BookMediaType.Ebook));
            Assert.That(downloadClient.RemoveCalls, Is.EqualTo(0));
        }

        [Test]
        public void should_not_remove_preserved_download_when_later_removal_event_fires()
        {
            var downloadClient = new StubDownloadClient();
            var resolver = new StubDownloadImportModeResolver { Preserve = true };
            var subject = BuildSubject(downloadClient, resolver);

            subject.Handle(new DownloadCanBeRemovedEvent(TrackedDownload()));

            Assert.That(downloadClient.RemoveCalls, Is.EqualTo(0));
        }

        [Test]
        public void should_not_remove_post_import_preserved_download_when_later_removal_event_fires()
        {
            var downloadClient = new StubDownloadClient { PreserveAfterImport = true };
            var resolver = new StubDownloadImportModeResolver { Preserve = false };
            var subject = BuildSubject(downloadClient, resolver);

            subject.Handle(new DownloadCanBeRemovedEvent(TrackedDownload()));

            Assert.That(downloadClient.PreserveAfterImportChecks, Is.EqualTo(1));
            Assert.That(downloadClient.RemoveCalls, Is.EqualTo(0));
        }

        [Test]
        public void should_not_remove_preserved_failed_download()
        {
            var downloadClient = new StubDownloadClient();
            var resolver = new StubDownloadImportModeResolver { Preserve = true };
            var subject = BuildSubject(downloadClient, resolver);

            subject.Handle(new DownloadFailedEvent { TrackedDownload = TrackedDownload() });

            Assert.That(downloadClient.RemoveCalls, Is.EqualTo(0));
        }

        [Test]
        public void should_not_use_post_import_preservation_for_failed_downloads()
        {
            var downloadClient = new StubDownloadClient { PreserveAfterImport = true };
            var resolver = new StubDownloadImportModeResolver { Preserve = false };
            var subject = BuildSubject(downloadClient, resolver);

            subject.Handle(new DownloadFailedEvent { TrackedDownload = TrackedDownload() });

            Assert.That(downloadClient.PreserveAfterImportChecks, Is.EqualTo(0));
            Assert.That(downloadClient.RemoveCalls, Is.EqualTo(1));
        }
    }
}
