using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.RootFolders;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    [Platform(Exclude = "Win", Reason = "Tests use Unix paths")]
    public class AuthorPathBuilderEnsureAuthorPathsFixture
    {
        private sealed class StubBuildFileNames : IBuildFileNames
        {
            public string GetAuthorFolder(Author author, NamingConfig namingConfig = null, string mediaType = "audiobook")
            {
                return author?.Name;
            }

            public string BuildBookFileName(Author author, Edition edition, BookFile bookFile, NamingConfig namingConfig = null, List<CustomFormat> customFormats = null) => throw new NotImplementedException();
            public string BuildBookFilePath(Author author, Edition edition, string fileName, string extension) => throw new NotImplementedException();
            public string BuildBookPath(Author author) => throw new NotImplementedException();
            public BasicNamingConfig GetBasicNamingConfig(NamingConfig nameSpec) => throw new NotImplementedException();
        }

        private sealed class StubRootFolderService : IRootFolderService
        {
            private readonly List<RootFolder> _rootFolders;

            public StubRootFolderService(List<RootFolder> rootFolders)
            {
                _rootFolders = rootFolders ?? new List<RootFolder>();
            }

            public List<RootFolder> All() => _rootFolders;
            public List<RootFolder> AllWithSpaceStats() => _rootFolders;
            public RootFolder Add(RootFolder rootFolder) => throw new NotImplementedException();
            public RootFolder Update(RootFolder rootFolder) => throw new NotImplementedException();
            public void Remove(int id) => throw new NotImplementedException();
            public RootFolder Get(int id) => throw new NotImplementedException();
            public List<RootFolder> AllForTag(int tagId) => throw new NotImplementedException();

            public RootFolder GetBestRootFolder(string path)
            {
                return GetBestRootFolder(path, _rootFolders);
            }

            public RootFolder GetBestRootFolder(string path, List<RootFolder> allRootFolders)
            {
                if (path.IsNullOrWhiteSpace())
                {
                    return null;
                }

                allRootFolders ??= new List<RootFolder>();

                return allRootFolders
                    .Where(r => r != null && r.Path.IsNotNullOrWhiteSpace())
                    .Where(r => r.Path.PathEquals(path) || r.Path.IsParentPath(path))
                    .OrderByDescending(r => r.Path.Length)
                    .FirstOrDefault();
            }

            public string GetBestRootFolderPath(string path)
            {
                return GetBestRootFolderPath(path, _rootFolders);
            }

            public string GetBestRootFolderPath(string path, List<RootFolder> allRootFolders)
            {
                var possibleRootFolder = GetBestRootFolder(path, allRootFolders);

                if (possibleRootFolder == null)
                {
                    var osPath = new OsPath(path);
                    return osPath.Directory.ToString().TrimEnd(osPath.IsUnixPath ? '/' : '\\');
                }

                return possibleRootFolder.Path;
            }
        }

        [Test]
        public void should_rebuild_audiobook_path_when_outside_configured_root()
        {
            var builder = new AuthorPathBuilder(
                new StubBuildFileNames(),
                new StubRootFolderService(new List<RootFolder> { new RootFolder { Path = "/media", FolderType = FolderType.Mixed } }));

            var author = new Author
            {
                Name = "Alexis Hall",
                AudiobookRootFolderPath = "/media",
                AudiobookPath = "/Audiobooks/Alexis Hall",
                Path = "/Audiobooks/Alexis Hall"
            };

            builder.EnsureAuthorPaths(author, useExistingRelativeFolder: true);

            Assert.That(author.AudiobookPath, Is.EqualTo("/media/Alexis Hall"));
            Assert.That(author.Path, Is.EqualTo("/media/Alexis Hall"));
        }

        [Test]
        public void should_rebuild_ebook_path_when_outside_configured_root()
        {
            var builder = new AuthorPathBuilder(
                new StubBuildFileNames(),
                new StubRootFolderService(new List<RootFolder> { new RootFolder { Path = "/media", FolderType = FolderType.Mixed } }));

            var author = new Author
            {
                Name = "Alexis Hall",
                EbookRootFolderPath = "/media",
                EbookPath = "/Ebooks/Alexis Hall",
                Path = "/Ebooks/Alexis Hall"
            };

            builder.EnsureAuthorPaths(author, useExistingRelativeFolder: true);

            Assert.That(author.EbookPath, Is.EqualTo("/media/Alexis Hall"));
            Assert.That(author.Path, Is.EqualTo("/media/Alexis Hall"));
        }

        [Test]
        public void should_set_both_media_paths_when_roots_are_shared()
        {
            var builder = new AuthorPathBuilder(
                new StubBuildFileNames(),
                new StubRootFolderService(new List<RootFolder> { new RootFolder { Path = "/media", FolderType = FolderType.Mixed } }));

            var author = new Author
            {
                Name = "Alexis Hall",
                AudiobookRootFolderPath = "/media",
                EbookRootFolderPath = "/media",
                AudiobookPath = "/Audiobooks/Alexis Hall",
                EbookPath = "",
                Path = "/Audiobooks/Alexis Hall"
            };

            builder.EnsureAuthorPaths(author, useExistingRelativeFolder: true);

            Assert.That(author.AudiobookPath, Is.EqualTo("/media/Alexis Hall"));
            Assert.That(author.EbookPath, Is.EqualTo("/media/Alexis Hall"));
            Assert.That(author.Path, Is.EqualTo("/media/Alexis Hall"));
        }

        [Test]
        public void should_use_stored_media_path_for_import_even_when_folder_does_not_exist_yet()
        {
            var builder = new AuthorPathBuilder(
                new StubBuildFileNames(),
                new StubRootFolderService(new List<RootFolder> { new RootFolder { Path = "/media", FolderType = FolderType.Mixed } }));
            var author = new Author
            {
                Name = "A. F. Kay",
                AudiobookRootFolderPath = "/media",
                AudiobookPath = "/media/A.F. Kay"
            };

            var path = builder.BuildPathForQuality(author, NzbDrone.Core.Qualities.Quality.MP3, useExistingRelativeFolder: false);

            Assert.That(path, Is.EqualTo("/media/A.F. Kay"));
        }

        [TestCase("/media")]
        [TestCase("/other/A.F. Kay")]
        public void should_fall_back_to_canonical_path_when_stored_media_path_is_unsafe(string storedPath)
        {
            var builder = new AuthorPathBuilder(
                new StubBuildFileNames(),
                new StubRootFolderService(new List<RootFolder> { new RootFolder { Path = "/media", FolderType = FolderType.Mixed } }));
            var author = new Author
            {
                Name = "A. F. Kay",
                AudiobookRootFolderPath = "/media",
                AudiobookPath = storedPath
            };

            var path = builder.BuildPathForQuality(author, NzbDrone.Core.Qualities.Quality.MP3, useExistingRelativeFolder: false);

            Assert.That(path, Is.EqualTo("/media/A. F. Kay"));
        }
    }
}
