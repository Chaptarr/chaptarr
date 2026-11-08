using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Chaptarr.Core.Test;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Messaging.Events;
using Chaptarr.Core.Test.Common;
using FluentValidation;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.ImportLists;
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
            public void EnsureAuthorPaths(Author author, bool useExistingRelativeFolder, Author previous = null) { }
        }

        private sealed class StubImportListExclusionService : IImportListExclusionService
        {
            public List<ImportListExclusion> Exclusions { get; } = new List<ImportListExclusion>();
            public int LookupCalls { get; private set; }
            public void ValidateAuthorExclusions(IEnumerable<Author> authors) => throw new NotImplementedException();
            public void AddAuthorExclusions(IEnumerable<Author> authors, BookMediaType? mediaType, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction) => throw new System.NotSupportedException();
            public void AddAuthorExclusions(IEnumerable<Author> authors, BookMediaType? mediaType = null) => throw new NotImplementedException();
            public ImportListExclusion Add(ImportListExclusion exclusion) => throw new NotImplementedException();
            public List<ImportListExclusion> All() => throw new AssertionException("The add gate must not load every exclusion");
            public void Delete(int id) => throw new NotImplementedException();
            public void Delete(List<int> ids) => throw new NotImplementedException();
            public ImportListExclusion Get(int id) => throw new NotImplementedException();
            public List<ImportListExclusion> FindByForeignId(List<string> foreignIds, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction) => throw new System.NotSupportedException();
            public List<ImportListExclusion> FindByForeignId(List<string> foreignIds)
            {
                LookupCalls++;
                return Exclusions.Where(row => foreignIds.Contains(row.ForeignId.Trim(), StringComparer.OrdinalIgnoreCase)).ToList();
            }
            public ImportListExclusion Update(ImportListExclusion exclusion) => throw new NotImplementedException();
        }

        private sealed class StubAuthorService : IAuthorService
        {
            public Func<string, string, Author> OnFindByProviderId { get; set; }
            public int AddAuthorCalls { get; private set; }
            public Author FindByProviderId(string provider, string providerId) => OnFindByProviderId?.Invoke(provider, providerId);

            public Author GetAuthor(int authorId) => throw new NotImplementedException();
            public List<Author> GetExistingAuthors(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Author> GetAuthors(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public Author AddAuthor(Author newAuthor, bool doRefresh) { AddAuthorCalls++; throw new NotImplementedException(); }
            public List<Author> AddAuthors(List<Author> newAuthors, bool doRefresh) => throw new NotImplementedException();
            public Author FindByName(string title) => throw new NotImplementedException();
            public List<Author> GetCandidates(string title) => throw new NotImplementedException();
            public List<Author> GetReportCandidates(string reportTitle) => throw new NotImplementedException();
            public void DeleteAuthor(int authorId, bool deleteFiles, bool addImportListExclusion = false) => throw new NotImplementedException();
            public void DeleteAuthors(List<int> authorIds, bool deleteFiles, bool addImportListExclusion = false, BookMediaType? mediaType = null, bool applyToBothFormats = false, bool deleteOtherAuthorsFiles = false) => throw new NotImplementedException();
            public List<Author> GetAllAuthors(bool bypassCache = false) => throw new NotImplementedException();
            public Dictionary<int, List<int>> GetAllAuthorTags() => throw new NotImplementedException();
            public List<Author> AllForTag(int tagId) => throw new NotImplementedException();
            public Author UpdateAuthor(Author author, System.Action<System.Data.IDbConnection, System.Data.IDbTransaction> beforeSave) => throw new System.NotSupportedException();
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
            public bool ActiveRequest { get; set; } = true;
            public bool IsActiveRequest(int id, long version) => ActiveRequest;

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

            public PendingAuthorImport Get(int id) => GetAll().Find(item => item.Id == id);
            public NzbDrone.Core.Datastore.PagingSpec<PendingAuthorImport> GetPaged(NzbDrone.Core.Datastore.PagingSpec<PendingAuthorImport> pagingSpec) => throw new NotImplementedException();
            public List<PendingAuthorImport> GetAll() => throw new NotImplementedException();
            public List<PendingAuthorImport> GetDueForProcessing(int limit = 10) => throw new NotImplementedException();
            public bool UpdateStatus(PendingAuthorImport item, PendingImportStatus status, string error, long? expectedVersion = null) => throw new NotImplementedException();
            public bool SaveProgress(PendingAuthorImport item, PendingAuthorImport previous) => throw new NotImplementedException();
            public void ScheduleRetry(PendingAuthorImport item, string error) => throw new NotImplementedException();
            public void Cancel(int id) => throw new NotImplementedException();
            public bool CancelIfQueued(int id) => throw new NotImplementedException();
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
            using var factory = new LogFactory();
            var warnings = new NLog.Targets.MemoryTarget { Layout = "${message}" };
            var logging = new NLog.Config.LoggingConfiguration(factory);
            logging.AddRule(LogLevel.Warn, LogLevel.Fatal, warnings);
            factory.Configuration = logging;
            var service = BuildSubject(info, pending, exclusions, logger: factory.GetLogger("exclusion-test"));
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
            Assert.That(warnings.Logs.Count(line => line.Contains("[IMPORT-LIST-EXCLUSION]")), Is.EqualTo(appliesExclusion ? 1 : 0));
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

        [TestCase(false)]
        [TestCase(true)]
        public void merged_manual_audio_request_does_not_bypass_list_only_ebook_exclusion_after_reopen(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var authors = new StubAuthorService();
                var events = InterfaceProxy.Proxy<IEventAggregator>((_, _) => null);
                var repository = new PendingAuthorImportRepository(database, events);
                var pending = new PendingAuthorImportService(repository, authors, events, LogManager.GetCurrentClassLogger());
                pending.EnqueueAsync("gr:58", new MonitoringConfig
                {
                    AuthorName = "Frank Herbert", CreateAudiobook = true, CreateEbook = true,
                    RequestedBy = "ImportList:Favorites"
                }, "ImportList:Favorites").GetAwaiter().GetResult();
                pending.EnqueueAsync("gr:58", new MonitoringConfig { CreateAudiobook = true, CreateEbook = false }, "UserInterface").GetAwaiter().GetResult();
                var saved = new PendingAuthorImportRepository(database, events).GetActiveByProviderId("gr:58");
                Assert.That(saved.RequestedBy, Is.EqualTo("UserInterface"));
                Assert.That(saved.AudiobookRequestedBy, Is.EqualTo("UserInterface"));
                Assert.That(saved.EbookRequestedBy, Is.EqualTo("ImportList:Favorites"));
                var handler = new ProcessPendingImportsCommandHandler(pending, null, null, null, null, events, LogManager.GetCurrentClassLogger());
                var config = (MonitoringConfig)typeof(ProcessPendingImportsCommandHandler).GetMethod("BuildMonitoringConfig", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(handler, new object[] { saved });
                config.QueueIfUnavailable = true;
                var exclusions = new StubImportListExclusionService();
                exclusions.Exclusions.Add(new ImportListExclusion { ForeignId = "58", Name = "Frank Herbert", MediaType = BookMediaType.Ebook });
                var queue = new StubPendingAuthorImportService(1, null);
                BuildSubject(new StubAuthorInfoNotFound(), queue, exclusions).AddAuthorAsync("gr:58", config).GetAwaiter().GetResult();
                Assert.That(config.CreateAudiobook, Is.True);
                Assert.That(config.CreateEbook, Is.False);
                Assert.That(exclusions.LookupCalls, Is.EqualTo(1));
            });
        }

        [Test]
        public void persisted_exclusion_with_case_and_whitespace_blocks_the_list_add(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var exclusions = new ImportListExclusionService(new ImportListExclusionRepository(database, null), LogManager.GetCurrentClassLogger());
                exclusions.Add(new ImportListExclusion { ForeignId = "  GR:58  ", Name = "Frank Herbert", MediaType = media });
                var pending = new StubPendingAuthorImportService(1, null);
                var info = new StubAuthorInfoNotFound();
                var service = BuildSubject(info, pending, exclusions);
                var error = Assert.ThrowsAsync<ValidationException>(async () => await service.AddAuthorAsync("gr:58", new MonitoringConfig
                {
                    QueueIfUnavailable = true, RequestedBy = "ImportList:Favorites",
                    CreateAudiobook = media == BookMediaType.Audiobook, CreateEbook = media == BookMediaType.Ebook
                }));
                Assert.That(error.Errors.Single().ErrorCode, Is.EqualTo(AuthorLibraryService.ImportListAuthorExcludedErrorCode));
                Assert.That(info.Calls, Is.Zero);
                Assert.That(pending.EnqueueCalls, Is.Zero);
            });
        }

        private static AuthorLibraryService BuildSubject(
            StubAuthorInfoNotFound authorInfo,
            StubPendingAuthorImportService pending,
            IImportListExclusionService exclusions,
            StubAuthorService authorService = null,
            Logger logger = null)
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
                logger: logger ?? LogManager.GetCurrentClassLogger());
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
            Assert.That(exclusions.LookupCalls, Is.EqualTo(2));
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
        [Test]
        public void cancelled_request_should_stop_after_provider_fetch_before_author_persistence()
        {
            var pending = new StubPendingAuthorImportService(1, null);
            var authorService = new StubAuthorService();
            var info = new StubAuthorInfoNotFound { OnGet = () =>
            {
                pending.ActiveRequest = false;
                return new Author { Name = "Fixture Author", HardcoverAuthorId = "hc:9001", Books = new List<Book>(),
                    AudiobookRootFolderPath = @"C:\Audio".AsOsAgnostic() };
            } };
            var service = BuildSubject(info, pending, new StubImportListExclusionService(), authorService);
            Assert.ThrowsAsync<OperationCanceledException>(async () => await service.AddAuthorAsync("hc:9001", new MonitoringConfig
            {
                RequestedBy = "UserInterface", PendingImportId = 42, PendingImportVersion = 1, IsFromQueue = true,
                CreateAudiobook = false, CreateEbook = false
            }));
            Assert.That(info.Calls, Is.EqualTo(1));
            Assert.That(authorService.AddAuthorCalls, Is.Zero);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void unavailable_explicit_narration_should_protect_only_its_server_present_audio_parent(bool explicitNarration)
        {
            var requested = new Book { HardcoverBookId = "hc:1001", MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition> { new() { ForeignEditionId = "hc:edition:2001", ReadingFormatId = 2, Narrator = "Fixture Narrator" } } };
            var unrelated = new Book { HardcoverBookId = "hc:1002", MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition> { new() { ForeignEditionId = "hc:edition:9999", ReadingFormatId = 2, Narrator = "Other Narrator" } } };
            var config = new MonitoringConfig { CreateAudiobook = true, CreateEbook = false,
                AudiobookNarrations = new List<RequestedNarration> { new() { WorkProviderId = "hc:1001", EditionProviderId = "hc:edition:9999", RequiresEdition = explicitNarration } } };
            AuthorLibraryService.PrepareRequestedNarrations(new Author { Books = new List<Book> { requested, unrelated } }, config);
            Assert.Multiple(() =>
            {
                Assert.That(config.ProtectedNarrationIds, Is.Empty);
                Assert.That(config.ProtectedNarrationWorkIds.GetValueOrDefault(BookMediaType.Audiobook)?.Contains("hc:1001") == true, Is.EqualTo(explicitNarration));
                Assert.That(config.ProtectedNarrationWorkIds.GetValueOrDefault(BookMediaType.Audiobook)?.Contains("hc:1002") == true, Is.False);
                Assert.That(config.KnownParentEditionRequests.Contains((BookMediaType.Audiobook, "hc:edition:9999")), Is.EqualTo(explicitNarration));
            });
        }

        [TestCase("wrong-work")]
        [TestCase("wrong-media")]
        [TestCase("partial-cast")]
        [TestCase("different-cast")]
        public void requested_cast_should_not_protect_an_incompatible_catalog_edition(string mismatch)
        {
            var parent = new Book { HardcoverBookId = "hc:1001", MediaType = BookMediaType.Audiobook, Editions = new List<Edition>() };
            var candidate = new Book { HardcoverBookId = mismatch == "wrong-work" ? "hc:1002" : "hc:1001",
                MediaType = mismatch == "wrong-media" ? BookMediaType.Ebook : BookMediaType.Audiobook,
                Editions = new List<Edition> { new() { ForeignEditionId = "hc:edition:2001", ReadingFormatId = 2,
                    NarratorNames = mismatch == "different-cast" ? new List<string> { "Other Narrator", "Jane Smith" } :
                        mismatch == "partial-cast" ? new List<string> { "Jim Dale" } : new List<string> { "Jim Dale", "Jane Smith" } } } };
            var config = new MonitoringConfig { CreateAudiobook = true, CreateEbook = false,
                AudiobookNarrations = new List<RequestedNarration> { new() { WorkProviderId = "hc:1001", EditionProviderId = "hc:edition:9999",
                    RequiresEdition = true, NarratorNames = new List<string> { "Jim Dale", "Jane Smith" } } } };

            AuthorLibraryService.PrepareRequestedNarrations(new Author { Books = new List<Book> { parent, candidate } }, config);

            Assert.Multiple(() =>
            {
                Assert.That(config.ProtectedNarrationIds, Is.Empty);
                Assert.That(config.KnownParentEditionRequests, Does.Contain((BookMediaType.Audiobook, "hc:edition:9999")));
                Assert.That(config.ProtectedNarrationWorkIds.GetValueOrDefault(BookMediaType.Audiobook), Does.Contain("hc:1001"));
                Assert.That(config.ProtectedNarrationWorkIds.GetValueOrDefault(BookMediaType.Audiobook), Does.Not.Contain("hc:1002"));
            });
        }

        [Test]
        public async Task pending_author_marker_preserves_known_name_and_keeps_unknown_name_null(
            [Values("Selected Search Author", null)] string authorName)
        {
            var info = new StubAuthorInfoNotFound();
            var pending = new StubPendingAuthorImportService(41, null);
            var service = BuildSubject(info, pending, new StubImportListExclusionService());
            var author = await service.AddAuthorAsync("hc:2222", new MonitoringConfig
            {
                QueueIfUnavailable = true, RequestedBy = "UserInterface", AuthorName = authorName,
                CreateAudiobook = true, CreateEbook = false
            });
            Assert.Multiple(() =>
            {
                Assert.That(author.Id, Is.EqualTo(-41));
                Assert.That(author.Name, Is.EqualTo(authorName));
                Assert.That(author.CleanName, Is.EqualTo(authorName == null ? null : NzbDrone.Core.Parser.Parser.CleanAuthorName(authorName)));
                Assert.That(info.Calls, Is.EqualTo(1));
                Assert.That(pending.EnqueueCalls, Is.EqualTo(1));
            });
        }

    }
}
