using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Chaptarr.Api.V1.PendingImport;
using Microsoft.AspNetCore.Mvc;
using NUnit.Framework;
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
            public Task<int> EnqueueAsync(string providerId, MonitoringConfig config, string sourceApplication) => throw new NotImplementedException();
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
