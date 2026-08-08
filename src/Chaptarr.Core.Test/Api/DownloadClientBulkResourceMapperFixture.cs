using System.Collections.Generic;
using Chaptarr.Api.V1;
using Chaptarr.Api.V1.DownloadClient;
using NUnit.Framework;
using NzbDrone.Core.Download;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class DownloadClientBulkResourceMapperFixture
    {
        [Test]
        public void should_add_legacy_bulk_tags_to_both_media_tag_sets()
        {
            var definition = new DownloadClientDefinition
            {
                AudiobookTags = new HashSet<int> { 1 },
                EbookTags = new HashSet<int> { 2 },
                Tags = new HashSet<int> { 1, 2, 3 }
            };

            Update(new DownloadClientBulkResource
            {
                Tags = new List<int> { 3 },
                ApplyTags = ApplyTags.Add
            }, definition);

            Assert.That(definition.AudiobookTags, Is.EquivalentTo(new[] { 1, 3 }));
            Assert.That(definition.EbookTags, Is.EquivalentTo(new[] { 2, 3 }));
            Assert.That(definition.Tags, Is.EquivalentTo(new[] { 1, 2, 3 }));
        }

        [Test]
        public void should_remove_legacy_bulk_tags_from_both_media_tag_sets()
        {
            var definition = new DownloadClientDefinition
            {
                AudiobookTags = new HashSet<int> { 1, 3 },
                EbookTags = new HashSet<int> { 2, 3 },
                Tags = new HashSet<int> { 1, 2 }
            };

            Update(new DownloadClientBulkResource
            {
                Tags = new List<int> { 3 },
                ApplyTags = ApplyTags.Remove
            }, definition);

            Assert.That(definition.AudiobookTags, Is.EquivalentTo(new[] { 1 }));
            Assert.That(definition.EbookTags, Is.EquivalentTo(new[] { 2 }));
            Assert.That(definition.Tags, Is.EquivalentTo(new[] { 1, 2 }));
        }

        [Test]
        public void should_replace_both_media_tag_sets_from_legacy_bulk_tags()
        {
            var definition = new DownloadClientDefinition
            {
                AudiobookTags = new HashSet<int> { 1 },
                EbookTags = new HashSet<int> { 2 },
                Tags = new HashSet<int> { 4, 5 }
            };

            Update(new DownloadClientBulkResource
            {
                Tags = new List<int> { 4, 5 },
                ApplyTags = ApplyTags.Replace
            }, definition);

            Assert.That(definition.AudiobookTags, Is.EquivalentTo(new[] { 4, 5 }));
            Assert.That(definition.EbookTags, Is.EquivalentTo(new[] { 4, 5 }));
            Assert.That(definition.Tags, Is.EquivalentTo(new[] { 4, 5 }));
        }

        [Test]
        public void should_preserve_other_bulk_fields()
        {
            var definition = new DownloadClientDefinition
            {
                Enable = false,
                Priority = 1,
                RemoveCompletedDownloads = true,
                RemoveFailedDownloads = true
            };

            Update(new DownloadClientBulkResource
            {
                Enable = true,
                Priority = 12,
                RemoveCompletedDownloads = false,
                RemoveFailedDownloads = false
            }, definition);

            Assert.That(definition.Enable, Is.True);
            Assert.That(definition.Priority, Is.EqualTo(12));
            Assert.That(definition.RemoveCompletedDownloads, Is.False);
            Assert.That(definition.RemoveFailedDownloads, Is.False);
        }

        private static void Update(DownloadClientBulkResource resource, DownloadClientDefinition definition)
        {
            var mapper = new DownloadClientBulkResourceMapper();

            mapper.UpdateModel(resource, new List<DownloadClientDefinition> { definition });
        }
    }
}
