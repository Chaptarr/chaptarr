using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Chaptarr.Api.V1.PendingImport;
using Microsoft.AspNetCore.Mvc;
using NUnit.Framework;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.SignalR;

namespace Chaptarr.Core.Test.PendingImport
{
    [TestFixture]
    public class PendingAuthorImportControllerFixture
    {
        private sealed class StubSignalRBroadcaster : IBroadcastSignalRMessage
        {
            public bool IsConnected => false;
            public Task BroadcastMessage(SignalRMessage message) => Task.CompletedTask;
        }

        private sealed class StubQualityProfileService : IQualityProfileService
        {
            private readonly List<QualityProfile> _profiles;

            public StubQualityProfileService(params QualityProfile[] profiles)
            {
                _profiles = profiles.ToList();
            }

            public List<QualityProfile> All() => _profiles;
            public List<QualityProfile> GetByType(ProfileType type) => _profiles.Where(p => p.ProfileType == type).ToList();
            public QualityProfile Add(QualityProfile profile) => throw new NotImplementedException();
            public void Update(QualityProfile profile) => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
            public QualityProfile Get(int id) => throw new NotImplementedException();
            public bool Exists(int id) => _profiles.Any(p => p.Id == id);
            public QualityProfile GetDefaultProfile(string name, Quality cutoff = null, params Quality[] allowed) => throw new NotImplementedException();
        }

        private sealed class StubMetadataProfileService : IMetadataProfileService
        {
            private readonly List<MetadataProfile> _profiles;

            public StubMetadataProfileService(params MetadataProfile[] profiles)
            {
                _profiles = profiles.ToList();
            }

            public List<MetadataProfile> All() => _profiles;
            public MetadataProfile Add(MetadataProfile profile) => throw new NotImplementedException();
            public void Update(MetadataProfile profile) => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
            public MetadataProfile Get(int id) => throw new NotImplementedException();
            public bool Exists(int id) => _profiles.Any(p => p.Id == id);
            public List<Book> FilterBooks(Author input, int profileId) => throw new NotImplementedException();
        }

        private sealed class StubRootFolderService : IRootFolderService
        {
            private readonly List<RootFolder> _rootFolders;

            public StubRootFolderService(params RootFolder[] rootFolders)
            {
                _rootFolders = rootFolders.ToList();
            }

            public List<RootFolder> All() => _rootFolders;
            public List<RootFolder> AllWithSpaceStats() => throw new NotImplementedException();
            public RootFolder Add(RootFolder rootFolder) => throw new NotImplementedException();
            public RootFolder Update(RootFolder rootFolder) => throw new NotImplementedException();
            public void Remove(int id) => throw new NotImplementedException();
            public RootFolder Get(int id) => throw new NotImplementedException();
            public List<RootFolder> AllForTag(int tagId) => throw new NotImplementedException();
            public RootFolder GetBestRootFolder(string path) => throw new NotImplementedException();
            public RootFolder GetBestRootFolder(string path, List<RootFolder> allRootFolders) => throw new NotImplementedException();
            public string GetBestRootFolderPath(string path) => throw new NotImplementedException();
            public string GetBestRootFolderPath(string path, List<RootFolder> allRootFolders) => throw new NotImplementedException();
        }

        private sealed class StubPendingAuthorImportService : IPendingAuthorImportService
        {
            public MonitoringConfig EnqueuedConfig { get; private set; }

            public Task<int> EnqueueAsync(string providerId, MonitoringConfig config, string sourceApplication)
            {
                EnqueuedConfig = config;
                return Task.FromResult(42);
            }
            public List<PendingAuthorImport> GetAll() => new();
            public List<PendingAuthorImport> GetDueForProcessing(int limit = 10) => throw new NotImplementedException();
            public PendingAuthorImport GetByProviderId(string providerId) => throw new NotImplementedException();
            public void UpdateStatus(PendingAuthorImport item, PendingImportStatus status, string error) => throw new NotImplementedException();
            public void ScheduleRetry(PendingAuthorImport item, string error) => throw new NotImplementedException();
            public void Cancel(int id) => throw new NotImplementedException();
            public void RetryNow(int id) => throw new NotImplementedException();
            public void CleanupOldCompleted() => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
        }

        [Test]
        public void profile_options_should_use_profile_and_folder_types_not_names_or_paths()
        {
            var controller = new PendingAuthorImportController(
                new StubSignalRBroadcaster(),
                new StubPendingAuthorImportService(),
                authorService: null,
                new StubQualityProfileService(
                    CreateQualityProfile(1, "Spoken", ProfileType.Audiobook),
                    CreateQualityProfile(2, "Text", ProfileType.Ebook)),
                new StubMetadataProfileService(
                    CreateMetadataProfile(10, "General", MetadataProfileType.General),
                    CreateMetadataProfile(11, "Spoken Metadata", MetadataProfileType.Audiobook),
                    CreateMetadataProfile(12, "Text Metadata", MetadataProfileType.Ebook)),
                new StubRootFolderService(
                    new RootFolder { Id = 1, Name = "Media", Path = "/media/audiobooks", FolderType = FolderType.Audiobook },
                    new RootFolder { Id = 2, Name = "Text", Path = "/text", FolderType = FolderType.Ebook },
                    new RootFolder { Id = 3, Name = "Mixed", Path = "/mixed", FolderType = FolderType.Mixed }));

            var result = (OkObjectResult)controller.GetProfileOptions().Result;
            var options = (PendingImportProfileOptionsResource)result.Value;

            AssertIds(options.Audiobook.QualityProfiles, 1);
            AssertIds(options.Ebook.QualityProfiles, 2);
            AssertIds(options.Audiobook.MetadataProfiles, 10, 11);
            AssertIds(options.Ebook.MetadataProfiles, 10, 12);
            AssertPaths(options.Audiobook.RootFolders, "/media/audiobooks", "/mixed");
            AssertPaths(options.Ebook.RootFolders, "/text", "/mixed");
        }

        [Test]
        public void retrying_row_beyond_the_legacy_ceiling_should_remain_visible_as_retrying()
        {
            var resource = new PendingAuthorImport
            {
                Id = 42,
                ProviderId = "gr:42",
                OverallStatus = PendingImportStatus.Retrying,
                AudiobookStatus = PendingImportStatus.Retrying,
                EbookStatus = PendingImportStatus.NotRequested,
                AttemptCount = 101,
                MaxAttempts = 0,
                NextAttemptAt = DateTime.UtcNow.AddMinutes(5)
            }.ToResource();

            Assert.That(resource.OverallStatus, Is.EqualTo(nameof(PendingImportStatus.Retrying)));
            Assert.That(resource.AttemptCount, Is.EqualTo(101));
            Assert.That(resource.MaxAttempts, Is.Zero);
        }

        [Test]
        public void exact_book_search_targets_should_round_trip_through_the_api_resource()
        {
            var resource = new PendingAuthorImport
            {
                Id = 42,
                AudiobookBooksToSearch = "[\"gr:audio\"]",
                EbookBooksToSearch = "[\"gr:ebook\"]"
            }.ToResource();

            Assert.That(resource.AudiobookBooksToSearch, Is.EqualTo(new[] { "gr:audio" }));
            Assert.That(resource.EbookBooksToSearch, Is.EqualTo(new[] { "gr:ebook" }));

            var model = resource.ToModel();

            Assert.That(model.AudiobookBooksToSearch, Is.EqualTo("[\"gr:audio\"]"));
            Assert.That(model.EbookBooksToSearch, Is.EqualTo("[\"gr:ebook\"]"));
        }

        [Test]
        public void per_media_monitoring_settings_should_round_trip_without_legacy_monitoring_fields()
        {
            var resource = new PendingAuthorImport
            {
                Id = 43,
                AudiobookMonitored = true,
                AudiobookMonitorNewItems = NewItemMonitorTypes.New,
                AudiobookMonitorExistingMode = MonitorTypes.None,
                EbookMonitored = false,
                EbookMonitorNewItems = NewItemMonitorTypes.None,
                EbookMonitorExistingMode = MonitorTypes.All
            }.ToResource();

            Assert.That(resource.AudiobookMonitored, Is.True);
            Assert.That(resource.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
            Assert.That(resource.AudiobookMonitorExistingMode, Is.EqualTo(MonitorTypes.None));
            Assert.That(resource.EbookMonitored, Is.False);
            Assert.That(resource.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
            Assert.That(resource.EbookMonitorExistingMode, Is.EqualTo(MonitorTypes.All));

            var model = resource.ToModel();
            Assert.That(model.AudiobookMonitored, Is.True);
            Assert.That(model.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
            Assert.That(model.AudiobookMonitorExistingMode, Is.EqualTo(MonitorTypes.None));
            Assert.That(model.EbookMonitored, Is.False);
            Assert.That(model.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
            Assert.That(model.EbookMonitorExistingMode, Is.EqualTo(MonitorTypes.All));
        }

        [Test]
        public void per_media_tags_should_round_trip_with_null_and_empty_remaining_distinct()
        {
            var resource = new PendingAuthorImport
            {
                Id = 44,
                Tags = "[99]",
                AudiobookTags = "[1,2]",
                EbookTags = "[]",
                LastSelectedMediaType = "ebook"
            }.ToResource();

            Assert.That(resource.Tags, Is.EquivalentTo(new[] { 99 }));
            Assert.That(resource.AudiobookTags, Is.EquivalentTo(new[] { 1, 2 }));
            Assert.That(resource.EbookTags, Is.Empty);
            Assert.That(resource.LastSelectedMediaType, Is.EqualTo("ebook"));

            resource.LastSelectedMediaType = " EBOOK ";
            var model = resource.ToModel();
            Assert.That(model.AudiobookTags, Is.EqualTo("[1,2]"));
            Assert.That(model.EbookTags, Is.EqualTo("[]"));
            Assert.That(model.LastSelectedMediaType, Is.EqualTo("ebook"));
        }

        [Test]
        public void explicit_empty_legacy_tags_should_remain_distinct_from_not_supplied()
        {
            var model = new PendingAuthorImportResource
            {
                Id = 45,
                Tags = new HashSet<int>()
            }.ToModel();

            Assert.That(model.Tags, Is.EqualTo("[]"));
        }

        [Test]
        public async Task queue_should_apply_shared_tags_only_as_a_fallback_for_each_media_side()
        {
            var pendingService = new StubPendingAuthorImportService();
            var controller = new PendingAuthorImportController(
                new StubSignalRBroadcaster(),
                pendingService,
                authorService: null,
                new StubQualityProfileService(),
                new StubMetadataProfileService(),
                new StubRootFolderService());

            var result = await controller.QueueAuthor(new QueueAuthorRequest
            {
                ProviderId = "gr:123",
                Tags = new HashSet<int> { 99 },
                Audiobook = new MediaTypeConfig
                {
                    Monitor = true,
                    Tags = new HashSet<int>()
                },
                Ebook = new MediaTypeConfig
                {
                    Monitor = true,
                    Tags = new HashSet<int> { 2 }
                }
            });

            Assert.That(result.Result, Is.TypeOf<OkObjectResult>());
            Assert.That(pendingService.EnqueuedConfig.AudiobookTags, Is.Empty);
            Assert.That(pendingService.EnqueuedConfig.EbookTags, Is.EquivalentTo(new[] { 2 }));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task exact_book_target_should_enable_and_queue_its_media_side(bool monitor)
        {
            var pendingService = new StubPendingAuthorImportService();
            var controller = new PendingAuthorImportController(
                new StubSignalRBroadcaster(),
                pendingService,
                authorService: null,
                new StubQualityProfileService(),
                new StubMetadataProfileService(),
                new StubRootFolderService());

            var result = await controller.QueueAuthor(new QueueAuthorRequest
            {
                ProviderId = "gr:123",
                Audiobook = new MediaTypeConfig
                {
                    Monitor = monitor,
                    BooksToMonitor = new List<string> { "gr:456" }
                }
            });

            Assert.That(result.Result, Is.TypeOf<OkObjectResult>());
            Assert.That(pendingService.EnqueuedConfig.CreateAudiobook, Is.True);
            Assert.That(pendingService.EnqueuedConfig.AudiobookMonitored, Is.True);
            Assert.That(pendingService.EnqueuedConfig.AudiobookMonitorExistingMode, Is.EqualTo(MonitorTypes.SpecificBook));
            Assert.That(pendingService.EnqueuedConfig.AudiobookBooksToMonitor, Is.EqualTo(new[] { "gr:456" }));
        }

        [Test]
        public async Task bare_monitor_true_should_enable_the_media_side()
        {
            var pendingService = new StubPendingAuthorImportService();
            var controller = new PendingAuthorImportController(
                new StubSignalRBroadcaster(),
                pendingService,
                authorService: null,
                new StubQualityProfileService(),
                new StubMetadataProfileService(),
                new StubRootFolderService());

            var result = await controller.QueueAuthor(new QueueAuthorRequest
            {
                ProviderId = "gr:123",
                Ebook = new MediaTypeConfig { Monitor = true }
            });

            Assert.That(result.Result, Is.TypeOf<OkObjectResult>());
            Assert.That(pendingService.EnqueuedConfig.CreateEbook, Is.True);
            Assert.That(pendingService.EnqueuedConfig.EbookMonitored, Is.True);
        }

        [Test]
        public async Task explicit_off_side_should_remain_configured_and_paused()
        {
            var pendingService = new StubPendingAuthorImportService();
            var controller = new PendingAuthorImportController(
                new StubSignalRBroadcaster(),
                pendingService,
                authorService: null,
                new StubQualityProfileService(),
                new StubMetadataProfileService(),
                new StubRootFolderService());

            var result = await controller.QueueAuthor(new QueueAuthorRequest
            {
                ProviderId = "gr:123",
                Ebook = new MediaTypeConfig
                {
                    Monitor = false,
                    MonitorExistingMode = MonitorTypes.None,
                    MonitorNewItems = NewItemMonitorTypes.None,
                    RootFolderPath = "/ebooks"
                }
            });

            Assert.That(result.Result, Is.TypeOf<OkObjectResult>());
            Assert.That(pendingService.EnqueuedConfig.CreateEbook, Is.True);
            Assert.That(pendingService.EnqueuedConfig.EbookMonitored, Is.False);
            Assert.That(pendingService.EnqueuedConfig.EbookMonitorExistingMode, Is.EqualTo(MonitorTypes.None));
            Assert.That(pendingService.EnqueuedConfig.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
        }

        [Test]
        public async Task legacy_monitor_false_should_not_create_the_media_side()
        {
            var pendingService = new StubPendingAuthorImportService();
            var controller = new PendingAuthorImportController(
                new StubSignalRBroadcaster(),
                pendingService,
                authorService: null,
                new StubQualityProfileService(),
                new StubMetadataProfileService(),
                new StubRootFolderService());

            var result = await controller.QueueAuthor(new QueueAuthorRequest
            {
                ProviderId = "gr:123",
                Audiobook = new MediaTypeConfig
                {
                    Monitor = false,
                    MonitorExisting = 1,
                    MonitorFuture = true,
                    BooksToMonitor = new List<string> { "gr:456" }
                }
            });

            Assert.That(result.Result, Is.TypeOf<OkObjectResult>());
            Assert.That(pendingService.EnqueuedConfig.CreateAudiobook, Is.False);
            Assert.That(pendingService.EnqueuedConfig.AudiobookMonitored, Is.Null);
        }

        [Test]
        public async Task legacy_monitoring_fields_should_translate_to_the_binary_model()
        {
            var pendingService = new StubPendingAuthorImportService();
            var controller = new PendingAuthorImportController(
                new StubSignalRBroadcaster(),
                pendingService,
                authorService: null,
                new StubQualityProfileService(),
                new StubMetadataProfileService(),
                new StubRootFolderService());

            var request = STJson.Deserialize<QueueAuthorRequest>(
                """
                {
                  "providerId": "gr:123",
                  "ebook": {
                    "monitor": true,
                    "monitorExisting": 2,
                    "monitorFuture": true,
                    "booksToMonitor": ["gr:456"]
                  }
                }
                """);

            var result = await controller.QueueAuthor(request);

            Assert.That(result.Result, Is.TypeOf<OkObjectResult>());
            Assert.That(pendingService.EnqueuedConfig.CreateEbook, Is.True);
            Assert.That(pendingService.EnqueuedConfig.EbookMonitored, Is.True);
            Assert.That(pendingService.EnqueuedConfig.EbookMonitorExistingMode, Is.EqualTo(MonitorTypes.SpecificBook));
            Assert.That(pendingService.EnqueuedConfig.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
            Assert.That(pendingService.EnqueuedConfig.EbookBooksToMonitor, Is.EqualTo(new[] { "gr:456" }));
        }

        private static QualityProfile CreateQualityProfile(int id, string name, ProfileType type)
        {
            return new QualityProfile
            {
                Id = id,
                Name = name,
                ProfileType = type
            };
        }

        private static MetadataProfile CreateMetadataProfile(int id, string name, MetadataProfileType type)
        {
            return new MetadataProfile
            {
                Id = id,
                Name = name,
                ProfileType = type
            };
        }

        private static void AssertIds(IEnumerable<PendingImportProfileOptionResource> options, params int[] expectedIds)
        {
            var values = options.Select(item => item.Id).ToArray();

            Assert.That(values, Is.EqualTo(expectedIds));
        }

        private static void AssertPaths(IEnumerable<PendingImportRootFolderOptionResource> options, params string[] expectedPaths)
        {
            var values = options.Select(item => item.Path).ToArray();

            Assert.That(values, Is.EqualTo(expectedPaths));
        }
    }
}
