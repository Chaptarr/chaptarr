using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.RootFolders
{
    [TestFixture]
    public class RootFolderDefaultResolverFixture
    {
        [Test]
        public void should_use_only_compatible_root_when_default_is_not_configured()
        {
            var rootFolders = new List<RootFolder>
            {
                new RootFolder { Path = "/library", FolderType = FolderType.Mixed }
            };

            var result = RootFolderDefaultResolver.TryGetEffectiveDefaultRootFolder(
                rootFolders,
                FolderType.Audiobook,
                string.Empty,
                out var rootFolder,
                out var error);

            Assert.That(result, Is.True);
            Assert.That(error, Is.Null);
            Assert.That(rootFolder.Path, Is.EqualTo("/library"));
        }

        [Test]
        public void should_reject_multiple_compatible_roots_when_default_is_not_configured()
        {
            var rootFolders = new List<RootFolder>
            {
                new RootFolder { Path = "/library/audiobooks", FolderType = FolderType.Audiobook },
                new RootFolder { Path = "/library/mixed", FolderType = FolderType.Mixed }
            };

            var result = RootFolderDefaultResolver.TryGetEffectiveDefaultRootFolder(
                rootFolders,
                FolderType.Audiobook,
                string.Empty,
                out var rootFolder,
                out var error);

            Assert.That(result, Is.False);
            Assert.That(rootFolder, Is.Null);
            Assert.That(error, Is.EqualTo("Multiple audiobook or mixed root folders are configured; select a default audiobook root folder"));
        }

        [Test]
        public void should_use_configured_default_when_multiple_compatible_roots_exist()
        {
            var rootFolders = new List<RootFolder>
            {
                new RootFolder { Path = "/library/audiobooks", FolderType = FolderType.Audiobook },
                new RootFolder { Path = "/library/mixed", FolderType = FolderType.Mixed }
            };

            var result = RootFolderDefaultResolver.TryGetEffectiveDefaultRootFolder(
                rootFolders,
                FolderType.Audiobook,
                "/library/mixed",
                out var rootFolder,
                out var error);

            Assert.That(result, Is.True);
            Assert.That(error, Is.Null);
            Assert.That(rootFolder.Path, Is.EqualTo("/library/mixed"));
        }

        [Test]
        public void should_reject_configured_default_that_is_not_compatible()
        {
            var rootFolders = new List<RootFolder>
            {
                new RootFolder { Path = "/library/ebooks", FolderType = FolderType.Ebook }
            };

            var result = RootFolderDefaultResolver.TryGetEffectiveDefaultRootFolder(
                rootFolders,
                FolderType.Audiobook,
                "/library/ebooks",
                out var rootFolder,
                out var error);

            Assert.That(result, Is.False);
            Assert.That(rootFolder, Is.Null);
            Assert.That(error, Is.EqualTo("Default audiobook root folder '/library/ebooks' is not compatible with audiobook imports"));
        }
    }
}
