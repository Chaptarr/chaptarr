using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Chaptarr.Api.V1.ManualImport;
using Chaptarr.Http.REST;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport.Manual;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class ManualImportControllerScopedPreviewFixture
    {
        private class ManualImportServiceProxy : DispatchProxy
        {
            public IReadOnlyCollection<string> ExactPaths { get; private set; }
            public string Folder { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IManualImportService.GetMediaFiles))
                {
                    Folder = args?[0] as string;
                    ExactPaths = args?[6] as IReadOnlyCollection<string>;
                    return new List<ManualImportItem>();
                }

                throw new NotImplementedException($"IManualImportService.{targetMethod?.Name}");
            }
        }

        private class MediaFileServiceProxy : DispatchProxy
        {
            public List<BookFile> Files { get; set; } = new List<BookFile>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IMediaFileService.GetUnmappedFiles))
                {
                    return Files;
                }

                if (targetMethod?.Name == nameof(IMediaFileService.Get) && args?.Length == 1)
                {
                    return Files;
                }

                throw new NotImplementedException($"IMediaFileService.{targetMethod?.Name}");
            }
        }

        private class RootFolderServiceProxy : DispatchProxy
        {
            public string RootPath { get; set; } = @"C:\library".AsOsAgnostic();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IRootFolderService.GetBestRootFolderPath))
                {
                    return RootPath;
                }

                throw new NotImplementedException($"IRootFolderService.{targetMethod?.Name}");
            }
        }

        [Test]
        public void one_selected_unmapped_id_should_expand_to_the_exact_same_unit_scope()
        {
            var folder = @"C:\library\Author\Book".AsOsAgnostic();
            var manualImportService = DispatchProxy.Create<IManualImportService, ManualImportServiceProxy>();
            var manualImportProxy = (ManualImportServiceProxy)(object)manualImportService;
            var mediaFileService = DispatchProxy.Create<IMediaFileService, MediaFileServiceProxy>();
            ((MediaFileServiceProxy)(object)mediaFileService).Files = new List<BookFile>
            {
                new BookFile { Id = 11, EditionId = 0, Path = $"{folder}{Sep}Disc 1.mp3", MediaType = "audiobook" },
                new BookFile { Id = 12, EditionId = 0, Path = $"{folder}{Sep}Disc 2.mp3", MediaType = "audiobook" },
                new BookFile { Id = 99, EditionId = 0, Path = @"C:\library\Author\Other\Other.mp3".AsOsAgnostic(), MediaType = "audiobook" }
            };
            var rootFolderService = DispatchProxy.Create<IRootFolderService, RootFolderServiceProxy>();
            var controller = new ManualImportController(
                manualImportService,
                null,
                null,
                null,
                mediaFileService,
                rootFolderService,
                LogManager.GetCurrentClassLogger())
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext()
                }
            };

            controller.GetMediaFiles(
                folder,
                null,
                null,
                filterExistingFiles: true,
                replaceExistingFiles: false,
                mediaType: "audiobook",
                bookFileIds: new List<int> { 11 });

            Assert.That(manualImportProxy.ExactPaths, Is.EquivalentTo(new[]
            {
                $"{folder}{Sep}Disc 1.mp3",
                $"{folder}{Sep}Disc 2.mp3"
            }));
        }

        [Test]
        public void selected_disc_folder_id_should_expand_to_the_shared_cross_folder_unit()
        {
            var folder = @"C:\library\Author\Book".AsOsAgnostic();
            var manualImportService = DispatchProxy.Create<IManualImportService, ManualImportServiceProxy>();
            var manualImportProxy = (ManualImportServiceProxy)(object)manualImportService;
            var mediaFileService = DispatchProxy.Create<IMediaFileService, MediaFileServiceProxy>();
            ((MediaFileServiceProxy)(object)mediaFileService).Files = new List<BookFile>
            {
                CreateTaggedFile(11, $"{folder}{Sep}CD1{Sep}01.mp3"),
                CreateTaggedFile(12, $"{folder}{Sep}Disc 2{Sep}02.mp3"),
                new BookFile { Id = 99, EditionId = 0, Path = @"C:\library\Author\Other\Other.mp3".AsOsAgnostic(), MediaType = "audiobook" }
            };
            var rootFolderService = DispatchProxy.Create<IRootFolderService, RootFolderServiceProxy>();
            var controller = new ManualImportController(
                manualImportService,
                null,
                null,
                null,
                mediaFileService,
                rootFolderService,
                LogManager.GetCurrentClassLogger())
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext()
                }
            };

            controller.GetMediaFiles(
                $"{folder}{Sep}CD1",
                null,
                null,
                filterExistingFiles: true,
                replaceExistingFiles: false,
                mediaType: "audiobook",
                bookFileIds: new List<int> { 11 });

            Assert.That(manualImportProxy.Folder, Is.EqualTo(folder));
            Assert.That(manualImportProxy.ExactPaths, Is.EquivalentTo(new[]
            {
                $"{folder}{Sep}CD1{Sep}01.mp3",
                $"{folder}{Sep}Disc 2{Sep}02.mp3"
            }));
        }

        [Test]
        public void selected_ids_from_different_units_should_be_rejected()
        {
            var manualImportService = DispatchProxy.Create<IManualImportService, ManualImportServiceProxy>();
            var mediaFileService = DispatchProxy.Create<IMediaFileService, MediaFileServiceProxy>();
            ((MediaFileServiceProxy)(object)mediaFileService).Files = new List<BookFile>
            {
                new BookFile { Id = 11, EditionId = 0, Path = @"C:\library\Author\First\First.mp3".AsOsAgnostic(), MediaType = "audiobook" },
                new BookFile { Id = 12, EditionId = 0, Path = @"C:\library\Author\Second\Second.mp3".AsOsAgnostic(), MediaType = "audiobook" }
            };
            var rootFolderService = DispatchProxy.Create<IRootFolderService, RootFolderServiceProxy>();
            var controller = new ManualImportController(
                manualImportService,
                null,
                null,
                null,
                mediaFileService,
                rootFolderService,
                LogManager.GetCurrentClassLogger())
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext()
                }
            };

            var exception = Assert.Throws<BadRequestException>(() => controller.GetMediaFiles(
                @"C:\library\Author\First".AsOsAgnostic(),
                null,
                null,
                filterExistingFiles: true,
                replaceExistingFiles: false,
                mediaType: "audiobook",
                bookFileIds: new List<int> { 11, 12 }));

            Assert.That(exception.Message, Does.Contain("one import unit"));
        }

        private static char Sep => System.IO.Path.DirectorySeparatorChar;

        private static BookFile CreateTaggedFile(int id, string path)
        {
            return new BookFile
            {
                Id = id,
                EditionId = 0,
                Path = path,
                MediaType = "audiobook",
                AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ARTIST"] = new List<string> { "Author" },
                    ["ALBUM"] = new List<string> { "Book" }
                }
            };
        }
    }
}
