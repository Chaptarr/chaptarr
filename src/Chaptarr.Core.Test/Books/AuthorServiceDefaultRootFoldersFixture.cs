using System.Collections.Generic;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class AuthorServiceDefaultRootFoldersFixture
    {
        private static readonly List<RootFolder> AudioAndEbook = new List<RootFolder>
        {
            new RootFolder { Id = 1, Path = "/audiobooks", FolderType = FolderType.Audiobook },
            new RootFolder { Id = 2, Path = "/ebooks", FolderType = FolderType.Ebook }
        };

        private static AuthorService BuildService(IConfigService configService = null)
        {
            return new AuthorService(
                authorRepository: null,
                eventAggregator: null,
                authorPathBuilder: null,
                rootFolderService: null,
                commandQueueManager: null,
                cacheManager: new CacheManager(),
                bookRepository: null,
                mediaFileService: null,
                logger: LogManager.GetCurrentClassLogger(),
                configService: configService);
        }

        [Test]
        public void should_fill_missing_ebook_root_for_audiobook_only_author()
        {
            var author = new Author { Name = "A", AudiobookRootFolderPath = "/audiobooks" };

            BuildService().ApplyDefaultRootFoldersOnAdd(author, AudioAndEbook);

            Assert.That(author.AudiobookRootFolderPath, Is.EqualTo("/audiobooks"));
            Assert.That(author.EbookRootFolderPath, Is.EqualTo("/ebooks"));
        }

        [Test]
        public void should_fill_missing_audiobook_root_for_ebook_only_author()
        {
            var author = new Author { Name = "A", EbookRootFolderPath = "/ebooks" };

            BuildService().ApplyDefaultRootFoldersOnAdd(author, AudioAndEbook);

            Assert.That(author.AudiobookRootFolderPath, Is.EqualTo("/audiobooks"));
            Assert.That(author.EbookRootFolderPath, Is.EqualTo("/ebooks"));
        }

        [Test]
        public void should_not_overwrite_explicit_roots()
        {
            var author = new Author { Name = "A", AudiobookRootFolderPath = "/custom-audio", EbookRootFolderPath = "/custom-ebook" };

            BuildService().ApplyDefaultRootFoldersOnAdd(author, AudioAndEbook);

            Assert.That(author.AudiobookRootFolderPath, Is.EqualTo("/custom-audio"));
            Assert.That(author.EbookRootFolderPath, Is.EqualTo("/custom-ebook"));
        }

        [Test]
        public void should_leave_blank_when_default_is_ambiguous()
        {
            var rootFolders = new List<RootFolder>
            {
                new RootFolder { Id = 1, Path = "/audio-a", FolderType = FolderType.Audiobook },
                new RootFolder { Id = 2, Path = "/audio-b", FolderType = FolderType.Audiobook },
                new RootFolder { Id = 3, Path = "/ebooks", FolderType = FolderType.Ebook }
            };
            var author = new Author { Name = "A", EbookRootFolderPath = "/ebooks" };

            BuildService().ApplyDefaultRootFoldersOnAdd(author, rootFolders);

            Assert.That(author.AudiobookRootFolderPath, Is.Null.Or.Empty);
        }

        [Test]
        public void should_use_configured_default_when_several_roots_exist()
        {
            var rootFolders = new List<RootFolder>
            {
                new RootFolder { Id = 1, Path = "/audio-a", FolderType = FolderType.Audiobook },
                new RootFolder { Id = 2, Path = "/audio-b", FolderType = FolderType.Audiobook },
                new RootFolder { Id = 3, Path = "/ebooks", FolderType = FolderType.Ebook }
            };
            var config = ConfigServiceTestProxy.Create();
            ((ConfigServiceTestProxy)config).DefaultAudiobookRootFolderPath = "/audio-b";
            var author = new Author { Name = "A", EbookRootFolderPath = "/ebooks" };

            BuildService(config).ApplyDefaultRootFoldersOnAdd(author, rootFolders);

            Assert.That(author.AudiobookRootFolderPath, Is.EqualTo("/audio-b"));
        }

        [Test]
        public void should_do_nothing_without_root_folders()
        {
            var author = new Author { Name = "A", EbookRootFolderPath = "/ebooks" };

            BuildService().ApplyDefaultRootFoldersOnAdd(author, new List<RootFolder>());

            Assert.That(author.AudiobookRootFolderPath, Is.Null.Or.Empty);
        }
    }
}
