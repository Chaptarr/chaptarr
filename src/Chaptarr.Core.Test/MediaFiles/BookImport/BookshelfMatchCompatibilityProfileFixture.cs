using System.Collections.Generic;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class BookshelfMatchCompatibilityProfileFixture
    {
        private sealed class RootFolderServiceStub : IRootFolderService
        {
            public RootFolder RootFolder { get; set; }

            public List<RootFolder> All() => new List<RootFolder>();
            public List<RootFolder> AllWithSpaceStats() => new List<RootFolder>();
            public RootFolder Add(RootFolder rootFolder) => rootFolder;
            public RootFolder Update(RootFolder rootFolder) => rootFolder;
            public void Remove(int id) { }
            public RootFolder Get(int id) => RootFolder;
            public List<RootFolder> AllForTag(int tagId) => new List<RootFolder>();
            public RootFolder GetBestRootFolder(string path) => RootFolder;
            public RootFolder GetBestRootFolder(string path, List<RootFolder> allRootFolders) => RootFolder;
            public string GetBestRootFolderPath(string path) => RootFolder?.Path;
            public string GetBestRootFolderPath(string path, List<RootFolder> allRootFolders) => RootFolder?.Path;
        }

        private sealed class MetadataProfileServiceStub : IMetadataProfileService
        {
            public Dictionary<int, MetadataProfile> Profiles { get; } = new Dictionary<int, MetadataProfile>();

            public MetadataProfile Add(MetadataProfile profile) => profile;
            public void Update(MetadataProfile profile) { }
            public void Delete(int id) { }
            public List<MetadataProfile> All() => Profiles.Values.ToList();
            public MetadataProfile Get(int id) => Profiles.TryGetValue(id, out var profile) ? profile : null;
            public bool Exists(int id) => Profiles.ContainsKey(id);
            public List<Book> FilterBooks(Author input, int profileId) => new List<Book>();
        }

        [Test]
        public void should_prefer_the_authors_media_specific_metadata_profile()
        {
            var profiles = new MetadataProfileServiceStub();
            profiles.Profiles[11] = new MetadataProfile { Id = 11, PreferredEditionTerms = "3|author audiobook" };
            profiles.Profiles[20] = new MetadataProfile { Id = 20, PreferredEditionTerms = "3|root audiobook" };
            var root = new RootFolder { Path = "/books" };
            root.SetAudiobookSettings(new MediaTypeSettings { MetadataProfileId = 20 });
            var service = CreateService(root, profiles);
            var author = new Author { MetadataProfileId = 10, AudiobookMetadataProfileId = 11 };

            Assert.That(service.GetBookshelfPreferredEditionTerms(author, "/books/incoming.m4b", BookMediaType.Audiobook),
                Is.EqualTo("3|author audiobook"));
        }

        [Test]
        public void should_fall_back_to_the_root_folder_profile_when_the_author_has_none()
        {
            var profiles = new MetadataProfileServiceStub();
            profiles.Profiles[21] = new MetadataProfile { Id = 21, PreferredEditionTerms = "2|root ebook" };
            var root = new RootFolder { Path = "/books" };
            root.SetEbookSettings(new MediaTypeSettings { MetadataProfileId = 21 });
            var service = CreateService(root, profiles);

            Assert.That(service.GetBookshelfPreferredEditionTerms(new Author(), "/books/incoming.epub", BookMediaType.Ebook),
                Is.EqualTo("2|root ebook"));
        }

        [Test]
        public void should_use_the_legacy_author_profile_before_the_root_default()
        {
            var profiles = new MetadataProfileServiceStub();
            profiles.Profiles[10] = new MetadataProfile { Id = 10, PreferredEditionTerms = "1|author default" };
            profiles.Profiles[20] = new MetadataProfile { Id = 20, PreferredEditionTerms = "3|root default" };
            var root = new RootFolder { Path = "/books" };
            root.SetEbookSettings(new MediaTypeSettings { MetadataProfileId = 20 });
            var service = CreateService(root, profiles);

            Assert.That(service.GetBookshelfPreferredEditionTerms(new Author { MetadataProfileId = 10, EbookMetadataProfileId = 0 }, "/books/incoming.epub", BookMediaType.Ebook),
                Is.EqualTo("1|author default"));
        }

        private static FileMatchingService CreateService(RootFolder root, MetadataProfileServiceStub profiles)
        {
            return new FileMatchingService(
                null, null, null, null, null, null,
                new RootFolderServiceStub { RootFolder = root },
                null, null, null, null, null, null, null, null, null,
                LogManager.GetCurrentClassLogger(),
                metadataProfileService: profiles);
        }
    }
}
