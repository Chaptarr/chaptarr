using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Chaptarr.Api.V1.RootFolders;
using Chaptarr.Http.REST;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Localization;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;
using NzbDrone.SignalR;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

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

            public List<RootFolder> Roots { get; set; } = new();
            public List<RootFolder> All() => Roots;
            public List<RootFolder> AllWithSpaceStats() => new();
            public RootFolder Add(RootFolder rootFolder) { rootFolder.Id = 42; Existing = rootFolder; return rootFolder; }

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

        private class ValidationEnvironmentProxy<T> : DispatchProxy where T : class
        {
            public bool Writable { get; set; } = true;
            protected override object Invoke(MethodInfo method, object[] args)
            {
                return method.Name switch
                {
                    "get_RecycleBin" => string.Empty,
                    "get_StartUpFolder" => @"C:\application".AsOsAgnostic(),
                    "get_IsWindowsService" => false,
                    nameof(IDiskProvider.FolderExists) => true,
                    nameof(IDiskProvider.FolderWritable) => Writable,
                    nameof(IDiskProvider.GetFileInfos) => new List<IFileInfo>(),
                    _ => throw new NotImplementedException(method.Name)
                };
            }
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
            public List<Author> GetExistingAuthors(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public List<Author> GetAuthors(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public Author AddAuthor(Author newAuthor, bool doRefresh) => throw new NotImplementedException();
            public List<Author> AddAuthors(List<Author> newAuthors, bool doRefresh) => throw new NotImplementedException();
            public Author FindByProviderId(string provider, string providerId) => throw new NotImplementedException();
            public Author FindByName(string title) => throw new NotImplementedException();
            public List<Author> GetCandidates(string title) => throw new NotImplementedException();
            public List<Author> GetReportCandidates(string reportTitle) => throw new NotImplementedException();
            public void DeleteAuthor(int authorId, bool deleteFiles, bool addImportListExclusion = false) => throw new NotImplementedException();
            public void DeleteAuthors(List<int> authorIds, bool deleteFiles, bool addImportListExclusion = false, BookMediaType? mediaType = null, bool applyToBothFormats = false, bool deleteOtherAuthorsFiles = false) => throw new NotImplementedException();
            public Dictionary<int, List<int>> GetAllAuthorTags() => throw new NotImplementedException();
            public List<Author> AllForTag(int tagId) => throw new NotImplementedException();
            public Author UpdateAuthor(Author author, System.Action<System.Data.IDbConnection, System.Data.IDbTransaction> beforeSave) => throw new System.NotSupportedException();
            public Author UpdateAuthor(Author author) => throw new NotImplementedException();
            public Author UpdateAuthorProgressiveSettings(Author author, int? audiobookQualityProfileId, int? audiobookMetadataProfileId, bool? audiobookMonitored, NewItemMonitorTypes? audiobookMonitorNewItems, int? ebookQualityProfileId, int? ebookMetadataProfileId, bool? ebookMonitored, NewItemMonitorTypes? ebookMonitorNewItems, string rootFolderPath) => throw new NotImplementedException();
            public List<Author> UpdateAuthors(List<Author> authors, bool useExistingRelativeFolder) => throw new NotImplementedException();
            public List<KeyValuePair<int, string>> AllAuthorPaths() => throw new NotImplementedException();
            public bool AuthorPathExists(string folder) => throw new NotImplementedException();
            public void SetAddOptions(Author author) => throw new NotImplementedException();
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

        private sealed class TestableRootFolderController : RootFolderController
        {
            public TestableRootFolderController(
                StubRootFolderService rootFolderService,
                StubAuthorService authorService,
                StubLocalizationService localizationService, bool writable = true, ICalibreProxy calibre = null)
                : base(
                    rootFolderService,
                    DispatchProxy.Create<IRootFolderScanService, ThrowingProxy<IRootFolderScanService>>(),
                    authorService,
                    calibre ?? DispatchProxy.Create<ICalibreProxy, ThrowingProxy<ICalibreProxy>>(),
                    ConfigServiceTestProxy.Create(),
                    localizationService,
                    DispatchProxy.Create<IBroadcastSignalRMessage, ThrowingProxy<IBroadcastSignalRMessage>>(),
                    new RecycleBinValidator(DispatchProxy.Create<IConfigService, ValidationEnvironmentProxy<IConfigService>>()),
                    new RootFolderValidator(rootFolderService),
                    new PathExistsValidator(DispatchProxy.Create<IDiskProvider, ValidationEnvironmentProxy<IDiskProvider>>()),
                    new MappedNetworkDriveValidator(
                        DispatchProxy.Create<IRuntimeInfo, ValidationEnvironmentProxy<IRuntimeInfo>>(),
                        DispatchProxy.Create<IDiskProvider, ValidationEnvironmentProxy<IDiskProvider>>()),
                    new StartupFolderValidator(DispatchProxy.Create<IAppFolderInfo, ValidationEnvironmentProxy<IAppFolderInfo>>()),
                    new SystemFolderValidator(),
                    new FolderWritableValidator(CreateWritableDisk(writable)),
                    new FolderReadableValidator(DispatchProxy.Create<IDiskProvider, ValidationEnvironmentProxy<IDiskProvider>>()),
                    new QualityProfileExistsValidator(new TestQualityProfileService()),
                    new MetadataProfileExistsValidator(new TestMetadataProfileService()))
            {
            }

            public ValidationResult ValidateShared(RootFolderResource resource)
            {
                return SharedValidator.Validate(resource);
            }

            public void ValidateRequest(string verb, RootFolderResource resource)
            {
                var http = new DefaultHttpContext();
                http.Request.Method = verb;
                http.Request.Path = "/api/v1/rootfolder";
                var descriptor = new ControllerActionDescriptor
                {
                    MethodInfo = typeof(RootFolderController).GetMethod(verb == "POST" ? nameof(CreateRootFolder) : nameof(UpdateRootFolder))
                };
                var action = new ActionContext(http, new RouteData(), descriptor);
                ControllerContext = new ControllerContext(action);
                OnActionExecuting(new ActionExecutingContext(action, new List<IFilterMetadata>(),
                    new Dictionary<string, object> { ["rootFolderResource"] = resource }, this));
            }
        }

        private static TestableRootFolderController BuildController(
            StubRootFolderService rootFolderService,
            StubAuthorService authorService,
            StubLocalizationService localizationService = null)
        {
            localizationService ??= new StubLocalizationService();

            return new TestableRootFolderController(rootFolderService, authorService, localizationService);
        }

        private static IDiskProvider CreateWritableDisk(bool writable)
        {
            var disk = DispatchProxy.Create<IDiskProvider, ValidationEnvironmentProxy<IDiskProvider>>();
            ((ValidationEnvironmentProxy<IDiskProvider>)(object)disk).Writable = writable;
            return disk;
        }

        [TestCase("create")]
        [TestCase("enable")]
        [TestCase("unchanged")]
        [TestCase("name")]
        [TestCase("anonymous-null")]
        [TestCase("anonymous-empty")]
        [TestCase("anonymous-legacy-password")]
        [TestCase("disable-auth")]
        [TestCase("password")]
        [TestCase("host")]
        [TestCase("library")]
        [TestCase("disable")]
        public void calibre_is_tested_only_for_creation_enabling_or_changed_settings(string change)
        {
            var existing = new RootFolder
            {
                Id = 42, Name = "Books", Path = @"C:\books".AsOsAgnostic(), FolderType = FolderType.Ebook,
                IsCalibreLibrary = change != "enable",
                CalibreSettings = new CalibreSettings { Host = "localhost", Library = "Books", Username = "reader", Password = "saved" }
            };
            if (change.StartsWith("anonymous"))
            {
                existing.CalibreSettings.Username = string.Empty;
                existing.CalibreSettings.Password = change == "anonymous-null" ? null : change == "anonymous-legacy-password" ? "saved" : string.Empty;
            }
            var roots = new StubRootFolderService { Existing = existing };
            if (change != "create") roots.Roots.Add(existing);
            var tests = 0;
            string testedPassword = null;
            var controller = new TestableRootFolderController(roots, new StubAuthorService(new List<Author>()), new StubLocalizationService(),
                calibre: Proxy<ICalibreProxy>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(ICalibreProxy.Test)));
                    tests++;
                    testedPassword = ((CalibreSettings)args[0]).Password;
                    return null;
                }));
            var originalPassword = existing.CalibreSettings.Password;
            var resource = existing.ToResource();
            resource.IsCalibreLibrary = change != "disable";
            if (change == "disable-auth") resource.Username = string.Empty;
            if (change == "name") resource.Name = "New name";
            if (change == "password") resource.Password = "changed";
            if (change == "host") resource.Host = "another-host";
            if (change == "library") resource.Library = "Other";
            if (change == "create") { resource.Password = "saved"; controller.CreateRootFolder(resource); }
            else controller.UpdateRootFolder(resource);
            if (change.StartsWith("anonymous")) Assert.That(roots.Updated.CalibreSettings.Password, Is.EqualTo(originalPassword));
            Assert.That(tests, Is.EqualTo(change is "unchanged" or "name" or "disable" or "anonymous-null" or "anonymous-empty" or "anonymous-legacy-password" ? 0 : 1));
            if (change != "create") Assert.That(roots.UpdateCallCount, Is.EqualTo(1));
            if (tests > 0) Assert.That(testedPassword, Is.EqualTo(change == "password" ? "changed" : change == "disable-auth" ? string.Empty : "saved"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void resolved_default_library_cannot_duplicate_an_existing_root(bool update)
        {
            var existing = new RootFolder { Id = 42, Path = @"C:\other".AsOsAgnostic(), FolderType = FolderType.Ebook };
            var roots = new StubRootFolderService { Existing = existing, Roots = new()
            {
                new RootFolder { Id = 1, CalibreSettings = new CalibreSettings { Host = "localhost", Library = "books" } }
            } };
            var controller = new TestableRootFolderController(roots, new StubAuthorService(new List<Author>()), new StubLocalizationService(),
                calibre: Proxy<ICalibreProxy>((_, args) => { ((CalibreSettings)args[0]).Library = "books"; return null; }));
            var resource = existing.ToResource();
            resource.IsCalibreLibrary = true;
            resource.Host = "localhost";
            resource.Port = roots.Roots[0].CalibreSettings.Port;
            resource.Library = string.Empty;
            Assert.That(controller.ValidateShared(resource).Errors.Select(failure => failure.ErrorMessage),
                Does.Not.Contain("Calibre library is already configured as a root folder"));
            var error = Assert.Throws<FluentValidation.ValidationException>(() =>
            {
                if (update) controller.UpdateRootFolder(resource);
                else controller.CreateRootFolder(resource);
            });
            Assert.That(error.Errors.Single().ErrorMessage, Is.EqualTo("Calibre library is already configured as a root folder"));
            Assert.That(roots.Existing, Is.SameAs(existing));
            Assert.That(roots.UpdateCallCount, Is.Zero);
        }

        [TestCase(false)] [TestCase(true)]
        public void duplicate_calibre_library_uses_the_same_normalized_identity_as_backoff(bool sameLibrary)
        {
            var settings = new CalibreSettings { Host = "localhost", Port = 8080, UrlBase = "/calibre", Library = "books" };
            var roots = new StubRootFolderService { Roots = new() { new RootFolder { Id = 1, Path = @"C:\existing".AsOsAgnostic(), CalibreSettings = settings } } };
            var controller = BuildController(roots, new StubAuthorService(new List<Author>()));
            var resource = new RootFolderResource { Id = 2, Name = "Other", Path = @"C:\other".AsOsAgnostic(), FolderType = (int)FolderType.Ebook,
                IsCalibreLibrary = true, Host = "LOCALHOST", Port = 8080, UrlBase = "/calibre/", Library = sameLibrary ? "books" : "other" };
            var result = controller.ValidateShared(resource);
            Assert.That(result.Errors.Any(failure => failure.ErrorMessage == "Calibre library is already configured as a root folder"), Is.EqualTo(sameLibrary));
        }

        [Test]
        public void invalid_calibre_identities_do_not_collide()
        {
            var roots = new StubRootFolderService { Roots = new() { new RootFolder { Id = 1, Path = @"C:\existing".AsOsAgnostic(), CalibreSettings = new CalibreSettings { Host = "" } } } };
            var controller = BuildController(roots, new StubAuthorService(new List<Author>()));
            var resource = new RootFolderResource { Id = 2, Name = "Other", Path = @"C:\other".AsOsAgnostic(), FolderType = (int)FolderType.Ebook,
                IsCalibreLibrary = true, Host = "", Port = 0 };
            Assert.That(controller.ValidateShared(resource).Errors, Has.None.Matches<FluentValidation.Results.ValidationFailure>(failure => failure.ErrorMessage == "Calibre library is already configured as a root folder"));
        }

        [TestCase("POST")]
        [TestCase("PUT")]
        public void unwritable_root_reports_the_docker_bind_mount_hint_at_the_api(string verb)
        {
            var controller = new TestableRootFolderController(new StubRootFolderService(),
                new StubAuthorService(new List<Author>()), new StubLocalizationService(), writable: false);
            var resource = new RootFolderResource
            {
                Id = verb == "PUT" ? 42 : 0, Name = "Books", Path = @"C:\books".AsOsAgnostic(),
                FolderType = (int)FolderType.Audiobook, AudiobookQualityProfileId = 10, AudiobookMetadataProfileId = 20
            };
            var error = Assert.Throws<FluentValidation.ValidationException>(() => controller.ValidateRequest(verb, resource));
            var failure = error.Errors.Single(item => item.PropertyName.Equals("path", StringComparison.OrdinalIgnoreCase));
            Assert.That(failure.ErrorCode, Is.EqualTo("RootFolderNotWritable"));
            Assert.That(failure.ErrorMessage, Does.Contain("Docker bind mounts").And.Contain("root:root"));
        }

        [Test]
        public void post_should_require_an_explicit_folder_type_in_json()
        {
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RootFolderResource>(
                "{\"name\":\"Books\",\"path\":\"/books\"}",
                STJson.GetSerializerSettings()));
        }

        [Test]
        public void should_reject_an_out_of_range_folder_type()
        {
            var controller = BuildController(new StubRootFolderService(), new StubAuthorService(new List<Author>()));
            var result = controller.ValidateShared(new RootFolderResource
            {
                Name = "Books",
                Path = null,
                FolderType = 99
            });

            Assert.That(result.Errors.Any(failure => failure.ErrorMessage.Contains("Invalid folder type")), Is.True);
        }

        [TestCase(FolderType.Audiobook)]
        [TestCase(FolderType.Ebook)]
        public void post_should_reject_a_single_type_root_without_profile_defaults(FolderType folderType)
        {
            var controller = BuildController(new StubRootFolderService(), new StubAuthorService(new List<Author>()));
            var result = controller.ValidateShared(new RootFolderResource
            {
                Name = "Books",
                Path = @"C:\books".AsOsAgnostic(),
                FolderType = (int)folderType
            });

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Errors.Any(failure =>
                failure.ErrorMessage.Contains("quality profile") || failure.ErrorMessage.Contains("metadata profile")), Is.True);
        }

        [Test]
        public void post_should_reject_a_mixed_root_without_any_media_defaults()
        {
            var controller = BuildController(new StubRootFolderService(), new StubAuthorService(new List<Author>()));
            var result = controller.ValidateShared(new RootFolderResource
            {
                Name = "Books",
                Path = @"C:\books".AsOsAgnostic(),
                FolderType = (int)FolderType.Mixed
            });

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Errors.Any(failure => failure.PropertyName == "audiobookQualityProfileId"), Is.True);
        }

        [Test]
        public void post_should_reject_a_mixed_root_with_one_complete_media_side_and_no_monitoring_defaults()
        {
            var controller = BuildController(new StubRootFolderService(), new StubAuthorService(new List<Author>()));
            var result = controller.ValidateShared(new RootFolderResource
            {
                Name = "Books",
                Path = @"C:\books".AsOsAgnostic(),
                FolderType = (int)FolderType.Mixed,
                AudiobookQualityProfileId = 10,
                AudiobookMetadataProfileId = 20
            });

            Assert.That(result.Errors.Select(failure => failure.PropertyName), Is.EquivalentTo(new[] { "ebookQualityProfileId", "ebookMetadataProfileId" }));
        }

        [Test]
        public void post_should_reject_an_incomplete_second_side_on_a_mixed_root()
        {
            var controller = BuildController(new StubRootFolderService(), new StubAuthorService(new List<Author>()));
            var result = controller.ValidateShared(new RootFolderResource
            {
                Name = "Books",
                Path = @"C:\books".AsOsAgnostic(),
                FolderType = (int)FolderType.Mixed,
                AudiobookQualityProfileId = 10,
                AudiobookMetadataProfileId = 20,
                EbookMonitored = false
            });

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Errors.Any(failure => failure.PropertyName.StartsWith("ebook", StringComparison.OrdinalIgnoreCase)), Is.True);
        }

        [Test]
        public void request_should_validate_profiles_for_each_supported_format(
            [Values("POST", "PUT")] string verb, [Values] FolderType folderType,
            [Values(false, true)] bool structured, [Values(false, true)] bool complete)
        {
            var controller = BuildController(new StubRootFolderService(), new StubAuthorService(new List<Author>()));
            var resource = new RootFolderResource
            {
                Id = verb == "PUT" ? 42 : 0,
                Name = "Books",
                Path = @"C:\books".AsOsAgnostic(),
                FolderType = (int)folderType
            };
            foreach (var side in new[] { BookMediaType.Audiobook, BookMediaType.Ebook })
            {
                if (folderType == FolderType.Audiobook && side != BookMediaType.Audiobook ||
                    folderType == FolderType.Ebook && side != BookMediaType.Ebook)
                {
                    continue;
                }

                var settings = new MediaTypeSettingsResource { QualityProfileId = 10, MetadataProfileId = complete ? 20 : null };
                if (structured)
                {
                    if (side == BookMediaType.Audiobook) { resource.Audiobook = settings; }
                    else { resource.Ebook = settings; }
                }
                else if (side == BookMediaType.Audiobook)
                {
                    resource.AudiobookQualityProfileId = settings.QualityProfileId;
                    resource.AudiobookMetadataProfileId = settings.MetadataProfileId;
                }
                else
                {
                    resource.EbookQualityProfileId = settings.QualityProfileId;
                    resource.EbookMetadataProfileId = settings.MetadataProfileId;
                }
            }

            if (complete)
            {
                Assert.DoesNotThrow(() => controller.ValidateRequest(verb, resource));
            }
            else
            {
                var error = Assert.Throws<FluentValidation.ValidationException>(() => controller.ValidateRequest(verb, resource));
                var expected = folderType == FolderType.Mixed
                    ? new[] { "audiobookMetadataProfileId", "ebookMetadataProfileId" }
                    : new[] { folderType == FolderType.Audiobook ? "audiobookMetadataProfileId" : "ebookMetadataProfileId" };
                Assert.That(error.Errors.Select(failure => failure.PropertyName), Is.EquivalentTo(expected));
            }
        }

        [Test]
        public void request_should_reject_an_omitted_mixed_side([Values("POST", "PUT")] string verb)
        {
            var controller = BuildController(new StubRootFolderService(), new StubAuthorService(new List<Author>()));
            var resource = JsonSerializer.Deserialize<RootFolderResource>(
                "{\"id\":42,\"name\":\"Books\",\"folderType\":0,\"audiobookQualityProfileId\":10,\"audiobookMetadataProfileId\":20}",
                STJson.GetSerializerSettings());
            resource.Path = @"C:\books".AsOsAgnostic();

            var error = Assert.Throws<FluentValidation.ValidationException>(() => controller.ValidateRequest(verb, resource));

            Assert.That(error.Errors.Select(failure => failure.PropertyName), Is.EquivalentTo(new[] { "ebookQualityProfileId", "ebookMetadataProfileId" }));
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
