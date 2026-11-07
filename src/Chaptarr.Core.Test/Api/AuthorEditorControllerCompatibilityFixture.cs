using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.Author;
using Chaptarr.Http.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class AuthorEditorControllerCompatibilityFixture
    {
        [Test]
        public void legacy_monitoring_write_should_project_bookshelf_author_identity_and_profile_ids()
        {
            var author = new Author
            {
                Id = 21,
                Name = "Legacy author",
                HardcoverAuthorId = "hc:10011",
                BookshelfNGQualityProfileId = 1,
                BookshelfNGAudiobookQualityProfileId = 1,
                BookshelfNGEbookQualityProfileId = 2,
                QualityProfileId = 3,
                AudiobookQualityProfileId = 3,
                EbookQualityProfileId = 4,
                AudiobookRootFolderPath = "/books/audio",
                EbookRootFolderPath = "/books/ebooks",
                AudiobookMonitored = true,
                EbookMonitored = true
            };
            var authorService = new Mock<IAuthorService>();
            authorService
                .Setup(service => service.GetAuthors(It.Is<IEnumerable<int>>(ids => ids.SequenceEqual(new[] { 21 }))))
                .Returns(new List<Author> { author });
            authorService
                .Setup(service => service.UpdateAuthors(It.IsAny<List<Author>>(), It.IsAny<bool>()))
                .Returns((List<Author> authors, bool _) => authors);

            var rootFolderService = new Mock<IRootFolderService>();
            rootFolderService.Setup(service => service.All()).Returns(new List<RootFolder>());

            var legacyIdentityMap = new Mock<IBookshelfLegacyIdentityMapService>();
            legacyIdentityMap
                .Setup(service => service.GetMappingsByTargetIds(
                    "AuthorMetadata",
                    It.Is<IEnumerable<int>>(ids => ids.SequenceEqual(new[] { 21 }))))
                .Returns(new List<BookshelfLegacyIdentityMapEntry>
                {
                    new() { SourceId = 11, TargetId = 21 }
                });

            var controller = new AuthorEditorController(
                authorService.Object,
                new Mock<IManageCommandQueue>().Object,
                rootFolderService.Object,
                new Mock<IQualityProfileService>().Object,
                new Mock<IMetadataProfileService>().Object,
                null,
                legacyIdentityMap.Object)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext()
                }
            };
            controller.ControllerContext.HttpContext.Items[ReadarrFacadeContext.ItemKey] =
                new ReadarrFacadeContext("hc", null, "/api", isBookshelfCompatibility: true);

            var result = controller.SaveAll(new AuthorEditorResource
            {
                AuthorIds = new List<int> { 21 },
                Monitored = false
            }) as AcceptedResult;

            Assert.That(result, Is.Not.Null);
            var resource = ((List<AuthorResource>)result!.Value!).Single();

            Assert.Multiple(() =>
            {
                Assert.That(resource.Id, Is.EqualTo(21));
                Assert.That(resource.AuthorMetadataId, Is.EqualTo(11));
                Assert.That(resource.ForeignAuthorId, Is.EqualTo("10011"));
                Assert.That(resource.EbookQualityProfileId, Is.EqualTo(2));
                Assert.That(resource.Monitored, Is.False);
            });
        }
    }
}
