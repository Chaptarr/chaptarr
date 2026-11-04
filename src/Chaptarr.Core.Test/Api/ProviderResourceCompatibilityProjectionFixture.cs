using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.Indexers;
using Chaptarr.Api.V1;
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
    }
}
