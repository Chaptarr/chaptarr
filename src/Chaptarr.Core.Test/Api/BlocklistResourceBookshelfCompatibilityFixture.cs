using System.Collections.Generic;
using Chaptarr.Api.V1.Blocklist;
using Chaptarr.Http.Middleware;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Blocklisting;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class BlocklistResourceBookshelfCompatibilityFixture
    {
        [Test]
        public void bookshelf_facade_should_restore_legacy_blocklist_and_nested_author_ids()
        {
            var author = new Author
            {
                Id = 20,
                Name = "Migrated author",
                HardcoverAuthorId = "hc:10010",
                BookshelfNGQualityProfileId = 2,
                BookshelfNGAudiobookQualityProfileId = 1,
                BookshelfNGEbookQualityProfileId = 2,
                AudiobookQualityProfileId = 1,
                EbookQualityProfileId = 4
            };
            var facade = new ReadarrFacadeContext("hc", null, "/api", isBookshelfCompatibility: true);

            var resource = BlocklistResourceMapper.MapToResource(
                new Blocklist { Id = 92, BookProviderIds = new List<string> { "hc:book-30" } },
                author,
                facade,
                legacyAuthorId: 20,
                legacyAuthorMetadataId: 10,
                legacyBookIds: new[] { 30, 30 });

            Assert.Multiple(() =>
            {
                Assert.That(resource.AuthorId, Is.EqualTo(20));
                Assert.That(resource.Author.AuthorMetadataId, Is.EqualTo(10));
                Assert.That(resource.Author.ForeignAuthorId, Is.EqualTo("10010"));
                Assert.That(resource.Author.QualityProfileId, Is.EqualTo(2));
                Assert.That(resource.Author.AudiobookQualityProfileId, Is.EqualTo(1));
                Assert.That(resource.Author.EbookQualityProfileId, Is.EqualTo(2));
                Assert.That(resource.BookIds, Is.EqualTo(new[] { 30 }));
            });
        }

        [Test]
        public void native_response_should_not_expose_bookshelf_identity_map_ids()
        {
            var resource = BlocklistResourceMapper.MapToResource(
                new Blocklist { Id = 93 },
                new Author { Id = 21, Name = "Native author", HardcoverAuthorId = "hc:10011" },
                facadeContext: null,
                legacyAuthorId: 20,
                legacyAuthorMetadataId: 11,
                legacyBookIds: new[] { 31 });

            Assert.Multiple(() =>
            {
                Assert.That(resource.AuthorId, Is.EqualTo(21));
                Assert.That(resource.Author.AuthorMetadataId, Is.EqualTo(21));
                Assert.That(resource.Author.ForeignAuthorId, Is.EqualTo("hc:10011"));
                Assert.That(resource.BookIds, Is.Empty);
            });
        }
    }
}
