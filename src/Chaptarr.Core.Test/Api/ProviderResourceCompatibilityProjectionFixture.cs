using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.Indexers;
using Chaptarr.Api.V1.ImportLists;
using Chaptarr.Api.V1;
using Chaptarr.Api.V1.Metadata;
using Chaptarr.Http.ClientSchema;
using NzbDrone.Core.Indexers.DirectDownload;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class ProviderResourceCompatibilityProjectionFixture
    {
        [Test]
        public void should_project_direct_download_urls_to_the_bookshelf_tag_field_shape()
        {
            var resource = new IndexerResource
            {
                ConfigContract = nameof(DirectDownloadSettings),
                Fields = new List<Field>
                {
                    new()
                    {
                        // SchemaBuilder camel-cases field names, so the runtime API uses "urls".
                        Name = "urls",
                        Type = "textArea",
                        Value = "https://primary.example\nhttps://secondary.example"
                    }
                }
            };

            ProviderResourceCompatibilityProjection.ProjectBookshelfLegacyFields(resource);

            var urls = resource.Fields.Single();
            Assert.Multiple(() =>
            {
                Assert.That(urls.Type, Is.EqualTo("tag"));
                Assert.That(urls.Value, Is.EqualTo(new[]
                {
                    "https://primary.example",
                    "https://secondary.example"
                }));
            });
        }

        [Test]
        public void should_project_chaptarr_import_provider_as_readarr_for_bookshelf_clients()
        {
            var resource = new ImportListResource
            {
                Implementation = "ChaptarrImport",
                ImplementationName = "Chaptarr",
                ConfigContract = "ChaptarrSettings"
            };

            ProviderResourceCompatibilityProjection.ProjectBookshelfLegacyProviderIdentity(resource);

            Assert.Multiple(() =>
            {
                Assert.That(resource.Implementation, Is.EqualTo("ReadarrImport"));
                Assert.That(resource.ImplementationName, Is.EqualTo("Readarr"));
                Assert.That(resource.ConfigContract, Is.EqualTo("ReadarrSettings"));
            });
        }

        [Test]
        public void should_accept_readarr_import_identity_from_bookshelf_clients()
        {
            var resource = new ImportListResource
            {
                Implementation = "ReadarrImport",
                ImplementationName = "Readarr",
                ConfigContract = "ReadarrSettings"
            };

            ProviderResourceCompatibilityProjection.ProjectBookshelfNativeProviderIdentity(resource);

            Assert.Multiple(() =>
            {
                Assert.That(resource.Implementation, Is.EqualTo("ChaptarrImport"));
                Assert.That(resource.ImplementationName, Is.EqualTo("Chaptarr"));
                Assert.That(resource.ConfigContract, Is.EqualTo("ChaptarrSettings"));
            });
        }

        [Test]
        public void should_project_legacy_audiobookshelf_metadata_consumer_identity()
        {
            var resource = new MetadataResource
            {
                Implementation = "LegacyAudiobookshelfMetadata",
                ConfigContract = "NullConfig"
            };

            ProviderResourceCompatibilityProjection.ProjectBookshelfLegacyProviderIdentity(resource);
            Assert.That(resource.Implementation, Is.EqualTo("AudiobookshelfMetadata"));

            ProviderResourceCompatibilityProjection.ProjectBookshelfNativeProviderIdentity(resource);
            Assert.That(resource.Implementation, Is.EqualTo("LegacyAudiobookshelfMetadata"));
        }
    }
}
