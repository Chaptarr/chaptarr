using System;
using System.Collections.Generic;
using Chaptarr.Api.V1.BookFiles;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.Swagger;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport.Manual;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class BookFileDeletionPreviewFixture
    {
        [Test]
        public void deletion_preview_is_exposed_in_the_generated_api_contract()
        {
            using var services = OpenApiNullableReferenceFixture.CreateServices();
            var api = services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
            Assert.That(api.Paths.ContainsKey("/api/v1/bookfile/deletepreview"), Is.True);
            Assert.That(api.Paths["/api/v1/bookfile/deletepreview"].Operations.ContainsKey(OperationType.Post), Is.True);
            Assert.That(api.Components.Schemas[nameof(BookFileDeletePreviewResource)].Properties.Keys,
                Does.Contain("files").And.Contain("hasCalibreFiles"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void preview_reuses_the_batch_inventory_without_deleting_or_reading_each_root_again(bool calibre)
        {
            var files = new List<BookFile> { new BookFile { Id = 1, Path = "/library/Dune.m4b" }, new BookFile { Id = 2, Path = "/library/Dune.epub" } };
            var expected = new List<ManualImportReplacementFile> { new ManualImportReplacementFile { Id = 1, Path = files[0].Path } };
            var reads = 0;
            var describes = 0;
            var rootReads = 0;
            var media = InterfaceProxy.Proxy<IMediaFileService>((method, args) =>
            {
                Assert.That(method.Name, Is.EqualTo("Get"));
                Assert.That(args[0], Is.EquivalentTo(new[] { 1, 2 }));
                reads++;
                return files;
            });
            var preview = InterfaceProxy.Proxy<IManualImportReplacementFiles>((method, args) =>
            {
                Assert.That(method.Name, Is.EqualTo("DescribeForDeletion"));
                Assert.That(args[0], Is.SameAs(files));
                describes++;
                return expected;
            });
            var rootList = new List<RootFolder> { new RootFolder { Path = "/library", IsCalibreLibrary = calibre } };
            var roots = InterfaceProxy.Proxy<IRootFolderService>((method, args) =>
            {
                if (method.Name == "All") { rootReads++; return rootList; }
                Assert.That(method.Name, Is.EqualTo("GetBestRootFolder"));
                Assert.That(args, Has.Length.EqualTo(2));
                Assert.That(args[1], Is.SameAs(rootList));
                return rootList[0];
            });
            var controller = new BookFileController(null, media, null, null, null, null, null, roots, LogManager.GetCurrentClassLogger(), preview);
            var result = controller.PreviewDeleteBookFiles(new BookFileDeletePreviewRequest { BookFileIds = new List<int> { 1, 2, 1 } });
            Assert.That(result.Files, Is.SameAs(expected));
            Assert.That(result.HasCalibreFiles, Is.EqualTo(calibre));
            Assert.That((reads, describes, rootReads), Is.EqualTo((1, 1, 1)));
        }

        [TestCase(null)]
        [TestCase(new int[0])]
        [TestCase(new[] { 0 })]
        public void empty_or_invalid_selection_is_rejected_without_deletion(int[] ids)
        {
            var controller = new BookFileController(null, null, null, null, null, null, null, null, LogManager.GetCurrentClassLogger(), null);
            Assert.Throws<BadRequestException>(() => controller.PreviewDeleteBookFiles(ids == null ? null : new BookFileDeletePreviewRequest { BookFileIds = new List<int>(ids) }));
        }

        [Test]
        public void book_selection_loads_all_current_files_and_allows_a_book_without_files()
        {
            var calls = 0;
            var media = InterfaceProxy.Proxy<IMediaFileService>((method, args) =>
            {
                Assert.That(method.Name, Is.EqualTo("GetFilesByBooks"));
                Assert.That(args[0], Is.EquivalentTo(new[] { 7, 8 }));
                calls++;
                return new List<BookFile>();
            });
            var inventory = InterfaceProxy.Proxy<IManualImportReplacementFiles>((method, args) =>
            {
                Assert.That(method.Name, Is.EqualTo("DescribeForDeletion"));
                return new List<ManualImportReplacementFile>();
            });
            var roots = InterfaceProxy.Proxy<IRootFolderService>((method, args) => new List<RootFolder>());
            var controller = new BookFileController(null, media, null, null, null, null, null, roots, LogManager.GetCurrentClassLogger(), inventory);
            Assert.That(controller.PreviewDeleteBookFiles(new BookFileDeletePreviewRequest { BookIds = new List<int> { 7, 8, 7 } }).Files, Is.Empty);
            Assert.That(calls, Is.EqualTo(1));
            Assert.Throws<BadRequestException>(() => controller.PreviewDeleteBookFiles(new BookFileDeletePreviewRequest { BookIds = new List<int> { 7 }, BookFileIds = new List<int> { 10 } }));
            Assert.That(calls, Is.EqualTo(1));
        }

        [Test]
        public void missing_row_requires_reloading_before_confirmation()
        {
            var media = InterfaceProxy.Proxy<IMediaFileService>((_, _) => new List<BookFile>());
            var controller = new BookFileController(null, media, null, null, null, null, null, null, LogManager.GetCurrentClassLogger(), null);
            var error = Assert.Throws<NzbDroneClientException>(() => controller.PreviewDeleteBookFiles(new BookFileDeletePreviewRequest { BookFileIds = new List<int> { 1 } }));
            Assert.That(error.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.NotFound));
        }
    }
}
