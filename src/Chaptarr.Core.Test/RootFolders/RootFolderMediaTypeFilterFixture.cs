using System.Collections.Generic;
using Chaptarr.Http.REST;
using Chaptarr.Api.V1.RootFolders;
using NUnit.Framework;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.RootFolders
{
    [TestFixture]
    public class RootFolderMediaTypeFilterFixture
    {
        [Test]
        public void should_filter_for_ebook()
        {
            var folders = new List<NzbDrone.Core.RootFolders.RootFolder>
            {
                new() { FolderType = FolderType.Mixed },
                new() { FolderType = FolderType.Audiobook },
                new() { FolderType = FolderType.Ebook }
            };

            var result = RootFolderMediaTypeFilter.Filter(folders, "ebook");

            Assert.That(result, Has.Count.EqualTo(2));
            Assert.That(result, Has.All.Matches<NzbDrone.Core.RootFolders.RootFolder>(f => f.FolderType != FolderType.Audiobook));
        }

        [Test]
        public void should_filter_for_audiobook()
        {
            var folders = new List<NzbDrone.Core.RootFolders.RootFolder>
            {
                new() { FolderType = FolderType.Mixed },
                new() { FolderType = FolderType.Audiobook },
                new() { FolderType = FolderType.Ebook }
            };

            var result = RootFolderMediaTypeFilter.Filter(folders, "audiobook");

            Assert.That(result, Has.Count.EqualTo(2));
            Assert.That(result, Has.All.Matches<NzbDrone.Core.RootFolders.RootFolder>(f => f.FolderType != FolderType.Ebook));
        }

        [Test]
        public void should_not_filter_when_media_type_is_missing()
        {
            var folders = new List<NzbDrone.Core.RootFolders.RootFolder>
            {
                new() { FolderType = FolderType.Mixed },
                new() { FolderType = FolderType.Audiobook },
                new() { FolderType = FolderType.Ebook }
            };

            Assert.That(RootFolderMediaTypeFilter.Filter(folders, null), Has.Count.EqualTo(3));
            Assert.That(RootFolderMediaTypeFilter.Filter(folders, string.Empty), Has.Count.EqualTo(3));
        }

        [Test]
        public void should_reject_unknown_media_type()
        {
            var folders = new List<NzbDrone.Core.RootFolders.RootFolder>
            {
                new() { FolderType = FolderType.Mixed },
                new() { FolderType = FolderType.Audiobook },
                new() { FolderType = FolderType.Ebook }
            };

            Assert.Throws<BadRequestException>(() => RootFolderMediaTypeFilter.Filter(folders, "tv"));
        }
    }
}
