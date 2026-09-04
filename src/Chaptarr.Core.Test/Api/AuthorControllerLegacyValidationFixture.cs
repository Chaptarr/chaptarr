using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Chaptarr.Api.V1.Author;
using Chaptarr.Http.Middleware;
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
        [TestCase(false)]
        [TestCase(true)]
        public async Task should_normalize_generic_ebook_fields_before_native_validation(bool useFacade)
        {
            var controller = BuildController(out var authorLibraryProxy);
            var resource = new AuthorResource
            {
                AuthorName = "Ted Chiang",
                ForeignAuthorId = useFacade ? "161938" : "gr:130698",
                RootFolderPath = "/ebooks",
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

            Assert.DoesNotThrow(() => controller.OnActionExecuting(executingContext));
            var result = await controller.AddAuthor(resource, queueIfUnavailable: false);

            Assert.Multiple(() =>
            {
                Assert.That(result.Result, Is.TypeOf<AcceptedResult>());
                Assert.That(resource.EbookQualityProfileId, Is.EqualTo(1));
                Assert.That(resource.EbookMetadataProfileId, Is.EqualTo(2));
                Assert.That(resource.EbookRootFolderPath, Is.EqualTo("/ebooks"));
                Assert.That(resource.EbookMonitored, Is.True);
                Assert.That(resource.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.All));
                Assert.That(resource.AudiobookQualityProfileId, Is.Null);
                Assert.That(resource.AudiobookMetadataProfileId, Is.Null);
                Assert.That(resource.AudiobookRootFolderPath, Is.Null);
                Assert.That(resource.AudiobookMonitored, Is.Null);
                Assert.That(resource.AudiobookMonitorNewItems, Is.Null);
                Assert.That(authorLibraryProxy.LastConfig.EbookQualityProfileId, Is.EqualTo(1));
                Assert.That(authorLibraryProxy.LastConfig.EbookMetadataProfileId, Is.EqualTo(2));
                Assert.That(authorLibraryProxy.LastConfig.EbookRootFolderPath, Is.EqualTo("/ebooks"));
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

        private static AuthorController BuildController(out AuthorLibraryServiceProxy authorLibraryProxy)
        {
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            var authorLibraryService = DispatchProxy.Create<IAuthorLibraryService, AuthorLibraryServiceProxy>();
            authorLibraryProxy = (AuthorLibraryServiceProxy)(object)authorLibraryService;
            var rootFolderService = DispatchProxy.Create<IRootFolderService, RootFolderServiceProxy>();
            ((RootFolderServiceProxy)(object)rootFolderService).RootFolders.Add(new RootFolder
            {
                Path = "/ebooks",
                FolderType = FolderType.Ebook
            });
            var fileNameBuilder = DispatchProxy.Create<IBuildFileNames, FileNameBuilderProxy>();

            return new AuthorController(
                signalRBroadcaster: null,
                authorService: authorService,
                bookService: null,
                bookMonitoredService: null,
                seriesService: null,
                authorLibraryService: authorLibraryService,
                authorStatisticsService: null,
                coverMapper: null,
                commandQueueManager: null,
                rootFolderService: rootFolderService,
                eventAggregator: null,
                appFolderInfo: null,
                fileNameBuilder: fileNameBuilder,
                logger: LogManager.GetLogger(nameof(AuthorControllerLegacyValidationFixture)),
                recycleBinValidator: new RecycleBinValidator(null),
                rootFolderValidator: new RootFolderValidator(rootFolderService),
                mappedNetworkDriveValidator: new MappedNetworkDriveValidator(null, null),
                authorPathValidator: new AuthorPathValidator(authorService),
                authorExistsValidator: new AuthorExistsValidator(authorService),
                authorAncestorValidator: new AuthorAncestorValidator(authorService),
                systemFolderValidator: new SystemFolderValidator(),
                qualityProfileExistsValidator: new QualityProfileExistsValidator(new TestQualityProfileService()),
                metadataProfileExistsValidator: new MetadataProfileExistsValidator(new TestMetadataProfileService()),
                authorFolderAsRootFolderValidator: new AuthorFolderAsRootFolderValidator(fileNameBuilder));
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
