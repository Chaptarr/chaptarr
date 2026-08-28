using System;
using System.Collections.Generic;
using System.Reflection;
using Chaptarr.Api.V1.RootFolders;
using Chaptarr.Http.REST;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;
using NzbDrone.SignalR;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class RootFolderControllerTypeChangeFixture
    {
        private const string TypeChangeAssignedMessage = "You can't split a mixed-content root folder or switch an assigned single-type root folder. Remove it and re-add it as audiobook-only or eBook-only.";

        private class ThrowingProxy<T> : DispatchProxy where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private sealed class StubRootFolderService : IRootFolderService
        {
            public RootFolder Existing { get; set; }
            public RootFolder Updated { get; private set; }
            public int UpdateCallCount { get; private set; }

            public List<RootFolder> All() => new();
            public List<RootFolder> AllWithSpaceStats() => new();
            public RootFolder Add(RootFolder rootFolder) => throw new NotImplementedException();

            public RootFolder Update(RootFolder rootFolder)
            {
                Updated = rootFolder;
                UpdateCallCount++;
                return rootFolder;
            }

            public void Remove(int id) => throw new NotImplementedException();
            public RootFolder Get(int id) => Existing?.Id == id ? Existing : null;
            public List<RootFolder> AllForTag(int tagId) => throw new NotImplementedException();
            public RootFolder GetBestRootFolder(string path) => throw new NotImplementedException();
            public RootFolder GetBestRootFolder(string path, List<RootFolder> allRootFolders) => throw new NotImplementedException();
            public string GetBestRootFolderPath(string path) => throw new NotImplementedException();
            public string GetBestRootFolderPath(string path, List<RootFolder> allRootFolders) => throw new NotImplementedException();
        }

        private sealed class StubAuthorService : IAuthorService
        {
            private readonly List<Author> _authors;

            public StubAuthorService(List<Author> authors)
            {
                _authors = authors;
            }

            public List<Author> GetAllAuthors(bool bypassCache = false) => _authors;

            public Author GetAuthor(int authorId) => throw new NotImplementedException();
            public List<Author> GetAuthors(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public Author AddAuthor(Author newAuthor, bool doRefresh) => throw new NotImplementedException();
            public List<Author> AddAuthors(List<Author> newAuthors, bool doRefresh) => throw new NotImplementedException();
            public Author FindByProviderId(string provider, string providerId) => throw new NotImplementedException();
            public Author FindByName(string title) => throw new NotImplementedException();
            public Author FindByNameInexact(string title) => throw new NotImplementedException();
            public List<Author> GetCandidates(string title) => throw new NotImplementedException();
            public List<Author> GetReportCandidates(string reportTitle) => throw new NotImplementedException();
            public void DeleteAuthor(int authorId, bool deleteFiles, bool addImportListExclusion = false) => throw new NotImplementedException();
            public Dictionary<int, List<int>> GetAllAuthorTags() => throw new NotImplementedException();
            public List<Author> AllForTag(int tagId) => throw new NotImplementedException();
            public Author UpdateAuthor(Author author) => throw new NotImplementedException();
            public Author UpdateAuthorProgressiveSettings(Author author, int? audiobookQualityProfileId, int? audiobookMetadataProfileId, bool? audiobookMonitored, NewItemMonitorTypes? audiobookMonitorNewItems, int? ebookQualityProfileId, int? ebookMetadataProfileId, bool? ebookMonitored, NewItemMonitorTypes? ebookMonitorNewItems, string rootFolderPath) => throw new NotImplementedException();
            public List<Author> UpdateAuthors(List<Author> authors, bool useExistingRelativeFolder) => throw new NotImplementedException();
            public Dictionary<int, string> AllAuthorPaths() => throw new NotImplementedException();
            public bool AuthorPathExists(string folder) => throw new NotImplementedException();
            public void RemoveAddOptions(Author author) => throw new NotImplementedException();
            public void SetMediaTypeMonitoring(int authorId, string mediaType, bool monitored) => throw new NotImplementedException();
            public long GetAuthorSizeForMediaType(int authorId, string mediaType) => throw new NotImplementedException();
            public void UpdateLastSelectedMediaType(int authorId, string mediaType) => throw new NotImplementedException();
            public List<Book> GetAuthorBooksFromCache(int authorId) => throw new NotImplementedException();
            public List<int> GetAuthorIdsByMetadataProfileId(int metadataProfileId) => throw new NotImplementedException();
            public void ClearAuthorCache() => throw new NotImplementedException();
        }

        private sealed class StubLocalizationService : ILocalizationService
        {
            public string LastPhrase { get; private set; }

            public Dictionary<string, string> GetLocalizationDictionary()
            {
                return new Dictionary<string, string>();
            }

            public string GetLocalizedString(string phrase)
            {
                LastPhrase = phrase;
                return phrase == "RootFolderTypeChangeAssignedMessage" ? TypeChangeAssignedMessage : phrase;
            }

            public string GetLocalizedString(string phrase, Dictionary<string, object> tokens)
            {
                return GetLocalizedString(phrase);
            }
        }

        private static RootFolderController BuildController(
            StubRootFolderService rootFolderService,
            StubAuthorService authorService,
            StubLocalizationService localizationService = null)
        {
            localizationService ??= new StubLocalizationService();

            return new RootFolderController(
                rootFolderService,
                DispatchProxy.Create<IRootFolderScanService, ThrowingProxy<IRootFolderScanService>>(),
                authorService,
                DispatchProxy.Create<ICalibreProxy, ThrowingProxy<ICalibreProxy>>(),
                ConfigServiceTestProxy.Create(),
                localizationService,
                DispatchProxy.Create<IBroadcastSignalRMessage, ThrowingProxy<IBroadcastSignalRMessage>>(),
                new RecycleBinValidator(DispatchProxy.Create<IConfigService, ThrowingProxy<IConfigService>>()),
                new RootFolderValidator(rootFolderService),
                new PathExistsValidator(DispatchProxy.Create<IDiskProvider, ThrowingProxy<IDiskProvider>>()),
                new MappedNetworkDriveValidator(
                    DispatchProxy.Create<IRuntimeInfo, ThrowingProxy<IRuntimeInfo>>(),
                    DispatchProxy.Create<IDiskProvider, ThrowingProxy<IDiskProvider>>()),
                new StartupFolderValidator(DispatchProxy.Create<IAppFolderInfo, ThrowingProxy<IAppFolderInfo>>()),
                new SystemFolderValidator(),
                new FolderWritableValidator(DispatchProxy.Create<IDiskProvider, ThrowingProxy<IDiskProvider>>()),
                new FolderReadableValidator(DispatchProxy.Create<IDiskProvider, ThrowingProxy<IDiskProvider>>()),
                new QualityProfileExistsValidator(DispatchProxy.Create<IQualityProfileService, ThrowingProxy<IQualityProfileService>>()),
                new MetadataProfileExistsValidator(DispatchProxy.Create<IMetadataProfileService, ThrowingProxy<IMetadataProfileService>>()));
        }

        [Test]
        public void should_reject_direct_single_type_change_when_authors_are_assigned_to_root_folder()
        {
            var rootFolderService = new StubRootFolderService
            {
                Existing = new RootFolder
                {
                    Id = 42,
                    Name = "Books",
                    Path = "/books",
                    FolderType = FolderType.Ebook
                }
            };

            var localizationService = new StubLocalizationService();
            var controller = BuildController(
                rootFolderService,
                new StubAuthorService(new List<Author>
                {
                    new()
                    {
                        Id = 100,
                        Name = "Greg Pak",
                        EbookRootFolderPath = "/books"
                    }
                }),
                localizationService);

            var resource = new RootFolderResource
            {
                Id = 42,
                Name = "Books",
                Path = "/books",
                FolderType = (int)FolderType.Audiobook
            };

            var ex = Assert.Throws<BadRequestException>(() => controller.UpdateRootFolder(resource));

            Assert.That(ex.Message, Does.EndWith(TypeChangeAssignedMessage));
            Assert.That(localizationService.LastPhrase, Is.EqualTo("RootFolderTypeChangeAssignedMessage"));
            Assert.That(rootFolderService.UpdateCallCount, Is.EqualTo(0));
        }

        [TestCase(FolderType.Audiobook)]
        [TestCase(FolderType.Ebook)]
        public void should_allow_widening_to_mixed_when_authors_are_assigned(FolderType existingType)
        {
            var rootFolderService = new StubRootFolderService
            {
                Existing = new RootFolder
                {
                    Id = 42,
                    Name = "Books",
                    Path = "/books",
                    FolderType = existingType
                }
            };

            var controller = BuildController(
                rootFolderService,
                new StubAuthorService(new List<Author>
                {
                    new()
                    {
                        Id = 100,
                        Name = "Assigned Author",
                        AudiobookRootFolderPath = "/books",
                        EbookRootFolderPath = "/books"
                    }
                }));

            controller.UpdateRootFolder(new RootFolderResource
            {
                Id = 42,
                Name = "Books",
                Path = "/books",
                FolderType = (int)FolderType.Mixed
            });

            Assert.That(rootFolderService.UpdateCallCount, Is.EqualTo(1));
            Assert.That(rootFolderService.Updated.FolderType, Is.EqualTo(FolderType.Mixed));
        }

        [Test]
        public void should_reject_narrowing_mixed_root_when_authors_are_assigned()
        {
            var rootFolderService = new StubRootFolderService
            {
                Existing = new RootFolder
                {
                    Id = 42,
                    Name = "Books",
                    Path = "/books",
                    FolderType = FolderType.Mixed
                }
            };
            var controller = BuildController(
                rootFolderService,
                new StubAuthorService(new List<Author>
                {
                    new()
                    {
                        Id = 100,
                        Name = "Assigned Author",
                        AudiobookRootFolderPath = "/books"
                    }
                }));

            var ex = Assert.Throws<BadRequestException>(() => controller.UpdateRootFolder(new RootFolderResource
            {
                Id = 42,
                Name = "Books",
                Path = "/books",
                FolderType = (int)FolderType.Audiobook
            }));

            Assert.That(ex.Message, Does.EndWith(TypeChangeAssignedMessage));
            Assert.That(rootFolderService.UpdateCallCount, Is.EqualTo(0));
        }

        [Test]
        public void should_allow_type_change_when_root_folder_is_unassigned()
        {
            var rootFolderService = new StubRootFolderService
            {
                Existing = new RootFolder
                {
                    Id = 42,
                    Name = "Books",
                    Path = "/books",
                    FolderType = FolderType.Ebook
                }
            };

            var controller = BuildController(rootFolderService, new StubAuthorService(new List<Author>()));

            var resource = new RootFolderResource
            {
                Id = 42,
                Name = "Books",
                Path = "/books",
                FolderType = (int)FolderType.Audiobook
            };

            controller.UpdateRootFolder(resource);

            Assert.That(rootFolderService.UpdateCallCount, Is.EqualTo(1));
            Assert.That(rootFolderService.Updated.FolderType, Is.EqualTo(FolderType.Audiobook));
        }

        [Test]
        public void should_still_reject_root_folder_path_edits()
        {
            var rootFolderService = new StubRootFolderService
            {
                Existing = new RootFolder
                {
                    Id = 42,
                    Name = "Books",
                    Path = "/books",
                    FolderType = FolderType.Ebook
                }
            };
            var controller = BuildController(rootFolderService, new StubAuthorService(new List<Author>()));

            var ex = Assert.Throws<BadRequestException>(() => controller.UpdateRootFolder(new RootFolderResource
            {
                Id = 42,
                Name = "Books",
                Path = "/other-books",
                FolderType = (int)FolderType.Ebook
            }));

            Assert.That(ex.Message, Does.EndWith("Cannot edit root folder path"));
            Assert.That(rootFolderService.UpdateCallCount, Is.EqualTo(0));
        }

        [Test]
        public void resource_should_preserve_default_sync_monitored_across_formats()
        {
            var resource = new RootFolderResource
            {
                Name = "Mixed",
                Path = "/library",
                FolderType = (int)FolderType.Mixed,
                DefaultSyncMonitoredAcrossFormats = true,
                AudiobookMonitored = true,
                AudiobookMonitorExistingMode = MonitorTypes.Missing,
                AudiobookMonitorNewItems = NewItemMonitorTypes.All,
                EbookMonitored = false,
                EbookMonitorExistingMode = MonitorTypes.Existing,
                EbookMonitorNewItems = NewItemMonitorTypes.None
            };

            var model = resource.ToModel();

            Assert.Multiple(() =>
            {
                Assert.That(model.DefaultSyncMonitoredAcrossFormats, Is.True);
                Assert.That(model.GetAudiobookSettings().Monitored, Is.True);
                Assert.That(model.GetAudiobookSettings().MonitorExistingMode, Is.EqualTo(MonitorTypes.Missing));
                Assert.That(model.GetAudiobookSettings().MonitorNewItems, Is.EqualTo(NewItemMonitorTypes.All));
                Assert.That(model.GetEbookSettings().Monitored, Is.False);
                Assert.That(model.GetEbookSettings().MonitorExistingMode, Is.EqualTo(MonitorTypes.Existing));
                Assert.That(model.GetEbookSettings().MonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
            });
        }

        [Test]
        public void structured_canonical_initial_mode_should_win_over_the_deprecated_boolean()
        {
            var resource = new RootFolderResource
            {
                Name = "Audio",
                Path = "/audio",
                FolderType = (int)FolderType.Audiobook,
                Audiobook = new MediaTypeSettingsResource
                {
                    Monitored = true,
                    MonitorExistingMode = MonitorTypes.Missing,
                    MonitorExistingBooks = false,
                    MonitorNewItems = NewItemMonitorTypes.None
                }
            };

            var settings = resource.ToModel().GetAudiobookSettings();

            Assert.That(settings.MonitorExistingMode, Is.EqualTo(MonitorTypes.Missing));
        }



        [Test]
        public void resource_should_round_trip_audiobookshelf_sidecar_settings_per_media_type()
        {
            var resource = new RootFolderResource
            {
                Name = "Mixed",
                Path = "/library",
                FolderType = (int)FolderType.Mixed,
                AudiobookMonitored = true,
                AudiobookMonitorNewItems = NewItemMonitorTypes.All,
                EbookMonitored = true,
                EbookMonitorNewItems = NewItemMonitorTypes.New,
                AudiobookWriteAudioBookShelfMetadataJson = true,
                AudiobookWriteAudioBookShelfCover = false,
                EbookWriteAudioBookShelfMetadataJson = false,
                EbookWriteAudioBookShelfCover = true
            };

            var model = resource.ToModel();
            var roundTripped = model.ToResource();

            Assert.Multiple(() =>
            {
                Assert.That(model.GetAudiobookSettings().WriteAudioBookShelfMetadataJson, Is.True);
                Assert.That(model.GetAudiobookSettings().WriteAudioBookShelfCover, Is.False);
                Assert.That(model.GetEbookSettings().WriteAudioBookShelfMetadataJson, Is.False);
                Assert.That(model.GetEbookSettings().WriteAudioBookShelfCover, Is.True);
                Assert.That(roundTripped.AudiobookWriteAudioBookShelfMetadataJson, Is.True);
                Assert.That(roundTripped.AudiobookWriteAudioBookShelfCover, Is.False);
                Assert.That(roundTripped.EbookWriteAudioBookShelfMetadataJson, Is.False);
                Assert.That(roundTripped.EbookWriteAudioBookShelfCover, Is.True);
            });
        }

        [Test]
        public void resource_should_allow_empty_opposite_media_settings_for_single_type_folder()
        {
            var resource = new RootFolderResource
            {
                Name = "Audio",
                Path = "/audio",
                FolderType = (int)FolderType.Audiobook,
                AudiobookMonitored = true,
                Ebook = new MediaTypeSettingsResource(),
                EbookTags = new List<int>()
            };

            var model = resource.ToModel();

            Assert.That(model.FolderType, Is.EqualTo(FolderType.Audiobook));
            Assert.That(model.GetEbookSettings(), Is.Null);
        }

        [Test]
        public void resource_should_translate_legacy_selected_future_to_binary_settings()
        {
            var resource = new RootFolderResource
            {
                Name = "Audio",
                Path = "/audio",
                FolderType = (int)FolderType.Audiobook,
                AudiobookMonitorExisting = 2,
                AudiobookMonitorFuture = true
            };

            var settings = resource.ToModel().GetAudiobookSettings();

            Assert.Multiple(() =>
            {
                Assert.That(settings.Monitored, Is.True);
                Assert.That(settings.MonitorExistingBooks, Is.False);
                Assert.That(settings.MonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
            });
        }

        [Test]
        public void resource_should_project_binary_settings_for_legacy_root_clients()
        {
            var root = new RootFolder
            {
                Name = "Audio",
                Path = "/audio",
                FolderType = FolderType.Audiobook
            };
            root.SetAudiobookSettings(new MediaTypeSettings
            {
                Monitored = true,
                MonitorExistingBooks = false,
                MonitorNewItems = NewItemMonitorTypes.New
            });

            var resource = root.ToResource();

            Assert.Multiple(() =>
            {
                Assert.That(resource.AudiobookMonitorExisting, Is.EqualTo(2));
                Assert.That(resource.AudiobookMonitorFuture, Is.True);
                Assert.That(resource.Audiobook.MonitorExisting, Is.EqualTo(2));
                Assert.That(resource.Audiobook.MonitorFuture, Is.True);
            });
        }

        [Test]
        public void legacy_root_monitoring_json_should_migrate_selected_future_to_binary_settings()
        {
            var root = new RootFolder
            {
                AudiobookSettings = "{\"MonitorExisting\":2,\"MonitorFuture\":true}"
            };

            var settings = root.GetAudiobookSettings();

            Assert.Multiple(() =>
            {
                Assert.That(settings.Monitored, Is.True);
                Assert.That(settings.MonitorExistingBooks, Is.False);
                Assert.That(settings.MonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
            });
        }

        [Test]
        public void canonical_root_json_should_round_trip_the_full_initial_mode_without_writing_the_old_boolean()
        {
            var root = new RootFolder();
            root.SetAudiobookSettings(new MediaTypeSettings
            {
                Monitored = true,
                MonitorExistingMode = MonitorTypes.Missing,
                MonitorNewItems = NewItemMonitorTypes.All
            });

            var settings = root.GetAudiobookSettings();

            Assert.Multiple(() =>
            {
                Assert.That(settings.MonitorExistingMode, Is.EqualTo(MonitorTypes.Missing));
                Assert.That(root.AudiobookSettings, Does.Contain("\"MonitorExistingMode\""));
                Assert.That(root.AudiobookSettings, Does.Not.Contain("\"MonitorExistingBooks\""));
            });
        }
    }
}
