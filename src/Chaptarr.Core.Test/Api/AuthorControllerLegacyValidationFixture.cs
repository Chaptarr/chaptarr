using System;
using System.Collections.Generic;
using System.Reflection;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Messaging.Events;
using Chaptarr.Core.Test.Common;
using System.Threading.Tasks;
using Chaptarr.Api.V1.Author;
using Chaptarr.Http.Middleware;
using Chaptarr.Http.REST;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class AuthorControllerLegacyValidationFixture
    {
        private static readonly string EbookRootPath = @"C:\ebooks".AsOsAgnostic();

        [Test]
        public void legacy_add_to_mixed_root_uses_the_saved_profile_type(
            [Values(false, true)] bool postgres, [Values(ProfileType.Audiobook, ProfileType.Ebook)] ProfileType type,
            [Values(false, true)] bool contradictRoot)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                var profiles = new QualityProfileService(new QualityProfileRepository(db, InterfaceProxy.Proxy<IEventAggregator>((_, _) => null), InterfaceProxy.Proxy<NzbDrone.Core.CustomFormats.ICustomFormatService>((_, _) => new List<NzbDrone.Core.CustomFormats.CustomFormat>())),
                    null, null, null, null, null, LogManager.GetCurrentClassLogger());
                var profile = profiles.Add(new QualityProfile { Name = "Mobile app profile", ProfileType = type,
                    Items = new List<QualityProfileQualityItem> { new QualityProfileQualityItem { Allowed = true,
                        Quality = type == ProfileType.Audiobook ? NzbDrone.Core.Qualities.Quality.M4B : NzbDrone.Core.Qualities.Quality.EPUB } } });
                var rootType = contradictRoot ? (type == ProfileType.Audiobook ? FolderType.Ebook : FolderType.Audiobook) : FolderType.Mixed;
                var controller = BuildController(out var setup, rootType, profiles);
                var resource = new AuthorResource
                {
                    AuthorName = "Ted Chiang", ForeignAuthorId = "gr:130698", RootFolderPath = EbookRootPath,
                    QualityProfileId = profile.Id, MetadataProfileId = 2, Monitored = true, MonitorNewItems = "none"
                };
                var context = BuildExecutingContext(controller, resource, null);
                if (contradictRoot)
                {
                    Assert.That(Assert.Throws<BadRequestException>(() => controller.OnActionExecuting(context)).Message, Does.Contain("does not match"));
                    Assert.That(setup.LastConfig, Is.Null);
                    return;
                }
                controller.OnActionExecuting(context);
                Assert.That(controller.AddAuthor(resource, queueIfUnavailable: false).GetAwaiter().GetResult().Result, Is.TypeOf<AcceptedResult>());
                Assert.That(type == ProfileType.Audiobook ? setup.LastConfig.AudiobookQualityProfileId : setup.LastConfig.EbookQualityProfileId, Is.EqualTo(profile.Id));
                Assert.That(type == ProfileType.Audiobook ? setup.LastConfig.EbookQualityProfileId : setup.LastConfig.AudiobookQualityProfileId, Is.Null);
            });
        }

        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(false, false)]
        public async Task should_normalize_generic_ebook_fields_before_native_validation(bool useFacade, bool useQuery)
        {
            var controller = BuildController(out var authorLibraryProxy);
            var resource = new AuthorResource
            {
                AuthorName = "Ted Chiang",
                ForeignAuthorId = useFacade ? "161938" : "gr:130698",
                RootFolderPath = EbookRootPath,
                QualityProfileId = 1,
                MetadataProfileId = 2,
                Monitored = true,
                MonitorNewItems = "all",
                LastSelectedMediaType = "ebook"
            };

            var facadeContext = useFacade
                ? new ReadarrFacadeContext("hc", "ebook", "/readarr/hc/ebook")
                : null;
            var executingContext = BuildExecutingContext(controller, resource, facadeContext);
            if (useQuery) executingContext.HttpContext.Request.QueryString = new QueryString("?mediaType=ebook");

            Assert.DoesNotThrow(() => controller.OnActionExecuting(executingContext));
            var result = await controller.AddAuthor(resource, queueIfUnavailable: false);

            Assert.Multiple(() =>
            {
                Assert.That(result.Result, Is.TypeOf<AcceptedResult>());
                Assert.That(resource.EbookQualityProfileId, Is.EqualTo(1));
                Assert.That(resource.EbookMetadataProfileId, Is.EqualTo(2));
                Assert.That(resource.EbookRootFolderPath, Is.EqualTo(EbookRootPath));
                Assert.That(resource.EbookMonitored, Is.True);
                Assert.That(resource.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.All));
                Assert.That(resource.AudiobookQualityProfileId, Is.Null);
                Assert.That(resource.AudiobookMetadataProfileId, Is.Null);
                Assert.That(resource.AudiobookRootFolderPath, Is.Null);
                Assert.That(resource.AudiobookMonitored, Is.Null);
                Assert.That(resource.AudiobookMonitorNewItems, Is.Null);
                Assert.That(authorLibraryProxy.LastConfig.EbookQualityProfileId, Is.EqualTo(1));
                Assert.That(authorLibraryProxy.LastConfig.EbookMetadataProfileId, Is.EqualTo(2));
                Assert.That(authorLibraryProxy.LastConfig.EbookRootFolderPath, Is.EqualTo(EbookRootPath));
                Assert.That(authorLibraryProxy.LastConfig.EbookMonitored, Is.True);
                Assert.That(authorLibraryProxy.LastConfig.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.All));
                Assert.That(authorLibraryProxy.LastConfig.AudiobookRootFolderPath, Is.Null);
                Assert.That(authorLibraryProxy.LastConfig.LastSelectedMediaType, Is.EqualTo("ebook"));
            });
        }

        [Test]
        public void should_leave_a_malformed_generic_root_for_validation()
        {
            var controller = BuildController(out _);
            var resource = new AuthorResource
            {
                AuthorName = "Ted Chiang",
                ForeignAuthorId = "gr:130698",
                RootFolderPath = "not/an/absolute/path",
                EbookMonitored = true,
                QualityProfileId = 1,
                MetadataProfileId = 2,
                Monitored = true,
                MonitorNewItems = "all"
            };

            var exception = Assert.Throws<ValidationException>(() =>
                controller.OnActionExecuting(BuildExecutingContext(controller, resource, null)));

            Assert.That(exception.Errors, Has.Some.Matches<FluentValidation.Results.ValidationFailure>(failure =>
                failure.ErrorMessage == "Invalid Path: 'not/an/absolute/path'"));
        }

        [TestCase(null, null)]
        [TestCase("?mediaType=ebook", "audiobook")]
        [TestCase("?mediaType=invalid", null)]
        public void should_reject_ambiguous_or_conflicting_scope_before_author_setup(string query, string facadeMediaType)
        {
            var controller = BuildController(out var setup, FolderType.Mixed);
            var resource = new AuthorResource
            {
                AuthorName = "Ted Chiang", ForeignAuthorId = "gr:130698",
                RootFolderPath = EbookRootPath, MetadataProfileId = 2
            };
            var context = BuildExecutingContext(controller, resource, facadeMediaType == null ? null :
                new ReadarrFacadeContext("gr", facadeMediaType, "/readarr/gr/" + facadeMediaType));
            if (query != null) context.HttpContext.Request.QueryString = new QueryString(query);
            Assert.Throws<BadRequestException>(() => controller.OnActionExecuting(context));
            Assert.That(setup.LastConfig, Is.Null);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void direct_author_writes_should_reject_unscoped_shared_settings_before_saving(bool update)
        {
            var controller = BuildController(out var setup);
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            var resource = new AuthorResource { Id = 42, MetadataProfileId = 2 };
            BadRequestException error;
            if (update) error = Assert.Throws<BadRequestException>(() => controller.UpdateAuthor(resource));
            else error = Assert.ThrowsAsync<BadRequestException>(async () => await controller.AddAuthor(resource));
            Assert.That(error.Message, Does.Contain("?mediaType=ebook").And.Contain("ebookQualityProfileId"));
            Assert.That(setup.LastConfig, Is.Null);
        }

        [Test]
        public void put_infers_existing_side_from_route_id_when_body_id_is_omitted()
        {
            var controller = BuildController(out _);
            var resource = new AuthorResource { Path = System.IO.Path.Combine(EbookRootPath, "Ted Chiang"), QualityProfileId = 1, MetadataProfileId = 2 };
            var context = BuildExecutingContext(controller, resource, null);
            context.HttpContext.Request.Method = HttpMethods.Put;
            context.RouteData.Values["id"] = 43;
            controller.ControllerContext = new ControllerContext(context);
            controller.OnActionExecuting(context);
            Assert.That(resource.Id, Is.EqualTo(43));
            Assert.That(resource.EbookQualityProfileId, Is.EqualTo(1));
            Assert.That(resource.AudiobookQualityProfileId, Is.Null);
            Assert.That(resource.ToModel().EbookQualityProfileId, Is.EqualTo(1));
        }

        [Test]
        public void folder_validator_should_map_without_request_scope_and_still_validate_the_folder()
        {
            var names = DispatchProxy.Create<IBuildFileNames, FileNameBuilderProxy>();
            var validator = new InlineValidator<AuthorResource>();
            validator.RuleFor(x => x.RootFolderPath).SetValidator(new AuthorFolderAsRootFolderValidator(names));
            var resource = new AuthorResource
            {
                AuthorName = "Ted Chiang", QualityProfileId = 1, MetadataProfileId = 2,
                RootFolderPath = System.IO.Path.Combine(EbookRootPath, "Ted Chiang")
            };
            var result = validator.Validate(resource);
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Errors[0].ErrorMessage, Does.Contain("contains author folder"));
            resource.RootFolderPath = EbookRootPath;
            Assert.That(validator.Validate(resource).IsValid, Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task explicit_single_book_scope_should_not_become_a_whole_author_settings_request(bool useFacade)
        {
            var controller = BuildController(out var setup);
            var resource = new AuthorResource
            {
                ForeignAuthorId = useFacade ? "130698" : "gr:130698",
                RootFolderPath = EbookRootPath, QualityProfileId = 1, MetadataProfileId = 2,
                AddOptions = new AddAuthorOptions
                {
                    Monitor = MonitorTypes.SpecificBook,
                    BooksToMonitor = new List<string> { useFacade ? "12345" : "gr:12345" }
                }
            };
            var http = new DefaultHttpContext();
            if (useFacade) http.Items[ReadarrFacadeContext.ItemKey] = new ReadarrFacadeContext("gr", "ebook", "/readarr/gr/ebook");
            controller.ControllerContext = new ControllerContext { HttpContext = http };
            await controller.AddAuthor(resource, mediaType: useFacade ? null : "ebook");
            Assert.Multiple(() =>
            {
                Assert.That(setup.LastConfig.IsAuthorSettingsRequest, Is.False);
                Assert.That(setup.LastConfig.EbookBooksToMonitor, Is.EqualTo(new[] { "gr:12345" }));
                Assert.That(setup.LastConfig.AudiobookBooksToMonitor, Is.Null);
                Assert.That(setup.LastConfig.EbookMonitored, Is.True);
                Assert.That(setup.LastConfig.CreateAudiobook, Is.False);
            });
        }

        private static AuthorController BuildController(out AuthorLibraryServiceProxy authorLibraryProxy, FolderType rootType = FolderType.Ebook, IQualityProfileService profiles = null)
        {
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            var authorLibraryService = DispatchProxy.Create<IAuthorLibraryService, AuthorLibraryServiceProxy>();
            authorLibraryProxy = (AuthorLibraryServiceProxy)(object)authorLibraryService;
            var rootFolderService = DispatchProxy.Create<IRootFolderService, RootFolderServiceProxy>();
            ((RootFolderServiceProxy)(object)rootFolderService).RootFolders.Add(new RootFolder
            {
                Path = EbookRootPath,
                FolderType = rootType
            });
            var fileNameBuilder = DispatchProxy.Create<IBuildFileNames, FileNameBuilderProxy>();

            return new AuthorController(
                signalRBroadcaster: null,
                authorService: authorService,
                bookService: null,
                bookMonitoredService: null,
                authorLibraryService: authorLibraryService,
                authorStatisticsService: null,
                coverMapper: null,
                commandQueueManager: null,
                rootFolderService: rootFolderService,
                eventAggregator: null,
                appFolderInfo: null,
                fileNameBuilder: fileNameBuilder,
                logger: LogManager.GetLogger(nameof(AuthorControllerLegacyValidationFixture)),
                recycleBinValidator: new RecycleBinValidator(Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<NzbDrone.Core.Configuration.IConfigService>((_, _) => string.Empty)),
                rootFolderValidator: new RootFolderValidator(rootFolderService),
                mappedNetworkDriveValidator: new MappedNetworkDriveValidator(Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<NzbDrone.Common.EnvironmentInfo.IRuntimeInfo>((_, _) => false), null),
                authorPathValidator: new AuthorPathValidator(authorService),
                authorExistsValidator: new AuthorExistsValidator(authorService),
                authorAncestorValidator: new AuthorAncestorValidator(authorService),
                systemFolderValidator: new SystemFolderValidator(),
                qualityProfileExistsValidator: new QualityProfileExistsValidator(profiles ?? new TestQualityProfileService()),
                metadataProfileExistsValidator: new MetadataProfileExistsValidator(new TestMetadataProfileService()),
                authorFolderAsRootFolderValidator: new AuthorFolderAsRootFolderValidator(fileNameBuilder), qualityProfileService: profiles);
        }

        private static ActionExecutingContext BuildExecutingContext(
            AuthorController controller,
            AuthorResource resource,
            ReadarrFacadeContext facadeContext)
        {
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Method = HttpMethods.Post;
            if (facadeContext != null)
            {
                httpContext.Items[ReadarrFacadeContext.ItemKey] = facadeContext;
            }

            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
            var descriptor = new ControllerActionDescriptor
            {
                MethodInfo = typeof(AuthorController).GetMethod(nameof(AuthorController.AddAuthor))
            };
            var actionContext = new ActionContext(
                httpContext,
                new RouteData(),
                descriptor,
                new ModelStateDictionary());

            return new ActionExecutingContext(
                actionContext,
                new List<IFilterMetadata>(),
                new Dictionary<string, object> { ["authorResource"] = resource },
                controller);
        }

        private class AuthorServiceProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IAuthorService.AllAuthorPaths)) return new List<KeyValuePair<int, string>>();
                if (targetMethod?.Name == nameof(IAuthorService.GetAuthor)) return (int)args[0] == 43
                    ? new Author { Id = 43, EbookQualityProfileId = 1, EbookRootFolderPath = EbookRootPath }
                    : new Author { Id = (int)args[0] };
                if (targetMethod?.Name == nameof(IAuthorService.FindByProviderId))
                {
                    return null;
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }

        private class RootFolderServiceProxy : DispatchProxy
        {
            public List<RootFolder> RootFolders { get; } = new List<RootFolder>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IRootFolderService.GetBestRootFolder))
                    return RootFolders.Find(root => root.Path == (string)args[0]);
                if (targetMethod?.Name == nameof(IRootFolderService.All))
                {
                    return RootFolders;
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }

        private class AuthorLibraryServiceProxy : DispatchProxy
        {
            public MonitoringConfig LastConfig { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IAuthorLibraryService.AddAuthorAsync))
                {
                    LastConfig = (MonitoringConfig)args[1];
                    return Task.FromResult(new Author { Id = -77, Name = "Pending Import" });
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }

        private class FileNameBuilderProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IBuildFileNames.GetAuthorFolder))
                {
                    return "Ted Chiang";
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }
    }
}
