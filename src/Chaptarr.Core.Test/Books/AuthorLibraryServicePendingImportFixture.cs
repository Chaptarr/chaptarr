using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Chaptarr.Core.Test;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class AuthorLibraryServicePendingImportFixture
    {
        private sealed class StubAuthorInfoNotFound : IProvideAuthorInfo
        {
            public Author GetAuthorInfo(string chaptarrId, bool useCache = true)
            {
                throw new AuthorNotFoundException(chaptarrId);
            }

            public RefreshResult RefreshAuthorInfo(string authorId, string etag = null, bool forceRefresh = false, string expectedPublishedETag = null, bool bypassEtag = false) => throw new NotImplementedException();
        }

        private sealed class StubAuthorService : IAuthorService
        {
            public Author FindByProviderId(string provider, string providerId) => null;

            public Author GetAuthor(int authorId) => throw new NotImplementedException();
            public List<Author> GetAuthors(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public Author AddAuthor(Author newAuthor, bool doRefresh) => throw new NotImplementedException();
            public List<Author> AddAuthors(List<Author> newAuthors, bool doRefresh) => throw new NotImplementedException();
            public Author FindByName(string title) => throw new NotImplementedException();
            public Author FindByNameInexact(string title) => throw new NotImplementedException();
            public List<Author> GetCandidates(string title) => throw new NotImplementedException();
            public List<Author> GetReportCandidates(string reportTitle) => throw new NotImplementedException();
            public void DeleteAuthor(int authorId, bool deleteFiles, bool addImportListExclusion = false) => throw new NotImplementedException();
            public List<Author> GetAllAuthors(bool bypassCache = false) => throw new NotImplementedException();
            public Dictionary<int, List<int>> GetAllAuthorTags() => throw new NotImplementedException();
            public List<Author> AllForTag(int tagId) => throw new NotImplementedException();
            public Author UpdateAuthor(Author author) => throw new NotImplementedException();
            public Author UpdateAuthorProgressiveSettings(Author author, int? audiobookQualityProfileId, int? audiobookMetadataProfileId, int? audiobookMonitorExisting, bool? audiobookMonitorFuture, int? ebookQualityProfileId, int? ebookMetadataProfileId, int? ebookMonitorExisting, bool? ebookMonitorFuture, string rootFolderPath) => throw new NotImplementedException();
            public List<Author> UpdateAuthors(List<Author> authors, bool useExistingRelativeFolder) => throw new NotImplementedException();
            public Dictionary<int, string> AllAuthorPaths() => throw new NotImplementedException();
            public bool AuthorPathExists(string folder) => throw new NotImplementedException();
            public void RemoveAddOptions(Author author) => throw new NotImplementedException();
            public void SetMediaTypeMonitoring(int authorId, string mediaType, bool monitored) => throw new NotImplementedException();
            public long GetAuthorSizeForMediaType(int authorId, string mediaType) => throw new NotImplementedException();
            public void UpdateLastSelectedMediaType(int authorId, string mediaType) => throw new NotImplementedException();
            public List<Book> GetAuthorBooksFromCache(int authorId) => throw new NotImplementedException();
            public List<int> GetAuthorIdsByMetadataProfileId(int metadataProfileId) => new List<int>();
            public void ClearAuthorCache() => throw new NotImplementedException();
        }

        private sealed class StubPendingAuthorImportService : IPendingAuthorImportService
        {
            private readonly int _enqueueResult;
            private readonly PendingAuthorImport _byProviderId;
            public MonitoringConfig LastConfig { get; private set; }

            public StubPendingAuthorImportService(int enqueueResult, PendingAuthorImport byProviderId)
            {
                _enqueueResult = enqueueResult;
                _byProviderId = byProviderId;
            }

            public Task<int> EnqueueAsync(string providerId, MonitoringConfig config, string sourceApplication)
            {
                LastConfig = config;
                return Task.FromResult(_enqueueResult);
            }
            public PendingAuthorImport GetByProviderId(string providerId) => _byProviderId;

            public List<PendingAuthorImport> GetAll() => throw new NotImplementedException();
            public List<PendingAuthorImport> GetDueForProcessing(int limit = 10) => throw new NotImplementedException();
            public void UpdateStatus(PendingAuthorImport item, PendingImportStatus status, string error) => throw new NotImplementedException();
            public void ScheduleRetry(PendingAuthorImport item, string error) => throw new NotImplementedException();
            public void Cancel(int id) => throw new NotImplementedException();
            public void RetryNow(int id) => throw new NotImplementedException();
            public void CleanupOldCompleted() => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
        }

        [Test]
        public async Task should_return_existing_pending_id_when_enqueue_returns_zero()
        {
            const string providerId = "hc:1205498";

            var pending = new PendingAuthorImport
            {
                Id = 123,
                ProviderId = providerId,
                OverallStatus = PendingImportStatus.Pending
            };

            var svc = new AuthorLibraryService(
                authorService: new StubAuthorService(),
                authorInfo: new StubAuthorInfoNotFound(),
                bookService: null,
                refreshSeriesService: null,
                editionService: null,
                narratorLinkService: null,
                metadataProfileService: null,
                qualityProfileService: new TestQualityProfileService(),
                authorPathBuilder: null,
                rootFolderService: null,
                commandQueueManager: null,
                eventAggregator: null,
                pendingImportService: new StubPendingAuthorImportService(enqueueResult: 0, byProviderId: pending),
                mainDatabase: null,
                importListExclusionService: null,
                editionMetadataProfileFilter: new EditionMetadataProfileFilter(new TestTermMatcherService()),
                syncMetadataService: null,
                logger: LogManager.GetCurrentClassLogger()
            );

            var config = new MonitoringConfig
            {
                QueueIfUnavailable = true,
                RequestedBy = "UserInterface",
                AuthorName = "Pending Import",
                CreateAudiobook = true,
                CreateEbook = false
            };

            var author = await svc.AddAuthorAsync(providerId, config);

            Assert.That(author.Id, Is.EqualTo(-pending.Id));
        }

        [Test]
        public async Task should_queue_the_selected_work_and_media_type_when_the_author_is_not_available()
        {
            const string authorProviderId = "hc:1205498";
            const string workProviderId = "hc:495645";
            var pending = new PendingAuthorImport
            {
                Id = 123,
                ProviderId = authorProviderId,
                OverallStatus = PendingImportStatus.Pending
            };
            var pendingService = new StubPendingAuthorImportService(123, pending);
            var svc = new AuthorLibraryService(
                authorService: new StubAuthorService(),
                authorInfo: new StubAuthorInfoNotFound(),
                bookService: null,
                refreshSeriesService: null,
                editionService: null,
                narratorLinkService: null,
                metadataProfileService: null,
                qualityProfileService: new TestQualityProfileService(),
                authorPathBuilder: null,
                rootFolderService: null,
                commandQueueManager: null,
                eventAggregator: null,
                pendingImportService: pendingService,
                mainDatabase: null,
                importListExclusionService: null,
                editionMetadataProfileFilter: new EditionMetadataProfileFilter(new TestTermMatcherService()),
                syncMetadataService: null,
                logger: LogManager.GetCurrentClassLogger()
            );
            var config = new MonitoringConfig
            {
                QueueIfUnavailable = false,
                RequestedBy = "DownloadedBooksImportService",
                AuthorName = "Pending Import",
                CreateAudiobook = true,
                CreateEbook = false
            };

            var result = await svc.MaterializeUserSelectedEditionAsync(
                new UserSelectedRemoteEdition
                {
                    AuthorProviderId = authorProviderId,
                    WorkProviderId = workProviderId,
                    EditionProviderId = "gr:9001",
                    EditionTitle = "Queued edition",
                    MediaType = BookMediaType.Audiobook,
                    QueueIfUnavailable = true
                },
                config);

            Assert.Multiple(() =>
            {
                Assert.That(result.Author.Id, Is.EqualTo(-pending.Id));
                Assert.That(pendingService.LastConfig.QueueIfUnavailable, Is.True);
                Assert.That(pendingService.LastConfig.AudiobookBooksToMonitor, Is.EquivalentTo(new[] { workProviderId }));
                Assert.That(pendingService.LastConfig.AudiobookMonitorExistingMode, Is.EqualTo(MonitorTypes.SpecificBook));
                Assert.That(pendingService.LastConfig.SelectedRemoteEditions, Has.Count.EqualTo(1));
                Assert.That(pendingService.LastConfig.SelectedRemoteEditions[0].EditionProviderId, Is.EqualTo("gr:9001"));
                Assert.That(pendingService.LastConfig.SelectedRemoteEditions[0].EditionTitle, Is.EqualTo("Queued edition"));
                Assert.That(pendingService.LastConfig.EbookBooksToMonitor, Is.Null);
                Assert.That(config.QueueIfUnavailable, Is.False);
                Assert.That(config.AudiobookBooksToMonitor, Is.Null);
            });
        }
    }
}
