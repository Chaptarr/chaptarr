using System.IO;
using Chaptarr.Api.V1.MediaCovers;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class AuthorCoverFallbackFixture
    {
        [TestCase(false, 0, true, 2048, "poster.jpg")]
        [TestCase(true, 0, true, 2048, "poster.jpg")]
        [TestCase(true, 1024, true, 2048, "poster-250.jpg")]
        [TestCase(false, 0, false, 0, null)]
        [TestCase(false, 0, true, 0, null)]
        public void should_serve_original_author_image_when_resizing_is_unavailable(
            bool renditionExists, long renditionSize, bool originalExists, long originalSize, string expectedFile)
        {
            var root = Path.Combine(Path.GetTempPath(), "chaptarrng-cover-fallback-fixture");
            var rendition = Path.Combine(root, "MediaCover", "10", "poster-250.jpg");
            var original = Path.Combine(root, "MediaCover", "10", "poster.jpg");
            var appFolder = new Mock<IAppFolderInfo>();
            appFolder.SetupGet(c => c.AppDataFolder).Returns(root);
            var disk = new Mock<IDiskProvider>();
            disk.Setup(c => c.FileExists(rendition)).Returns(renditionExists);
            disk.Setup(c => c.GetFileSize(rendition)).Returns(renditionSize);
            disk.Setup(c => c.FileExists(original)).Returns(originalExists);
            disk.Setup(c => c.GetFileSize(original)).Returns(originalSize);

            var response = new MediaCoverController(appFolder.Object, disk.Object).GetAuthorMediaCover(10, "poster-250.jpg");
            if (expectedFile == null)
            {
                Assert.That(response, Is.TypeOf<NotFoundResult>());
            }
            else
            {
                Assert.That(response, Is.TypeOf<PhysicalFileResult>());
                Assert.That(((PhysicalFileResult)response).FileName, Is.EqualTo(Path.Combine(root, "MediaCover", "10", expectedFile)));
                Assert.That(((PhysicalFileResult)response).ContentType, Is.EqualTo("image/jpeg"));
            }
        }

        [TestCase("../poster-250.jpg")]
        [TestCase("poster-250.svg")]
        [TestCase("poster-250.exe")]
        public void should_not_use_cover_fallback_to_serve_unsafe_paths_or_formats(string filename)
        {
            var appFolder = new Mock<IAppFolderInfo>();
            appFolder.SetupGet(c => c.AppDataFolder).Returns(Path.GetTempPath());
            var disk = new Mock<IDiskProvider>();
            disk.Setup(c => c.FileExists(It.IsAny<string>())).Returns(true);
            disk.Setup(c => c.GetFileSize(It.IsAny<string>())).Returns(2048);
            Assert.That(new MediaCoverController(appFolder.Object, disk.Object).GetAuthorMediaCover(10, filename), Is.TypeOf<NotFoundResult>());
        }
    }
}
