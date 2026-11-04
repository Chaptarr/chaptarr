using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.Books;
using Chaptarr.Http.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Messaging.Commands;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class BookEditorControllerCompatibilityFixture
    {
        [Test]
        public void legacy_monitoring_write_should_project_bookshelf_provider_ids_and_grabbed_state()
        {
            var bookService = new Mock<IBookService>();
            bookService
                .Setup(service => service.GetBooks(It.Is<IEnumerable<int>>(ids => ids.SequenceEqual(new[] { 31 }))))
                .Returns(new List<Book>
                {
                    new()
                    {
                        Id = 31,
                        AuthorId = 20,
                        MediaType = BookMediaType.Audiobook,
                        HardcoverBookId = "hc:100031",
                        Title = "Fixture audiobook",
                        Editions = new List<Edition>
                        {
                            new()
                            {
                                Id = 42,
                                ForeignEditionId = "hc:200042",
                                Monitored = true
                            }
                        }
                    }
                });

            var legacyIdentityMap = new Mock<IBookshelfLegacyIdentityMapService>();
            legacyIdentityMap
                .Setup(service => service.ResolveBookIds(It.Is<IEnumerable<int>>(ids => ids.SequenceEqual(new[] { 30 })), null))
                .Returns(new List<int> { 31 });
            legacyIdentityMap
                .Setup(service => service.GetMappingsByTargetIds("Book", It.Is<IEnumerable<int>>(ids => ids.SequenceEqual(new[] { 31 }))))
                .Returns(new List<BookshelfLegacyIdentityMapEntry>
                {
                    new() { SourceId = 30, TargetId = 31 }
                });
            legacyIdentityMap
                .Setup(service => service.GetMappingsByTargetIds("AuthorMetadata", It.IsAny<IEnumerable<int>>()))
                .Returns(new List<BookshelfLegacyIdentityMapEntry>());

            var controller = new BookEditorController(
                bookService.Object,
                new Mock<IManageCommandQueue>().Object,
                legacyIdentityMap.Object)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext()
                }
            };
            controller.ControllerContext.HttpContext.Items[ReadarrFacadeContext.ItemKey] =
                new ReadarrFacadeContext("hc", null, "/api", isBookshelfCompatibility: true);

            var result = controller.SaveAll(new BookEditorResource
            {
                BookIds = new List<int> { 30 },
                Monitored = false
            }) as AcceptedResult;

            Assert.That(result, Is.Not.Null);
            var resource = ((List<BookResource>)result!.Value!).Single();

            Assert.Multiple(() =>
            {
                Assert.That(resource.Id, Is.EqualTo(30));
                Assert.That(resource.ForeignBookId, Is.EqualTo("100031"));
                Assert.That(resource.ForeignEditionId, Is.EqualTo("200042"));
                Assert.That(resource.Grabbed, Is.False);
            });
        }
    }
}
