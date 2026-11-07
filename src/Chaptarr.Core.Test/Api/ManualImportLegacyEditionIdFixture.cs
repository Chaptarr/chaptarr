using System;
using System.Collections.Generic;
using System.Reflection;
using Chaptarr.Api.V1.ManualImport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.MediaFiles.BookImport.Manual;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class ManualImportLegacyEditionIdFixture
    {
        private class ManualImportServiceProxy : DispatchProxy
        {
            public List<ManualImportItem> UpdatedItems { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IManualImportService.UpdateItems))
                {
                    UpdatedItems = (List<ManualImportItem>)args[0];
                    return UpdatedItems;
                }

                throw new NotImplementedException($"IManualImportService.{targetMethod?.Name}");
            }
        }

        private class EditionServiceProxy : DispatchProxy
        {
            public string RequestedForeignEditionId { get; private set; }
            public Edition Edition { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IEditionService.GetEditionByForeignEditionId))
                {
                    RequestedForeignEditionId = (string)args[0];
                    return Edition;
                }

                throw new NotImplementedException($"IEditionService.{targetMethod?.Name}");
            }
        }

        [Test]
        public void update_should_resolve_bookshelf_foreign_edition_id_when_local_edition_id_is_absent()
        {
            var manualImportService = DispatchProxy.Create<IManualImportService, ManualImportServiceProxy>();
            var manualImportProxy = (ManualImportServiceProxy)(object)manualImportService;
            var editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
            var editionServiceProxy = (EditionServiceProxy)(object)editionService;
            var expectedEdition = new Edition
            {
                Id = 23,
                BookId = 10,
                ForeignEditionId = "hc:edition:123",
                Title = "Selected edition"
            };
            editionServiceProxy.Edition = expectedEdition;
            var bookService = new Mock<IBookService>();
            bookService.Setup(service => service.GetBook(expectedEdition.BookId)).Returns(new Book
            {
                Id = expectedEdition.BookId,
                Author = new Author { Id = 4, Name = "Fixture Author" },
                Title = "Fixture Book"
            });
            var controller = new ManualImportController(
                manualImportService,
                null,
                editionService,
                bookService.Object,
                null,
                null,
                LogManager.GetCurrentClassLogger())
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };

            controller.UpdateItems(new List<ManualImportUpdateResource>
            {
                new ManualImportUpdateResource
                {
                    Id = 7,
                    Path = "/books/Author/Book.m4b",
                    Name = "Book.m4b",
                    ForeignEditionId = "hc:edition:123"
                }
            });

            Assert.That(editionServiceProxy.RequestedForeignEditionId, Is.EqualTo("hc:edition:123"));
            Assert.That(manualImportProxy.UpdatedItems, Has.Count.EqualTo(1));
            Assert.That(manualImportProxy.UpdatedItems[0].Edition, Is.SameAs(expectedEdition));
        }

        [Test]
        public void preview_should_keep_legacy_audio_tags_alongside_field_agnostic_tags()
        {
            var resource = new ManualImportItem
            {
                Path = "/books/Author/Book.m4b",
                Name = "Book.m4b",
                Quality = new QualityModel { Quality = Quality.MP3 },
                DurationSeconds = 2520,
                Tags = new Dictionary<string, List<string>>
                {
                    ["TITLE"] = new List<string> { "Book" },
                    ["ARTIST"] = new List<string> { "Author" },
                    ["NARRATOR"] = new List<string> { "Narrator" }
                }
            };

            var apiResource = resource.ToResource();

            Assert.That(apiResource.AudioTags, Is.Not.Null);
            Assert.That(apiResource.AudioTags.Title, Is.EqualTo("Book"));
            Assert.That(apiResource.AudioTags.Authors, Is.EqualTo(new[] { "Author" }));
            Assert.That(apiResource.AudioTags.Narrator, Is.EqualTo("Narrator"));
            Assert.That(apiResource.AudioTags.Duration, Is.EqualTo(TimeSpan.FromMinutes(42)));
            Assert.That(apiResource.Tags["TITLE"], Is.EqualTo(new[] { "Book" }));
        }
    }
}
