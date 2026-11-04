using System.Collections.Generic;
using Chaptarr.Api.V1.Author;
using Chaptarr.Http.Middleware;
using NUnit.Framework;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class AuthorResourceBookshelfCompatibilityFixture
    {
        [Test]
        public void native_resource_should_project_legacy_single_format_fields()
        {
            var author = new Author
            {
                Id = 7,
                Name = "Author",
                LastSelectedMediaType = "ebook",
                AudiobookQualityProfileId = 11,
                EbookQualityProfileId = 19,
                AudiobookMetadataProfileId = 23,
                EbookMetadataProfileId = 29,
                AudiobookRootFolderPath = "/audiobooks",
                EbookRootFolderPath = "/ebooks",
                AudiobookMonitorNewItems = NewItemMonitorTypes.All,
                EbookMonitorNewItems = NewItemMonitorTypes.New
            };

            var resource = AuthorResourceMapper.ToResource(author);

            Assert.That(resource.AuthorMetadataId, Is.EqualTo(author.Id));
            Assert.That(resource.QualityProfileId, Is.EqualTo(19));
            Assert.That(resource.MetadataProfileId, Is.EqualTo(29));
            Assert.That(resource.RootFolderPath, Is.EqualTo("/ebooks"));
            Assert.That(resource.MonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
        }

        [Test]
        public void native_resource_should_return_non_null_legacy_profile_ids_when_no_format_profile_is_configured()
        {
            var resource = AuthorResourceMapper.ToResource(new Author { Id = 8, Name = "Unconfigured" });

            Assert.That(resource.QualityProfileId, Is.Zero);
            Assert.That(resource.MetadataProfileId, Is.Zero);
        }

        [Test]
        public void bookshelf_compatibility_should_project_the_original_single_profile_ids()
        {
            var author = new Author
            {
                Id = 9,
                Name = "Migrated author",
                LastSelectedMediaType = "ebook",
                QualityProfileId = 2,
                BookshelfNGQualityProfileId = 2,
                BookshelfNGAudiobookQualityProfileId = 1,
                BookshelfNGEbookQualityProfileId = 2,
                AudiobookQualityProfileId = 2,
                EbookQualityProfileId = 4,
                MetadataProfileId = 5,
                AudiobookMetadataProfileId = 5,
                EbookMetadataProfileId = 5
            };
            var facade = new ReadarrFacadeContext("bookshelf", null, "", isBookshelfCompatibility: true);

            var resource = AuthorResourceMapper.ToResource(author, facade);

            Assert.That(resource.QualityProfileId, Is.EqualTo(2));
            Assert.That(resource.AudiobookQualityProfileId, Is.EqualTo(1));
            Assert.That(resource.EbookQualityProfileId, Is.EqualTo(2));
            Assert.That(resource.MetadataProfileId, Is.EqualTo(5));
        }

        [Test]
        public void bookshelf_profile_inputs_should_map_to_target_ids_and_keep_source_ids_for_compatibility()
        {
            var resource = new AuthorResource
            {
                QualityProfileId = 2,
                AudiobookQualityProfileId = 2,
                EbookQualityProfileId = 2
            };
            var mappings = new[]
            {
                new BookshelfLegacyIdentityMapEntry { SourceId = 2, TargetId = 3, MediaType = (int)BookMediaType.Audiobook },
                new BookshelfLegacyIdentityMapEntry { SourceId = 2, TargetId = 7, MediaType = (int)BookMediaType.Ebook }
            };

            AuthorResourceMapper.MapBookshelfQualityProfileIds(resource, mappings);
            var model = resource.ToModel(new ReadarrFacadeContext("bookshelf", null, "", isBookshelfCompatibility: true));

            Assert.Multiple(() =>
            {
                Assert.That(resource.QualityProfileId, Is.EqualTo(3));
                Assert.That(resource.AudiobookQualityProfileId, Is.EqualTo(3));
                Assert.That(resource.EbookQualityProfileId, Is.EqualTo(7));
                Assert.That(model.QualityProfileId, Is.EqualTo(3));
                Assert.That(model.AudiobookQualityProfileId, Is.EqualTo(3));
                Assert.That(model.EbookQualityProfileId, Is.EqualTo(7));
                Assert.That(model.BookshelfNGQualityProfileId, Is.EqualTo(2));
                Assert.That(model.BookshelfNGAudiobookQualityProfileId, Is.EqualTo(2));
                Assert.That(model.BookshelfNGEbookQualityProfileId, Is.EqualTo(2));
            });
        }

        [Test]
        public void legacy_monitoring_and_tags_should_apply_to_both_formats_without_format_specific_fields()
        {
            var resource = new AuthorResource
            {
                Monitored = false,
                MonitorNewItems = NewItemMonitorTypes.New,
                Tags = new HashSet<int> { 3, 5 }
            };

            var author = resource.ToModel();

            Assert.That(author.AudiobookMonitored, Is.False);
            Assert.That(author.EbookMonitored, Is.False);
            Assert.That(author.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
            Assert.That(author.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
            Assert.That(author.AudiobookTags, Is.EquivalentTo(new[] { 3, 5 }));
            Assert.That(author.EbookTags, Is.EquivalentTo(new[] { 3, 5 }));
        }
    }
}
