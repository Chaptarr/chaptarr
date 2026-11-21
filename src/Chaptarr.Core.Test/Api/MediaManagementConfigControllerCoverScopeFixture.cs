using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Chaptarr.Api.V1.Config;
using Chaptarr.Http.ErrorManagement;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Housekeeping.Housekeepers;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class MediaManagementConfigControllerCoverScopeFixture
    {
        private class ConfigServiceProxy : DispatchProxy
        {
            public CoverDownloadScope Scope { get; set; }
            public List<string> Events { get; set; }
            public int SaveCalls { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IConfigService.SaveConfigDictionary))
                {
                    var values = (Dictionary<string, object>)args[0];
                    Events.Add("persist:start");
                    Scope = (CoverDownloadScope)values[nameof(MediaManagementConfigResource.CoverDownloadScope)];
                    SaveCalls++;
                    Events.Add("persist:complete");
                    return null;
                }

                return targetMethod?.Name switch
                {
                    "get_CoverDownloadScope" => Scope,
                    "get_AutoUnmonitorPreviouslyDownloadedBooks" => false,
                    "get_RecycleBin" => null,
                    "get_RecycleBinCleanupDays" => 0,
                    "get_DownloadPropersAndRepacks" => default(ProperDownloadTypes),
                    "get_CreateEmptyAuthorFolders" => false,
                    "get_CreateEmptyEbookAuthorFolders" => false,
                    "get_DeleteEmptyFolders" => false,
                    "get_FileDate" => default(FileDateType),
                    "get_WatchLibraryForChanges" => false,
                    "get_GranularFileSystemScanning" => false,
                    "get_RescanAfterRefresh" => default(RescanAfterRefreshType),
                    "get_AllowFingerprinting" => default(AllowFingerprinting),
                    "get_BookMatchingStrictness" => default(BookMatchingStrictness),
                    "get_UsePathAsTagsFallback" => false,
                    "get_AutoAddMissingAuthorsFromCompletedDownloads" => false,
                    "get_DefaultAudiobookRootFolderPath" => null,
                    "get_DefaultEbookRootFolderPath" => null,
                    "get_SetPermissionsLinux" => false,
                    "get_ChmodFolder" => null,
                    "get_ChownGroup" => null,
                    "get_SkipFreeSpaceCheckWhenImporting" => false,
                    "get_MinimumFreeSpaceWhenImporting" => 100,
                    "get_CopyUsingHardlinks" => false,
                    "get_ImportExtraFiles" => false,
                    "get_ExtraFileExtensions" => null,
                    _ => throw new System.NotImplementedException(targetMethod?.Name)
                };
            }
        }

        private class DefaultDependencyProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.ReturnType == typeof(bool))
                {
                    return false;
                }

                if (targetMethod?.ReturnType == typeof(int))
                {
                    return 0;
                }

                return null;
            }
        }

        private sealed class TestableMediaManagementConfigController : MediaManagementConfigController
        {
            public TestableMediaManagementConfigController(
                IConfigService configService,
                PathExistsValidator pathExistsValidator,
                FolderChmodValidator folderChmodValidator,
                FolderWritableValidator folderWritableValidator,
                AuthorPathValidator authorPathValidator,
                StartupFolderValidator startupFolderValidator,
                SystemFolderValidator systemFolderValidator,
                RootFolderAncestorValidator rootFolderAncestorValidator,
                RootFolderValidator rootFolderValidator,
                CleanupOrphanedBookCovers coverCleanup)
                : base(
                    configService,
                    pathExistsValidator,
                    folderChmodValidator,
                    folderWritableValidator,
                    authorPathValidator,
                    startupFolderValidator,
                    systemFolderValidator,
                    rootFolderAncestorValidator,
                    rootFolderValidator,
                    coverCleanup)
            {
            }

            public bool ValidateResource(MediaManagementConfigResource resource)
            {
                return SharedValidator.Validate(resource).IsValid;
            }
        }

        private class EstimateBookServiceProxy : DispatchProxy
        {
            public List<int> LibraryBookIds { get; set; } = new();
            public int LibraryBookIdCalls { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IBookService.GetLibraryBookIds))
                {
                    LibraryBookIdCalls++;
                    return LibraryBookIds;
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }

        private class EstimateDiskProviderProxy : DispatchProxy
        {
            public bool RootExists { get; set; } = true;
            public List<string> Directories { get; } = new();
            public List<string> SizedDirectories { get; } = new();
            public List<string> DeletedDirectories { get; } = new();
            public Func<string, long> FolderSize { get; set; } = _ => 0;

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    nameof(IDiskProvider.FolderExists) => RootExists,
                    nameof(IDiskProvider.GetDirectories) => Directories,
                    nameof(IDiskProvider.GetFolderSize) => GetFolderSize((string)args[0]),
                    nameof(IDiskProvider.DeleteFolder) => DeleteFolder((string)args[0]),
                    _ => throw new NotImplementedException(targetMethod?.Name)
                };
            }

            private long GetFolderSize(string path)
            {
                SizedDirectories.Add(path);
                return FolderSize(path);
            }

            private object DeleteFolder(string path)
            {
                DeletedDirectories.Add(path);
                return null;
            }
        }

        private class EstimateAppFolderProxy : DispatchProxy
        {
            public string AppDataFolder { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name == "get_AppDataFolder"
                    ? AppDataFolder
                    : throw new NotImplementedException(targetMethod?.Name);
            }
        }

        private static TestableMediaManagementConfigController CreateController(
            IConfigService configService,
            CleanupOrphanedBookCovers coverCleanup)
        {
            var disk = DispatchProxy.Create<IDiskProvider, DefaultDependencyProxy>();
            var authors = DispatchProxy.Create<IAuthorService, DefaultDependencyProxy>();
            var appFolder = DispatchProxy.Create<IAppFolderInfo, DefaultDependencyProxy>();
            var rootFolders = DispatchProxy.Create<IRootFolderService, DefaultDependencyProxy>();

            return new TestableMediaManagementConfigController(
                configService,
                new PathExistsValidator(disk),
                new FolderChmodValidator(disk),
                new FolderWritableValidator(disk),
                new AuthorPathValidator(authors),
                new StartupFolderValidator(appFolder),
                new SystemFolderValidator(),
                new RootFolderAncestorValidator(rootFolders),
                new RootFolderValidator(rootFolders),
                coverCleanup);
        }

        [Test]
        public void cover_cleanup_endpoint_should_return_the_read_only_housekeeper_estimate()
        {
            var configApi = DispatchProxy.Create<IConfigService, ConfigServiceProxy>();
            ((ConfigServiceProxy)(object)configApi).Scope = CoverDownloadScope.AllBooks;
            var appFolder = DispatchProxy.Create<IAppFolderInfo, EstimateAppFolderProxy>();
            var appFolderProxy = (EstimateAppFolderProxy)(object)appFolder;
            appFolderProxy.AppDataFolder = @"C:\config".AsOsAgnostic();
            var coverRoot = Path.Combine(appFolderProxy.AppDataFolder, "MediaCover", "Books");
            var diskProvider = DispatchProxy.Create<IDiskProvider, EstimateDiskProviderProxy>();
            var disk = (EstimateDiskProviderProxy)(object)diskProvider;
            disk.Directories.AddRange(new[] { "101", "102", "non-numeric" }.Select(id => Path.Combine(coverRoot, id)));
            disk.FolderSize = path => Path.GetFileName(path) == "102" ? 5120L : 0L;
            var bookService = DispatchProxy.Create<IBookService, EstimateBookServiceProxy>();
            var books = (EstimateBookServiceProxy)(object)bookService;
            books.LibraryBookIds = new List<int> { 101 };
            var coverService = DispatchProxy.Create<IMapCoversToLocal, DefaultDependencyProxy>();
            var cleanup = new CleanupOrphanedBookCovers(
                bookService,
                diskProvider,
                appFolder,
                LogManager.GetCurrentClassLogger(),
                configApi,
                coverService);
            var controller = CreateController(configApi, cleanup);

            var response = controller.GetCoverCleanupEstimate(CancellationToken.None);

            Assert.That(response.Result, Is.TypeOf<OkObjectResult>());
            var resource = (BookCoverCleanupResource)((OkObjectResult)response.Result).Value;
            Assert.That(resource.Bytes, Is.EqualTo(5120L));
            Assert.That(resource.BookCount, Is.EqualTo(1));
            Assert.That(books.LibraryBookIdCalls, Is.EqualTo(1));
            Assert.That(disk.SizedDirectories, Is.EqualTo(new[] { Path.Combine(coverRoot, "102") }));
            Assert.That(disk.DeletedDirectories, Is.Empty);
        }

        [Test]
        public async Task cover_cleanup_timeout_should_return_a_client_error_without_error_or_fatal_logs()
        {
            var configApi = DispatchProxy.Create<IConfigService, ConfigServiceProxy>();
            var config = (ConfigServiceProxy)(object)configApi;
            var appFolder = DispatchProxy.Create<IAppFolderInfo, EstimateAppFolderProxy>();
            ((EstimateAppFolderProxy)(object)appFolder).AppDataFolder = @"C:\config".AsOsAgnostic();
            var diskProvider = DispatchProxy.Create<IDiskProvider, EstimateDiskProviderProxy>();
            var disk = (EstimateDiskProviderProxy)(object)diskProvider;
            disk.Directories.Add(Path.Combine(appFolder.AppDataFolder, "MediaCover", "Books", "102"));
            disk.FolderSize = _ => throw new TimeoutException("Book-cover savings could not be estimated in time");
            var books = DispatchProxy.Create<IBookService, EstimateBookServiceProxy>();
            var coverService = DispatchProxy.Create<IMapCoversToLocal, DefaultDependencyProxy>();
            var cleanup = new CleanupOrphanedBookCovers(books, diskProvider, appFolder,
                LogManager.GetCurrentClassLogger(), configApi, coverService);
            var controller = CreateController(configApi, cleanup);

            var exception = Assert.Throws<NzbDroneClientException>(() => controller.GetCoverCleanupEstimate(CancellationToken.None));
            using var factory = new LogFactory();
            var target = new MemoryTarget { Layout = "${level}|${message}" };
            var logging = new LoggingConfiguration();
            logging.AddRule(LogLevel.Debug, LogLevel.Fatal, target);
            factory.Configuration = logging;
            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();
            context.Features.Set<IExceptionHandlerPathFeature>(new ExceptionHandlerFeature { Error = exception });

            await new ChaptarrErrorPipeline(factory.GetLogger(nameof(ChaptarrErrorPipeline))).HandleException(context);

            Assert.That(context.Response.StatusCode, Is.EqualTo((int)HttpStatusCode.RequestTimeout));
            Assert.That(target.Logs, Has.None.StartWith("Error|").And.None.StartWith("Fatal|"));
            Assert.That(disk.DeletedDirectories, Is.Empty);
            Assert.That(config.SaveCalls, Is.Zero);
        }

        [TestCase(CoverDownloadScope.AllBooks, CoverDownloadScope.LibraryBooks)]
        [TestCase(CoverDownloadScope.LibraryBooks, CoverDownloadScope.LibraryBooks)]
        [TestCase(CoverDownloadScope.LibraryBooks, CoverDownloadScope.AllBooks)]
        public void save_should_persist_the_requested_cover_scope(
            CoverDownloadScope initialScope,
            CoverDownloadScope requestedScope)
        {
            var events = new List<string>();
            var configApi = DispatchProxy.Create<IConfigService, ConfigServiceProxy>();
            var config = (ConfigServiceProxy)(object)configApi;
            config.Scope = initialScope;
            config.Events = events;
            var controller = CreateController(configApi, null);
            var resource = new MediaManagementConfigResource
            {
                Id = 1,
                CoverDownloadScope = requestedScope,
                MinimumFreeSpaceWhenImporting = 100
            };

            Assert.That(controller.ValidateResource(resource), Is.True);
            var response = controller.SaveConfig(resource);

            Assert.That(response.Result, Is.Not.Null);
            Assert.That(config.SaveCalls, Is.EqualTo(1));
            Assert.That(config.Scope, Is.EqualTo(requestedScope));
            Assert.That(events, Is.EqualTo(new[] { "persist:start", "persist:complete" }));
        }
    }
}
