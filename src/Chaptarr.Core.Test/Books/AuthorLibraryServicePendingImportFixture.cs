using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Chaptarr.Core.Test;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using FluentValidation;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class AuthorLibraryServicePendingImportFixture
    {
        private sealed class StubAuthorInfoNotFound : IProvideAuthorInfo
        {
            public Func<Author> OnGet { get; set; }
            public int Calls { get; private set; }

            public Author GetAuthorInfo(string chaptarrId, bool useCache = true)
            {
                Calls++;
                if (OnGet != null) return OnGet();
                throw new AuthorNotFoundException(chaptarrId);
            }

            public RefreshResult RefreshAuthorInfo(string authorId, string etag = null, bool forceRefresh = false, string expectedPublishedETag = null, bool bypassEtag = false) => throw new NotImplementedException();
        }

        private sealed class StubAuthorPathBuilder : IBuildAuthorPaths
        {
            public string BuildPath(Author author, bool useExistingRelativeFolder) => Path.Combine(@"C:\authors".AsOsAgnostic(), author.Name);
            public string BuildPathForQuality(Author author, NzbDrone.Core.Qualities.Quality quality, bool useExistingRelativeFolder) =>
                quality == NzbDrone.Core.Qualities.Quality.EPUB
                    ? Path.Combine(author.EbookRootFolderPath, author.Name)
                    : Path.Combine(author.AudiobookRootFolderPath, author.Name);
            public void EnsureAuthorPaths(Author author, bool useExistingRelativeFolder) { }
        }

        private sealed class StubImportListExclusionService : IImportListExclusionService
        {
            public List<ImportListExclusion> Exclusions { get; } = new List<ImportListExclusion>();
            public int AllCalls { get; private set; }
            public void ValidateAuthorExclusions(IEnumerable<Author> authors) => throw new NotImplementedException();
            public void AddAuthorExclusions(IEnumerable<Author> authors, BookMediaType? mediaType = null) => throw new NotImplementedException();
            public ImportListExclusion Add(ImportListExclusion exclusion) => throw new NotImplementedException();
            public List<ImportListExclusion> All() { AllCalls++; return Exclusions.ToList(); }
            public void Delete(int id) => throw new NotImplementedException();
            public void Delete(List<int> ids) => throw new NotImplementedException();
            public void Delete(string foreignId) => throw new NotImplementedException();
            public ImportListExclusion Get(int id) => throw new NotImplementedException();
            public ImportListExclusion FindByForeignId(string foreignId) => throw new NotImplementedException();
            public List<ImportListExclusion> FindByForeignId(List<string> foreignIds) => throw new NotImplementedException();
            public ImportListExclusion Update(ImportListExclusion exclusion) => throw new NotImplementedException();
        }

        private sealed class StubAuthorService : IAuthorService
        {
            public Func<string, string, Author> OnFindByProviderId { get; set; }
            public int AddAuthorCalls { get; private set; }
            public Author FindByProviderId(string provider, string providerId) => OnFindByProviderId?.Invoke(provider, providerId);

            public Author GetAuthor(int authorId) => throw new NotImplementedException();
            public List<Author> GetAuthors(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public Author AddAuthor(Author newAuthor, bool doRefresh) { AddAuthorCalls++; throw new NotImplementedException(); }
            public List<Author> AddAuthors(List<Author> newAuthors, bool doRefresh) => throw new NotImplementedException();
            public Author FindByName(string title) => throw new NotImplementedException();
            public Author FindByNameInexact(string title) => throw new NotImplementedException();
            public List<Author> GetCandidates(string title) => throw new NotImplementedException();
            public List<Author> GetReportCandidates(string reportTitle) => throw new NotImplementedException();
            public void DeleteAuthor(int authorId, bool deleteFiles, bool addImportListExclusion = false) => throw new NotImplementedException();
            public void DeleteAuthors(List<int> authorIds, bool deleteFiles, bool addImportListExclusion = false, BookMediaType? mediaType = null, bool applyToBothFormats = false) => throw new NotImplementedException();
            public List<Author> GetAllAuthors(bool bypassCache = false) => throw new NotImplementedException();
            public Dictionary<int, List<int>> GetAllAuthorTags() => throw new NotImplementedException();
            public List<Author> AllForTag(int tagId) => throw new NotImplementedException();
            public Author UpdateAuthor(Author author) => throw new NotImplementedException();
            public Author UpdateAuthorProgressiveSettings(Author author, int? audiobookQualityProfileId, int? audiobookMetadataProfileId, int? audiobookMonitorExisting, bool? audiobookMonitorFuture, int? ebookQualityProfileId, int? ebookMetadataProfileId, int? ebookMonitorExisting, bool? ebookMonitorFuture, string rootFolderPath) => throw new NotImplementedException();
            public List<Author> UpdateAuthors(List<Author> authors, bool useExistingRelativeFolder) => throw new NotImplementedException();
            public List<KeyValuePair<int, string>> AllAuthorPaths() => throw new NotImplementedException();
            public bool AuthorPathExists(string folder) => throw new NotImplementedException();
            public void SetAddOptions(Author author) => throw new NotImplementedException();
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
            public int EnqueueCalls { get; private set; }

            public StubPendingAuthorImportService(int enqueueResult, PendingAuthorImport byProviderId)
            {
                _enqueueResult = enqueueResult;
                _byProviderId = byProviderId;
            }

            public Task<int> EnqueueAsync(string providerId, MonitoringConfig config, string sourceApplication)
            {
                EnqueueCalls++;
                return Task.FromResult(_enqueueResult);
            }
            public PendingAuthorImport GetByProviderId(string providerId) => _byProviderId;

            public List<PendingAuthorImport> GetAll() => throw new NotImplementedException();
            public List<PendingAuthorImport> GetDueForProcessing(int limit = 10) => throw new NotImplementedException();
            public bool UpdateStatus(PendingAuthorImport item, PendingImportStatus status, string error, long? expectedVersion = null) => throw new NotImplementedException();
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

            var svc = BuildSubject(
                new StubAuthorInfoNotFound(),
                new StubPendingAuthorImportService(enqueueResult: 0, byProviderId: pending),
                new StubImportListExclusionService());

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

        [TestCase("ImportList:Dune", true)]
        [TestCase("ImportListSync", true)]
        [TestCase("UserInterface", false)]
        public async Task import_list_provenance_applies_media_exclusion_but_direct_request_keeps_user_choice(string requestedBy, bool appliesExclusion)
        {
            var exclusions = new StubImportListExclusionService();
            exclusions.Exclusions.Add(new ImportListExclusion { ForeignId = "gr:58", Name = "Frank Herbert", MediaType = BookMediaType.Ebook });
            var pending = new StubPendingAuthorImportService(1, null);
            var info = new StubAuthorInfoNotFound();
            var service = BuildSubject(info, pending, exclusions);
            var config = new MonitoringConfig
            {
                QueueIfUnavailable = true,
                RequestedBy = requestedBy,
                CreateAudiobook = true,
                CreateEbook = true,
                EbookBooksToMonitor = new List<string> { "gr:3634639" },
                EbookBooksToSearch = new List<string> { "gr:3634639" }
            };
            var result = await service.AddAuthorAsync("gr:58", config);
            Assert.That(result.Id, Is.EqualTo(-1));
            Assert.That(config.CreateAudiobook, Is.True);
            Assert.That(config.CreateEbook, Is.EqualTo(!appliesExclusion));
            if (appliesExclusion)
            {
                Assert.That(config.EbookBooksToMonitor, Is.Null);
                Assert.That(config.EbookBooksToSearch, Is.Null);
            }
            else
            {
                Assert.That(config.EbookBooksToMonitor, Is.EqualTo(new[] { "gr:3634639" }));
                Assert.That(config.EbookBooksToSearch, Is.EqualTo(new[] { "gr:3634639" }));
            }
            Assert.That(pending.EnqueueCalls, Is.EqualTo(1));
        }

        [Test]
        public void ebook_only_list_request_is_blocked_before_fetch_or_queue()
        {
            var exclusions = new StubImportListExclusionService();
            exclusions.Exclusions.Add(new ImportListExclusion { ForeignId = "gr:58", Name = "Frank Herbert", MediaType = BookMediaType.Ebook });
            var pending = new StubPendingAuthorImportService(1, null);
            var info = new StubAuthorInfoNotFound();
            var service = BuildSubject(info, pending, exclusions);
            Assert.ThrowsAsync<ValidationException>(async () => await service.AddAuthorAsync("gr:58", new MonitoringConfig
            {
                QueueIfUnavailable = true,
                RequestedBy = "ImportList:Dune",
                CreateAudiobook = false,
                CreateEbook = true
            }));
            Assert.That(info.Calls, Is.Zero);
            Assert.That(pending.EnqueueCalls, Is.Zero);
        }

        private static AuthorLibraryService BuildSubject(
            StubAuthorInfoNotFound authorInfo,
            StubPendingAuthorImportService pending,
            StubImportListExclusionService exclusions,
            StubAuthorService authorService = null)
        {
            return new AuthorLibraryService(
                authorService: authorService ?? new StubAuthorService(),
                authorInfo: authorInfo,
                bookService: null,
                refreshSeriesService: null,
                editionService: null,
                narratorLinkService: null,
                metadataProfileService: null,
                qualityProfileService: new TestQualityProfileService(),
                authorPathBuilder: new StubAuthorPathBuilder(),
                rootFolderService: null,
                commandQueueManager: null,
                eventAggregator: null,
                pendingImportService: pending,
                mainDatabase: null,
                importListExclusionService: exclusions,
                editionMetadataProfileFilter: new EditionMetadataProfileFilter(new TestTermMatcherService()),
                syncMetadataService: null,
                logger: LogManager.GetCurrentClassLogger());
        }

        [Test]
        public void stale_import_list_author_is_rejected_before_provider_fetch()
        {
            const string providerId = "hc:1205498";
            var authorInfo = new StubAuthorInfoNotFound();
            var pending = new StubPendingAuthorImportService(1, null);
            var exclusions = new StubImportListExclusionService();
            exclusions.Exclusions.Add(new ImportListExclusion { ForeignId = providerId, Name = "Deleted author" });
            var service = BuildSubject(authorInfo, pending, exclusions);

            var error = Assert.ThrowsAsync<ValidationException>(async () => await service.AddAuthorAsync(providerId, new MonitoringConfig
            {
                QueueIfUnavailable = true,
                RequestedBy = "ImportList:Favorites"
            }));

            Assert.That(error.Errors.Single().ErrorCode, Is.EqualTo(AuthorLibraryService.ImportListAuthorExcludedErrorCode));
            Assert.That(authorInfo.Calls, Is.Zero);
            Assert.That(pending.EnqueueCalls, Is.Zero);
        }

        [Test]
        public void provider_id_requested_before_fetch_remains_excluded_after_metadata_returns_only_alias_target()
        {
            const string requestedProviderId = "gr:old-alias";
            var exclusions = new StubImportListExclusionService();
            var authorInfo = new StubAuthorInfoNotFound
            {
                OnGet = () =>
                {
                    exclusions.Exclusions.Add(new ImportListExclusion { ForeignId = requestedProviderId, Name = "Deleted alias" });
                    return new Author
                    {
                        Name = "Canonical Author",
                        HardcoverAuthorId = "hc:new-canonical",
                        Path = @"C:\root\audio\Canonical Author".AsOsAgnostic(),
                        AudiobookPath = @"C:\root\audio\Canonical Author".AsOsAgnostic(),
                        EbookPath = @"C:\root\ebook\Canonical Author".AsOsAgnostic(),
                        AudiobookRootFolderPath = @"C:\root\audio".AsOsAgnostic(),
                        EbookRootFolderPath = @"C:\root\ebook".AsOsAgnostic()
                    };
                }
            };
            var pending = new StubPendingAuthorImportService(1, null);
            var service = BuildSubject(authorInfo, pending, exclusions);

            var error = Assert.ThrowsAsync<ValidationException>(async () => await service.AddAuthorAsync(requestedProviderId, new MonitoringConfig
            {
                RequestedBy = "ImportList:Favorites"
            }));

            Assert.That(error.Errors.Single().ErrorCode, Is.EqualTo(AuthorLibraryService.ImportListAuthorExcludedErrorCode));
            Assert.That(authorInfo.Calls, Is.EqualTo(1));
            Assert.That(exclusions.AllCalls, Is.EqualTo(2));
        }

        [Test]
        public void exclusion_committed_after_lookup_still_blocks_a_deleted_author_from_being_readded()
        {
            const string providerId = "hc:1205498";
            var exclusions = new StubImportListExclusionService();
            var authorInfo = new StubAuthorInfoNotFound
            {
                OnGet = () => new Author
                {
                    Name = "Deleted Author",
                    HardcoverAuthorId = providerId,
                    Path = @"C:\root\audio\Deleted Author".AsOsAgnostic(),
                    AudiobookPath = @"C:\root\audio\Deleted Author".AsOsAgnostic(),
                    EbookPath = @"C:\root\ebook\Deleted Author".AsOsAgnostic(),
                    AudiobookRootFolderPath = @"C:\root\audio".AsOsAgnostic(),
                    EbookRootFolderPath = @"C:\root\ebook".AsOsAgnostic()
                }
            };
            var pending = new StubPendingAuthorImportService(1, null);
            var authorService = new StubAuthorService
            {
                OnFindByProviderId = (_, _) =>
                {
                    // Model deletion finishing between lookup and the final insert gate:
                    // the row is gone and its durable exclusion is now visible.
                    exclusions.Exclusions.Add(new ImportListExclusion { ForeignId = providerId, Name = "Deleted Author" });
                    return null;
                }
            };
            var service = BuildSubject(authorInfo, pending, exclusions, authorService);

            var error = Assert.ThrowsAsync<ValidationException>(async () => await service.AddAuthorAsync(providerId, new MonitoringConfig
            {
                RequestedBy = "ImportList:Favorites"
            }));

            Assert.That(error.Errors.Single().ErrorCode, Is.EqualTo(AuthorLibraryService.ImportListAuthorExcludedErrorCode));
            Assert.That(authorService.AddAuthorCalls, Is.Zero);
        }

        [Test]
        public async Task author_exclusion_does_not_match_another_provider_with_the_same_numeric_payload()
        {
            const string providerId = "gr:12345";
            var authorInfo = new StubAuthorInfoNotFound();
            var pending = new StubPendingAuthorImportService(1, null);
            var exclusions = new StubImportListExclusionService();
            exclusions.Exclusions.Add(new ImportListExclusion { ForeignId = "hc:12345", Name = "Different provider identity" });
            var service = BuildSubject(authorInfo, pending, exclusions);

            var result = await service.AddAuthorAsync(providerId, new MonitoringConfig
            {
                QueueIfUnavailable = true,
                RequestedBy = "ImportList:Favorites"
            });

            Assert.That(result.Id, Is.EqualTo(-1));
            Assert.That(pending.EnqueueCalls, Is.EqualTo(1));
        }
    }
}
