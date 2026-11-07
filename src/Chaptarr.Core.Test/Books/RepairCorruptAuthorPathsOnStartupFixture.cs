using System.Collections.Generic;
using Moq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    [Platform(Exclude = "Win", Reason = "Tests use Unix paths")]
    public class RepairCorruptAuthorPathsOnStartupFixture
    {
        [Test]
        public void should_preserve_distinct_media_paths_when_author_path_is_the_shared_root()
        {
            const string authorRoot = "/media/Alexis Hall";
            var author = new Author
            {
                Id = 7,
                Name = "Alexis Hall",
                Path = authorRoot,
                AudiobookRootFolderPath = authorRoot,
                EbookRootFolderPath = authorRoot,
                AudiobookPath = "/media/Alexis Hall/Audiobooks",
                EbookPath = "/media/Alexis Hall/Ebooks"
            };
            var rootFolders = new List<RootFolder>
            {
                new RootFolder { Path = "/media", FolderType = FolderType.Mixed },
                new RootFolder { Path = authorRoot, FolderType = FolderType.Mixed }
            };

            var authorService = new Mock<IAuthorService>();
            authorService.Setup(service => service.GetAllAuthors(true)).Returns(new List<Author> { author });
            var rootFolderService = new Mock<IRootFolderService>();
            rootFolderService.Setup(service => service.All()).Returns(rootFolders);
            var fileNameBuilder = new Mock<IBuildFileNames>();
            fileNameBuilder.Setup(builder => builder.GetAuthorFolder(author, null, "audiobook")).Returns("Alexis Hall");

            var service = new RepairCorruptAuthorPathsOnStartup(
                authorService.Object,
                rootFolderService.Object,
                fileNameBuilder.Object,
                LogManager.GetCurrentClassLogger());

            service.Handle(new ApplicationStartedEvent());

            authorService.Verify(service => service.UpdateAuthor(It.IsAny<Author>()), Times.Never);
            Assert.Multiple(() =>
            {
                Assert.That(author.Path, Is.EqualTo(authorRoot));
                Assert.That(author.AudiobookPath, Is.EqualTo("/media/Alexis Hall/Audiobooks"));
                Assert.That(author.EbookPath, Is.EqualTo("/media/Alexis Hall/Ebooks"));
            });
        }
    }
}
