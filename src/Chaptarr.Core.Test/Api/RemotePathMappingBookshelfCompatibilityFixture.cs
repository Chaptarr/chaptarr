using Chaptarr.Api.V1.RemotePathMappings;
using NzbDrone.Common.Serializer;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class RemotePathMappingBookshelfCompatibilityFixture
    {
        [Test]
        public void legacy_mapping_payload_without_download_client_id_should_deserialize_as_global_mapping()
        {
            const string json = "{\"host\":\"seedbox\",\"remotePath\":\"/downloads\",\"localPath\":\"/mnt/downloads\"}";

            var resource = STJson.Deserialize<RemotePathMappingResource>(json);
            var model = resource.ToModel();

            Assert.That(resource.DownloadClientId, Is.Zero);
            Assert.That(model.DownloadClientId, Is.Zero);
            Assert.That(model.Host, Is.EqualTo("seedbox"));
            Assert.That(model.RemotePath, Is.EqualTo("/downloads"));
            Assert.That(model.LocalPath, Is.EqualTo("/mnt/downloads"));
        }
    }
}
