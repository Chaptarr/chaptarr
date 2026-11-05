using System;
using System.Collections.Generic;
using System.IO;
using Moq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaCover;
using Cover = NzbDrone.Core.MediaCover.MediaCover;

namespace Chaptarr.Core.Test.MediaCover
{
    [TestFixture]
    public class BookshelfCoverUrlFixture
    {
        [TestCase(MediaCoverEntity.Book, MediaCoverTypes.Cover, ".jpeg", true)]
        [TestCase(MediaCoverEntity.Book, MediaCoverTypes.Cover, "", true)]
        [TestCase(MediaCoverEntity.Book, MediaCoverTypes.Cover, ".webp", false)]
        [TestCase(MediaCoverEntity.Author, MediaCoverTypes.Poster, ".png", true)]
        [TestCase(MediaCoverEntity.Author, MediaCoverTypes.Clearlogo, ".jpg", false)]
        public void should_preserve_bookshelf_url_extension_url_base_and_timestamp(MediaCoverEntity entity, MediaCoverTypes type, string extension, bool cached)
        {
            var app = Mock.Of<IAppFolderInfo>(info => info.AppDataFolder == "/fixture/config");
            var config = Mock.Of<IConfigFileProvider>(provider => provider.UrlBase == "/bookshelf");
            var disk = new Mock<IDiskProvider>();
            var stamp = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            var actualExtension = type == MediaCoverTypes.Clearlogo ? ".png" : extension;
            var name = type.ToString().ToLowerInvariant() + actualExtension;
            var path = entity == MediaCoverEntity.Book
                ? Path.Combine("/fixture/config", "MediaCover", "Books", "123", name)
                : Path.Combine("/fixture/config", "MediaCover", "123", name);
            disk.Setup(provider => provider.FolderExists(Path.GetDirectoryName(path))).Returns(cached);
            disk.Setup(provider => provider.FileExists(path)).Returns(cached);
            disk.Setup(provider => provider.FileGetLastWrite(path)).Returns(stamp);
            var subject = new MediaCoverService(Mock.Of<IMediaCoverProxy>(), null, null, null, disk.Object, app, null, null, config, null, null, LogManager.GetCurrentClassLogger());
            var remote = "https://images.example/fixture" + extension;
            var cover = new Cover(type, remote);

            subject.ConvertToBookshelfLocalUrls(123, entity, new List<Cover> { cover });

            var expected = "/bookshelf/MediaCover/" + (entity == MediaCoverEntity.Book ? "Books/" : "") + "123/" + name;
            Assert.That(cover.Url, Is.EqualTo(expected + (cached ? "?lastWrite=" + stamp.Ticks : "")));
            Assert.That(cover.RemoteUrl, Is.EqualTo(remote));
            Assert.That(cover.Extension, Is.EqualTo(extension));
            if (!cached)
            {
                disk.Verify(provider => provider.FileExists(It.IsAny<string>()), Times.Never);
            }
        }
    }
}
