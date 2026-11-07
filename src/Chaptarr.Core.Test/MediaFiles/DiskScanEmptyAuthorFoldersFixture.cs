using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using NzbDrone.Common;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class DiskScanEmptyAuthorFoldersFixture
    {
        private class ConfigServiceProxy : DispatchProxy
        {
            public bool CreateEmptyAuthorFolders { get; set; }
            public bool CreateEmptyEbookAuthorFolders { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    "get_CreateEmptyAuthorFolders" => CreateEmptyAuthorFolders,
                    "get_CreateEmptyEbookAuthorFolders" => CreateEmptyEbookAuthorFolders,
                    _ => GetDefaultValue(targetMethod?.ReturnType)
                };
            }
        }

        private class DiskProviderProxy : DispatchProxy
        {
            public HashSet<string> ExistingFolders { get; } = new HashSet<string>(PathEqualityComparer.Instance);
            public List<string> EnsuredFolders { get; } = new List<string>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case "FolderExists":
                        return ExistingFolders.Contains(args?[0] as string);
                    case "EnsureFolder":
                    {
                        var path = args?[0] as string;
                        EnsuredFolders.Add(path);
                        ExistingFolders.Add(path);
                        return null;
                    }
                    default:
                        return GetDefaultValue(targetMethod?.ReturnType);
                }
            }
        }

        private class MediaFileAttributeServiceProxy : DispatchProxy
        {
            public List<string> PermissionPaths { get; } = new List<string>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IMediaFileAttributeService.SetFolderPermissions))
                {
                    PermissionPaths.Add(args?[0] as string);
                }

                return GetDefaultValue(targetMethod?.ReturnType);
            }
        }

        private class AuthorServiceProxy : DispatchProxy
        {
            public List<Author> Authors { get; set; } = new List<Author>();
            public int GetAuthorsCalls { get; private set; }
            public int GetAllAuthorsCalls { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IAuthorService.GetAllAuthors))
                {
                    GetAllAuthorsCalls++;
                    return Authors;
                }

                if (targetMethod?.Name == nameof(IAuthorService.GetAuthors))
                {
                    GetAuthorsCalls++;
                    var ids = (args?[0] as IEnumerable<int>)?.ToHashSet() ?? new HashSet<int>();
                    return Authors.Where(author => ids.Contains(author.Id)).ToList();
                }

                return GetDefaultValue(targetMethod?.ReturnType);
            }
        }

        private class RootFolderServiceProxy : DispatchProxy
        {
            public RootFolder RootFolder { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    nameof(IRootFolderService.GetBestRootFolder) => RootFolder,
                    nameof(IRootFolderService.All) => new List<RootFolder> { RootFolder },
                    _ => GetDefaultValue(targetMethod?.ReturnType)
                };
            }
        }

        private class ImportOrchestratorProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name == nameof(IImportOrchestrator.ProcessFilesAsync)
                    ? Task.FromResult(new OrchestratorImportResult())
                    : GetDefaultValue(targetMethod?.ReturnType);
            }
        }

        private class NoOpProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return GetDefaultValue(targetMethod?.ReturnType);
            }
        }

        [Test]
        public void should_create_missing_legacy_author_folder_and_apply_permissions_when_enabled()
        {
            var author = new Author { Id = 1, Path = "/books/Author" };
            var service = CreateService(
                createEmptyAuthorFolders: true,
                createEmptyEbookAuthorFolders: false,
                authors: new[] { author },
                scanFolder: "/books",
                out var diskProvider,
                out var mediaFileAttributeService,
                out _);

            service.Scan(new List<string> { "/books" });

            Assert.That(diskProvider.EnsuredFolders, Is.EqualTo(new[] { "/books/Author" }));
            Assert.That(mediaFileAttributeService.PermissionPaths, Is.EqualTo(new[] { "/books/Author" }));
        }

        [Test]
        public void should_not_create_author_folders_when_both_settings_are_disabled()
        {
            var service = CreateService(
                createEmptyAuthorFolders: false,
                createEmptyEbookAuthorFolders: false,
                authors: new[] { new Author { Id = 1, Path = "/books/Author", EbookPath = "/books/ebooks/Author" } },
                scanFolder: "/books",
                out var diskProvider,
                out _,
                out _);

            service.Scan(new List<string> { "/books" });

            Assert.That(diskProvider.EnsuredFolders, Is.Empty);
        }

        [Test]
        public void should_create_only_author_folders_within_both_root_and_scanned_folder()
        {
            var authors = new[]
            {
                new Author { Id = 1, Path = "/books/selected/Included" },
                new Author { Id = 2, Path = "/books/other/OutsideScannedFolder" },
                new Author { Id = 3, Path = "/bookshelf/EscapesRootPrefix" },
                new Author { Id = 4, Path = "/outside/EscapesRoot" }
            };
            var service = CreateService(
                createEmptyAuthorFolders: true,
                createEmptyEbookAuthorFolders: false,
                authors: authors,
                scanFolder: "/books/selected",
                out var diskProvider,
                out _,
                out _);

            service.Scan(new List<string> { "/books/selected" });

            Assert.That(diskProvider.EnsuredFolders, Is.EqualTo(new[] { "/books/selected/Included" }));
        }

        [Test]
        public void should_use_selected_authors_for_scoped_scan()
        {
            var authors = new[]
            {
                new Author { Id = 1, Path = "/books/Selected" },
                new Author { Id = 2, Path = "/books/Unselected" }
            };
            var service = CreateService(
                createEmptyAuthorFolders: true,
                createEmptyEbookAuthorFolders: false,
                authors: authors,
                scanFolder: "/books",
                out var diskProvider,
                out _,
                out var authorService);

            service.Scan(new List<string> { "/books" }, authorIds: new List<int> { 1 });

            Assert.That(diskProvider.EnsuredFolders, Is.EqualTo(new[] { "/books/Selected" }));
            Assert.That(authorService.GetAuthorsCalls, Is.EqualTo(2));
            Assert.That(authorService.GetAllAuthorsCalls, Is.EqualTo(0));
        }

        [Test]
        public void should_create_ebook_path_only_when_ebook_folder_setting_is_enabled()
        {
            var service = CreateService(
                createEmptyAuthorFolders: false,
                createEmptyEbookAuthorFolders: true,
                authors: new[] { new Author { Id = 1, Path = "/books/Author", EbookPath = "/books/ebooks/Author" } },
                scanFolder: "/books",
                out var diskProvider,
                out _,
                out _);

            service.Scan(new List<string> { "/books" });

            Assert.That(diskProvider.EnsuredFolders, Is.EqualTo(new[] { "/books/ebooks/Author" }));
        }

        private static DiskScanService CreateService(
            bool createEmptyAuthorFolders,
            bool createEmptyEbookAuthorFolders,
            IEnumerable<Author> authors,
            string scanFolder,
            out DiskProviderProxy diskProvider,
            out MediaFileAttributeServiceProxy mediaFileAttributeService,
            out AuthorServiceProxy authorService)
        {
            var config = DispatchProxy.Create<IConfigService, ConfigServiceProxy>();
            var configProxy = (ConfigServiceProxy)(object)config;
            configProxy.CreateEmptyAuthorFolders = createEmptyAuthorFolders;
            configProxy.CreateEmptyEbookAuthorFolders = createEmptyEbookAuthorFolders;

            var disk = DispatchProxy.Create<NzbDrone.Common.Disk.IDiskProvider, DiskProviderProxy>();
            diskProvider = (DiskProviderProxy)(object)disk;
            diskProvider.ExistingFolders.Add("/books");
            diskProvider.ExistingFolders.Add(scanFolder);

            var attributes = DispatchProxy.Create<IMediaFileAttributeService, MediaFileAttributeServiceProxy>();
            mediaFileAttributeService = (MediaFileAttributeServiceProxy)(object)attributes;

            var roots = DispatchProxy.Create<IRootFolderService, RootFolderServiceProxy>();
            var rootFolderProxy = (RootFolderServiceProxy)(object)roots;
            rootFolderProxy.RootFolder = new RootFolder { Id = 1, Path = "/books", FolderType = FolderType.Audiobook };

            var authorsProxy = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            authorService = (AuthorServiceProxy)(object)authorsProxy;
            authorService.Authors = authors.ToList();

            var orchestrator = DispatchProxy.Create<IImportOrchestrator, ImportOrchestratorProxy>();

            var cleanup = DispatchProxy.Create<IMediaFileTableCleanupService, NoOpProxy>();
            var commandQueue = DispatchProxy.Create<IManageCommandQueue, NoOpProxy>();
            var eventAggregator = DispatchProxy.Create<IEventAggregator, NoOpProxy>();

            return new DiskScanService(
                configService: config,
                diskProvider: disk,
                mediaFileAttributeService: attributes,
                calibre: null,
                mediaFileService: null,
                metadataTagService: null,
                ingestQueueRepository: null,
                importOrchestrator: orchestrator,
                authorService: authorsProxy,
                rootFolderService: roots,
                mediaFileTableCleanupService: cleanup,
                commandQueueManager: commandQueue,
                eventAggregator: eventAggregator,
                logger: LogManager.GetCurrentClassLogger());
        }

        private static object GetDefaultValue(Type type)
        {
            if (type == null || type == typeof(void))
            {
                return null;
            }

            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }
}
